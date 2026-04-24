using System.Net;
using System.Text;
using AristaMcp.Core.Retrieval;
using AristaMcp.Core.Settings;
using AristaMcp.Server.Retrieval;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AristaMcp.Server.Tests.Retrieval;

public class HydeExpanderTests
{
    [Fact]
    public async Task HappyPath_RewritesQueryAndCachesResult()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var body = """
        {"choices":[{"message":{"role":"assistant","content":"Arista EOS supports EVPN overlays via VXLAN tunnels. The BGP control plane carries type-5 routes."}}]}
        """;
        var handler = StubHandler.RespondWith(HttpStatusCode.OK, body);
        using var http = new HttpClient(handler);
        var expander = new HydeExpander(http, new HydeSettings { Enabled = true }, time);

        var first = await expander.ExpandAsync("EVPN overlay", CancellationToken.None);
        first.DenseQuery.Should().StartWith("Arista EOS supports EVPN");
        first.CacheHit.Should().BeFalse();
        first.UsedFallback.Should().BeFalse();

        var second = await expander.ExpandAsync("EVPN overlay", CancellationToken.None);
        second.CacheHit.Should().BeTrue();
        second.DenseQuery.Should().Be(first.DenseQuery);

        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task FallsBackToRawQuery_OnServerError()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var handler = StubHandler.RespondWith(HttpStatusCode.InternalServerError, "{}");
        using var http = new HttpClient(handler);
        var expander = new HydeExpander(http, new HydeSettings { Enabled = true }, time);

        var result = await expander.ExpandAsync("EVPN overlay", CancellationToken.None);

        result.DenseQuery.Should().Be("EVPN overlay");
        result.UsedFallback.Should().BeTrue();
    }

    [Fact]
    public async Task FallsBackToRawQuery_OnMalformedJson()
    {
        // llama.cpp under memory pressure has been observed to return 200 OK
        // with a truncated JSON body. Must degrade to the raw query instead
        // of crashing the caller.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var handler = StubHandler.RespondWith(HttpStatusCode.OK, "{not actually json");
        using var http = new HttpClient(handler);
        var expander = new HydeExpander(http, new HydeSettings { Enabled = true }, time);

        var result = await expander.ExpandAsync("EVPN overlay", CancellationToken.None);

        result.DenseQuery.Should().Be("EVPN overlay");
        result.UsedFallback.Should().BeTrue();
    }

    [Fact]
    public async Task FallsBackToRawQuery_OnSlowServer()
    {
        // TimeoutMs must actually wire through CancelAfter — a hang on the
        // sidecar side should not hang the retriever.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var handler = SlowHandler.HangFor(TimeSpan.FromSeconds(10));
        using var http = new HttpClient(handler);
        var expander = new HydeExpander(
            http, new HydeSettings { Enabled = true, TimeoutMs = 100 }, time);

        var result = await expander.ExpandAsync("EVPN overlay", CancellationToken.None);

        result.DenseQuery.Should().Be("EVPN overlay");
        result.UsedFallback.Should().BeTrue();
    }

    [Fact]
    public async Task FallsBackToRawQuery_OnEmptyResponse()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var body = """{"choices":[{"message":{"role":"assistant","content":""}}]}""";
        var handler = StubHandler.RespondWith(HttpStatusCode.OK, body);
        using var http = new HttpClient(handler);
        var expander = new HydeExpander(http, new HydeSettings { Enabled = true }, time);

        var result = await expander.ExpandAsync("EVPN overlay", CancellationToken.None);

        result.DenseQuery.Should().Be("EVPN overlay");
        result.UsedFallback.Should().BeTrue();
    }

    [Fact]
    public async Task CircuitOpensAfterConsecutiveFailures_ThenResetsOnCooldown()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var handler = StubHandler.RespondWith(HttpStatusCode.InternalServerError, "{}");
        using var http = new HttpClient(handler);
        var settings = new HydeSettings
        {
            Enabled = true,
            CircuitFailureThreshold = 3,
            CircuitCooldownSeconds = 30,
        };
        var expander = new HydeExpander(http, settings, time);

        // 3 failures trip the breaker.
        for (var i = 0; i < 3; i++)
        {
            await expander.ExpandAsync($"q{i}", CancellationToken.None);
        }
        handler.CallCount.Should().Be(3);

        // Breaker open — no HTTP call made.
        var open = await expander.ExpandAsync("q4", CancellationToken.None);
        open.UsedFallback.Should().BeTrue();
        handler.CallCount.Should().Be(3, "breaker should suppress HTTP traffic while open");

        // Advance past cooldown — next call hits the wire again.
        time.Advance(TimeSpan.FromSeconds(31));
        handler.SetResponse(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"Long enough paragraph about EVPN details."}}]}""");
        var recovered = await expander.ExpandAsync("q5", CancellationToken.None);
        recovered.UsedFallback.Should().BeFalse();
        handler.CallCount.Should().Be(4);
    }

    [Fact]
    public async Task CircuitArms_ExactlyOnce_UnderConcurrentFailures()
    {
        // M1 regression guard: without CompareExchange on the cooldown write,
        // parallel failures could each sample GetUtcNow() at slightly
        // different instants and race to extend the cooldown window.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var handler = StubHandler.RespondWith(HttpStatusCode.InternalServerError, "{}");
        using var http = new HttpClient(handler);
        var settings = new HydeSettings
        {
            Enabled = true,
            CircuitFailureThreshold = 3,
            CircuitCooldownSeconds = 60,
        };
        var expander = new HydeExpander(http, settings, time);

        // Fire 10 concurrent queries while the stub is returning 5xx — at
        // least CircuitFailureThreshold of them will RecordFailure before
        // the breaker closes further calls.
        var tasks = Enumerable.Range(0, 10)
            .Select(i => expander.ExpandAsync($"q{i}", CancellationToken.None))
            .ToArray();
        await Task.WhenAll(tasks);

        // All should have returned a fallback (no crashes, no exceptions).
        tasks.Should().OnlyContain(t => t.Result.UsedFallback);

        // Breaker is armed — advance past cooldown and it should re-arm
        // exactly once. Advance exactly 60s from the first failure's
        // GetUtcNow() sample = DateTimeOffset.UnixEpoch (since FakeTimeProvider
        // didn't advance during the race). If the breaker was re-armed at a
        // later time by a racing writer, 60s wouldn't be enough.
        time.Advance(TimeSpan.FromSeconds(60).Add(TimeSpan.FromTicks(1)));
        handler.SetResponse(HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"Valid paragraph longer than twenty chars about EVPN."}}]}""");

        var recovered = await expander.ExpandAsync("post-cooldown", CancellationToken.None);
        recovered.UsedFallback.Should().BeFalse(
            "cooldown deadline should have been set exactly once — if racing writers extended it, 60s advance would be insufficient");
    }

    [Fact]
    public async Task Disabled_BehaviourUsesNoopExpander()
    {
        var noop = new NoopHydeExpander();
        var result = await noop.ExpandAsync("EVPN overlay", CancellationToken.None);
        result.DenseQuery.Should().Be("EVPN overlay");
        result.LatencyMs.Should().Be(0);
        result.UsedFallback.Should().BeTrue();
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;

        private SlowHandler(TimeSpan delay) => _delay = delay;

        public static SlowHandler HangFor(TimeSpan delay) => new(delay);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode _status;
        private string _body;
        public int CallCount { get; private set; }

        private StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public static StubHandler RespondWith(HttpStatusCode status, string body) => new(status, body);

        public void SetResponse(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var resp = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }
}
