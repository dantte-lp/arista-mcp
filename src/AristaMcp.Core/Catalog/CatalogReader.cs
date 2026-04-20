using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AristaMcp.Core.Catalog;

public static class CatalogReader
{
    public static async Task<CatalogReadResult> ReadAsync(string catalogPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(catalogPath);
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException($"catalog.json not found at {catalogPath}", catalogPath);
        }

        var bytes = await File.ReadAllBytesAsync(catalogPath, ct).ConfigureAwait(false);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var doc = JsonSerializer.Deserialize<CatalogDocument>(bytes, JsonOptions)
            ?? throw new InvalidOperationException("catalog.json deserialized to null");

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(catalogPath))
            ?? throw new InvalidOperationException($"Cannot resolve directory for {catalogPath}");

        return new CatalogReadResult
        {
            Document = doc,
            Sha256 = sha,
            BaseDirectory = baseDir,
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
