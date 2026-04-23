namespace AristaMcp.Cli.Curation;

public sealed record TripleEntry(
    string DocumentId,
    string DocumentSlug,
    string DocumentTitle,
    string? Product,
    long ChunkId,
    int? PageStart,
    int? PageEnd,
    string Text);
