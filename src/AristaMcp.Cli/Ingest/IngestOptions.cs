namespace AristaMcp.Cli.Ingest;

public sealed class IngestOptions
{
    public required string CatalogPath { get; init; }
    public bool DryRun { get; init; }
    public bool Force { get; init; }
    public string? Category { get; init; }
    public bool Verbose { get; init; }
}
