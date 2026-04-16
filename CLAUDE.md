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
