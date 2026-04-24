using System.Text.Json.Serialization;

namespace AristaMcp.Cli.Benchmarks;

public sealed record BenchmarkQuerySet
{
    // Schema version. Absent / 1 = slug-substring scoring via ExpectAny+ExpectProduct.
    // 2 = chunk-ID scoring via ExpectAnyOfChunkIds (Sprint 13).
    [JsonPropertyName("version")] public int Version { get; init; } = 1;

    [JsonPropertyName("queries")] public required IReadOnlyList<BenchmarkQuery> Queries { get; init; }
}
