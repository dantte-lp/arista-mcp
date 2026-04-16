# arista-mcp v0.1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `arista-mcp` v0.1 — a .NET 10 MCP server that ingests `arista-docs` output into pgvector-backed PostgreSQL 18 and serves hybrid search (dense + BM25 + rerank) via stdio and Streamable HTTP.

**Architecture:** Layered solution — `Core` (models, chunker, RRF, QueryExpander) ← `Embedding` (ONNX) + `Data` (EF Core + Pgvector) ← `Server` (MCP tools) ← `Cli` (System.CommandLine). Custom PostgreSQL image via `tensorchord/vchord-suite:pg18-latest` preloads pgvector + vchord + vchord_bm25 + pg_tokenizer. All models (snowflake-arctic-embed-m-v1.5 + bge-reranker-base) loaded via ONNX Runtime, CPU default with CUDA opt-in.

**Tech Stack:** .NET 10 (SDK 10.0.201, TFM net10.0) · ModelContextProtocol 1.2.0 · **EF Core 9.0.15** (pinned — Pgvector.EntityFrameworkCore 0.3.0 lags EF 10) · Npgsql.EntityFrameworkCore.PostgreSQL 9.0.4 · Pgvector 0.3.2 · Pgvector.EntityFrameworkCore 0.3.0 · Microsoft.ML.OnnxRuntime 1.24.4 · Testcontainers.PostgreSql 4.11.0 · Microsoft.Extensions.AI 10.5.0 · Microsoft.Extensions.Hosting 10.0.6 · xUnit 2.9.3 · Microsoft.NET.Test.Sdk 18.4.0 · FluentAssertions 8.9.0 · Spectre.Console 0.55.0 · System.CommandLine 2.0.6 · PostgreSQL 18 + pgvector 0.8.x + vchord_bm25 0.2.x · Podman

**Reference spec:** [`docs/superpowers/specs/2026-04-16-arista-mcp-design.md`](../specs/2026-04-16-arista-mcp-design.md)

---

## Waterfall Overview

Four sequential sprints, each producing a shippable slice with an explicit acceptance gate. **Do not start a later sprint until the previous gate is green.**

| # | Sprint | Outcome | Gate |
|---|--------|---------|------|
| 1 | Infrastructure + data layer | Solution skeleton, Podman postgres, EF Core + pgvector schema, integration tests green | `dotnet test` all pass; `podman-compose up -d postgres` healthy; `HalfVectorRoundtripTest` + `HnswIndexSearchTest` green |
| 2 | Embedding + ingest | ONNX embedder/reranker, chunker, `ingest` CLI command | `arista-mcp ingest` on 5-doc fixture produces ≥30 chunks with valid halfvec + bm25v; `IncrementalReingestTest` passes |
| 3 | Retrieval + MCP server | HybridRetriever, 5 MCP tools, stdio + HTTP transports | `/search "LANZ mirroring"` returns expected doc; stdio transport works with mock MCP client; `HttpTransportBootTest` green |
| 4 | Polish + v0.1 release | Diagnostics, CLI helpers, README, tag | `git tag v0.1.0`, benchmarks on 30-query set, README published, ruff/ty/pytest analogues (dotnet format + analyzers) clean |

---

# Sprint 1 — Infrastructure + Data Layer

**Deliverables:**
- Solution with 5 src projects + 4 test projects (skeleton only)
- CPM, Directory.Build.props, BannedSymbols, analyzers wired
- `docker/` with Containerfile + compose + init.sql
- `AristaMcp.Core.Models` — `AristaDocument`, `AristaChunk`, `SearchResult` (pure data)
- `AristaMcp.Core.Settings` — `AristaMcpSettings`
- `AristaMcp.Data` — DbContext, DocumentEntity, ChunkEntity, IngestRunEntity, initial migration
- Testcontainers fixture + `HalfVectorRoundtripTest`, `HnswIndexSearchTest`, `Bm25IndexSearchTest`, `DocumentRepositoryTest`, `ChunkRepositoryBulkInsertTest`

**Definition of Done:**
- [ ] `dotnet build` clean from repo root
- [ ] `dotnet test` all pass (including Testcontainers integration)
- [ ] `podman-compose -f docker/compose.yaml up -d postgres` → healthcheck green within 30s
- [ ] `psql -h localhost -p 5434 -U arista arista -c "\dx"` lists: vector, vchord, vchord_bm25, pg_tokenizer, pg_trgm

---

## Task 1.1: Initialize solution skeleton

**Files:**
- Create: `C:\SHARE\arista-mcp\arista-mcp.slnx`
- Create: `C:\SHARE\arista-mcp\global.json`
- Create: `C:\SHARE\arista-mcp\Directory.Build.props`
- Create: `C:\SHARE\arista-mcp\Directory.Packages.props`
- Create: `C:\SHARE\arista-mcp\BannedSymbols.txt`

- [ ] **Step 1: Write `global.json`**

```json
{
  "sdk": {
    "version": "10.0.201",
    "rollForward": "latestFeature"
  }
}
```

- [ ] **Step 2: Write `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors>CS1591</WarningsNotAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <ErrorLog>$(MSBuildProjectDirectory)/bin/$(Configuration)/analyzers.sarif</ErrorLog>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup>
    <AdditionalFiles Include="$(MSBuildThisFileDirectory)BannedSymbols.txt" />
  </ItemGroup>

  <ItemGroup>
    <GlobalPackageReference Include="Meziantou.Analyzer" Version="2.0.210" />
    <GlobalPackageReference Include="Roslynator.Analyzers" Version="4.14.0" />
    <GlobalPackageReference Include="SonarAnalyzer.CSharp" Version="10.5.0.98468" />
    <GlobalPackageReference Include="AsyncFixer" Version="1.6.0" />
    <GlobalPackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="3.11.0-beta1.24553.1" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write `Directory.Packages.props`**

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="ModelContextProtocol" Version="1.2.0" />
    <PackageVersion Include="ModelContextProtocol.AspNetCore" Version="1.2.0" />
    <PackageVersion Include="Microsoft.Extensions.AI" Version="10.4.1" />
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.1" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.1" />
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.1" />
    <PackageVersion Include="Pgvector" Version="0.3.2" />
    <PackageVersion Include="Pgvector.EntityFrameworkCore" Version="0.3.0" />
    <PackageVersion Include="Microsoft.ML.OnnxRuntime" Version="1.24.4" />
    <PackageVersion Include="Microsoft.ML.OnnxRuntime.Gpu" Version="1.24.4" />
    <PackageVersion Include="Microsoft.ML.Tokenizers" Version="10.4.1" />
    <PackageVersion Include="System.CommandLine" Version="2.0.5" />
    <PackageVersion Include="Spectre.Console" Version="0.51.1" />
    <PackageVersion Include="Testcontainers.PostgreSql" Version="4.7.0" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.0.1" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageVersion Include="FluentAssertions" Version="7.0.0" />
    <PackageVersion Include="NSubstitute" Version="5.3.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Write `BannedSymbols.txt`**

```
T:System.DateTime;use DateTimeOffset.UtcNow or TimeProvider
P:System.DateTime.Now;use DateTimeOffset.UtcNow or TimeProvider
P:System.DateTimeOffset.Now;use DateTimeOffset.UtcNow or TimeProvider
T:System.Net.WebClient;use HttpClient
M:System.Threading.Thread.Abort;removed in .NET Core
T:System.Runtime.Serialization.Formatters.Binary.BinaryFormatter;use System.Text.Json
M:System.Security.Cryptography.MD5.Create;use SHA256 or stronger
M:System.Security.Cryptography.SHA1.Create;use SHA256 or stronger
```

- [ ] **Step 5: Write `arista-mcp.slnx`**

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/AristaMcp.Core/AristaMcp.Core.csproj" />
    <Project Path="src/AristaMcp.Embedding/AristaMcp.Embedding.csproj" />
    <Project Path="src/AristaMcp.Data/AristaMcp.Data.csproj" />
    <Project Path="src/AristaMcp.Server/AristaMcp.Server.csproj" />
    <Project Path="src/AristaMcp.Cli/AristaMcp.Cli.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/AristaMcp.Core.Tests/AristaMcp.Core.Tests.csproj" />
    <Project Path="tests/AristaMcp.Embedding.Tests/AristaMcp.Embedding.Tests.csproj" />
    <Project Path="tests/AristaMcp.Data.Tests/AristaMcp.Data.Tests.csproj" />
    <Project Path="tests/AristaMcp.Server.Tests/AristaMcp.Server.Tests.csproj" />
    <Project Path="tests/AristaMcp.E2E/AristaMcp.E2E.csproj" />
  </Folder>
</Solution>
```

- [ ] **Step 6: Commit**

```bash
cd C:/SHARE/arista-mcp
git add global.json Directory.Build.props Directory.Packages.props BannedSymbols.txt arista-mcp.slnx
git commit -m "feat(skeleton): solution layout, CPM, analyzers, banned symbols"
```

---

## Task 1.2: Create empty project files

**Files:**
- Create: `src/AristaMcp.Core/AristaMcp.Core.csproj`
- Create: `src/AristaMcp.Embedding/AristaMcp.Embedding.csproj`
- Create: `src/AristaMcp.Data/AristaMcp.Data.csproj`
- Create: `src/AristaMcp.Server/AristaMcp.Server.csproj`
- Create: `src/AristaMcp.Cli/AristaMcp.Cli.csproj`
- Create: 5 corresponding `tests/*.csproj`

- [ ] **Step 1: Write `src/AristaMcp.Core/AristaMcp.Core.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>AristaMcp.Core</RootNamespace>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Write `src/AristaMcp.Embedding/AristaMcp.Embedding.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>AristaMcp.Embedding</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../AristaMcp.Core/AristaMcp.Core.csproj" />
    <PackageReference Include="Microsoft.ML.OnnxRuntime" />
    <PackageReference Include="Microsoft.ML.Tokenizers" />
    <PackageReference Include="Microsoft.Extensions.AI" />
    <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write `src/AristaMcp.Data/AristaMcp.Data.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>AristaMcp.Data</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../AristaMcp.Core/AristaMcp.Core.csproj" />
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="Pgvector" />
    <PackageReference Include="Pgvector.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Write `src/AristaMcp.Server/AristaMcp.Server.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <RootNamespace>AristaMcp.Server</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../AristaMcp.Core/AristaMcp.Core.csproj" />
    <ProjectReference Include="../AristaMcp.Embedding/AristaMcp.Embedding.csproj" />
    <ProjectReference Include="../AristaMcp.Data/AristaMcp.Data.csproj" />
    <PackageReference Include="ModelContextProtocol" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Write `src/AristaMcp.Cli/AristaMcp.Cli.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <RootNamespace>AristaMcp.Cli</RootNamespace>
    <AssemblyName>arista-mcp</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../AristaMcp.Core/AristaMcp.Core.csproj" />
    <ProjectReference Include="../AristaMcp.Embedding/AristaMcp.Embedding.csproj" />
    <ProjectReference Include="../AristaMcp.Data/AristaMcp.Data.csproj" />
    <ProjectReference Include="../AristaMcp.Server/AristaMcp.Server.csproj" />
    <PackageReference Include="System.CommandLine" />
    <PackageReference Include="Spectre.Console" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Write 5 test csproj files** (template shown for Core.Tests)

`tests/AristaMcp.Core.Tests/AristaMcp.Core.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>AristaMcp.Core.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/AristaMcp.Core/AristaMcp.Core.csproj" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="NSubstitute" />
  </ItemGroup>
</Project>
```

Repeat same shape for `Embedding.Tests` (refs AristaMcp.Embedding), `Data.Tests` (refs AristaMcp.Data + `Testcontainers.PostgreSql`), `Server.Tests` (refs AristaMcp.Server), `E2E` (refs all + Testcontainers).

- [ ] **Step 7: Verify build**

```bash
cd C:/SHARE/arista-mcp && dotnet restore && dotnet build
```

Expected: `Build succeeded.` (10 projects build clean).

- [ ] **Step 8: Commit**

```bash
git add src/ tests/
git commit -m "feat(skeleton): csproj files for all src and tests projects"
```

---

## Task 1.3: Docker/Podman infrastructure

**Files:**
- Create: `docker/Containerfile`
- Create: `docker/Containerfile.from-scratch`
- Create: `docker/Containerfile.app`
- Create: `docker/compose.yaml`
- Create: `docker/init.sql`
- Create: `docker/postgresql.conf`
- Create: `docker/README.md`

- [ ] **Step 1: Write `docker/Containerfile`**

```dockerfile
FROM tensorchord/vchord-suite:pg18-latest
COPY init.sql /docker-entrypoint-initdb.d/00-init.sql
HEALTHCHECK --interval=10s --timeout=3s --retries=10 \
    CMD pg_isready -U ${POSTGRES_USER:-arista} -d ${POSTGRES_DB:-arista}
```

- [ ] **Step 2: Write `docker/init.sql`**

```sql
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS vchord CASCADE;
CREATE EXTENSION IF NOT EXISTS pg_tokenizer;
CREATE EXTENSION IF NOT EXISTS vchord_bm25 CASCADE;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

ALTER DATABASE arista SET search_path TO "$user", public, tokenizer_catalog, bm25_catalog;

-- Must be called qualified — ALTER DATABASE ... SET search_path only affects future
-- sessions, not the init script currently running.
SELECT tokenizer_catalog.create_text_analyzer('english_analyzer', $$
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

- [ ] **Step 3: Write `docker/compose.yaml`**

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
    command: >-
      postgres
      -c shared_preload_libraries=vector,vchord,vchord_bm25,pg_tokenizer
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

volumes:
  arista-pgdata:
```

- [ ] **Step 4: Write `docker/README.md` (operator runbook)**

```markdown
# arista-mcp Postgres container

## Build

    cd docker
    podman-compose build postgres

## Start

    podman-compose up -d postgres
    # wait for healthcheck
    until pg_isready -h localhost -p 5434 -U arista; do sleep 1; done

## Connect

    psql -h localhost -p 5434 -U arista -d arista

## Backup / restore

    podman exec arista-mcp-postgres pg_dump -U arista arista | gzip > backup.sql.gz
    gunzip < backup.sql.gz | podman exec -i arista-mcp-postgres psql -U arista arista

## Reset

    podman-compose down -v
    podman-compose up -d postgres
```

- [ ] **Step 5: Build + start + verify extensions**

```bash
cd C:/SHARE/arista-mcp/docker
podman-compose build postgres
podman-compose up -d postgres
sleep 20
psql -h localhost -p 5434 -U arista -d arista -c "\dx"
```

Expected output includes rows for: `vector`, `vchord`, `vchord_bm25`, `pg_tokenizer`, `pg_trgm`.

- [ ] **Step 6: Commit**

```bash
cd C:/SHARE/arista-mcp
git add docker/
git commit -m "feat(infra): postgres 18 OCI image with pgvector+vchord_bm25+analyzer"
```

---

## Task 1.4: Core models — AristaDocument, AristaChunk, SearchResult

**Files:**
- Create: `src/AristaMcp.Core/Models/AristaDocument.cs`
- Create: `src/AristaMcp.Core/Models/AristaChunk.cs`
- Create: `src/AristaMcp.Core/Models/SearchResult.cs`
- Create: `tests/AristaMcp.Core.Tests/Models/AristaDocumentTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/AristaMcp.Core.Tests/Models/AristaDocumentTests.cs
using AristaMcp.Core.Models;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Core.Tests.Models;

public class AristaDocumentTests
{
    [Fact]
    public void AristaDocument_DefaultTags_IsEmptyList()
    {
        var doc = new AristaDocument { Id = "x", Url = "u", Category = "toi", Title = "t", Slug = "s", MdPath = "a", JsonPath = "b" };
        doc.Tags.Should().BeEmpty();
    }

    [Fact]
    public void AristaDocument_Equality_IsByValue()
    {
        var a = new AristaDocument { Id = "x", Url = "u", Category = "toi", Title = "t", Slug = "s", MdPath = "a", JsonPath = "b" };
        var b = a with { Title = "t" };
        a.Should().Be(b);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/AristaMcp.Core.Tests -v q
```

Expected: FAIL — `AristaDocument` does not exist.

- [ ] **Step 3: Implement `AristaDocument`**

```csharp
// src/AristaMcp.Core/Models/AristaDocument.cs
namespace AristaMcp.Core.Models;

public sealed record AristaDocument
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public required string Category { get; init; }
    public string? Product { get; init; }
    public string? Version { get; init; }
    public required string Title { get; init; }
    public required string Slug { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public int? Pages { get; init; }
    public long? SizeBytes { get; init; }
    public string? PdfSha256 { get; init; }
    public required string MdPath { get; init; }
    public required string JsonPath { get; init; }
    public string? ConvertMode { get; init; }
    public int ImageCount { get; init; }
    public int SectionCount { get; init; }
    public int Level1SectionCount { get; init; }
    public int TocCount { get; init; }
    public DateTimeOffset? DownloadedAt { get; init; }
    public DateTimeOffset? ConvertedAt { get; init; }
}
```

- [ ] **Step 4: Implement `AristaChunk`**

```csharp
// src/AristaMcp.Core/Models/AristaChunk.cs
namespace AristaMcp.Core.Models;

public sealed record AristaChunk
{
    public long Id { get; init; }
    public required string DocumentId { get; init; }
    public required int ChunkIndex { get; init; }
    public required string Content { get; init; }
    public required string RawContent { get; init; }
    public string? SectionTitle { get; init; }
    public short? SectionLevel { get; init; }
    public int? PageStart { get; init; }
    public int? PageEnd { get; init; }
    public required int TokenCount { get; init; }
    public required float[] Embedding { get; init; }
    public string EmbeddingModel { get; init; } = "snowflake-arctic-embed-m-v1.5";
}
```

- [ ] **Step 5: Implement `SearchResult` + `SearchResponse` + `SearchDiagnostics`**

```csharp
// src/AristaMcp.Core/Models/SearchResult.cs
namespace AristaMcp.Core.Models;

public sealed record ChunkResult(
    long ChunkId,
    string DocumentId,
    string DocumentTitle,
    string DocumentSlug,
    string Category,
    string? Product,
    string? Version,
    string? SectionTitle,
    short? SectionLevel,
    int? PageStart,
    int? PageEnd,
    string RawContent,
    float Score,
    float? DenseSimilarity,
    float? Bm25Score,
    float? RrfScore,
    float? RerankScore);

public sealed record SearchResponse(
    IReadOnlyList<ChunkResult> Results,
    SearchDiagnostics Diagnostics);

public sealed record SearchDiagnostics(
    int DenseHits,
    int SparseHits,
    int AfterRrf,
    int AfterRerank,
    double EmbedMs,
    double DenseQueryMs,
    double SparseQueryMs,
    double RrfMs,
    double RerankMs,
    double TotalMs);
```

- [ ] **Step 6: Run test to verify pass**

```bash
dotnet test tests/AristaMcp.Core.Tests -v q
```

Expected: 2 passed.

- [ ] **Step 7: Commit**

```bash
git add src/AristaMcp.Core/Models tests/AristaMcp.Core.Tests/Models
git commit -m "feat(core): AristaDocument, AristaChunk, SearchResult records"
```

---

## Task 1.5: Core settings (AristaMcpSettings)

**Files:**
- Create: `src/AristaMcp.Core/Settings/AristaMcpSettings.cs`
- Create: `tests/AristaMcp.Core.Tests/Settings/AristaMcpSettingsTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/AristaMcp.Core.Tests/Settings/AristaMcpSettingsTests.cs
using AristaMcp.Core.Settings;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AristaMcp.Core.Tests.Settings;

public class AristaMcpSettingsTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var s = new AristaMcpSettings();
        s.EmbeddingModel.Should().Be("snowflake-arctic-embed-m-v1.5");
        s.EmbeddingDim.Should().Be(768);
        s.Transport.Should().Be(McpTransport.Stdio);
        s.HttpPort.Should().Be(8080);
        s.Gpu.Should().BeFalse();
    }

    [Fact]
    public void BindsFromConfiguration()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ARISTA_MCP:ConnectionString"] = "Host=x",
                ["ARISTA_MCP:Gpu"] = "true",
                ["ARISTA_MCP:HttpPort"] = "9090",
            })
            .Build();

        var s = cfg.GetSection("ARISTA_MCP").Get<AristaMcpSettings>()!;
        s.ConnectionString.Should().Be("Host=x");
        s.Gpu.Should().BeTrue();
        s.HttpPort.Should().Be(9090);
    }
}
```

- [ ] **Step 2: Run — fail (class does not exist)**

```bash
dotnet test tests/AristaMcp.Core.Tests --filter FullyQualifiedName~AristaMcpSettingsTests -v q
```

- [ ] **Step 3: Implement `AristaMcpSettings`**

```csharp
// src/AristaMcp.Core/Settings/AristaMcpSettings.cs
namespace AristaMcp.Core.Settings;

public enum McpTransport
{
    Stdio,
    Http,
}

public sealed class AristaMcpSettings
{
    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5434;Database=arista;Username=arista;Password=arista";
    public string ModelsDir { get; set; } = "models";
    public string EmbeddingModel { get; set; } = "snowflake-arctic-embed-m-v1.5";
    public int EmbeddingDim { get; set; } = 768;
    public string RerankerModel { get; set; } = "bge-reranker-base";
    public bool Gpu { get; set; }
    public McpTransport Transport { get; set; } = McpTransport.Stdio;
    public int HttpPort { get; set; } = 8080;
    public int IngestBatchSize { get; set; } = 32;
    public int IngestParallelism { get; set; } = 4;
    public int ChunkMaxTokens { get; set; } = 1200;
    public int ChunkTargetTokens { get; set; } = 512;
    public int ChunkOverlapTokens { get; set; } = 64;
    public int ChunkMinTokens { get; set; } = 40;
}
```

- [ ] **Step 4: Run — pass**

```bash
dotnet test tests/AristaMcp.Core.Tests -v q
```

Expected: 4 passed (2 existing + 2 new).

- [ ] **Step 5: Commit**

```bash
git add src/AristaMcp.Core/Settings tests/AristaMcp.Core.Tests/Settings
git commit -m "feat(core): AristaMcpSettings with Options pattern binding"
```

---

## Task 1.6: Data entities + DbContext

**Files:**
- Create: `src/AristaMcp.Data/Entities/DocumentEntity.cs`
- Create: `src/AristaMcp.Data/Entities/ChunkEntity.cs`
- Create: `src/AristaMcp.Data/Entities/IngestRunEntity.cs`
- Create: `src/AristaMcp.Data/AristaDbContext.cs`
- Create: `src/AristaMcp.Data/DataSourceFactory.cs`

- [ ] **Step 1: Write `DocumentEntity`**

```csharp
// src/AristaMcp.Data/Entities/DocumentEntity.cs
using System.ComponentModel.DataAnnotations.Schema;

namespace AristaMcp.Data.Entities;

[Table("documents")]
public class DocumentEntity
{
    [Column("id")]            public string Id { get; set; } = "";
    [Column("url")]           public string Url { get; set; } = "";
    [Column("category")]      public string Category { get; set; } = "";
    [Column("product")]       public string? Product { get; set; }
    [Column("version")]       public string? Version { get; set; }
    [Column("title")]         public string Title { get; set; } = "";
    [Column("slug")]          public string Slug { get; set; } = "";
    [Column("tags", TypeName = "jsonb")]
    public string TagsJson { get; set; } = "[]";
    [Column("pages")]         public int? Pages { get; set; }
    [Column("size_bytes")]    public long? SizeBytes { get; set; }
    [Column("pdf_sha256")]    public string? PdfSha256 { get; set; }
    [Column("md_path")]       public string MdPath { get; set; } = "";
    [Column("json_path")]     public string JsonPath { get; set; } = "";
    [Column("convert_mode")]  public string? ConvertMode { get; set; }
    [Column("image_count")]           public int ImageCount { get; set; }
    [Column("section_count")]         public int SectionCount { get; set; }
    [Column("level1_section_count")]  public int Level1SectionCount { get; set; }
    [Column("toc_count")]             public int TocCount { get; set; }
    [Column("downloaded_at")] public DateTimeOffset? DownloadedAt { get; set; }
    [Column("converted_at")]  public DateTimeOffset? ConvertedAt { get; set; }
    [Column("ingested_at")]   public DateTimeOffset IngestedAt { get; set; }
}
```

- [ ] **Step 2: Write `ChunkEntity`**

```csharp
// src/AristaMcp.Data/Entities/ChunkEntity.cs
using System.ComponentModel.DataAnnotations.Schema;
using Pgvector;

namespace AristaMcp.Data.Entities;

[Table("chunks")]
public class ChunkEntity
{
    [Column("id")]                public long Id { get; set; }
    [Column("document_id")]       public string DocumentId { get; set; } = "";
    [Column("chunk_index")]       public int ChunkIndex { get; set; }
    [Column("content")]           public string Content { get; set; } = "";
    [Column("raw_content")]       public string RawContent { get; set; } = "";
    [Column("section_title")]     public string? SectionTitle { get; set; }
    [Column("section_level")]     public short? SectionLevel { get; set; }
    [Column("page_start")]        public int? PageStart { get; set; }
    [Column("page_end")]          public int? PageEnd { get; set; }
    [Column("token_count")]       public int TokenCount { get; set; }

    [Column("embedding", TypeName = "halfvec(768)")]
    public HalfVector Embedding { get; set; } = null!;

    [Column("embedding_model")]   public string EmbeddingModel { get; set; } = "snowflake-arctic-embed-m-v1.5";
    [Column("created_at")]        public DateTimeOffset CreatedAt { get; set; }

    public DocumentEntity Document { get; set; } = null!;
}
```

- [ ] **Step 3: Write `IngestRunEntity`**

```csharp
// src/AristaMcp.Data/Entities/IngestRunEntity.cs
using System.ComponentModel.DataAnnotations.Schema;

namespace AristaMcp.Data.Entities;

[Table("ingest_runs")]
public class IngestRunEntity
{
    [Column("id")]               public long Id { get; set; }
    [Column("started_at")]       public DateTimeOffset StartedAt { get; set; }
    [Column("finished_at")]      public DateTimeOffset? FinishedAt { get; set; }
    [Column("status")]           public string Status { get; set; } = "running";
    [Column("docs_total")]       public int DocsTotal { get; set; }
    [Column("docs_skipped")]     public int DocsSkipped { get; set; }
    [Column("docs_upserted")]    public int DocsUpserted { get; set; }
    [Column("chunks_upserted")]  public int ChunksUpserted { get; set; }
    [Column("catalog_sha256")]   public string? CatalogSha256 { get; set; }
    [Column("error_msg")]        public string? ErrorMsg { get; set; }
}
```

- [ ] **Step 4: Write `AristaDbContext`**

```csharp
// src/AristaMcp.Data/AristaDbContext.cs
using AristaMcp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AristaMcp.Data;

public class AristaDbContext(DbContextOptions<AristaDbContext> options) : DbContext(options)
{
    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();
    public DbSet<ChunkEntity> Chunks => Set<ChunkEntity>();
    public DbSet<IngestRunEntity> IngestRuns => Set<IngestRunEntity>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.HasPostgresExtension("vector");
        mb.HasPostgresExtension("vchord");
        mb.HasPostgresExtension("pg_tokenizer");
        mb.HasPostgresExtension("vchord_bm25");
        mb.HasPostgresExtension("pg_trgm");

        mb.Entity<DocumentEntity>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.IngestedAt).HasDefaultValueSql("now()");
            e.HasIndex(d => new { d.Category, d.Product, d.Version }).HasDatabaseName("idx_documents_category_product_version");
            e.HasIndex(d => d.PdfSha256).HasDatabaseName("idx_documents_pdf_sha256");
        });

        mb.Entity<ChunkEntity>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(c => new { c.DocumentId, c.ChunkIndex }).IsUnique();
            e.HasIndex(c => c.DocumentId);
            e.HasIndex(c => c.SectionLevel).HasFilter("section_level IS NOT NULL");
            e.HasIndex(c => c.Embedding)
                .HasMethod("hnsw")
                .HasOperators("halfvec_cosine_ops")
                .HasStorageParameter("m", 16)
                .HasStorageParameter("ef_construction", 200);
            e.HasOne(c => c.Document)
                .WithMany()
                .HasForeignKey(c => c.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<IngestRunEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.StartedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.StartedAt).IsDescending();
        });
    }
}
```

- [ ] **Step 5: Write `DataSourceFactory`**

```csharp
// src/AristaMcp.Data/DataSourceFactory.cs
using Npgsql;

namespace AristaMcp.Data;

public static class DataSourceFactory
{
    public static NpgsqlDataSource Build(string connectionString)
    {
        var b = new NpgsqlDataSourceBuilder(connectionString);
        b.UseVector();
        return b.Build();
    }
}
```

- [ ] **Step 6: Build**

```bash
dotnet build src/AristaMcp.Data
```

Expected: clean.

- [ ] **Step 7: Commit**

```bash
git add src/AristaMcp.Data
git commit -m "feat(data): DbContext, entities (document/chunk/ingest_run), HNSW index, halfvec(768)"
```

---

## Task 1.7: Initial EF Core migration

**Files:**
- Create: `src/AristaMcp.Data/Migrations/*` (EF Core generated)

- [ ] **Step 1: Start postgres**

```bash
cd C:/SHARE/arista-mcp/docker && podman-compose up -d postgres && sleep 15
```

- [ ] **Step 2: Create migration**

```bash
cd C:/SHARE/arista-mcp
dotnet ef migrations add Initial \
  --project src/AristaMcp.Data \
  --startup-project src/AristaMcp.Data \
  --connection "Host=localhost;Port=5434;Database=arista;Username=arista;Password=arista"
```

(If this fails because `AristaMcp.Data` has no `Program.cs`, a temporary `DesignTimeDbContextFactory` is needed — see Step 3.)

- [ ] **Step 3: If design-time factory needed, create `DesignTimeDbContextFactory.cs`**

```csharp
// src/AristaMcp.Data/DesignTimeDbContextFactory.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AristaMcp.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AristaDbContext>
{
    public AristaDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ARISTA_MCP_CS")
            ?? "Host=localhost;Port=5434;Database=arista;Username=arista;Password=arista";
        var ds = DataSourceFactory.Build(cs);
        var opt = new DbContextOptionsBuilder<AristaDbContext>()
            .UseNpgsql(ds, o => o.UseVector())
            .Options;
        return new AristaDbContext(opt);
    }
}
```

Retry Step 2.

- [ ] **Step 4: Apply migration**

```bash
dotnet ef database update \
  --project src/AristaMcp.Data \
  --startup-project src/AristaMcp.Data
```

Expected: `Applied migration 'Initial'.`

- [ ] **Step 5: Verify tables**

```bash
psql -h localhost -p 5434 -U arista arista -c "\dt"
```

Expected output includes: `documents`, `chunks`, `ingest_runs`, `__EFMigrationsHistory`.

- [ ] **Step 6: Verify HNSW index exists**

```bash
psql -h localhost -p 5434 -U arista arista -c "\d chunks"
```

Expected: index named `ix_chunks_embedding` using `hnsw` method.

- [ ] **Step 7: Commit**

```bash
git add src/AristaMcp.Data/Migrations src/AristaMcp.Data/DesignTimeDbContextFactory.cs
git commit -m "feat(data): initial EF Core migration with pgvector HNSW"
```

---

## Task 1.8: Add raw-SQL migration for bm25v generated column

EF Core cannot express `GENERATED ALWAYS AS (tokenize(content, 'english_analyzer')) STORED` + `USING bm25` index. We add a SQL script migration after the EF migration.

**Files:**
- Create: `src/AristaMcp.Data/Migrations/Manual/001_bm25v_column.sql`
- Modify: `src/AristaMcp.Data/AristaDbContext.cs:OnModelCreating` (document bm25v via `HasAnnotation`)

- [ ] **Step 1: Write manual migration SQL**

```sql
-- src/AristaMcp.Data/Migrations/Manual/001_bm25v_column.sql
-- pg_tokenizer.rs tokenize() is STABLE (not IMMUTABLE), so STORED GENERATED columns
-- are rejected. Use create_custom_model_tokenizer_and_trigger instead — it provisions
-- a custom tokenizer + BM25 model + a BEFORE INSERT/UPDATE trigger that writes the
-- bm25vector into target_column. Queries use bm25query + tokenize(@q, 'chunks_tokenizer').
ALTER TABLE chunks ADD COLUMN IF NOT EXISTS bm25v bm25vector;

SELECT tokenizer_catalog.create_custom_model_tokenizer_and_trigger(
    tokenizer_name     => 'chunks_tokenizer',
    model_name         => 'chunks_model',
    text_analyzer_name => 'english_analyzer',
    table_name         => 'chunks',
    source_column      => 'content',
    target_column      => 'bm25v'
);

CREATE INDEX IF NOT EXISTS idx_chunks_bm25 ON chunks USING bm25 (bm25v bm25_ops);
```

- [ ] **Step 2: Apply manually**

```bash
psql -h localhost -p 5434 -U arista arista -f src/AristaMcp.Data/Migrations/Manual/001_bm25v_column.sql
```

Expected: `ALTER TABLE`, `CREATE INDEX`.

- [ ] **Step 3: Verify column + index**

```bash
psql -h localhost -p 5434 -U arista arista -c "\d chunks"
```

Expected: `bm25v` column present, `idx_chunks_bm25` listed.

- [ ] **Step 4: Commit**

```bash
git add src/AristaMcp.Data/Migrations/Manual
git commit -m "feat(data): manual migration for bm25v column + index"
```

---

## Task 1.9: Testcontainers fixture

**Files:**
- Create: `tests/AristaMcp.Data.Tests/Fixtures/PgvectorFixture.cs`
- Create: `tests/AristaMcp.Data.Tests/Fixtures/FixtureCollection.cs`

- [ ] **Step 1: Write fixture**

```csharp
// tests/AristaMcp.Data.Tests/Fixtures/PgvectorFixture.cs
using AristaMcp.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace AristaMcp.Data.Tests.Fixtures;

public sealed class PgvectorFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("tensorchord/vchord-suite:pg18-latest")
            .WithDatabase("arista_test")
            .WithUsername("arista")
            .WithPassword("arista")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        // Seed extensions + analyzer BEFORE EF migrations
        await using (var seedConn = new NpgsqlConnection(ConnectionString))
        {
            await seedConn.OpenAsync();
            await using var cmd = seedConn.CreateCommand();
            cmd.CommandText = await File.ReadAllTextAsync("../../../../../docker/init.sql");
            await cmd.ExecuteNonQueryAsync();
        }

        DataSource = DataSourceFactory.Build(ConnectionString);

        var opt = new DbContextOptionsBuilder<AristaDbContext>()
            .UseNpgsql(DataSource, o => o.UseVector())
            .Options;
        await using var ctx = new AristaDbContext(opt);
        await ctx.Database.MigrateAsync();

        // Apply manual bm25v migration
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var bm25Cmd = conn.CreateCommand();
        bm25Cmd.CommandText = await File.ReadAllTextAsync(
            "../../../../../src/AristaMcp.Data/Migrations/Manual/001_bm25v_column.sql");
        await bm25Cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    public AristaDbContext CreateContext()
    {
        var opt = new DbContextOptionsBuilder<AristaDbContext>()
            .UseNpgsql(DataSource, o => o.UseVector())
            .Options;
        return new AristaDbContext(opt);
    }
}
```

- [ ] **Step 2: Write collection definition**

```csharp
// tests/AristaMcp.Data.Tests/Fixtures/FixtureCollection.cs
using Xunit;

namespace AristaMcp.Data.Tests.Fixtures;

[CollectionDefinition("Pgvector")]
public class FixtureCollection : ICollectionFixture<PgvectorFixture> { }
```

- [ ] **Step 3: Build**

```bash
dotnet build tests/AristaMcp.Data.Tests
```

- [ ] **Step 4: Commit**

```bash
git add tests/AristaMcp.Data.Tests/Fixtures
git commit -m "test(data): Testcontainers fixture with pgvector+vchord_bm25+migrations"
```

---

## Task 1.10: HalfVectorRoundtripTest

**Files:**
- Create: `tests/AristaMcp.Data.Tests/HalfVectorRoundtripTest.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/AristaMcp.Data.Tests/HalfVectorRoundtripTest.cs
using AristaMcp.Data.Entities;
using AristaMcp.Data.Tests.Fixtures;
using FluentAssertions;
using Pgvector;
using Xunit;

namespace AristaMcp.Data.Tests;

[Collection("Pgvector")]
public class HalfVectorRoundtripTest(PgvectorFixture fx)
{
    [Fact]
    public async Task InsertAndReadHalfVector()
    {
        await using var ctx = fx.CreateContext();
        var doc = new DocumentEntity
        {
            Id = "doc1", Url = "u", Category = "toi",
            Title = "T", Slug = "s", MdPath = "m", JsonPath = "j",
        };
        ctx.Documents.Add(doc);
        await ctx.SaveChangesAsync();

        var vec = new Half[768];
        for (int i = 0; i < 768; i++) vec[i] = (Half)(i / 768f);

        var chunk = new ChunkEntity
        {
            DocumentId = "doc1",
            ChunkIndex = 0,
            Content = "title > section\n\nhello world",
            RawContent = "hello world",
            TokenCount = 3,
            Embedding = new HalfVector(vec),
        };
        ctx.Chunks.Add(chunk);
        await ctx.SaveChangesAsync();

        var loaded = await ctx.Chunks.FirstAsync(c => c.DocumentId == "doc1");
        loaded.Embedding.Should().NotBeNull();
        loaded.Embedding.ToArray().Length.Should().Be(768);
        ((float)loaded.Embedding.ToArray()[100]).Should().BeApproximately(100f / 768f, 1e-2f);
    }
}
```

- [ ] **Step 2: Run**

```bash
dotnet test tests/AristaMcp.Data.Tests --filter FullyQualifiedName~HalfVectorRoundtripTest -v n
```

Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/AristaMcp.Data.Tests/HalfVectorRoundtripTest.cs
git commit -m "test(data): HalfVector round-trip via EF Core"
```

---

## Task 1.11: HnswIndexSearchTest

**Files:**
- Create: `tests/AristaMcp.Data.Tests/HnswIndexSearchTest.cs`

- [ ] **Step 1: Write test**

```csharp
// tests/AristaMcp.Data.Tests/HnswIndexSearchTest.cs
using AristaMcp.Data.Entities;
using AristaMcp.Data.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Xunit;

namespace AristaMcp.Data.Tests;

[Collection("Pgvector")]
public class HnswIndexSearchTest(PgvectorFixture fx)
{
    [Fact]
    public async Task NearestNeighboursReturnedInOrder()
    {
        await using var ctx = fx.CreateContext();
        var rng = new Random(42);

        ctx.Documents.Add(new DocumentEntity
        {
            Id = "hnsw-doc", Url = "u", Category = "toi",
            Title = "T", Slug = "s", MdPath = "m", JsonPath = "j",
        });
        await ctx.SaveChangesAsync();

        for (int i = 0; i < 500; i++)
        {
            var v = new Half[768];
            for (int j = 0; j < 768; j++) v[j] = (Half)(rng.NextSingle() * 2 - 1);
            ctx.Chunks.Add(new ChunkEntity
            {
                DocumentId = "hnsw-doc", ChunkIndex = i,
                Content = $"c{i}", RawContent = $"c{i}",
                TokenCount = 1,
                Embedding = new HalfVector(v),
            });
        }
        await ctx.SaveChangesAsync();

        var query = ctx.Chunks.AsNoTracking().First(c => c.ChunkIndex == 0).Embedding;

        var nearest = await ctx.Chunks
            .OrderBy(c => c.Embedding.CosineDistance(query))
            .Take(5)
            .Select(c => c.ChunkIndex)
            .ToListAsync();

        nearest.Should().HaveCount(5);
        nearest[0].Should().Be(0); // self is nearest
    }
}
```

- [ ] **Step 2: Run — pass**

```bash
dotnet test tests/AristaMcp.Data.Tests --filter FullyQualifiedName~HnswIndexSearchTest -v n
```

- [ ] **Step 3: Commit**

```bash
git add tests/AristaMcp.Data.Tests/HnswIndexSearchTest.cs
git commit -m "test(data): HNSW cosine nearest-neighbour ordering"
```

---

## Task 1.12: Bm25IndexSearchTest

**Files:**
- Create: `tests/AristaMcp.Data.Tests/Bm25IndexSearchTest.cs`

- [ ] **Step 1: Write test**

```csharp
// tests/AristaMcp.Data.Tests/Bm25IndexSearchTest.cs
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
        await using var ctx = fx.CreateContext();

        ctx.Documents.Add(new DocumentEntity
        {
            Id = "bm25-doc", Url = "u", Category = "toi",
            Title = "T", Slug = "s", MdPath = "m", JsonPath = "j",
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

        for (int i = 0; i < texts.Length; i++)
        {
            var v = new Half[768];
            for (int j = 0; j < 768; j++) v[j] = (Half)0.1f;
            ctx.Chunks.Add(new ChunkEntity
            {
                DocumentId = "bm25-doc",
                ChunkIndex = i,
                Content = texts[i],
                RawContent = texts[i],
                TokenCount = texts[i].Split(' ').Length,
                Embedding = new HalfVector(v),
            });
        }
        await ctx.SaveChangesAsync();

        await using var conn = await fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT chunk_index,
                   bm25v <&> bm25query('idx_chunks_bm25', tokenize($1, 'english_analyzer')) AS score
            FROM chunks
            ORDER BY bm25v <&> bm25query('idx_chunks_bm25', tokenize($1, 'english_analyzer'))
            LIMIT 3;";
        cmd.Parameters.Add(new NpgsqlParameter { Value = "EVPN overlay" });

        var results = new List<int>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) results.Add(r.GetInt32(0));

        results.Should().HaveCount(3);
        results.Should().Contain(0).And.Contain(3);
    }
}
```

- [ ] **Step 2: Run**

```bash
dotnet test tests/AristaMcp.Data.Tests --filter FullyQualifiedName~Bm25IndexSearchTest -v n
```

Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/AristaMcp.Data.Tests/Bm25IndexSearchTest.cs
git commit -m "test(data): BM25 query returns relevance-ordered results"
```

---

## Task 1.13: DocumentRepository with catalog upsert

**Files:**
- Create: `src/AristaMcp.Data/Repositories/IDocumentRepository.cs`
- Create: `src/AristaMcp.Data/Repositories/DocumentRepository.cs`
- Create: `tests/AristaMcp.Data.Tests/DocumentRepositoryTest.cs`

- [ ] **Step 1: Write interface**

```csharp
// src/AristaMcp.Data/Repositories/IDocumentRepository.cs
using AristaMcp.Core.Models;

namespace AristaMcp.Data.Repositories;

public interface IDocumentRepository
{
    Task UpsertAsync(AristaDocument doc, CancellationToken ct);
    Task<AristaDocument?> GetByIdAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<string>> GetAllIdsAsync(CancellationToken ct);
    Task<string?> GetPdfSha256Async(string id, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}
```

- [ ] **Step 2: Write implementation**

```csharp
// src/AristaMcp.Data/Repositories/DocumentRepository.cs
using System.Text.Json;
using AristaMcp.Core.Models;
using AristaMcp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AristaMcp.Data.Repositories;

public class DocumentRepository(AristaDbContext db) : IDocumentRepository
{
    public async Task UpsertAsync(AristaDocument d, CancellationToken ct)
    {
        var entity = await db.Documents.FirstOrDefaultAsync(x => x.Id == d.Id, ct);
        if (entity is null)
        {
            entity = new DocumentEntity { Id = d.Id };
            db.Documents.Add(entity);
        }
        entity.Url = d.Url;
        entity.Category = d.Category;
        entity.Product = d.Product;
        entity.Version = d.Version;
        entity.Title = d.Title;
        entity.Slug = d.Slug;
        entity.TagsJson = JsonSerializer.Serialize(d.Tags);
        entity.Pages = d.Pages;
        entity.SizeBytes = d.SizeBytes;
        entity.PdfSha256 = d.PdfSha256;
        entity.MdPath = d.MdPath;
        entity.JsonPath = d.JsonPath;
        entity.ConvertMode = d.ConvertMode;
        entity.ImageCount = d.ImageCount;
        entity.SectionCount = d.SectionCount;
        entity.Level1SectionCount = d.Level1SectionCount;
        entity.TocCount = d.TocCount;
        entity.DownloadedAt = d.DownloadedAt;
        entity.ConvertedAt = d.ConvertedAt;
        entity.IngestedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<AristaDocument?> GetByIdAsync(string id, CancellationToken ct)
    {
        var e = await db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? null : MapToDomain(e);
    }

    public async Task<IReadOnlyList<string>> GetAllIdsAsync(CancellationToken ct) =>
        await db.Documents.AsNoTracking().Select(x => x.Id).ToListAsync(ct);

    public async Task<string?> GetPdfSha256Async(string id, CancellationToken ct) =>
        await db.Documents.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => x.PdfSha256)
            .FirstOrDefaultAsync(ct);

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await db.Documents.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
    }

    private static AristaDocument MapToDomain(DocumentEntity e) => new()
    {
        Id = e.Id, Url = e.Url, Category = e.Category,
        Product = e.Product, Version = e.Version,
        Title = e.Title, Slug = e.Slug,
        Tags = JsonSerializer.Deserialize<List<string>>(e.TagsJson) ?? [],
        Pages = e.Pages, SizeBytes = e.SizeBytes, PdfSha256 = e.PdfSha256,
        MdPath = e.MdPath, JsonPath = e.JsonPath, ConvertMode = e.ConvertMode,
        ImageCount = e.ImageCount, SectionCount = e.SectionCount,
        Level1SectionCount = e.Level1SectionCount, TocCount = e.TocCount,
        DownloadedAt = e.DownloadedAt, ConvertedAt = e.ConvertedAt,
    };
}
```

- [ ] **Step 3: Write test**

```csharp
// tests/AristaMcp.Data.Tests/DocumentRepositoryTest.cs
using AristaMcp.Core.Models;
using AristaMcp.Data.Repositories;
using AristaMcp.Data.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Data.Tests;

[Collection("Pgvector")]
public class DocumentRepositoryTest(PgvectorFixture fx)
{
    [Fact]
    public async Task UpsertTwice_ResultsInOneRow_LatestWins()
    {
        await using var ctx = fx.CreateContext();
        var repo = new DocumentRepository(ctx);

        await repo.UpsertAsync(new AristaDocument
        {
            Id = "u1", Url = "u", Category = "toi", Title = "first",
            Slug = "s", MdPath = "m", JsonPath = "j",
            Tags = ["a"],
        }, default);

        await repo.UpsertAsync(new AristaDocument
        {
            Id = "u1", Url = "u", Category = "toi", Title = "second",
            Slug = "s", MdPath = "m", JsonPath = "j",
            Tags = ["a", "b"],
        }, default);

        var ids = await repo.GetAllIdsAsync(default);
        ids.Should().ContainSingle().Which.Should().Be("u1");

        var doc = await repo.GetByIdAsync("u1", default);
        doc!.Title.Should().Be("second");
        doc.Tags.Should().BeEquivalentTo(["a", "b"]);
    }
}
```

- [ ] **Step 4: Run**

```bash
dotnet test tests/AristaMcp.Data.Tests --filter FullyQualifiedName~DocumentRepositoryTest -v n
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AristaMcp.Data/Repositories tests/AristaMcp.Data.Tests/DocumentRepositoryTest.cs
git commit -m "feat(data): DocumentRepository upsert/get/delete via EF Core"
```

---

## Task 1.14: ChunkRepository with COPY BINARY bulk insert

**Files:**
- Create: `src/AristaMcp.Data/Repositories/IChunkRepository.cs`
- Create: `src/AristaMcp.Data/Repositories/ChunkRepository.cs`
- Create: `tests/AristaMcp.Data.Tests/ChunkRepositoryBulkInsertTest.cs`

- [ ] **Step 1: Write interface**

```csharp
// src/AristaMcp.Data/Repositories/IChunkRepository.cs
using AristaMcp.Core.Models;

namespace AristaMcp.Data.Repositories;

public interface IChunkRepository
{
    Task<int> BulkInsertAsync(IReadOnlyList<AristaChunk> chunks, CancellationToken ct);
    Task<int> DeleteByDocumentAsync(string documentId, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
}
```

- [ ] **Step 2: Write implementation**

```csharp
// src/AristaMcp.Data/Repositories/ChunkRepository.cs
using AristaMcp.Core.Models;
using NpgsqlTypes;
using Npgsql;
using Pgvector;

namespace AristaMcp.Data.Repositories;

public class ChunkRepository(NpgsqlDataSource dataSource, AristaDbContext db) : IChunkRepository
{
    public async Task<int> BulkInsertAsync(IReadOnlyList<AristaChunk> chunks, CancellationToken ct)
    {
        if (chunks.Count == 0) return 0;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY chunks (document_id, chunk_index, content, raw_content, " +
            "section_title, section_level, page_start, page_end, token_count, " +
            "embedding, embedding_model) FROM STDIN BINARY", ct);

        foreach (var c in chunks)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(c.DocumentId, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(c.ChunkIndex, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(c.Content, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(c.RawContent, NpgsqlDbType.Text, ct);
            if (c.SectionTitle is null) await writer.WriteNullAsync(ct);
            else await writer.WriteAsync(c.SectionTitle, NpgsqlDbType.Text, ct);
            if (c.SectionLevel is null) await writer.WriteNullAsync(ct);
            else await writer.WriteAsync(c.SectionLevel.Value, NpgsqlDbType.Smallint, ct);
            if (c.PageStart is null) await writer.WriteNullAsync(ct);
            else await writer.WriteAsync(c.PageStart.Value, NpgsqlDbType.Integer, ct);
            if (c.PageEnd is null) await writer.WriteNullAsync(ct);
            else await writer.WriteAsync(c.PageEnd.Value, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(c.TokenCount, NpgsqlDbType.Integer, ct);

            var halfArr = new Half[c.Embedding.Length];
            for (int i = 0; i < c.Embedding.Length; i++) halfArr[i] = (Half)c.Embedding[i];
            await writer.WriteAsync(new HalfVector(halfArr));

            await writer.WriteAsync(c.EmbeddingModel, NpgsqlDbType.Text, ct);
        }
        await writer.CompleteAsync(ct);
        return chunks.Count;
    }

    public async Task<int> DeleteByDocumentAsync(string documentId, CancellationToken ct)
    {
        return await db.Chunks
            .Where(c => c.DocumentId == documentId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<int> CountAsync(CancellationToken ct) => await db.Chunks.CountAsync(ct);
}
```

- [ ] **Step 3: Write test**

```csharp
// tests/AristaMcp.Data.Tests/ChunkRepositoryBulkInsertTest.cs
using AristaMcp.Core.Models;
using AristaMcp.Data.Entities;
using AristaMcp.Data.Repositories;
using AristaMcp.Data.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Data.Tests;

[Collection("Pgvector")]
public class ChunkRepositoryBulkInsertTest(PgvectorFixture fx)
{
    [Fact]
    public async Task BulkInsert1000Chunks_CompletesUnderThreeSeconds()
    {
        await using var ctx = fx.CreateContext();
        ctx.Documents.Add(new DocumentEntity
        {
            Id = "bulk-doc", Url = "u", Category = "toi",
            Title = "T", Slug = "s", MdPath = "m", JsonPath = "j",
        });
        await ctx.SaveChangesAsync();

        var rng = new Random(7);
        var chunks = Enumerable.Range(0, 1000).Select(i =>
        {
            var vec = new float[768];
            for (int j = 0; j < 768; j++) vec[j] = rng.NextSingle();
            return new AristaChunk
            {
                DocumentId = "bulk-doc",
                ChunkIndex = i,
                Content = $"title > s\n\nbody {i}",
                RawContent = $"body {i}",
                TokenCount = 4,
                Embedding = vec,
            };
        }).ToList();

        var repo = new ChunkRepository(fx.DataSource, ctx);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var inserted = await repo.BulkInsertAsync(chunks, default);
        sw.Stop();

        inserted.Should().Be(1000);
        sw.ElapsedMilliseconds.Should().BeLessThan(3000);

        var count = await repo.CountAsync(default);
        count.Should().BeGreaterOrEqualTo(1000);
    }

    [Fact]
    public async Task DeleteByDocument_RemovesAllChunks()
    {
        await using var ctx = fx.CreateContext();
        ctx.Documents.Add(new DocumentEntity
        {
            Id = "del-doc", Url = "u", Category = "toi",
            Title = "T", Slug = "s", MdPath = "m", JsonPath = "j",
        });
        await ctx.SaveChangesAsync();

        var chunks = Enumerable.Range(0, 10).Select(i => new AristaChunk
        {
            DocumentId = "del-doc",
            ChunkIndex = i,
            Content = $"c{i}",
            RawContent = $"c{i}",
            TokenCount = 1,
            Embedding = Enumerable.Repeat(0.1f, 768).ToArray(),
        }).ToList();

        var repo = new ChunkRepository(fx.DataSource, ctx);
        await repo.BulkInsertAsync(chunks, default);

        var deleted = await repo.DeleteByDocumentAsync("del-doc", default);
        deleted.Should().Be(10);
    }
}
```

- [ ] **Step 4: Run**

```bash
dotnet test tests/AristaMcp.Data.Tests --filter FullyQualifiedName~ChunkRepositoryBulkInsertTest -v n
```

Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add src/AristaMcp.Data/Repositories/IChunkRepository.cs \
        src/AristaMcp.Data/Repositories/ChunkRepository.cs \
        tests/AristaMcp.Data.Tests/ChunkRepositoryBulkInsertTest.cs
git commit -m "feat(data): ChunkRepository bulk insert via Npgsql COPY BINARY with HalfVector"
```

---

## Task 1.15: Sprint 1 verification + tag

- [ ] **Step 1: Full test run**

```bash
cd C:/SHARE/arista-mcp
dotnet build
dotnet test
```

Expected: all green.

- [ ] **Step 2: Start compose, verify extensions**

```bash
cd docker && podman-compose up -d postgres && sleep 15
psql -h localhost -p 5434 -U arista arista -c "\dx" | head
```

Expected: vector, vchord, vchord_bm25, pg_tokenizer, pg_trgm visible.

- [ ] **Step 3: Write `CLAUDE.md` skeleton**

```markdown
# arista-mcp — architecture notes

## Quick reference

    podman-compose -f docker/compose.yaml up -d postgres
    dotnet test
    dotnet run --project src/AristaMcp.Cli -- serve --transport stdio

## Layers (strict)

    Cli → Server → Core ← Embedding, Data

Core has no reference to Data/Embedding/Server.

## DO NOT

- Reference Python; arista-mcp is pure .NET.
- Switch `halfvec` back to `vector`; halfvec is 50% smaller at negligible cost.
- Skip `NpgsqlDataSource.UseVector()` — binary COPY of HalfVector depends on it.
- Mention AI/assistant attribution in commits, code, or docs.
```

- [ ] **Step 4: Tag sprint review**

```bash
git add CLAUDE.md
git commit -m "docs: CLAUDE.md quick reference + DO NOT list"
git tag sprint-1-review
```

---

## 🚧 GATE: Sprint 1 Review

- [ ] `dotnet build` clean from repo root
- [ ] `dotnet test` all pass (Core + Data integration)
- [ ] `podman-compose up -d postgres` → healthy within 30 s
- [ ] `psql -c "\dx"` shows all 5 expected extensions
- [ ] EF Core migrations applied; `bm25v` column + index present
- [ ] `HalfVectorRoundtripTest` + `HnswIndexSearchTest` + `Bm25IndexSearchTest` pass
- [ ] `ChunkRepositoryBulkInsertTest` demonstrates <3 s for 1000 rows

---

**Plan continues in:** Sprint 2 (embedding + ingest), Sprint 3 (retrieval + MCP), Sprint 4 (polish + release) — see following files in this directory.

For now, Sprint 1 is fully specified and self-contained. When Sprint 1 gate passes, the engineer or agent runs `/write-plan` again scoped to Sprint 2.
