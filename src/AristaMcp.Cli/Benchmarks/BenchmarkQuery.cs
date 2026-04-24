using System.Text.Json.Serialization;

namespace AristaMcp.Cli.Benchmarks;

public sealed record BenchmarkQuery
{
    [JsonPropertyName("query")] public required string Query { get; init; }

    // v1 bench: substring tokens — the query is considered a hit if any result's
    // document slug or title contains (case-insensitive) at least one of these tokens.
    [JsonPropertyName("expect_any")] public IReadOnlyList<string> ExpectAny { get; init; } = [];

    // v1 bench: optional exact-match on ChunkResult.Product. Useful when the product
    // line uses model-number slugs (e.g. `7050X3-Datasheet`) that don't contain the
    // product word.
    [JsonPropertyName("expect_product")] public string? ExpectProduct { get; init; }

    // v2 bench (Sprint 13): ground-truth chunk IDs. If populated, scoring is done
    // purely on ChunkId membership and the ExpectAny/ExpectProduct heuristics are
    // ignored. Produced by scripts/annotate_multi_positives.py after validation +
    // LLM multi-positive labelling.
    [JsonPropertyName("expect_any_of_chunk_ids")] public IReadOnlyList<long> ExpectAnyOfChunkIds { get; init; } = [];
}
