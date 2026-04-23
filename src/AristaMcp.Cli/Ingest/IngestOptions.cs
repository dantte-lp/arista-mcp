namespace AristaMcp.Cli.Ingest;

public sealed class IngestOptions
{
    public required string CatalogPath { get; init; }
    public bool DryRun { get; init; }
    public bool Force { get; init; }
    public string? Category { get; init; }
    public bool Verbose { get; init; }

    // Sub-batch ceiling for per-doc chunk ingest. EOS-User-Manual produces
    // ~40 k chunks post-CRLF-fix and Npgsql's COPY BINARY buffer plus
    // postgres's bm25v trigger work together spike memory at the all-at-once
    // scale. 2000 is empirically below the spike threshold while keeping
    // overhead low for normal-sized docs (< 2000 chunks → single sub-batch,
    // same as pre-8.2 behaviour).
    public int ChunkSubBatchSize { get; init; } = 2000;
}
