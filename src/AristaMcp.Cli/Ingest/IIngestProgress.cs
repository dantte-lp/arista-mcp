namespace AristaMcp.Cli.Ingest;

public interface IIngestProgress
{
    void Log(string message);

    void BeginDocument(string docId, string title, int index, int total);

    void EndDocument(string docId, int chunks, bool skipped);
}
