# arista-mcp — architecture notes

## Quick reference

    podman compose -f docker/compose.yaml up -d postgres
    dotnet test
    dotnet run --project src/AristaMcp.Cli -- serve --transport stdio

## Layers (strict)

    Cli → Server → Core ← Embedding, Data

Core has no reference to Data, Embedding, or Server. Tests may reference any layer.

## DO NOT

- Reference Python; arista-mcp is pure .NET.
- Switch `halfvec` back to `vector`; halfvec is 50% smaller at negligible cost.
- Skip `NpgsqlDataSource.UseVector()` — binary COPY of `HalfVector` depends on it.

## Version pins (as of Sprint 1)

- **.NET 10** SDK 10.0.201 / TFM `net10.0`.
- **EF Core 9.0.15** + **Npgsql.EntityFrameworkCore.PostgreSQL 9.0.4** — held back because
  `Pgvector.EntityFrameworkCore` (0.3.0, the only release) pins Npgsql.EFCore 9.0.1. Bump
  when Pgvector.EFCore ships an EF Core 10 build.
- **pgvector 0.8.2**, **vchord 1.1.1**, **vchord_bm25 0.3.0**, **pg_tokenizer 0.1.1** —
  all bundled in the `tensorchord/vchord-suite:pg18-latest` base image.
- Analyzers: Meziantou 3.0.48, Roslynator 4.15.0, SonarAnalyzer 10.23.0,
  Microsoft.CodeAnalysis.BannedApiAnalyzers 4.14.0.

## Postgres gotchas

- `shared_preload_libraries` must include `vector,vchord,vchord_bm25,pg_tokenizer` or
  `CREATE EXTENSION vchord` fails at startup and `ALTER DATABASE SET hnsw.*` fails with
  "unrecognized configuration parameter".
- `tokenizer_catalog.tokenize(...)` is `STABLE`, not `IMMUTABLE` → a `GENERATED ALWAYS AS
  (tokenize(...)) STORED` column is rejected. Use `tokenizer_catalog.create_custom_model_tokenizer_and_trigger`,
  which provisions a trigger that writes the `bm25vector` target column on INSERT/UPDATE.
- BM25 query shape: `bm25v <&> to_bm25query('idx_chunks_bm25'::regclass, tokenize($1, 'chunks_tokenizer')::bm25vector)`.
  The `<&>` operator returns the negative BM25 score, so `ORDER BY … ASC` ranks best matches first.

## Windows / Podman caveats

- The WSL2 backend binds ports at `0.0.0.0` inside the VM, not Windows localhost. Use the
  WSL IP (`wsl -d podman-machine-default -- ip -4 addr show eth0`) for connections, or
  enable `podman machine set --user-mode-networking` for persistent gvproxy forwarding.
- Compose runs through `docker-compose.exe` via `podman compose` (the native subcommand
  delegates to the installed `C:\Users\user\AppData\Local\Microsoft\WindowsApps\docker-compose.exe`).
- Testcontainers races with the postgres image's two-stage init on Podman/WSL; integration
  tests hit a shared local container instead (see `tests/AristaMcp.Data.Tests/Fixtures/PgvectorFixture.cs`
  and the `ARISTA_MCP_TEST_CS` env var override).

## Analyzers / style

- `Directory.Build.props` sets repo-wide analyzers (Meziantou, Roslynator, Sonar, AsyncFixer,
  BannedApiAnalyzers) and `NoWarn=MA0004;MA0051` since we only run in console/ASP.NET Core
  hosts and COPY writer loops stay deliberately flat.
- `tests/Directory.Build.props` additionally suppresses `CA1707` (underscore test names),
  `CA1711` (xUnit `*Collection` naming), `MA0004`, and `MA0051`.
- Migrations folder has its own `.editorconfig` that disables analyzers on EF-generated code.

## Sprint cadence

Waterfall sprints, each gated by a full `dotnet test` green + extension check. Don't start
Sprint N+1 until Sprint N's gate passes and `git tag sprint-N-review` exists.

## Sprint 2 additions (embedding + ingest)

- Model assets land under `models/embedder/` via `pwsh scripts/fetch-models.ps1`. The
  snowflake-arctic-embed-m-v1.5 ONNX export exposes `input_ids` + `attention_mask` inputs
  (no `token_type_ids`) and emits a pre-pooled `sentence_embedding` `[B, 768]` output —
  do not rebuild mean-pooling in .NET.
- `Microsoft.ML.Tokenizers 2.0.0` `BertTokenizer.Create` loads `vocab.txt` (WordPiece),
  **not** `tokenizer.json`. No batch API — `BertWordPieceTokenizer.EncodeBatch` pads to
  max-in-batch and builds attention mask.
- Queries get the Snowflake prefix "Represent this sentence for searching relevant
  passages: ". Documents are embedded raw (the chunker prepends its own
  `"{doc.title} > {section.title}\n\n"` context header).
- Ingest pipeline: `CatalogReader` → `DocumentLoader` → `SectionAwareChunker` →
  `IEmbedder` → `DocumentRepository` + `ChunkRepository`. Per-doc errors are counted;
  runs finish `partial` rather than aborting.
- Incremental skip has two layers: catalog-level (SHA256 of `catalog.json` matches the
  last successful run) and per-doc (`pdf_sha256` unchanged). `--force` bypasses both.
- `System.CommandLine 2.0.6` uses `new Option<T>(name)`, collection-init subcommands,
  `SetAction((ParseResult pr, CancellationToken ct) => ...)`. The beta4 `SetHandler`
  positional-binding API is gone.

## Sprint 3 additions (retrieval + MCP server)

- `QueryExpander` annotates 20+ Arista acronyms on first occurrence (EVPN/VXLAN/MLAG/
  BGP/OSPF/LACP/sFlow/SR/MSS/AVD/LANZ/VARP/VRRP/VRF/QoS/ACL/TCAM/EOS/CVP/DMF). Dict is
  case-insensitive, casing is preserved, each acronym expands at most once per query.
- `HybridRetriever` (in `AristaMcp.Server.Retrieval`) runs dense (`embedding <=> $1`) +
  sparse (`bm25v <&> to_bm25query(...)`) SQL in parallel, fuses via RRF k=60, reranks
  top-N, returns `SearchResponse` + `SearchDiagnostics` with per-stage timings.
- `IReranker` → `NoopReranker` ships now; `OnnxReranker` against bge-reranker-base is
  deferred (adds 278 MB model + needs cross-encoder `[query, doc]` pair tokenization
  which Microsoft.ML.Tokenizers 2.0.0 doesn't expose cleanly).
- Five MCP tools (`search_docs`, `lookup_section`, `list_documents`, `get_document`,
  `get_status`) live in `AristaMcp.Server.Tools` as `[McpServerToolType]` classes with
  constructor DI.
- `ServerHosting.AddAristaMcpServices(settings)` is the single source of truth for DI
  lifetimes — used by both `StdioHost` (console, logs to stderr) and `HttpHost`
  (ASP.NET Core, `MapMcp()`, `Stateless=true`).
- `arista-mcp serve --transport stdio|http --port 8080` launches either host. Verified
  end-to-end: `tools/list` over Streamable HTTP returns all 5 tools with JSON schemas.
- `vchord_bm25`'s `<&>` operator only returns rows with ≥1 matching token — zero-overlap
  chunks never appear in the result set. Don't write tests that assume a specific fixed
  result count from BM25; assert on top-ranked identities instead.

## Sprint 4 additions (polish + v0.1.0)

- **`bm25v` provisioned by EF migration** (`20260420132646_AddBm25Column`). No more
  `Migrations/Manual/` file to remember — `dotnet ef database update` does everything.
- **`OnnxReranker`** — cross-encoder/ms-marco-MiniLM-L6-v2 (BERT-base-uncased,
  shares Arctic Embed's vocab). Pair-tokenizes `[CLS] query [SEP] doc [SEP]` with
  `token_type_ids = 0` on segment 0 and `1` on segment 1. `ServerHosting.BuildReranker`
  auto-detects `models/reranker/` and falls back to `NoopReranker` when absent.
- **`HybridRetriever` invariants** (post-review):
  - `DenseSimilarity = 1 - cosine_distance` (conventional similarity, not distance).
  - `Bm25Score = -sparse_distance` (sign-flipped to "higher = better"). Do not assert
    specific score values in tests; assert on sign/magnitude bounds.
  - Co-hit chunks carry BOTH `DenseDistance` and `SparseDistance` through the fusion
    accumulator. `FusedCandidate` is the only place that enforces this — don't split it.
  - Dense/sparse SQL timings are measured inside each `Run*Async`; do not add a second
    outer `Stopwatch` or the two will drift.
- **`TimeProvider` everywhere** — repositories inject `TimeProvider`; DI registers
  `TimeProvider.System`. Never call `DateTimeOffset.UtcNow` directly from new code.
- **C# 14 / .NET 10 idioms in use:** `FrozenDictionary` (`QueryExpander.Synonyms`),
  `CollectionsMarshal.GetValueRefOrAddDefault` (RRF accumulator),
  `TensorPrimitives.Norm`/`Divide` (`OnnxEmbedder` L2-normalize), collection expressions
  `[.. x.Select(...)]` for `Half[]` conversions.
- **Benchmark harness** — `arista-mcp bench --queries tests/fixtures/bench-queries.json
  --limit 10` runs the curated 30-query set end-to-end; exit code 1 if top-10 hit rate
  < 80%. Uses the same embedder + reranker + retriever wiring as `serve`.
- **Known non-fix:** `AsyncFixer 1.6.0 → 2.1.0` is a major bump; not taken in v0.1.0.
  EF Core 10.x bump is blocked by `Pgvector.EntityFrameworkCore 0.3.0` targeting EF 9.x.

## Sprint 5 additions (v0.1.1)

- **DocumentLoader now reads per-doc JSON.** Pages stamp via
  `StampPagesFromJson(mdSections, jsonSections)` — order-preserving pair by
  `(level, CleanHeading(title))`. `CleanHeading` mirrors Python
  `arista-docs.enrich._clean_heading`. Don't drop the normalisation — an MD
  `**Configuration**` won't match a JSON `Configuration` without it.
- **FakeTimeProvider** — `Microsoft.Extensions.Time.Testing` namespace, pin
  `10.5.0`. Inject as base `TimeProvider` (repos already did this in v0.1.0).
  Tests: `new FakeTimeProvider(t0)` + `Advance(TimeSpan)`. Leave
  `AutoAdvanceAmount = Zero` — silent drift bug otherwise.
- **`bench --history <path> --label <tag>`** — append-only JSONL. Runs compare across
  retrievals over time; CI can diff baselines. Directory auto-created.
- **E2E test layers:**
  - `tests/AristaMcp.E2E/` — C# tests that spawn `arista-mcp` via the `CliProcess`
    helper. Both transports covered via raw JSON-RPC (stdio) and `HttpClient` SSE
    parse (HTTP). `SkippableFact` guards skip on missing embedder or empty chunks.
  - `tests/e2e/*.sh` — shell scenarios for schema + ingest; `set -euo pipefail`, use
    `podman exec` for `psql` so host-side psql isn't required.
- **EF.Relational 9.0.15 direct pin** in `AristaMcp.Data.csproj` — otherwise
  `Pgvector.EFCore 0.3.0` wins the transitive battle with 9.0.1 and `dotnet run`
  throws `MissingMethodException` at first `DbSet<>` access.
- **AsyncFixer 2.1.0** active; no new warnings surfaced on the existing codebase
  (zero suppressions anywhere in `src/`).

## Sprint 6 additions (v0.1.2)

- **Integration tests run against `arista_test`, not `arista`.** `PgvectorFixture`
  creates the test DB on first use + migrates it. Do NOT change `ResetAsync`'s
  truncate target — the DB-name `_test` suffix guard refuses to fire against
  anything else, so a wrong `ARISTA_MCP_TEST_CS` value now throws at startup
  instead of silently wiping prod. Prod `arista` is the CLI's / user's DB.
- **Full-corpus ingest timing: ~25 min CPU, not "several hours".** EOS-User-Manual
  accounts for ~80 % of wall-clock (5234 pages → ~5400 chunks → ONNX dominates).
  Remaining 2425 docs ingest in the last ~5 min. GPU would cut total well below
  10 min but we haven't wired `Microsoft.ML.OnnxRuntime.Gpu` yet.
- **Bench history append-only** — `tests/fixtures/bench-history.jsonl` now has 3
  rows; use `jq` or similar to inspect the trend. Full-corpus v0.1.2 baseline:
  70 % top-1 / 86.7 % top-10 / p50 1.8 s / p95 2.3 s.
- **Bench query-set curation matters.** A miss in `bench` output doesn't imply a
  retrieval failure — the `expect_any` token might not match the slug convention
  for that product (observed for `hardware`, `aboot`, `CVA`, `CVW`). Investigate
  a miss by running `list_documents --product <x>` first, then patch the query set.
