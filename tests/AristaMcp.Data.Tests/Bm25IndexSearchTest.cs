using System.Globalization;
using AristaMcp.Data.Entities;
using AristaMcp.Data.Tests.Fixtures;
using FluentAssertions;
using Npgsql;
using Pgvector;
using Xunit;

namespace AristaMcp.Data.Tests;

[Collection("Pgvector")]
public class Bm25IndexSearchTest(PgvectorFixture fx)
{
    [Fact]
    public async Task Bm25QueryReturnsOrderedByRelevance()
    {
        await fx.ResetAsync();

        await using var ctx = fx.CreateContext();

        ctx.Documents.Add(new DocumentEntity
        {
            Id = "bm25-doc",
            Url = "u",
            Category = "toi",
            Title = "T",
            Slug = "s",
            MdPath = "m",
            JsonPath = "j",
        });
        await ctx.SaveChangesAsync();

        var texts = new[]
        {
            "BGP over VXLAN overlay is common in EVPN deployments",
            "OSPF single area design for small campus networks",
            "MLAG configuration on Arista 7050X3 switches",
            "EVPN type-5 routes for data center overlay networks",
            "Static routing for simple hub-and-spoke topologies",
        };

        var flat = new Half[768];
        for (var j = 0; j < 768; j++)
        {
            flat[j] = (Half)0.1f;
        }

        for (var i = 0; i < texts.Length; i++)
        {
            ctx.Chunks.Add(new ChunkEntity
            {
                DocumentId = "bm25-doc",
                ChunkIndex = i,
                Content = texts[i],
                RawContent = texts[i],
                TokenCount = texts[i].Split(' ').Length,
                Embedding = new HalfVector(flat),
            });
        }

        await ctx.SaveChangesAsync();

        // vchord_bm25 query shape:
        //   bm25v <&> to_bm25query('idx_chunks_bm25'::regclass, tokenize($1, 'chunks_tokenizer')::bm25vector)
        // Uses the same custom tokenizer registered for the BM25 model, so query tokens are
        // vocabulary-aligned with the indexed tokens.
        const string sql = """
            SELECT chunk_index,
                   bm25v <&> to_bm25query(
                       'idx_chunks_bm25'::regclass,
                       tokenize($1, 'chunks_tokenizer')::bm25vector) AS score
            FROM chunks
            WHERE document_id = 'bm25-doc'
            ORDER BY bm25v <&> to_bm25query(
                'idx_chunks_bm25'::regclass,
                tokenize($1, 'chunks_tokenizer')::bm25vector)
            LIMIT 3;
            """;

        await using var conn = await fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new NpgsqlParameter { Value = "EVPN overlay" });

        var results = new List<(int Idx, float Score)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((reader.GetInt32(0), reader.GetFloat(1)));
        }

        // The BM25 index is sparse — it only returns chunks with at least one matching token.
        // "EVPN overlay" tokens land in chunks 0 ("BGP VXLAN overlay ... EVPN") and 3
        // ("EVPN type-5 ... overlay networks"); the other three chunks have zero overlap
        // with the query vocabulary and won't be returned at all.
        results.Should().HaveCountGreaterThanOrEqualTo(2);
        var topIndices = results.Select(r => r.Idx).ToList();
        topIndices.Should().Contain(0);
        topIndices.Should().Contain(3);

        results[0].Score.Should().BeLessThan(0f,
            $"best match must have negative score; got {results[0].Score.ToString(CultureInfo.InvariantCulture)}");
    }
}
