# arista-mcp Release Polish Plan — cut v0.2.0

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or executing-plans.

**Goal:** tag **v0.2.0** — first release post-Sprint 13 — with all
release-hygiene boxes ticked: code-review follow-ups closed, legal
metadata present, CI green on clean PRs, assembly version aligned with
the tag, documented release process.

**Why v0.2.0 (not v0.1.5 or v0.3.0):** per semver 2.0 in pre-1.0
projects the minor axis is the "breaking-or-meaningful-change" dial.
We are NOT shipping retrieval-quality improvements yet — that's
v0.3.0 — but Sprint 10 + 13 introduced new opt-in features (HyDE),
a new bench format, a new CLI verb, and a new reranker family path.
That is material, non-trivial, additive change → minor bump. v0.3.0
is reserved for the "top-1 ≥ 95 %" gate.

## Scope summary

| Tier | Item | Needs input? | Gate |
|------|------|--------------|------|
| **Tier 0** — pre-flight code review follow-ups |
| 0.1 | Close M1 (HyDE breaker CompareExchange guard) | no | test proves single-arm under concurrent failure |
| 0.2 | Close M6 (empty-input guard in `validate-bench-queries`) | no | empty JSONL → clear error + exit code 2 |
| **Tier 1** — legal + CI + metadata (blocks tag) |
| 1.1 | LICENSE file | **YES — choose license** | `LICENSE` at repo root |
| 1.2 | SECURITY.md | no | responsible-disclosure contact + supported-versions table |
| 1.3 | Assembly metadata in Directory.Build.props | no | `dotnet --info` / `arista-mcp --version` report 0.2.0 |
| 1.4 | CI workflow `ci.yml` — fast build + unit tests on PR | no | green on a PR with zero Postgres |
| 1.5 | Dependabot config | no | `.github/dependabot.yml` valid, weekly schedule |
| **Tier 2** — docs + process |
| 2.1 | `releasing.md` in docs/en + docs/ru | no | cut process documented step-by-step |
| 2.2 | Verify all doc cross-links | no | no broken relative paths |
| **Tier 3** — cut the release |
| 3.1 | Roll CHANGELOG [Unreleased] → [0.2.0] | no | Keep a Changelog 1.1.0 format preserved |
| 3.2 | Bump `<Version>` to 0.2.0 in Directory.Build.props | no | matches CHANGELOG + tag |
| 3.3 | Tag `v0.2.0`, push, `gh release create` with notes | no | GitHub Release page populated from CHANGELOG section |

## Deliberately out of scope (post-v0.2.0)

- **CONTRIBUTING.md** — private repo, no external contributors yet.
- **Issue / PR templates, CODEOWNERS, CODE_OF_CONDUCT** — same reason.
- **NuGet packaging** — this is an application, not a library.
  `dotnet publish` + release artefact is Tier-3-post if ever.
- **Container image** — separate release channel; not blocking tag.
- **Automated release-please / semantic-release** — manual cut is fine at 1 release / 1-2 weeks cadence.
- **M3, M4 (extra tests), M5 follow-ups from code review** — non-blocking
  observations. M4 already partially addressed (malformed JSON + timeout
  tests landed in commit `801a96b`).

## Tier 0 — pre-flight

### 0.1 Close M1 — HyDE breaker CompareExchange guard

**File:** `src/AristaMcp.Server/Retrieval/HydeExpander.cs`, method `RecordFailure`.

**Problem:** `Interlocked.Exchange(ref _circuitOpenUntilTicks, openUntil)`
is unconditional — two threads observing `count == threshold`
simultaneously both write, last writer wins, cooldown extends by the
skew. Bounded and microsecond-scale but untidy.

**Fix:** `Interlocked.CompareExchange(ref _circuitOpenUntilTicks,
openUntil, 0L)` — only arm when currently disarmed. Subsequent
failures during cooldown are no-ops for the ticks field; still
increment `_consecutiveFailures` for observability.

**Test:** parallel 10 threads call `RecordFailure()` past the
threshold; assert `_circuitOpenUntilTicks` written exactly once by
measuring the value and verifying no cooldown drift.

### 0.2 Close M6 — empty-input guard

**File:** `src/AristaMcp.Cli/Commands/ValidateBenchQueriesCommand.cs`.

**Fix:** after the JSONL read loop, if `candidates.Count == 0`, print
`[red]error[/] no candidates in input {path}` via Spectre and return
exit code **2** (`InputError`). Don't proceed to DI construction —
embedder init is expensive and pointless on empty input.

## Tier 1 — legal + CI + metadata

### 1.1 LICENSE

**Needs user choice.** Four plausible:

| License | Gist | Best when… |
|---------|------|------------|
| **MIT** | Permissive, 1 paragraph | Max adoption, no concerns about downstream closed-source use |
| **Apache 2.0** | Permissive + explicit patent grant + NOTICE file | You want a patent-defence clause against contributors |
| **AGPL-3.0** | Copyleft even over the network | You want network-served derivatives to remain open |
| **Proprietary** | `LICENSE` says "All rights reserved, see LICENSE-TERMS.md for details" | Internal tool, no external distribution intended |

Recommend: **MIT** for an internal MCP server that might later be open-sourced, OR **Proprietary** if there's any chance of commercial licensing. Apache-2 is the middle-ground pick.

Once decided: drop the single-file text from <https://choosealicense.com/>
as `LICENSE` at repo root, update README license badge + "License" section.

### 1.2 SECURITY.md

Standard GitHub-recognised file. Content:

- **Supported versions:** `v0.2.x` (current) + `v0.1.x` (best-effort) table.
- **Reporting:** private security advisory via
  `https://github.com/dantte-lp/arista-mcp/security/advisories/new`.
  Email fallback to the maintainer.
- **Scope:** in-scope = the .NET server, docker compose file, scripts.
  Out-of-scope = underlying ONNX models, postgres extensions (report
  upstream).
- **Response target:** 72 h acknowledgement, 30 d fix target for
  high-severity, 90 d disclosure window.

### 1.3 Assembly metadata — `Directory.Build.props`

Per [MS docs MSBuild package-properties](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#package-properties):

```xml
<PropertyGroup>
  <!-- Identity — consumed by dotnet build + any future dotnet pack. -->
  <Version>0.2.0</Version>
  <AssemblyVersion>0.2.0.0</AssemblyVersion>
  <FileVersion>0.2.0.0</FileVersion>
  <!-- InformationalVersion auto-appends +SHA via SourceLink on .NET 8+. -->

  <!-- Product metadata — shows in Win/Linux file properties + bug reports. -->
  <Authors>dantte-lp</Authors>
  <Company>dantte-lp</Company>
  <Product>arista-mcp</Product>

  <!-- Repo + project pointers — useful if we ever dotnet pack. -->
  <RepositoryUrl>https://github.com/dantte-lp/arista-mcp</RepositoryUrl>
  <RepositoryType>git</RepositoryType>
  <PackageProjectUrl>https://github.com/dantte-lp/arista-mcp</PackageProjectUrl>

  <!-- CI-aware: deterministic builds when building on CI. -->
  <ContinuousIntegrationBuild Condition="'$(GITHUB_ACTIONS)' == 'true'">true</ContinuousIntegrationBuild>
</PropertyGroup>
```

Set once in `Directory.Build.props` so every project inherits. `<Version>`
will be bumped per release in Tier 3; other fields are identity-stable.

### 1.4 CI workflow

**File:** `.github/workflows/ci.yml` (new, separate from `e2e.yml`).

Runs on every push + PR to `master`. Fast path — no postgres service,
no E2E tests. Just `dotnet restore`, `dotnet build`, `dotnet test`
filtered to `Category != Integration` (plus SkippableFacts skip
cleanly on missing resources).

Components per Context7 [.NET Actions guide](https://docs.github.com/en/actions/tutorials/build-and-test-code/net):

- `actions/checkout@v4`
- `actions/setup-dotnet@v4` with `cache: true` → auto NuGet caching keyed on `Directory.Packages.props`.
- `dotnet restore`
- `dotnet build --no-restore -c Release /warnaserror`
- `dotnet test --no-build -c Release --logger trx --results-directory TestResults`
- `actions/upload-artifact@v4` with `if: ${{ always() }}` for trx logs.

Concurrency group = `ci-${{ github.ref }}`, cancel-in-progress.

Target wall-clock ≤ 3 minutes.

### 1.5 Dependabot

**File:** `.github/dependabot.yml`:

```yaml
version: 2
updates:
  - package-ecosystem: "nuget"
    directory: "/"
    schedule:
      interval: "weekly"
    labels: ["deps", "nuget"]
    open-pull-requests-limit: 5
  - package-ecosystem: "github-actions"
    directory: "/"
    schedule:
      interval: "weekly"
    labels: ["deps", "github-actions"]
```

Weekly not daily — avoids noise on a small repo. Labels make triage
fast. Central package management means the NuGet ecosystem watches
`Directory.Packages.props` implicitly.

## Tier 2 — docs + process

### 2.1 `releasing.md`

**Files:** `docs/en/releasing.md` + `docs/ru/releasing.md`.

Documented steps:

1. `dotnet test` green locally.
2. Edit `CHANGELOG.md`: move `[Unreleased]` entries into new `[x.y.z] — YYYY-MM-DD` section, leave empty `[Unreleased]` above.
3. Update `<Version>` in `Directory.Build.props`.
4. Update link-footer compare URLs in CHANGELOG.
5. Commit: `chore(release): v<x.y.z>`.
6. Tag: `git tag -a v<x.y.z> -m "v<x.y.z>"`.
7. Push: `git push && git push --tags`.
8. GitHub Release: `gh release create v<x.y.z> --title "v<x.y.z>" --notes-from-tag` (or `--notes-file changelog-section.md`).
9. Verify release page rendered.

### 2.2 Link verification

Spot-check every relative path in the freshly-written docs. Targets:

- Root `README.md` → `docs/en/*`, `docs/ru/*`, `CHANGELOG.md`.
- `docs/en/README.md` → neighbour pages + `../mcp-integration.md`, `../onnx-provider.md`, `../otel.md`, `../../CLAUDE.md`, `../../CHANGELOG.md`.
- Same for `docs/ru/README.md`.
- Plan's `docs/superpowers/plans/2026-04-24-…v0.3-revised.md` referenced from README + CHANGELOG.

## Tier 3 — cut v0.2.0

### 3.1 Roll CHANGELOG

Replace:
```md
## [Unreleased]

Work toward v0.3.0 …

### Added
- …
### Changed
- …
```

With:
```md
## [Unreleased]

_(no entries yet)_

## [0.2.0] — 2026-04-24

### Added
- …
### Changed
- …
```

Compare-link footer:
```md
[Unreleased]: https://github.com/dantte-lp/arista-mcp/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/dantte-lp/arista-mcp/compare/v0.1.4...v0.2.0
[v0.1.4]: …  (existing)
```

### 3.2 Version bump

`Directory.Build.props`:
```xml
<Version>0.2.0</Version>
<AssemblyVersion>0.2.0.0</AssemblyVersion>
<FileVersion>0.2.0.0</FileVersion>
```

### 3.3 Tag + GitHub Release

```bash
git add CHANGELOG.md Directory.Build.props
git commit -m "chore(release): v0.2.0"
git tag -a v0.2.0 -m "v0.2.0 — HyDE scaffolding + bench v2 + bilingual docs"
git push && git push --tags
gh release create v0.2.0 \
  --title "v0.2.0 — platform polish for measurement + rewrite" \
  --notes-file <(sed -n '/^## \[0\.2\.0\]/,/^## \[/p' CHANGELOG.md | head -n -1)
```

## Definition of Done

- [ ] Tier 0 items committed (M1 + M6 fixes landed with tests).
- [ ] LICENSE file present at repo root, license section in README accurate.
- [ ] SECURITY.md at repo root (GitHub picks it up automatically for security tab).
- [ ] `Directory.Build.props` carries release metadata; `Version = 0.2.0`.
- [ ] `.github/workflows/ci.yml` green on its first run.
- [ ] `.github/dependabot.yml` valid.
- [ ] `docs/en/releasing.md` + `docs/ru/releasing.md` committed and linked from docs index.
- [ ] CHANGELOG rolled to `[0.2.0]`.
- [ ] `v0.2.0` tag exists + pushed + visible as a GitHub Release page with notes populated.

## Risks + mitigations

| Risk | Mitigation |
|------|------------|
| License choice wrong for downstream intent | Surface as a direct question before writing LICENSE; default to MIT if user is silent. |
| CI fails on first push (missing NuGet cache, unknown runner image) | Run `act` locally or use `workflow_dispatch` dry-run before declaring done. |
| `TreatWarningsAsErrors` breaks on Release config when `ci.yml` adds `/warnaserror` that conflicts with debug-only tolerances | Reuse exact same `dotnet build -c Release` invocation as local; repo already builds clean in Release. |
| Dependabot floods with 30 PRs on first run | `open-pull-requests-limit: 5` caps the first wave. |

## Sources

- [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/) — CHANGELOG format.
- [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html) — 0.x.y rules.
- [MS Docs — MSBuild package properties](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#package-properties) — Version / Authors / RepositoryUrl / ContinuousIntegrationBuild.
- [MS Docs — Develop libraries with the .NET CLI](https://learn.microsoft.com/en-us/dotnet/core/tutorials/libraries) — directory layout + metadata.
- [GitHub Docs — Build and test .NET](https://docs.github.com/en/actions/tutorials/build-and-test-code/net) — `setup-dotnet@v4` with `cache: true`.
- [GitHub Docs — Configuring Dependabot](https://docs.github.com/en/code-security/dependabot/working-with-dependabot/dependabot-options-reference) — schedule, labels, limits.
