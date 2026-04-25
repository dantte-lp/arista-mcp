namespace AristaMcp.Core.Retrieval;

// Default IMultiQueryExpander when multi-query is disabled. Returns the
// input unchanged in a single-element list.
public sealed class NoopMultiQueryExpander : IMultiQueryExpander
{
    public IReadOnlyList<string> Expand(string expandedQuery)
    {
        ArgumentNullException.ThrowIfNull(expandedQuery);
        return [expandedQuery];
    }
}
