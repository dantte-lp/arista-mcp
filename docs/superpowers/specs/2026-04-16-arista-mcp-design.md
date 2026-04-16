# arista-mcp Design Spec — v0.1

**Date:** 2026-04-16
**Status:** Draft, awaiting user review
**Scope:** .NET 10 MCP server that ingests `arista-docs` output (catalog.json + per-doc MD/JSON) into pgvector-backed PostgreSQL 18 and serves hybrid search (dense + BM25 + rerank) over the Model Context Protocol.
**Runtime targets:** `stdio` (Claude Desktop, CLI) and `Streamable HTTP` (stateless, for shared/remote access).

## Goals

1. Consume `arista-docs v0.1.4+` output as a read-only contract (`data/catalog.json`, `data/converted/md/**/*.md`, `data/converted/json/**/*.json` with `toc`/`sections`).
2. Index 2,426 documents (32,274 sections, ~315 MB source PDFs) into pgvector with HNSW + BM25 indexes for sub-100 ms hybrid retrieval.
3. Expose MCP tools (`search_docs`, `lookup_section`, `list_documents`, `get_document`, `get_status`) to any MCP-compatible client.
4. Run fully on CPU by default; use CUDA when available, transparently.
5. Deploy via Podman + a single custom Postgres image (TensorChord vchord-suite bundles all required extensions).

## Non-goals (v0.1)

- No chat/RAG-generation tools (MCP clients bring their own LLM; we only serve retrieval).
- No multilingual corpus — Arista English docs only. BGE-M3 not used.
- No LLM-based query rewriting or HyDE — rely on deterministic synonym expansion.
- No write APIs (arista-docs is upstream; arista-mcp is strictly read + index).
- No cross-corpus search — scope limited to Arista.
- No Semantic Kernel orchestration — ModelContextProtocol directly via its C# SDK.

## Context

### `arista-docs` contract (read-only inputs)

- `data/catalog.json` — top-level index. Schema per doc: `id` (16-hex), `url`, `category` ("manual"|"toi"), `product` (nullable), `version` (nullable), `title`, `slug`, `tags` (string[]), `pages`, `size_bytes`, `pdf_sha256`, `md_path`, `json_path`, `convert_mode`, `image_count`, `section_count`, `level1_section_count`, `toc_count`, `downloaded_at`, `converted_at`.
- `data/converted/md/{category}/{product?}/{version?}/{slug}.md` — markdown with `{N}----` page markers and, for chunked docs only, `<!-- chunk: pages N..M -->` boundaries.
- `data/converted/json/{category}/{product?}/{version?}/{slug}/{slug}.json` — per-doc `title`, `pages`, `toc[]` (level/title/page/polygon), `sections[]` (title/level/page_start/page_end), `image_names[]`, `marker_metadata` (raw).
- Corpus totals (v0.1.4): 2,426 docs (225 manual + 2,201 toi), 32,274 sections total, avg 13 sections/doc, ~8 MB markdown + ~14 MB JSON + ~250 MB images + 315 MB source PDFs.

### `ntx-mcp` lessons carried forward

- Core/Server/Cli layered solution, centralised package management, BannedSymbols — reuse.
- `[McpServerToolType]` + `[McpServerTool]` attribute-based registration — reuse.
- `RrfFusion.cs` implementation — port verbatim.
- `McpClientBridge` proxying to Python — **not reused**; arista-mcp implements native pgvector retrieval.
- `ListGuides` hardcoded array — **not reused**; arista-mcp drives off catalog query.
- `docker/` + pgvector wiring — **new**; ntx-mcp only had schema sketches.

## Architecture (Approach: layered, native pgvector)

```
cli.py → runner → [core.protocols] ← adapters
                       ↓
                   core.{models, catalog, state}
```

Adapted to .NET:

```
AristaMcp.Cli                           ── command entry (System.CommandLine)
       └──▶ AristaMcp.Server            ── MCP tool hosts, DI wiring
                └──▶ AristaMcp.Core     ── models, chunker, RRF, QueryExpander, interfaces
                        └──▶ AristaMcp.Embedding  ── ONNX Embedder + Reranker
                        └──▶ AristaMcp.Data       ── EF Core + Pgvector + Npgsql
                                └──▶ PostgreSQL (custom OCI image)
```

`Core` has no reference to `Data`/`Embedding`/`Server`. `Data` knows `Core`. `Embedding` knows `Core`. `Server` wires all three via DI. `Cli` composes and invokes.

### Solution layout

```
arista-mcp/
├── arista-mcp.slnx
├── Directory.Build.props           (net10.0, Nullable, WarningsAsErrors, SARIF, analyzers)
├── Directory.Packages.props        (Central Package Management)
├── BannedSymbols.txt               (DateTime.Now, WebClient, MD5, etc.)
├── global.json                     (SDK 10.0.201, rollForward latestFeature)
├── src/
│   ├── AristaMcp.Core/
│   │   ├── Models/                 (AristaDocument, AristaChunk, SearchResult, SearchResponse, ChunkResult)
│   │   ├── Chunking/               (IChunker, SectionAwareChunker)
│   │   ├── Retrieval/              (IRetriever, HybridRetriever, RrfFusion, QueryExpander)
│   │   └── Settings/               (AristaMcpSettings, OptionsPattern)
│   ├── AristaMcp.Embedding/
│   │   ├── IEmbedder.cs
│   │   ├── IReranker.cs
│   │   ├── OnnxEmbedder.cs         (snowflake-arctic-embed-m-v1.5)
│   │   ├── OnnxReranker.cs         (bge-reranker-base)
│   │   ├── Tokenization/           (WordPiece, BPE)
│   │   └── ModelLoader.cs          (CPU/CUDA provider selection)
│   ├── AristaMcp.Data/
│   │   ├── AristaDbContext.cs
│   │   ├── Entities/               (DocumentEntity, ChunkEntity, IngestRunEntity)
│   │   ├── Migrations/
│   │   └── Repositories/           (DocumentRepository, ChunkRepository with bulk COPY)
│   ├── AristaMcp.Server/
│   │   ├── Tools/                  (AristaDocsTools.cs with [McpServerToolType])
│   │   └── ServerSetup.cs          (AddMcpServer, WithStdioServerTransport | WithHttpTransport)
│   └── AristaMcp.Cli/
│       ├── Program.cs              (System.CommandLine root)
│       ├── Commands/               (IngestCommand, ServeCommand, StatusCommand, SearchCommand, ReindexCommand)
│       └── appsettings.json
├── tests/
│   ├── AristaMcp.Core.Tests/
│   ├── AristaMcp.Embedding.Tests/
│   ├── AristaMcp.Data.Tests/       (Testcontainers-based)
│   ├── AristaMcp.Server.Tests/
│   └── AristaMcp.E2E/              ([Trait("Category","E2E")] — real models, real ingest)
├── docker/
│   ├── Containerfile               (FROM tensorchord/vchord-suite:pg18-latest — primary)
│   ├── Containerfile.from-scratch  (FROM postgres:18-trixie — fallback)
│   ├── Containerfile.app           (.NET binary for HTTP transport deployments)
│   ├── compose.yaml                (podman-compose / docker compose compatible)
│   ├── init.sql                    (CREATE EXTENSION, analyzer, performance defaults)
│   └── README.md                   (operator runbook)
├── models/                         (.gitignored; populated by scripts/download_models.ps1)
│   ├── snowflake-arctic-embed-m-v1.5/
│   └── bge-reranker-base/
├── scripts/
│   └── download_models.ps1         (HuggingFace ONNX export mirror)
├── docs/
│   └── superpowers/{specs,plans}/
├── CLAUDE.md
└── README.md
```

### Package versions (`Directory.Packages.props`)

Central Package Management. Versions verified via NuGet on 2026-04-16.

| Package | Version | Purpose |
|---|---|---|
| `ModelContextProtocol` | 1.2.0 | MCP server + client abstractions |
| `ModelContextProtocol.AspNetCore` | 1.2.0 | `WithHttpTransport` for Streamable HTTP |
| `Microsoft.Extensions.AI` | 10.4.1 | `IEmbeddingGenerator` abstractions (future-proof for chat) |
| `Microsoft.Extensions.Hosting` | 10.0.0 | DI, logging, config |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | 10.0.0 | OptionsPattern |
| `Microsoft.EntityFrameworkCore` | 10.0.1 | ORM |
| `Microsoft.EntityFrameworkCore.Design` | 10.0.1 | migrations tooling |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.1 | provider |
| `Pgvector.EntityFrameworkCore` | 0.3.0 | EF Core integration (`UseVector()`, halfvec, hnsw index mapping) |
| `Pgvector` | 0.3.2 | `Vector`, `HalfVector`, `SparseVector` types |
| `Microsoft.ML.OnnxRuntime` | 1.24.4 | CPU embedding inference |
| `Microsoft.ML.OnnxRuntime.Gpu` | 1.24.4 | optional CUDA provider |
| `Microsoft.ML.Tokenizers` | 10.4.1 | WordPiece / BPE for text → ids |
| `System.CommandLine` | 2.0.5 | CLI |
| `Spectre.Console` | 0.51.1 | rich CLI progress/tables |
| `Testcontainers.PostgreSql` | 4.7.0 | integration tests |

Analyzers via `GlobalPackageReference`:

| Analyzer | Role |
|---|---|
| `Meziantou.Analyzer` | style, async correctness |
| `Roslynator.Analyzers` | general quality |
| `SonarAnalyzer.CSharp` | security + bug patterns |
| `AsyncFixer` | async pitfalls |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | enforces BannedSymbols.txt |

## Database schema (PostgreSQL 18 + pgvector 0.8 + vchord_bm25)

### Extensions (run once per cluster, via `docker/init.sql`)

```sql
CREATE EXTENSION IF NOT EXISTS vector;             -- pgvector 0.8.x
CREATE EXTENSION IF NOT EXISTS vchord CASCADE;     -- VectorChord 0.5.x
CREATE EXTENSION IF NOT EXISTS pg_tokenizer;       -- 0.1.x
CREATE EXTENSION IF NOT EXISTS vchord_bm25 CASCADE; -- 0.2.x
CREATE EXTENSION IF NOT EXISTS pg_trgm;            -- for fuzzy section lookup

ALTER DATABASE arista SET search_path TO "$user", public, tokenizer_catalog, bm25_catalog;

SELECT create_text_analyzer('english_analyzer', $$
    pre_tokenizer = "unicode_segmentation"
    [[character_filters]]
    to_lowercase = {}
    [[character_filters]]
    unicode_normalization = "nfkd"
    [[token_filters]]
    skip_non_alphanumeric = {}
    [[token_filters]]
    stopwords = "nltk_english"
    [[token_filters]]
    stemmer = "english_porter2"
$$);

ALTER DATABASE arista SET hnsw.iterative_scan = 'relaxed_order';
ALTER DATABASE arista SET hnsw.max_scan_tuples = 20000;
ALTER DATABASE arista SET hnsw.ef_search = 100;
ALTER DATABASE arista SET maintenance_work_mem = '2GB';
ALTER DATABASE arista SET jit = off;
```

### `documents` table

```sql
CREATE TABLE documents (
    id                   TEXT PRIMARY KEY,
    url                  TEXT NOT NULL,
    category             TEXT NOT NULL,
    product              TEXT,
    version              TEXT,
    title                TEXT NOT NULL,
    slug                 TEXT NOT NULL,
    tags                 JSONB NOT NULL DEFAULT '[]'::jsonb,
    pages                INTEGER,
    size_bytes           BIGINT,
    pdf_sha256           TEXT,
    md_path              TEXT NOT NULL,
    json_path            TEXT NOT NULL,
    convert_mode         TEXT,
    image_count          INTEGER NOT NULL DEFAULT 0,
    section_count        INTEGER NOT NULL DEFAULT 0,
    level1_section_count INTEGER NOT NULL DEFAULT 0,
    toc_count            INTEGER NOT NULL DEFAULT 0,
    downloaded_at        TIMESTAMPTZ,
    converted_at         TIMESTAMPTZ,
    ingested_at          TIMESTAMPTZ NOT NULL DEFAULT now(),

    product_family       TEXT GENERATED ALWAYS AS (
        CASE
            WHEN product LIKE 'eos%'   THEN 'eos'
            WHEN product LIKE 'cvp%' OR product = 'cv-cue' THEN 'cvp'
            WHEN product LIKE 'dmf%'   THEN 'dmf'
            ELSE product
        END
    ) VIRTUAL
);

CREATE INDEX idx_documents_category_product_version ON documents(category, product, version);
CREATE INDEX idx_documents_product_family           ON documents(product_family);
CREATE INDEX idx_documents_tags_gin                 ON documents USING GIN(tags jsonb_path_ops);
CREATE INDEX idx_documents_pdf_sha256               ON documents(pdf_sha256);
```

`product_family` is a PG18 virtual generated column (no storage); it collapses versioned product variants for simpler filtering.

### `chunks` table

```sql
CREATE TABLE chunks (
    id                  BIGSERIAL PRIMARY KEY,
    document_id         TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    chunk_index         INTEGER NOT NULL,
    content             TEXT NOT NULL,
    raw_content         TEXT NOT NULL,
    section_title       TEXT,
    section_level       SMALLINT,
    page_start          INTEGER,
    page_end            INTEGER,
    token_count         INTEGER NOT NULL,

    embedding           halfvec(768) NOT NULL,
    embedding_model     TEXT NOT NULL DEFAULT 'snowflake-arctic-embed-m-v1.5',

    bm25v               bm25vector GENERATED ALWAYS AS (
        tokenize(content, 'english_analyzer')
    ) STORED,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),

    UNIQUE (document_id, chunk_index)
);

CREATE INDEX idx_chunks_embedding_hnsw ON chunks
    USING hnsw (embedding halfvec_cosine_ops)
    WITH (m = 16, ef_construction = 200);

CREATE INDEX idx_chunks_bm25 ON chunks
    USING bm25 (bm25v bm25_ops);

CREATE INDEX idx_chunks_document_id ON chunks(document_id);
CREATE INDEX idx_chunks_section_level ON chunks(section_level) WHERE section_level IS NOT NULL;
```

Design choices:
- **`halfvec(768)`** rather than `vector(768)` — pgvector 0.8 best practice, −50 % storage and HNSW memory with negligible recall loss.
- **`content` vs `raw_content`** — dense embed sees `{doc.title} > {section.title}\n\n{raw}`; BM25 index also tokenises `content` (context helps retrieval). UI/reranker see `raw_content` only.
- **`bm25v` as `GENERATED STORED`** — automatically maintained; tokenisation happens at write time, never at query time.
- **`embedding_model` column** — lets us do gradual re-embedding when we swap the model without a full downtime.

### `ingest_runs` table

```sql
CREATE TABLE ingest_runs (
    id               BIGSERIAL PRIMARY KEY,
    started_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    finished_at      TIMESTAMPTZ,
    status           TEXT NOT NULL,
    docs_total       INTEGER NOT NULL DEFAULT 0,
    docs_skipped     INTEGER NOT NULL DEFAULT 0,
    docs_upserted    INTEGER NOT NULL DEFAULT 0,
    chunks_upserted  INTEGER NOT NULL DEFAULT 0,
    catalog_sha256   TEXT,
    error_msg        TEXT
);
CREATE INDEX idx_ingest_runs_started_at ON ingest_runs(started_at DESC);
```

### EF Core mapping excerpt

```csharp
public class ChunkEntity
{
    public long Id { get; set; }
    public string DocumentId { get; set; } = "";
    public int ChunkIndex { get; set; }
    public string Content { get; set; } = "";
    public string RawContent { get; set; } = "";
    public string? SectionTitle { get; set; }
    public short? SectionLevel { get; set; }
    public int? PageStart { get; set; }
    public int? PageEnd { get; set; }
    public int TokenCount { get; set; }

    [Column(TypeName = "halfvec(768)")]
    public HalfVector Embedding { get; set; } = null!;

    public string EmbeddingModel { get; set; } = "snowflake-arctic-embed-m-v1.5";
    public DateTime CreatedAt { get; set; }

    public DocumentEntity Document { get; set; } = null!;
}
```

```csharp
protected override void OnModelCreating(ModelBuilder mb)
{
    mb.HasPostgresExtension("vector");
    mb.HasPostgresExtension("vchord");
    mb.HasPostgresExtension("pg_tokenizer");
    mb.HasPostgresExtension("vchord_bm25");
    mb.HasPostgresExtension("pg_trgm");

    mb.Entity<ChunkEntity>()
        .HasIndex(c => c.Embedding)
        .HasMethod("hnsw")
        .HasOperators("halfvec_cosine_ops")
        .HasStorageParameter("m", 16)
        .HasStorageParameter("ef_construction", 200);
}
```

## Ingest pipeline

### Flow for `arista-mcp ingest`

1. Read `catalog.json`; compute `catalog_sha256`.
2. If last `ingest_runs` with `status='ok'` has the same `catalog_sha256` → early exit (unless `--force`).
3. Insert new `ingest_runs(status='running', catalog_sha256=…)`.
4. Diff catalog vs `documents` by `id` + `pdf_sha256`:
   - catalog-only → `NEW` (insert)
   - both, same `pdf_sha256` → `SKIP`
   - both, different `pdf_sha256` → `UPDATE` (re-chunk)
   - DB-only → `DELETE` (cascades to chunks)
5. For each `NEW`/`UPDATE`:
   - upsert `documents` row with catalog fields;
   - `DELETE FROM chunks WHERE document_id = ?` if `UPDATE`;
   - read MD + per-doc JSON; run `SectionAwareChunker`;
   - concurrent batch embed (4 workers × batch 32) via `OnnxEmbedder`;
   - bulk `COPY … FROM STDIN BINARY` into chunks;
   - update `documents.ingested_at`.
6. Set `ingest_runs` to `ok`/`error` with counts.

### Chunking (`SectionAwareChunker`)

- **Primary**: one chunk per `sections[]` entry from per-doc JSON.
- **Fallback**: for a section over 1,200 tokens, split into ~512-token windows with 64-token overlap, using `Microsoft.ML.Tokenizers` (model-specific WordPiece for snowflake).
- **Skip**: sections under 40 tokens without children.
- **Empty docs**: insert document row with zero chunks.
- **Chunked docs** (`<!-- chunk: pages N..M -->` markers): use markers to recover true page numbers when section.page_start/end are coarse (50-page chunk approximation).

Chunk content:
```
content      = "{doc.title} > {section.title}\n\n{raw}"   (fed to embedder and bm25)
raw_content  = the section body, no prepend                 (shown in results)
```

### Embedding

- `OnnxEmbedder` singleton, loaded at CLI startup.
- CPU provider by default; CUDA provider if `AristaMcpSettings.Gpu=true` and CUDA runtime found.
- Batch 32; parallel degree 4 via `SemaphoreSlim`.
- `snowflake-arctic-embed-m-v1.5` → 768-dim normalised (cosine-ready) → `HalfVector` conversion.

### Bulk insert

EF Core `AddRange`+`SaveChanges` is too slow for 32 K chunks. Use Npgsql binary COPY via an `NpgsqlDataSource` built with `.UseVector()` so that `HalfVector` has a registered binary writer:

```csharp
var ds = new NpgsqlDataSourceBuilder(cs).UseVector().Build();
await using var conn = await ds.OpenConnectionAsync(ct);
await using var writer = await conn.BeginBinaryImportAsync(
    "COPY chunks (document_id, chunk_index, content, raw_content, section_title, "
    + "section_level, page_start, page_end, token_count, embedding, embedding_model) "
    + "FROM STDIN BINARY", ct);
foreach (var c in chunks)
{
    await writer.StartRowAsync(ct);
    await writer.WriteAsync(c.DocumentId, NpgsqlDbType.Text, ct);
    await writer.WriteAsync(c.ChunkIndex, NpgsqlDbType.Integer, ct);
    await writer.WriteAsync(c.Content, NpgsqlDbType.Text, ct);
    await writer.WriteAsync(c.RawContent, NpgsqlDbType.Text, ct);
    await writer.WriteAsync(c.SectionTitle, NpgsqlDbType.Text, ct);
    await writer.WriteAsync(c.SectionLevel, NpgsqlDbType.Smallint, ct);
    await writer.WriteAsync(c.PageStart, NpgsqlDbType.Integer, ct);
    await writer.WriteAsync(c.PageEnd, NpgsqlDbType.Integer, ct);
    await writer.WriteAsync(c.TokenCount, NpgsqlDbType.Integer, ct);
    await writer.WriteAsync(c.Embedding);
    await writer.WriteAsync(c.EmbeddingModel, NpgsqlDbType.Text, ct);
}
await writer.CompleteAsync(ct);
```

`bm25v` auto-generated, not passed.

### Transactions and resume

- One transaction per document; failure isolates to that doc and records in `ingest_runs.error_msg`.
- Re-running `ingest` after crash: diff logic detects already-done docs as `SKIP`; only what's missing is processed.

### CLI

```bash
arista-mcp ingest                                     # full diff+ingest
arista-mcp ingest --force                             # bypass catalog_sha256 early exit
arista-mcp ingest --doc-id <16-hex>                   # single doc
arista-mcp ingest --category toi --product eos        # subset filter
arista-mcp ingest --dry-run                           # show diff counts only
arista-mcp reindex --rebuild-embeddings               # keep docs, re-embed all chunks
```

## MCP tools

All declared via `[McpServerToolType]` on a static class; `[McpServerTool]` on methods. DI-injected dependencies resolved via `WithToolsFromAssembly()`.

### Tool signatures

```csharp
// search_docs
public static Task<SearchResponse> SearchDocs(
    IRetriever retriever,
    string query,
    int limit = 10,              // max 50
    string? category = null,     // 'manual' | 'toi'
    string? product = null,      // 'eos' | 'cvp' | 'dmf' | ...
    string? version = null,
    string? tags = null,         // JSON array
    bool skipRerank = false,
    CancellationToken ct = default);

// lookup_section
public static Task<IReadOnlyList<ChunkResult>> LookupSection(
    ISectionLookup lookup,
    string title,
    string? product = null,
    int limit = 5,
    CancellationToken ct = default);

// list_documents
public static Task<DocumentPage> ListDocuments(
    IDocumentRepository docs,
    string? category = null,
    string? product = null,
    int page = 0,
    int pageSize = 50,
    CancellationToken ct = default);

// get_document
public static Task<DocumentDetail> GetDocument(
    IDocumentRepository docs,
    string docId,
    bool includeContent = false,
    CancellationToken ct = default);

// get_status
public static Task<StatusResponse> GetStatus(
    IStatusProvider status,
    CancellationToken ct = default);
```

### Hybrid retrieval flow (`HybridRetriever.SearchAsync`)

1. `QueryExpander.Expand(query)` — insert Arista-specific synonyms.
2. `OnnxEmbedder.EmbedAsync([expanded])` — 1 × 768-dim vector.
3. Parallel dense + sparse SQL (same connection pool, separate commands):

```sql
-- Dense
SELECT c.id, c.document_id, c.raw_content, c.section_title, c.page_start,
       1 - (c.embedding <=> @qvec::halfvec) AS dense_score
  FROM chunks c JOIN documents d ON d.id = c.document_id
 WHERE (@category IS NULL OR d.category = @category)
   AND (@product  IS NULL OR d.product  = @product)
   AND (@version  IS NULL OR d.version  = @version)
 ORDER BY c.embedding <=> @qvec::halfvec
 LIMIT 50;

-- Sparse (vchord_bm25 operator <&> returns NEGATIVE BM25; lower = more relevant)
SELECT c.id, c.document_id, c.raw_content, c.section_title, c.page_start,
       c.bm25v <&> bm25query('idx_chunks_bm25',
                              tokenize(@q, 'english_analyzer')) AS bm25_raw
  FROM chunks c JOIN documents d ON d.id = c.document_id
 WHERE (@category IS NULL OR d.category = @category)
   AND (@product  IS NULL OR d.product  = @product)
   AND (@version  IS NULL OR d.version  = @version)
 ORDER BY c.bm25v <&> bm25query('idx_chunks_bm25',
                                 tokenize(@q, 'english_analyzer'))
 LIMIT 50;
```

In .NET, `bm25Score = -bm25_raw` before handing to `RrfFusion` so both inputs are "higher = better".

4. `RrfFusion.Merge(dense, sparse, k: 60)` → top 50 merged by rank.
5. Optional `OnnxReranker.RerankAsync(query, top50)` with `bge-reranker-base` → rescored pairs.
6. Apply quality safeguards:
   - dedup by `(document_id, section_title)` — keep best-scoring;
   - boost ×1.15 if `section_title` matches query tokens (case-insensitive);
   - boost ×1.10 if `doc.title` matches query tokens;
   - drop rerank_score < 0.1 (noise floor).
7. Return top `limit` with full `SearchDiagnostics` for observability.

### Query expansion

Hardcoded dictionary (extendable via YAML in v0.2):

```csharp
private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.OrdinalIgnoreCase)
{
    ["EVPN"]  = ["Ethernet VPN", "Ethernet Virtual Private Network"],
    ["VXLAN"] = ["Virtual eXtensible LAN"],
    ["MLAG"]  = ["multi-chassis LAG", "multi-chassis link aggregation"],
    ["EOS"]   = ["Arista EOS", "Extensible Operating System"],
    ["CVP"]   = ["CloudVision", "CloudVision Portal"],
    ["DMF"]   = ["DANZ Monitoring Fabric"],
    ["TOI"]   = ["Transfer of Information"],
    ["BGP"]   = ["Border Gateway Protocol"],
    ["OSPF"]  = ["Open Shortest Path First"],
    ["LACP"]  = ["Link Aggregation Control Protocol"],
    ["sFlow"] = ["sampled flow"],
    ["SR"]    = ["Segment Routing"],
    ["MSS"]   = ["Macro Segmentation Service"],
    ["AVD"]   = ["Arista Validated Designs"],
};
```

Expanded query serves both dense (richer semantic input) and sparse (more BM25 term matches) paths.

### Response schemas

```csharp
public sealed record SearchResponse(
    IReadOnlyList<ChunkResult> Results,
    SearchDiagnostics Diagnostics);

public sealed record ChunkResult(
    long ChunkId,
    string DocumentId,
    string DocumentTitle,
    string DocumentSlug,
    string Category,
    string? Product,
    string? Version,
    string? SectionTitle,
    int? SectionLevel,
    int? PageStart,
    int? PageEnd,
    string RawContent,
    float Score,
    float? DenseSimilarity,
    float? Bm25Score,
    float? RrfScore,
    float? RerankScore);

public sealed record SearchDiagnostics(
    int DenseHits, int SparseHits, int AfterRrf, int AfterRerank,
    double EmbedMs, double DenseQueryMs, double SparseQueryMs,
    double RrfMs, double RerankMs, double TotalMs);

public sealed record DocumentDetail(
    string Id, string Title, string Slug, string Category,
    string? Product, string? Version,
    IReadOnlyList<string> Tags, int Pages, int ImageCount, int SectionCount,
    string? ContentMarkdown, IReadOnlyList<TocEntry> Toc);

public sealed record TocEntry(int Level, string Title, int? Page);

public sealed record StatusResponse(
    int DocumentsTotal, int ChunksTotal, int ChunksWithEmbeddings,
    DateTime? LastIngestFinished, string? LastIngestStatus,
    string EmbeddingModel, int EmbeddingDim);
```

### `lookup_section`

```sql
SELECT c.id, c.document_id, d.title AS doc_title,
       c.section_title, c.raw_content,
       similarity(c.section_title, @query) AS sim
  FROM chunks c JOIN documents d ON d.id = c.document_id
 WHERE c.section_title IS NOT NULL
   AND similarity(c.section_title, @query) > 0.3
   AND (@product IS NULL OR d.product = @product)
 ORDER BY sim DESC
 LIMIT @limit;
```

Uses `pg_trgm.similarity` for fuzzy matching (handles typos and word-order variations).

### Transport selection

```csharp
public static void ConfigureMcp(IServiceCollection services, AristaMcpSettings s)
{
    var mcp = services.AddMcpServer().WithToolsFromAssembly(typeof(AristaDocsTools).Assembly);
    switch (s.Transport)
    {
        case McpTransport.Stdio:
            mcp.WithStdioServerTransport();
            break;
        case McpTransport.Http:
            mcp.WithHttpTransport(opt => opt.Stateless = true);
            break;
    }
}
```

`serve --transport stdio` (default) for Claude Desktop / CLI; `serve --transport http --port 8080` for shared HTTP. Stateless mode means no server-to-client requests (no sampling/elicitation — we don't need them).

## Docker / Podman infrastructure

### Primary Containerfile — TensorChord bundle

```dockerfile
FROM tensorchord/vchord-suite:pg18-latest
COPY init.sql /docker-entrypoint-initdb.d/00-init.sql
HEALTHCHECK --interval=10s --timeout=3s --retries=10 \
    CMD pg_isready -U ${POSTGRES_USER:-arista} -d ${POSTGRES_DB:-arista}
```

Bundles (as of 2026-04):
- postgres 18
- vchord 0.5.3
- vchord_bm25 0.2.2
- pg_tokenizer 0.1.0
- vector 0.8.1

### Fallback Containerfile (`Containerfile.from-scratch`)

Used only if `postgres:18-trixie` base is required. Builds vchord/vchord_bm25/pg_tokenizer via `cargo-pgrx 0.15+` (first version with PG18 support), `VCHORD_VERSION=0.5.3`, `VCHORD_BM25_VERSION=0.2.2`, `PG_TOKENIZER_VERSION=0.1.0`, `PGVECTOR_VERSION=v0.8.2`. Multi-stage build keeps runtime slim.

### `compose.yaml`

```yaml
services:
  postgres:
    build:
      context: .
      dockerfile: Containerfile
    image: arista-mcp-postgres:18
    container_name: arista-mcp-postgres
    restart: unless-stopped
    environment:
      POSTGRES_DB: arista
      POSTGRES_USER: arista
      POSTGRES_PASSWORD: arista
      POSTGRES_INITDB_ARGS: --data-checksums --encoding=UTF-8 --locale=C.UTF-8
      PGDATA: /var/lib/postgresql/18/data
    ports:
      - "127.0.0.1:5434:5432"
    volumes:
      - arista-pgdata:/var/lib/postgresql/18/data
      - ./postgresql.conf:/etc/postgresql/postgresql.conf:ro,Z
    command: >-
      postgres
      -c config_file=/etc/postgresql/postgresql.conf
      -c max_connections=50
      -c shared_buffers=512MB
      -c effective_cache_size=2GB
      -c work_mem=32MB
      -c maintenance_work_mem=2GB
      -c wal_buffers=16MB
      -c io_method=worker
      -c io_workers=3
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U arista -d arista"]
      interval: 10s
      timeout: 3s
      retries: 10
      start_period: 30s

  arista-mcp:
    build:
      context: ..
      dockerfile: docker/Containerfile.app
    image: arista-mcp:dev
    container_name: arista-mcp-http
    restart: unless-stopped
    depends_on:
      postgres:
        condition: service_healthy
    environment:
      ARISTA_MCP__ConnectionString: Host=postgres;Port=5432;Database=arista;Username=arista;Password=arista
      ARISTA_MCP__ModelsDir: /models
      ARISTA_MCP__Transport: http
      ARISTA_MCP__HttpPort: 8080
    ports:
      - "127.0.0.1:8080:8080"
    volumes:
      - ../models:/models:ro,Z
      - ../../arista-docs/data:/arista-data:ro,Z
    profiles:
      - server

volumes:
  arista-pgdata:
```

`127.0.0.1` binding + `profiles: [server]` mean the MCP HTTP server starts only with `--profile server` and never exposes to the LAN by default. For remote access, place a reverse proxy (caddy/nginx/traefik) with TLS + auth in front; see `docker/README.md`.

### `Containerfile.app`

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/AristaMcp.Cli/AristaMcp.Cli.csproj \
      -c Release -r linux-x64 --self-contained false -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
USER 1000:1000
ENTRYPOINT ["dotnet", "AristaMcp.Cli.dll", "serve"]
```

Models mounted at runtime (not baked) to keep image lean.

### Podman specifics

- SELinux label `:Z` required on bind mounts when SELinux enforcing.
- Rootless works out of the box; bind-mounting host-owned files may need `--userns=keep-id`.
- Use `podman-compose up -d` or `docker compose up -d` — the compose file targets the 3.x specification that both support.

## Error handling and robustness

### Ingest

- **Per-document transaction** isolates failures; one bad doc doesn't abort the run.
- **`catalog_sha256` early exit** avoids re-reading unchanged catalogs.
- **`pdf_sha256` skip** avoids re-embedding unchanged docs.
- **Orphan cleanup**: docs missing from catalog → `DELETE`; cascade removes chunks.
- **Partial failure** → `ingest_runs.status='partial'`, `error_msg` lists first N failed doc ids.

### Retrieval

- Query timeout: default 30 s per MCP tool call; warn if > 5 s.
- `IRetriever` returns empty list + `SearchDiagnostics` on DB-down rather than throwing.
- Rerank model fails to load → fall back to RRF-only with log warning; tool still returns.

### HTTP transport

- Health endpoint `/health` returns 200 when DB reachable; 503 when not (compose healthcheck-compatible).
- No auth — *must* be fronted by reverse proxy for remote use. `127.0.0.1` binding prevents accidental exposure.

### Model management

- `scripts/download_models.ps1` fetches ONNX + tokenizer from HuggingFace (`Snowflake/snowflake-arctic-embed-m-v1.5`, `BAAI/bge-reranker-base`) into `models/`.
- `OnnxEmbedder` constructor validates model files exist and embedding dim matches schema (768); throws on mismatch.
- Swap model (e.g. to BGE-M3): requires `reindex --rebuild-embeddings` and schema migration if dim differs.

## Testing strategy

### Pyramid

```
       e2e (real models, real postgres)             ~5-8 tests
      ──────────────────────────────────
      integration (Testcontainers postgres)         ~20 tests
    ──────────────────────────────────────────
    unit (fast, isolated)                           ~80-120 tests
```

### Unit

**`AristaMcp.Core.Tests`**
- `RrfFusionTests` (port from ntx-mcp).
- `SectionAwareChunkerTests`: section-boundary path, fixed-window fallback, empty doc, chunk-marker recovery for chunked PDFs, title/section prepending.
- `QueryExpanderTests`: synonym expansion including multi-word outputs and case-insensitivity.
- `AristaMcpSettingsTests`: TOML + env var + default merging.

**`AristaMcp.Embedding.Tests`**
- `OnnxEmbedderTests` against a tiny ONNX fixture (≈5 MB) — batch 32 → shape (32, 768); tokenizer truncation at 512; CPU provider always; GPU skipped if no CUDA.
- `OnnxRerankerTests` analogous.

### Integration (Testcontainers)

`PgvectorFixture` spins up `tensorchord/vchord-suite:pg18-latest`, runs `init.sql`, then EF migrations.

- `HalfVectorRoundtripTest`.
- `HnswIndexSearchTest` — 1,000 random vectors → nearest N correct.
- `Bm25IndexSearchTest` — 50 real markdown chunks → expected top-5 ordering.
- `HybridRetrievalTest` — 1,000 mixed chunks, dense + sparse + RRF returns known-answer chunk in top 10.
- `IngestIdempotencyTest` — running ingest twice is a no-op (catalog_sha256 short-circuit).
- `IncrementalReingestTest` — flip one doc's `pdf_sha256` → only that doc's chunks are re-embedded.
- `DocumentCascadeDeleteTest` — deleting a document deletes its chunks.

### Server

- `SearchToolEndToEndTest` — in-process MCP server + in-memory `IRetriever` mock; client invokes `search_docs`; response shape matches `SearchResponse`.
- `FilterPassThroughTest` — `product=eos` reaches `IRetriever`.
- `HttpTransportBootTest` — `WithHttpTransport(Stateless=true)` responds 200 on `/health`.

### E2E (`[Trait("Category","E2E")]`)

- `RealModelEmbedTest` — loads real `snowflake-arctic-embed-m-v1.5`; embeds "BGP over VXLAN" → expected cosine to "routing over vxlan overlay" > 0.6.
- `RealIngestFiveDocsTest` — 5 cherry-picked docs from `tests/fixtures/arista-data-mini/` → ingest → search `"LANZ mirroring"` → expected doc present.
- `RealRerankerTest` — rerank 10 candidates; ranking stable run-to-run.

E2E are GPU-optional. CI fast lane runs `dotnet test --filter "Category!=E2E"`; full suite runs on a GPU runner weekly.

### Coverage

- `Core` target 95 %.
- `Embedding` public surface 85 %; ONNX inference internals covered by E2E only.
- `Data` 85 % via integration.
- `Server` tools 80 %.
- Overall fail_under 85 %.

### Commands

```
dotnet test                                     # everything
dotnet test --filter "Category!=E2E"            # fast lane (CI default)
dotnet test --filter "Category=Integration"     # just DB tests
dotnet test --collect:"XPlat Code Coverage"     # + coverage
```

## Open questions / risks

1. **vchord_bm25 index build time at scale** — 32 K chunks is small (expected <30 s); if the corpus grows to millions, we may need periodic `REINDEX CONCURRENTLY`.
2. **ONNX model licensing** — snowflake-arctic-embed-m-v1.5 is Apache 2.0; bge-reranker-base is MIT. OK to vendor, but we fetch via `download_models.ps1` rather than baking into git.
3. **HalfVector binary COPY** — depends on `NpgsqlDataSource.UseVector()` registering the handler; integration test `HalfVectorRoundtripTest` is the gate.
4. **Container base image drift** — `tensorchord/vchord-suite:pg18-latest` is a floating tag. Pin to a digest in `Containerfile` before prod deploy (`FROM tensorchord/vchord-suite@sha256:…`).
5. **Synonym dictionary staleness** — Arista adds product names over time. Move to a YAML/JSON file outside code in v0.2 so non-coders can edit.
6. **Windows dev experience** — Testcontainers needs Docker Desktop or Podman Desktop with WSL2 backend; documented in README.

## Downstream consumers

- Claude Desktop via `claude_desktop_config.json` with stdio.
- Claude Code via MCP config.
- Any other MCP client via `http://localhost:8080/` (Streamable HTTP) when `serve --transport http`.

## Milestones

**Sprint 1 — Infrastructure + data layer**
- Solution skeleton, CPM, analyzers.
- docker/ (Containerfile, compose, init.sql).
- `AristaMcp.Data` with EF Core + Pgvector + migrations.
- Integration test harness with Testcontainers.

**Sprint 2 — Embedding + ingest**
- `AristaMcp.Embedding` with ONNX (snowflake + reranker).
- `SectionAwareChunker`, `QueryExpander`.
- `IngestCommand` end-to-end on arista-data-mini fixture.

**Sprint 3 — Retrieval + MCP server**
- `HybridRetriever` with dense + sparse + RRF.
- `OnnxReranker` wired.
- MCP tools (`search_docs`, `lookup_section`, `list_documents`, `get_document`, `get_status`).
- `ServeCommand` with stdio + HTTP transports.

**Sprint 4 — Polish + v0.1 release**
- `StatusCommand`, `SearchCommand` CLI helpers.
- Observability (`SearchDiagnostics`, OpenTelemetry).
- Quality evaluation on 30-query benchmark set.
- README, CLAUDE.md.
- `v0.1.0` tag.

## Rejected alternatives

- **Stdio-only** — limits to single-client Claude Desktop; losing HTTP means no shared deployment. Dual transport is cheap (~30 lines).
- **BGE-M3 as default embedder** — 5-13 h on CPU for 93 K chunks per nutanix notes-005. English-only corpus doesn't benefit from multilingual capability.
- **Ollama for embeddings** — adds a sidecar daemon for marginal gain; `bge-m3` isn't officially in Ollama registry; ONNX in-process is simpler.
- **Python FastAPI embedding sidecar** — nutanix's approach was fine but brings a second runtime into a .NET project unnecessarily.
- **Postgres FTS** (`tsvector`) instead of vchord_bm25 — weaker BM25 quality; vchord_bm25 tokenizer is proper multilingual-ready BM25 with Porter stemmer.
- **Semantic Kernel orchestration layer** — more than we need; MCP SDK directly is cleaner.
- **Proxying to Python backend** (as ntx-mcp does) — defeats the purpose of a .NET server; kill the Python round-trip for good.

## Appendix: contract invariants (expected from `arista-docs`)

If these break in a future `arista-docs` version, ingest must fail loudly rather than corrupt the index.

- `catalog.json` contains `documents: [...]` with at least the fields listed in §Context.
- Every `documents[i].json_path` resolves to a file whose JSON has `sections[]` and `toc[]` arrays (possibly empty).
- `documents[i].md_path` resolves to UTF-8 markdown containing `{N}----` page markers; for `chunked=true` docs it additionally contains `<!-- chunk: pages N..M -->`.
- `documents[i].id` is a stable 16-hex string derived from content (same doc → same id across runs).
- `documents[i].pdf_sha256` changes iff PDF content changes.

If a contract violation is detected at ingest time, abort and record to `ingest_runs.error_msg`.
