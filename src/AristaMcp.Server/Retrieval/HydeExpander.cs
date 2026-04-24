using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AristaMcp.Core.Retrieval;
using AristaMcp.Core.Settings;

namespace AristaMcp.Server.Retrieval;

// Talks to a local llama.cpp (or any OpenAI-compatible) chat endpoint to
// rewrite the raw user query into a hypothetical answer paragraph. The
// paragraph becomes the dense-retrieval query. Always degrades gracefully:
//
//   - 3 s timeout → fallback to raw query
//   - empty / too-short response → fallback
//   - 5xx, connection refused, socket error → fallback, increment breaker
//   - N consecutive failures → circuit open, skip LLM calls for T seconds
//
// Cache keyed by the raw query string (post-QueryExpander, so acronym
// annotations collapse "EVPN" and "EVPN (Ethernet VPN)" onto the same
// entry). 512 entries default — empirically enough for one bench run.
public sealed class HydeExpander : IHydeExpander
{
    // System prompt anchors the model on Arista-EOS domain framing so the
    // hypothetical paragraph uses vocabulary that matches the indexed
    // corpus. No hedging, no preface — the whole output becomes a dense
    // query, so every token needs to carry signal.
    private const string SystemPrompt =
        "You are a senior Arista EOS networking engineer. When given a question, "
        + "you write one concise paragraph (2 to 4 sentences, about 100 words) that "
        + "would answer it as if it were a passage from an Arista technical document. "
        + "Do not preface, do not hedge, do not repeat the question. Output only the paragraph.";

    private readonly HttpClient _http;
    private readonly HydeSettings _opt;
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    private long _accessCounter;
    private int _consecutiveFailures;
    private long _circuitOpenUntilTicks;

    public HydeExpander(HttpClient http, HydeSettings opt, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(opt);
        ArgumentNullException.ThrowIfNull(time);
        _http = http;
        _opt = opt;
        _time = time;
    }

    public async Task<HydeResult> ExpandAsync(string query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (_cache.TryGetValue(query, out var cached))
        {
            cached.LastAccess = Interlocked.Increment(ref _accessCounter);
            return new HydeResult(cached.DenseQuery, 0, CacheHit: true, UsedFallback: false);
        }

        if (IsCircuitOpen())
        {
            return new HydeResult(query, 0, CacheHit: false, UsedFallback: true);
        }

        var sw = Stopwatch.StartNew();
        var rewritten = await CallLlmAsync(query, ct).ConfigureAwait(false);
        sw.Stop();
        var elapsed = sw.Elapsed.TotalMilliseconds;

        if (rewritten is null || rewritten.Length < 20 || string.Equals(rewritten, query, StringComparison.Ordinal))
        {
            RecordFailure();
            return new HydeResult(query, elapsed, CacheHit: false, UsedFallback: true);
        }

        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Add(query, rewritten);
        return new HydeResult(rewritten, elapsed, CacheHit: false, UsedFallback: false);
    }

    private async Task<string?> CallLlmAsync(string query, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_opt.TimeoutMs));
        var token = timeoutCts.Token;

        var request = new ChatRequest(
            Model: _opt.Model,
            Messages:
            [
                new Message("system", SystemPrompt),
                new Message("user", query),
            ],
            MaxTokens: _opt.MaxTokens,
            Temperature: 0.2f,
            Stream: false);

        try
        {
            using var response = await _http.PostAsJsonAsync(_opt.Endpoint, request, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<ChatResponse>(token)
                .ConfigureAwait(false);
            var text = body is { Choices.Count: > 0 } ? body.Choices[0].Message?.Content : null;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private bool IsCircuitOpen()
    {
        var until = Interlocked.Read(ref _circuitOpenUntilTicks);
        if (until == 0)
        {
            return false;
        }

        if (_time.GetUtcNow().Ticks < until)
        {
            return true;
        }

        Interlocked.Exchange(ref _circuitOpenUntilTicks, 0);
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        return false;
    }

    private void RecordFailure()
    {
        var count = Interlocked.Increment(ref _consecutiveFailures);
        if (count >= _opt.CircuitFailureThreshold)
        {
            var openUntil = _time.GetUtcNow().AddSeconds(_opt.CircuitCooldownSeconds).Ticks;
            Interlocked.Exchange(ref _circuitOpenUntilTicks, openUntil);
        }
    }

    private void Add(string key, string value)
    {
        _cache[key] = new Entry
        {
            DenseQuery = value,
            LastAccess = Interlocked.Increment(ref _accessCounter),
        };

        if (_cache.Count > _opt.CacheCapacity)
        {
            EvictOldestHalf();
        }
    }

    private void EvictOldestHalf()
    {
        var snapshot = _cache.ToArray();
        Array.Sort(snapshot, static (a, b) => a.Value.LastAccess.CompareTo(b.Value.LastAccess));
        var evictCount = snapshot.Length - (_opt.CacheCapacity / 2);
        for (var i = 0; i < evictCount; i++)
        {
            _cache.TryRemove(snapshot[i].Key, out _);
        }
    }

    private sealed class Entry
    {
        public required string DenseQuery { get; init; }
        public long LastAccess { get; set; }
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<Message> Messages,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("temperature")] float Temperature,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record Message(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<Choice> Choices);

    private sealed record Choice(
        [property: JsonPropertyName("message")] Message? Message);
}
