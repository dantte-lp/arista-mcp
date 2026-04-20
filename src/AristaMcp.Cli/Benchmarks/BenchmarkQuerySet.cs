using System.Text.Json.Serialization;

namespace AristaMcp.Cli.Benchmarks;

public sealed record BenchmarkQuerySet
{
    [JsonPropertyName("queries")] public required IReadOnlyList<BenchmarkQuery> Queries { get; init; }
}
