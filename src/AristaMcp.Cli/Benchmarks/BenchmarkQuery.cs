using System.Text.Json.Serialization;

namespace AristaMcp.Cli.Benchmarks;

public sealed record BenchmarkQuery
{
    [JsonPropertyName("query")] public required string Query { get; init; }

    // Substring tokens — the query is considered a hit if any result's document slug
    // or title contains (case-insensitive) at least one of these tokens.
    [JsonPropertyName("expect_any")] public IReadOnlyList<string> ExpectAny { get; init; } = [];
}
