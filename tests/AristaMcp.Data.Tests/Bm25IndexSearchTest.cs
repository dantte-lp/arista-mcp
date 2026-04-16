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

        results.Should().HaveCount(3);
        // Chunks 0 ("BGP over VXLAN overlay ... EVPN") and 3 ("EVPN type-5 ... overlay networks")
        // contain both query tokens and must rank in the top 3.
        var topIndices = results.Select(r => r.Idx).ToList();
        topIndices.Should().Contain(0);
        topIndices.Should().Contain(3);

        // <&> returns negative BM25 score (lower = better match); both top hits should be < 0.
        results[0].Score.Should().BeLessThan(0f,
            $"best match must have negative score; got {results[0].Score.ToString(CultureInfo.InvariantCulture)}");
    }
}
