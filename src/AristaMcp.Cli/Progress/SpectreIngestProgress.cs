using AristaMcp.Cli.Ingest;
using Spectre.Console;

namespace AristaMcp.Cli.Progress;

public sealed class SpectreIngestProgress(IAnsiConsole console, bool verbose) : IIngestProgress
{
    public void Log(string message) => console.MarkupLine($"[grey]{Markup.Escape(message)}[/]");

    public void BeginDocument(string docId, string title, int index, int total)
    {
        if (!verbose)
        {
            return;
        }

        console.MarkupLine(
            $"[grey]({index}/{total})[/] [bold]{Markup.Escape(docId)}[/] [grey]{Markup.Escape(title)}[/]");
    }

    public void EndDocument(string docId, int chunks, bool skipped)
    {
        if (skipped)
        {
            if (verbose)
            {
                console.MarkupLine($"  [yellow]skipped[/] {Markup.Escape(docId)}");
            }

            return;
        }

        if (verbose)
        {
            console.MarkupLine($"  [green]+{chunks}[/] chunks");
        }
    }
}
