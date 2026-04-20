# Changelog

All notable changes to arista-mcp are documented here. Format follows
[keep-a-changelog](https://keepachangelog.com); dates use ISO-8601.

## [v0.1.0] — 2026-04-20

Initial release. Four waterfall sprints from spec to tag.

### Added — Sprint 1 (infrastructure + data layer, tag `sprint-1-review`)

- .NET 10 solution skeleton with Central Package Management, Directory.Build.props-level
  analyzers (Meziantou, Roslynator, SonarAnalyzer, AsyncFixer, BannedApiAnalyzers).
- Docker/Podman postgres 18 image on `tensorchord/vchord-suite:pg18-latest` with pgvector,
  vchord, vchord_bm25, pg_tokenizer, pg_trgm pre-loaded; `shared_preload_libraries` wired
  for `vector,vchord,vchord_bm25,pg_tokenizer` so `ALTER DATABASE SET hnsw.*` works.
- Core models (`AristaDocument`, `AristaChunk`, `ChunkResult`, `SearchResponse`,
  `SearchDiagnostics`), settings class, `McpTransport` enum.
- EF Core 9.x + Npgsql.EFCore 9.x + Pgvector.EFCore 0.3.0 data layer. Initial migration
  provisions documents / chunks / ingest_runs with `halfvec(768)` embeddings and HNSW
  `halfvec_cosine_ops` index (m=16, ef_construction=200).
- `DocumentRepository` (upsert/get/delete) + `ChunkRepository` with Npgsql `COPY BINARY`
  bulk insert (1000 rows < 3 s).
- Testcontainers pivoted to local shared postgres on Windows/Podman (race with two-stage
  init). Three integration fixtures: HalfVector round-trip, HNSW cosine nearest neighbour,
  BM25 `<&>` relevance ordering.

### Added — Sprint 2 (embedding + ingest, tag `sprint-2-review`)

- PowerShell model fetcher (`scripts/fetch-models.ps1`) with idempotent size checks.
- `OnnxEmbedder` targeting snowflake-arctic-embed-m-v1.5: reads the pre-pooled
  `sentence_embedding` output directly, defensively re-normalizes via span copy.
- `BertWordPieceTokenizer` wraps `Microsoft.ML.Tokenizers.BertTokenizer` with a batch
  encode that pads to max-in-batch and builds the attention mask.
- `SectionAwareChunker`: section-boundary splits (target 512, max 1200, overlap 64, min
  40 tokens). Content gets `"{doc.title} > {section.title}\n\n"` prefix for embedding;
  `raw_content` unprefixed for BM25 + display.
- `CatalogReader` + `DocumentLoader` — arista-docs v0.1.x catalog contract, markdown
  split by ATX headings (named-group `[GeneratedRegex]` with `ExplicitCapture`).
- `IngestService` orchestrator with two-layer incremental skip (catalog SHA256 + per-doc
  pdf_sha256). `IngestRunRepository` tracks every run.
- `arista-mcp ingest` CLI verb via `System.CommandLine 2.0.6` (`--catalog`, `--force`,
  `--dry-run`, `--category`, `--verbose`, `--models`).

### Added — Sprint 3 (retrieval + MCP server, tag `sprint-3-review`)

- `QueryExpander` with 20 Arista acronyms (EVPN/VXLAN/MLAG/BGP/OSPF/LACP/sFlow/SR/MSS/
  AVD/LANZ/VARP/VRRP/VRF/QoS/ACL/TCAM/EOS/CVP/DMF).
- `HybridRetriever` with parallel dense (`embedding <=> $1::halfvec`) + sparse
  (`bm25v <&> to_bm25query(...)`) SQL, Reciprocal Rank Fusion (k=60), optional rerank.
- Five MCP tools (`search_docs`, `lookup_section`, `list_documents`, `get_document`,
  `get_status`) as `[McpServerToolType]` classes with constructor DI.
- `StdioHost` (console `Host`, logs to stderr) and `HttpHost` (`WebApplication` + `MapMcp`,
  stateless) backed by shared DI in `ServerHosting.AddAristaMcpServices`.
- `arista-mcp serve --transport stdio|http --port <n>` verb. Verified end-to-end: MCP
  Streamable HTTP `tools/list` returns all five tools with JSON schemas.

### Added — Sprint 4 (polish + release, tag `v0.1.0`)

- `OnnxReranker` — BERT cross-encoder (ms-marco-MiniLM-L6-v2, ~91 MB). Pair-encodes
  `[CLS] query [SEP] doc [SEP]` with proper `token_type_ids`. Falls back to `NoopReranker`
  when `models/reranker/` is absent.
- `TimeProvider` injected into `DocumentRepository` + `IngestRunRepository`; DI registers
  `TimeProvider.System` as singleton.
- `arista-mcp bench` — 30-query curated retrieval harness, Spectre-rendered per-query table
  + p50/p95/avg latency summary; exits non-zero if top-10 hit rate < 80%.
- User-facing `README.md` with quickstart, Claude Desktop config snippet, tool list,
  architecture diagram, Windows/Podman WSL port notes.
- `CHANGELOG.md` (this file).

### Fixed — Sprint 4 (three bugs surfaced by the Sprint 3 code review)

- **`Bm25Score` on dense+sparse co-hits** — RRF accumulator now tracks `DenseDistance`
  and `SparseDistance` separately; `Build()` derives `DenseSimilarity = 1 - dense_distance`
  and `Bm25Score = -sparse_distance` (sign-flipped to "higher = better" convention).
  Previously co-hit chunks returned `-cosine_distance` as the BM25 score.
- **`DenseQueryMs` / `SparseQueryMs` hard-coded to 0** — `RunDenseAsync` / `RunSparseAsync`
  now return `(rows, elapsed_ms)` via inner `Stopwatch`es; `SearchDiagnostics` reports
  real per-stage timings.
- **Manual `bm25v` SQL never auto-applied** — moved into a proper
  `AddBm25Column` EF migration with `migrationBuilder.Sql`; `Migrations/Manual/` folder
  deleted. A fresh `dotnet ef database update` now provisions the full schema.

### Changed — Sprint 4 (C# 14 / .NET 10 modernizations)

- `QueryExpander.Synonyms` → `FrozenDictionary<string, string>` (read-mostly lookup).
- `HybridRetriever.ReciprocalRankFusion` uses `CollectionsMarshal.GetValueRefOrAddDefault`
  (one hash probe per row instead of two).
- `OnnxEmbedder.ExtractAndNormalize` replaces scalar sum-of-squares loop with
  `TensorPrimitives.Norm` + `TensorPrimitives.Divide` (SIMD).
- `ChunkRepository.BulkInsertAsync` and `HybridRetriever.SearchAsync` replace manual
  `Half[]` populate loops with `[.. src.Select(static f => (Half)f)]` collection
  expressions.

### Chore — Sprint 4

- Cleanup: removed `_ = jsonFull` dead code in `DocumentLoader`; sealed entity classes and
  repositories; `GetDocumentTool` returns tags as `string[]` (not raw JSON).
- Safe patch bumps: Spectre.Console 0.55.0 → 0.55.2, Meziantou.Analyzer 3.0.48 → 3.0.50,
  System.Security.Cryptography.Xml 9.0.15 → 10.0.6.

### Known limitations

- **EF Core pinned to 9.x** — `Pgvector.EntityFrameworkCore 0.3.0` (the only release)
  targets EF Core 9.x via `net8.0` TFM. Bump blocked until upstream publishes an EF Core
  10 build. Everything else is on latest stable.
- **FluentAssertions 8.x** — non-commercial license. Solo / personal use is fine; a
  commercial deployment should either migrate to Shouldly or pin back to
  FluentAssertions 7.2.0 (last Apache-2.0).
- **Benchmark harness ships, but `bench` hit-rate numbers are indicative** — the 30-query
  set was hand-curated; a larger held-out set would be needed before claiming an objective
  quality number.
- **Reranker downloads an additional ~91 MB** — if `models/reranker/` is empty,
  `NoopReranker` (passthrough) is used automatically. Quality is noticeably better with
  the reranker.

### Stack summary

.NET 10 SDK 10.0.201 · TFM `net10.0` · ModelContextProtocol 1.2.0 · EF Core 9.0.15 ·
Npgsql.EFCore 9.0.4 · Pgvector.EFCore 0.3.0 · Microsoft.ML.OnnxRuntime 1.24.4 ·
Microsoft.ML.Tokenizers 2.0.0 · System.CommandLine 2.0.6 · Spectre.Console 0.55.2 ·
xUnit 2.9.3 · FluentAssertions 8.9.0 · PostgreSQL 18 + pgvector 0.8.2 + vchord 1.1.1 +
vchord_bm25 0.3.0 + pg_tokenizer 0.1.1.
