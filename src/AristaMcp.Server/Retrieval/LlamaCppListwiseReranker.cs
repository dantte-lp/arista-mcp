using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using AristaMcp.Core.Retrieval;
using AristaMcp.Core.Settings;

namespace AristaMcp.Server.Retrieval;

// Listwise top-5 re-rank via a local llama.cpp-served instruction model.
// Sits AFTER the cross-encoder reranker and reorders only its top-5 in
// the hope that a generative model catches semantic-equivalence ties
// the cross-encoder leaves at sub-σ score deltas. Failures degrade
// gracefully — input order is returned, the breaker arms, retrieval
// continues uninterrupted.
//
// The prompt is deliberately rigid: candidates labelled A..E (max five),
// answer must be a comma-separated permutation of those letters. Strict
// regex parse, fall back on any deviation. No CoT — that doubles output
// tokens and we observed Qwen2.5-3B get LESS reliable when given room
// to ramble before the final answer.
public sealed partial class LlamaCppListwiseReranker : IListwiseReranker
{
    private const string SystemPrompt =
        "You rank technical documentation passages by relevance to a user query. "
        + "You are given the query and 5 candidate passages labelled A through E. "
        + "Reply with EXACTLY one line containing the candidate letters in MOST "
        + "to LEAST relevant order, comma-separated. Example: B,A,D,C,E. "
        + "No prose, no explanation, no markdown.";

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b(?<p1>[A-E])\s*,\s*(?<p2>[A-E])\s*,\s*(?<p3>[A-E])\s*,\s*(?<p4>[A-E])\s*,\s*(?<p5>[A-E])\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.ExplicitCapture)]
    private static partial System.Text.RegularExpressions.Regex Permutation5Regex();

    private readonly HttpClient _http;
    private readonly ListwiseRerankSettings _opt;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    private long _accessCounter;
    private int _consecutiveFailures;
    private long _circuitOpenUntilTicks;

    public LlamaCppListwiseReranker(HttpClient http, ListwiseRerankSettings opt, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(opt);
        ArgumentNullException.ThrowIfNull(time);
        _http = http;
        _opt = opt;
        _time = time;
    }

    public int MaxCandidates => _opt.MaxCandidates;

    public async Task<ListwiseResult> ReorderAsync(
        string query,
        IReadOnlyList<RerankCandidate> candidates,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        // Less than 2 candidates — nothing to reorder; skip the LLM.
        if (candidates.Count < 2)
        {
            return Fallback(candidates, latencyMs: 0, cacheHit: false);
        }

        // Cap at MaxCandidates (default 5). Anything past the cap stays
        // in input order and is simply not reordered. The retriever
        // re-stitches the listwise prefix onto the unchanged tail.
        var slice = candidates.Count > _opt.MaxCandidates
            ? [.. candidates.Take(_opt.MaxCandidates)]
            : candidates;

        var cacheKey = BuildCacheKey(query, slice);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            cached.LastAccess = Interlocked.Increment(ref _accessCounter);
            return new ListwiseResult(cached.OrderedIds, 0, CacheHit: true, UsedFallback: false);
        }

        if (IsCircuitOpen())
        {
            return Fallback(slice, latencyMs: 0, cacheHit: false);
        }

        var sw = Stopwatch.StartNew();
        var orderedIds = await CallLlmAsync(query, slice, ct).ConfigureAwait(false);
        sw.Stop();

        if (orderedIds is null)
        {
            RecordFailure();
            return Fallback(slice, latencyMs: sw.Elapsed.TotalMilliseconds, cacheHit: false);
        }

        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Add(cacheKey, orderedIds);
        return new ListwiseResult(orderedIds, sw.Elapsed.TotalMilliseconds, CacheHit: false, UsedFallback: false);
    }

    private async Task<long[]?> CallLlmAsync(
        string query, IReadOnlyList<RerankCandidate> slice, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_opt.TimeoutMs));
        var token = timeoutCts.Token;

        var userPrompt = BuildUserPrompt(query, slice);
        var request = new ChatRequest(
            Model: _opt.Model,
            Messages:
            [
                new Message("system", SystemPrompt),
                new Message("user", userPrompt),
            ],
            // 32 tokens is more than enough for "X,X,X,X,X" — bounding
            // it tight stops Qwen from spilling into prose.
            MaxTokens: 32,
            Temperature: 0.0f,
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
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return ParseOrder(text, slice);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string BuildUserPrompt(string query, IReadOnlyList<RerankCandidate> slice)
    {
        // 400 chars per candidate keeps total prompt under ~3 k chars, well
        // inside Qwen2.5-3B's 4 k context. The first 400 chars of a 512-token
        // chunk's parent already carries enough signal — the cross-encoder
        // already saw the full text, so the listwise pass exists just for
        // disambiguation, not for reading.
        var sb = new StringBuilder(2400);
        sb.Append("Query: ").Append(query).Append("\n\n");
        for (var i = 0; i < slice.Count; i++)
        {
            var letter = (char)('A' + i);
            var text = slice[i].Text;
            if (text.Length > 400)
            {
                text = text[..400];
            }
            sb.Append("Candidate ").Append(letter).Append(":\n").Append(text).Append("\n\n");
        }
        sb.Append("Reply with the candidate letters in MOST to LEAST relevant order, "
            + "comma-separated. Example: B,A,D,C,E");
        return sb.ToString();
    }

    private static long[]? ParseOrder(string text, IReadOnlyList<RerankCandidate> slice)
    {
        if (slice.Count != 5)
        {
            // Fall through to a more general parser. The strict regex below
            // is sized for 5 candidates — for other counts we'd need a
            // different shape. Plan only ever calls this with N=5 today.
            return ParseOrderGeneric(text, slice);
        }

        var match = Permutation5Regex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        Span<int> seenLetters = stackalloc int[5];
        var ids = new long[5];
        ReadOnlySpan<string> groupNames = ["p1", "p2", "p3", "p4", "p5"];
        for (var i = 0; i < 5; i++)
        {
            var letter = char.ToUpperInvariant(match.Groups[groupNames[i]].Value[0]);
            var idx = letter - 'A';
            if (idx < 0 || idx >= 5 || seenLetters[idx] != 0)
            {
                return null;
            }
            seenLetters[idx] = 1;
            ids[i] = slice[idx].ChunkId;
        }
        return ids;
    }

    private static long[]? ParseOrderGeneric(string text, IReadOnlyList<RerankCandidate> slice)
    {
        // Walk the text picking the first occurrence of each candidate
        // letter A.. Up to slice.Count distinct letters. If we get a
        // full permutation, return it.
        var ids = new long[slice.Count];
        var seen = new bool[slice.Count];
        var written = 0;

        foreach (var raw in text)
        {
            var letter = char.ToUpperInvariant(raw);
            var idx = letter - 'A';
            if (idx < 0 || idx >= slice.Count || seen[idx])
            {
                continue;
            }
            seen[idx] = true;
            ids[written++] = slice[idx].ChunkId;
            if (written == slice.Count)
            {
                return ids;
            }
        }
        return null;
    }

    private static ListwiseResult Fallback(IReadOnlyList<RerankCandidate> input, double latencyMs, bool cacheHit)
    {
        var ids = new long[input.Count];
        for (var i = 0; i < input.Count; i++)
        {
            ids[i] = input[i].ChunkId;
        }
        return new ListwiseResult(ids, latencyMs, cacheHit, UsedFallback: true);
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
        if (count < _opt.CircuitFailureThreshold)
        {
            return;
        }
        var openUntil = _time.GetUtcNow().AddSeconds(_opt.CircuitCooldownSeconds).Ticks;
        Interlocked.CompareExchange(ref _circuitOpenUntilTicks, openUntil, 0L);
    }

    private static string BuildCacheKey(string query, IReadOnlyList<RerankCandidate> slice)
    {
        var sb = new StringBuilder(query.Length + slice.Count * 12);
        sb.Append(query).Append('|');
        for (var i = 0; i < slice.Count; i++)
        {
            sb.Append(slice[i].ChunkId).Append(',');
        }
        return sb.ToString();
    }

    private void Add(string key, long[] orderedIds)
    {
        _cache[key] = new Entry
        {
            OrderedIds = orderedIds,
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
        public required long[] OrderedIds { get; init; }
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
