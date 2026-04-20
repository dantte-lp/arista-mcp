using AristaMcp.Core.Chunking;
using AristaMcp.Core.Models;

namespace AristaMcp.Core.Catalog;

public sealed record LoadedDocument
{
    public required AristaDocument Metadata { get; init; }
    public required IReadOnlyList<Section> Sections { get; init; }
}
