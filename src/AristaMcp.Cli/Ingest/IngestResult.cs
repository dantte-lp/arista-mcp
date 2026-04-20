namespace AristaMcp.Cli.Ingest;

public sealed record IngestResult(
    string Status,
    int DocsTotal,
    int DocsSkipped,
    int DocsUpserted,
    int ChunksUpserted,
    string? Error);
