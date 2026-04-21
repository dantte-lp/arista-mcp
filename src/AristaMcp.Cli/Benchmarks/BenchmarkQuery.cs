using System.Text.Json.Serialization;

namespace AristaMcp.Cli.Benchmarks;

public sealed record BenchmarkQuery
{
    [JsonPropertyName("query")] public required string Query { get; init; }

    // Substring tokens — the query is considered a hit if any result's document slug
    // or title contains (case-insensitive) at least one of these tokens.
    [JsonPropertyName("expect_any")] public IReadOnlyList<string> ExpectAny { get; init; } = [];

    // Optional exact-match on ChunkResult.Product. Useful when the product line uses
    // model-number slugs (e.g. `7050X3-Datasheet`) that don't contain the product word.
    [JsonPropertyName("expect_product")] public string? ExpectProduct { get; init; }
}
