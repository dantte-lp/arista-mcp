namespace AristaMcp.Core.Catalog;

public sealed class CatalogReadResult
{
    public required CatalogDocument Document { get; init; }
    public required string Sha256 { get; init; }
    public required string BaseDirectory { get; init; }
}
