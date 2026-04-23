namespace AristaMcp.Cli.Curation;

public sealed record TripleCandidate(
    string Query,
    TripleEntry Positive,
    IReadOnlyList<TripleEntry> Negatives);
