using System.Text.Json.Serialization;

namespace AristaMcp.Core.Catalog;

// Mirrors the shape of arista-docs/data/converted/json/<slug>.json produced by
// arista_docs/core/enrich.py. Only fields we actually consume are projected.
public sealed record EnrichedDocumentJson
{
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("sections")] public IReadOnlyList<JsonSection> Sections { get; init; } = [];
    [JsonPropertyName("image_names")] public IReadOnlyList<string> ImageNames { get; init; } = [];
}
