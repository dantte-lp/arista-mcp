namespace AristaMcp.Cli.Curation;

public sealed record TripleCurationStats(
    int QueriesTotal,
    int QueriesWithPositive,
    int TriplesEmitted,
    int QueriesSkippedNoPositive,
    int QueriesSkippedInsufficientNegatives);
