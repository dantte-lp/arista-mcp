using System.Text.Json;

namespace AristaMcp.Data.Tests.Ingest;

internal static class FakeCatalogJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
}
