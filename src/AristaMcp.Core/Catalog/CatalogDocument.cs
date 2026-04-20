using System.Text.Json.Serialization;

namespace AristaMcp.Core.Catalog;

// Mirrors the top-level arista-docs catalog.json envelope.
public sealed record CatalogDocument
{
    [JsonPropertyName("generated_at")] public DateTimeOffset GeneratedAt { get; init; }
    [JsonPropertyName("documents")] public required IReadOnlyList<CatalogEntry> Documents { get; init; }
}
