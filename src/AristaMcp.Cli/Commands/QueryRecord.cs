using System.Text.Json.Serialization;

namespace AristaMcp.Cli.Commands;

public sealed record QueryRecord(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("source_chunk_id")] long SourceChunkId,
    [property: JsonPropertyName("source_product")] string? SourceProduct,
    [property: JsonPropertyName("source_doc_title")] string? SourceDocTitle,
    [property: JsonPropertyName("source_section_title")] string? SourceSectionTitle,
    [property: JsonPropertyName("generation_model")] string? GenerationModel);
