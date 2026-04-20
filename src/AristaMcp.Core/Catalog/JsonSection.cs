using System.Text.Json.Serialization;

namespace AristaMcp.Core.Catalog;

public sealed record JsonSection
{
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("level")] public short Level { get; init; }
    [JsonPropertyName("page_start")] public int? PageStart { get; init; }
    [JsonPropertyName("page_end")] public int? PageEnd { get; init; }
}
