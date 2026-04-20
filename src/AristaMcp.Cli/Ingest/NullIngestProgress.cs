namespace AristaMcp.Cli.Ingest;

public sealed class NullIngestProgress : IIngestProgress
{
    public static readonly NullIngestProgress Instance = new();

    public void Log(string message) { }
    public void BeginDocument(string docId, string title, int index, int total) { }
    public void EndDocument(string docId, int chunks, bool skipped) { }
}
