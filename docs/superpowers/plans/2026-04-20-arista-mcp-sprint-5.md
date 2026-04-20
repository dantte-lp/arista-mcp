# arista-mcp Sprint 5 Implementation Plan — Post-v0.1 Hygiene + JSON Enrichment + E2E

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or superpowers:executing-plans.

**Goal:** Land the follow-ups surfaced by the post-v0.1 research: bump AsyncFixer, adopt FakeTimeProvider, wire `DocumentLoader` to the per-doc JSON (page numbers in `search_docs` output — the first user-visible quality win after v0.1.0), and ship E2E scenarios 1, 2, 4, 5 from the e2e plan. Close with a full-corpus bench run, a `v0.1.1` tag, and a GitHub Actions job wiring the E2E set into CI.

**Reference:**
- Research report: `docs/superpowers/research/2026-04-20-post-v0.1-research.md`
- E2E testing plan: `docs/superpowers/plans/2026-04-20-arista-mcp-e2e-testing.md`

---

## Sprint 5 Overview

| # | Task | Priority | Touches |
|---|------|---------|---------|
| 5.1 | AsyncFixer 1.6.0 → 2.1.0 | 🟢 trivial | `Directory.Build.props` |
| 5.2 | FakeTimeProvider adoption + stale-run test | 🟡 quality | `tests/` new package, one new test |
| 5.3 | JSON enrichment in `DocumentLoader` | ⭐ feature | `Core.Catalog`, 3 new tests |
| 5.4 | E2E-4: stdio transport C# test | 🟡 coverage | `tests/AristaMcp.E2E` |
| 5.5 | E2E-5: HTTP transport C# test | 🟡 coverage | `tests/AristaMcp.E2E` |
| 5.6 | E2E-1 + E2E-2 shell scripts | 🟡 coverage | `tests/e2e/` |
| 5.7 | Full-corpus bench baseline | 🔵 data | `tests/fixtures/bench-history.jsonl` |
| 5.8 | GitHub Actions E2E workflow | 🟡 ops | `.github/workflows/e2e.yml` |
| 5.9 | Sprint 5 gate + `v0.1.1` tag | 🏁 gate | tag + CHANGELOG + CLAUDE.md |

**Definition of Done:**
- [ ] AsyncFixer 2.1.0 active; any new warnings triaged.
- [ ] `FakeTimeProvider` consumed in a new test that asserts exact-offset `FinishedAt - StartedAt`.
- [ ] `DocumentLoader` attaches `page_start`/`page_end` from per-doc JSON onto chunks; `search_docs` output carries real page numbers (not null).
- [ ] E2E-4 + E2E-5 pass via `dotnet test` (in-process spawn of `arista-mcp serve`).
- [ ] `tests/e2e/01-fresh-deploy.sh` + `02-category-ingest.sh` executable on bash+WSL; mirror `.ps1` for pure Windows.
- [ ] `arista-mcp bench` against full catalog produces a row in `tests/fixtures/bench-history.jsonl` (baseline).
- [ ] `.github/workflows/e2e.yml` runs the in-C# E2E tests on PRs.
- [ ] `git tag v0.1.1` exists; CHANGELOG updated.

---

## Task 5.1: AsyncFixer bump

**Files:** `Directory.Build.props`

Flip `1.6.0` → `2.1.0` (line 28), rebuild, triage any new warnings from the expanded `AsyncFixer01` (async local functions / terminal-await patterns) or new `AsyncFixer06` (`Task<T>→Task` discard). Suppress via `.editorconfig` only if a warning is truly noisy; default is to fix.

Commit: `chore(deps): AsyncFixer 1.6.0 → 2.1.0`.

## Task 5.2: FakeTimeProvider adoption

**Files:**
- Modify: `Directory.Packages.props` — add `Microsoft.Extensions.TimeProvider.Testing 10.5.0`
- Modify: `tests/AristaMcp.Data.Tests/AristaMcp.Data.Tests.csproj`
- Create: `tests/AristaMcp.Data.Tests/FakeTimeProviderIngestRunTest.cs`

The new test proves `IngestRunRepository` reads time via injected clock:

```csharp
[Fact]
public async Task StartThenFinish_ReportsExactElapsedFromFakeClock()
{
    await fx.ResetAsync();
    var t0 = new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero);
    var clock = new FakeTimeProvider(t0);

    await using var ctx = fx.CreateContext();
    var repo = new IngestRunRepository(ctx, clock);
    var run = await repo.StartAsync("sha", CancellationToken.None);

    clock.Advance(TimeSpan.FromMinutes(5));
    await repo.FinishAsync(run.Id, "success", 10, 2, 8, 42, null, CancellationToken.None);

    var last = await repo.GetLastAsync(CancellationToken.None);
    last!.StartedAt.Should().Be(t0);
    last.FinishedAt.Should().Be(t0 + TimeSpan.FromMinutes(5));
}
```

Commit: `test(data): FakeTimeProvider-driven elapsed-time assertion on ingest runs`.

## Task 5.3: JSON enrichment in DocumentLoader

**Files:**
- Create: `src/AristaMcp.Core/Catalog/EnrichedDocumentJson.cs` (DTO + `JsonSection`)
- Modify: `src/AristaMcp.Core/Catalog/DocumentLoader.cs` — read per-doc JSON, match MD headings to JSON sections, stamp `PageStart`/`PageEnd`
- Modify: `src/AristaMcp.Core/Catalog/DocumentLoader.cs` — port `_clean_heading` regex normaliser from arista-docs `enrich.py`
- Create/extend: `tests/AristaMcp.Core.Tests/Catalog/DocumentLoaderPageEnrichmentTests.cs`

**Contract to mirror (from `arista-docs/src/arista_docs/core/enrich.py`):**
- `_clean_heading(s)`: strip `**bold**`, `*italic*`, `_underscore_`, `<span>`/any HTML, collapse whitespace.

**Shape of per-doc JSON:**
```json
{
  "title": "…",
  "sections": [
    {"title": "Introduction", "level": 1, "page_start": 0, "page_end": 2},
    {"title": "Details",      "level": 2, "page_start": 3, "page_end": 5},
    ...
  ],
  "image_names": [...]        // not consumed in this sprint
}
```

**Matching algorithm:**

```
mdHeadings  = [(level, title) from MD ATX regex, in order]
jsonByLevel = multimap: level → Queue<JsonSection in JSON order>
for (level, rawTitle) in mdHeadings:
    key = (level, normalise(rawTitle))
    jsonQ = jsonByLevel[level]
    match = jsonQ.Dequeue() if jsonQ.Peek().NormalisedTitle == key.Title else null
    yield Section { ..., PageStart = match?.PageStart, PageEnd = match?.PageEnd }
```

Order-preserving; a mismatch at level N doesn't shift matches at level N+1.

**Tests:**
- Real-shape JSON with populated `sections`: pages stamp on every section.
- Empty `sections` array: pages stay null (no regression).
- Mismatch case: one extra MD heading absent from JSON — that heading gets null pages, subsequent headings at the same level still match.
- Normalisation: MD `**Configuration**` matches JSON `Configuration`.

Commit: `feat(core): page-number enrichment from per-doc JSON in DocumentLoader`.

## Task 5.4: E2E-4 stdio transport test

**Files:**
- Create: `tests/AristaMcp.E2E/StdioTransportE2ETest.cs`
- Possibly: `tests/AristaMcp.E2E/Helpers/CliProcess.cs` (spawn helper)

Uses `ModelContextProtocol.Client` (already in the solution's NuGet graph via
`ModelContextProtocol` 1.2.0). Shape:

```csharp
[SkippableFact]
public async Task StdioHandshake_ListsAllFiveTools_AndSearchReturnsResults()
{
    Skip.If(!HasIngestedData(), "seed data required; run 02-category-ingest first");

    await using var cli = await CliProcess.StartAsync("serve", "--transport", "stdio");
    await using var client = await McpClient.CreateAsync(new StdioClientTransport(cli.Stdout, cli.Stdin));

    var tools = await client.ListToolsAsync();
    tools.Select(t => t.Name).Should().BeEquivalentTo(
        "search_docs", "lookup_section", "list_documents", "get_document", "get_status");

    var result = await client.CallToolAsync("search_docs",
        new Dictionary<string, object?> { ["query"] = "EVPN overlay" });
    result.Content.Should().NotBeEmpty();
}
```

Data prerequisite (check helper): `SELECT COUNT(*) FROM chunks > 0`. Skip if empty.

Commit: `test(e2e): stdio transport handshake + search_docs smoke`.

## Task 5.5: E2E-5 HTTP transport test

**Files:**
- Create: `tests/AristaMcp.E2E/HttpTransportE2ETest.cs`

Shape:

```csharp
[SkippableFact]
public async Task HttpTransport_ToolsListAndSearchDocs_Succeed()
{
    Skip.If(!HasIngestedData(), "...");

    var port = GetFreePort();
    await using var cli = await CliProcess.StartAsync("serve", "--transport", "http", "--port", port.ToString());
    await WaitForReadyAsync($"http://127.0.0.1:{port}/");

    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
    http.DefaultRequestHeaders.Accept.Add(new("application/json"));
    http.DefaultRequestHeaders.Accept.Add(new("text/event-stream"));

    var toolsList = await http.PostJsonAsync("", new {
        jsonrpc = "2.0", id = 1, method = "tools/list" });
    toolsList.Should().Contain("search_docs").And.Contain("lookup_section");
}
```

Commit: `test(e2e): HTTP streamable transport list + call smoke`.

## Task 5.6: E2E-1 + E2E-2 shell scripts

**Files:**
- Create: `tests/e2e/lib/assert-psql.sh`, `tests/e2e/lib/wait-for.sh`
- Create: `tests/e2e/01-fresh-deploy.sh`
- Create: `tests/e2e/02-category-ingest.sh`
- Create: `tests/e2e/README.md`

Scripts are bash (Linux / WSL / git-bash). Mirror `.ps1` deferred until a real Windows
user reports friction. Each script:
- `set -euo pipefail`
- Colour-coded pass/fail output
- Explicit `trap` cleanup (stop containers, remove temp volume)
- Exits non-zero on first failure

Commit: `test(e2e): scripted fresh-deploy + category-ingest scenarios`.

## Task 5.7: Full-corpus bench baseline

**Files:**
- Create: `tests/fixtures/bench-history.jsonl` (append-only)
- Modify: `src/AristaMcp.Cli/Commands/BenchCommand.cs` — add `--history <path>` flag
  that appends `{date, top1, top10, p50, p95, avg}` as a JSONL row.

Run once: `arista-mcp ingest --force` (full catalog) then `arista-mcp bench --limit 10
--history tests/fixtures/bench-history.jsonl`. Capture the first row as the v0.1.1
baseline.

Commit: `feat(bench): --history flag + v0.1.1 baseline row`.

## Task 5.8: GitHub Actions E2E workflow

**Files:**
- Create: `.github/workflows/e2e.yml`

Runs on PR to any branch:
1. Checkout + setup .NET 10.
2. Start podman + compose up postgres (GitHub Actions has docker; use docker-compose).
3. Cache + fetch models (only embedder; reranker is optional for CI).
4. `dotnet ef database update`.
5. `dotnet test tests/AristaMcp.E2E` — runs E2E-4 + E2E-5.
6. Run `tests/e2e/02-category-ingest.sh` against a tiny category.

Commit: `ci: E2E workflow on PRs (stdio + HTTP + category ingest)`.

## Task 5.9: Sprint 5 gate + tag v0.1.1

1. Full `dotnet build` + `dotnet test`.
2. `arista-mcp ingest --force` + `arista-mcp bench` — baseline row appended.
3. Verify CI workflow runs clean on a dry-run branch.
4. Append Sprint 5 section to `CHANGELOG.md` under `## [v0.1.1]` heading.
5. Append Sprint 5 block to `CLAUDE.md`.
6. `git tag v0.1.1`.

## Gate checklist

- [ ] AsyncFixer 2.1.0 active; no new errors.
- [ ] FakeTimeProvider test passes.
- [ ] JSON enrichment test passes; chunks produced by real ingest have non-null pages
      in ≥ 80% of rows for docs with populated `sections` arrays.
- [ ] E2E-4 + E2E-5 tests pass locally.
- [ ] `tests/e2e/01-*.sh` + `02-*.sh` executable + green.
- [ ] Bench baseline row written.
- [ ] CI workflow green on a PR preview.
- [ ] `v0.1.1` tag exists.
