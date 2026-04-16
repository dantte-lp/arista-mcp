# arista-mcp Sprint 2 Implementation Plan — Embedding + Ingest

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans.

**Goal:** Ship the embedding layer and ingest pipeline so `arista-mcp ingest` reads the `arista-docs` catalog, chunks, embeds, and persists into the pgvector store.

**Architecture:** `Embedding` ← ONNX Runtime + `BertTokenizer` (WordPiece vocab.txt). `Core` ← Chunker + IngestService. `Cli` adds `ingest` verb.

**Tech Stack:** Microsoft.ML.OnnxRuntime 1.24.4 · Microsoft.ML.Tokenizers 2.0.0 · snowflake-arctic-embed-m-v1.5 (ONNX) · System.CommandLine 2.0.6 · Spectre.Console 0.55.0.

**Reference spec:** `docs/superpowers/specs/2026-04-16-arista-mcp-design.md`
**Sprint 1 plan:** `docs/superpowers/plans/2026-04-16-arista-mcp-implementation.md`

---

## Sprint 2 Overview

| # | Task | Outcome |
|---|------|---------|
| 2.1 | Model assets | `models/` layout + `scripts/fetch-models.ps1` download script |
| 2.2 | Embedder | `IEmbedder` + `OnnxEmbedder` (CPU default, GPU opt-in) + tests |
| 2.3 | Chunker | `IChunker` + `SectionAwareChunker` + tests |
| 2.4 | Catalog loader | Reads `arista-docs/data/catalog.json` + per-doc JSON + MD |
| 2.5 | IngestRun repo | `IIngestRunRepository` on `ingest_runs` table |
| 2.6 | IngestService | Orchestrates loader → chunker → embedder → repos, tracks runs |
| 2.7 | CLI `ingest` | System.CommandLine verb + Spectre progress |
| 2.8 | Sprint gate | Full test run, 5-doc fixture ingest, `sprint-2-review` tag |

**Definition of Done:**
- [ ] `dotnet build` clean on all 10 projects
- [ ] `dotnet test` all green (Sprint 1 + new Sprint 2 tests)
- [ ] Fixture ingest: 5 tiny docs → ≥30 chunks, every chunk has `halfvec(768)` + `bm25v IS NOT NULL`
- [ ] `IncrementalReingestTest` passes (SHA256 early-exit skips untouched docs)
- [ ] `arista-mcp ingest --help` works

**Model download note:** snowflake-arctic-embed-m-v1.5 ONNX is ~436 MB. The fetch script pulls it to `models/` (gitignored). Integration embedder tests skip gracefully when `models/model.onnx` is absent — logic tests use a mock embedder.

---

## Task 2.1: Model assets

**Files:**
- Create: `scripts/fetch-models.ps1`
- Create: `models/.gitkeep`
- Create: `models/README.md`

**Steps:**

1. Write `scripts/fetch-models.ps1` — downloads `model.onnx` (fp32) + `vocab.txt` from HuggingFace:
   - `https://huggingface.co/Snowflake/snowflake-arctic-embed-m-v1.5/resolve/main/onnx/model.onnx`
   - `https://huggingface.co/Snowflake/snowflake-arctic-embed-m-v1.5/resolve/main/vocab.txt`
   - Skips download if file exists + SHA256 matches.
2. Write `models/README.md` — documents expected layout:
   ```
   models/
     embedder/
       model.onnx
       vocab.txt
   ```
3. Commit: `feat(models): fetch script for snowflake-arctic-embed-m-v1.5`

---

## Task 2.2: Embedder

**Files:**
- Create: `src/AristaMcp.Embedding/IEmbedder.cs`
- Create: `src/AristaMcp.Embedding/EmbeddingOptions.cs`
- Create: `src/AristaMcp.Embedding/OnnxEmbedder.cs`
- Create: `src/AristaMcp.Embedding/BertWordPieceTokenizer.cs` (wraps Microsoft.ML.Tokenizers.BertTokenizer for batch)
- Create: `tests/AristaMcp.Embedding.Tests/OnnxEmbedderTests.cs`

**Key facts (verified via research):**
- Inputs: `input_ids`, `attention_mask`, `token_type_ids` (all int64, shape [B, L])
- Output: `last_hidden_state` shape [B, L, 768] — **no pooler_output**, must mean-pool + L2-normalize
- Query prefix: `"Represent this sentence for searching relevant passages: "` (applied only to queries, not documents)
- max_seq_len = 512, hidden = 768, vocab = 30522
- `BertTokenizer.Create(vocabFile, BertOptions)` loads `vocab.txt` (WordPiece) — **not** `tokenizer.json`
- Use `OrtValue.CreateTensorValueFromMemory` + `session.Run(RunOptions, feeds, outputs)` (new style)

### Interface

```csharp
// src/AristaMcp.Embedding/IEmbedder.cs
namespace AristaMcp.Embedding;

public interface IEmbedder : IDisposable
{
    int Dimension { get; }
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool isQuery, CancellationToken ct);
}
```

### Options

```csharp
// src/AristaMcp.Embedding/EmbeddingOptions.cs
namespace AristaMcp.Embedding;

public sealed class EmbeddingOptions
{
    public required string ModelPath { get; init; }
    public required string VocabPath { get; init; }
    public int MaxSequenceLength { get; init; } = 512;
    public int BatchSize { get; init; } = 16;
    public bool Gpu { get; init; }
    public string QueryPrefix { get; init; } = "Represent this sentence for searching relevant passages: ";
}
```

### Tokenizer wrapper (batch, pad, attention mask)

```csharp
// src/AristaMcp.Embedding/BertWordPieceTokenizer.cs
using System.IO;
using Microsoft.ML.Tokenizers;

namespace AristaMcp.Embedding;

public sealed class BertWordPieceTokenizer
{
    private readonly BertTokenizer _tok;

    public int PaddingTokenId => _tok.PaddingTokenId;
    public int ClassificationTokenId => _tok.ClassificationTokenId;
    public int SeparatorTokenId => _tok.SeparatorTokenId;

    public BertWordPieceTokenizer(string vocabPath)
    {
        using var fs = File.OpenRead(vocabPath);
        _tok = BertTokenizer.Create(fs, new BertOptions
        {
            LowerCaseBeforeTokenization = true,
            ApplyBasicTokenization = true,
        });
    }

    // Returns (inputIds [B*L], attentionMask [B*L], actualLength L).
    public (long[] InputIds, long[] AttentionMask, int SeqLen) EncodeBatch(
        IReadOnlyList<string> texts, int maxSeqLen)
    {
        var encoded = new List<int[]>(texts.Count);
        var maxLen = 0;
        foreach (var t in texts)
        {
            var ids = _tok.EncodeToIds(t, addSpecialTokens: true, considerPreTokenization: true).ToArray();
            if (ids.Length > maxSeqLen) ids = ids[..maxSeqLen];
            encoded.Add(ids);
            maxLen = Math.Max(maxLen, ids.Length);
        }

        var seqLen = maxLen;
        var input = new long[texts.Count * seqLen];
        var mask = new long[texts.Count * seqLen];
        for (var b = 0; b < texts.Count; b++)
        {
            var ids = encoded[b];
            for (var i = 0; i < seqLen; i++)
            {
                if (i < ids.Length) { input[(b * seqLen) + i] = ids[i]; mask[(b * seqLen) + i] = 1; }
                else { input[(b * seqLen) + i] = _tok.PaddingTokenId; mask[(b * seqLen) + i] = 0; }
            }
        }
        return (input, mask, seqLen);
    }
}
```

### Embedder

```csharp
// src/AristaMcp.Embedding/OnnxEmbedder.cs
using Microsoft.ML.OnnxRuntime;

namespace AristaMcp.Embedding;

public sealed class OnnxEmbedder : IEmbedder
{
    private readonly InferenceSession _session;
    private readonly BertWordPieceTokenizer _tok;
    private readonly EmbeddingOptions _opt;
    private bool _disposed;

    public int Dimension => 768;

    public OnnxEmbedder(EmbeddingOptions opt)
    {
        _opt = opt;
        _tok = new BertWordPieceTokenizer(opt.VocabPath);

        var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Environment.ProcessorCount,
        };
        if (opt.Gpu) so.AppendExecutionProvider_CUDA();
        _session = new InferenceSession(opt.ModelPath, so);
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool isQuery, CancellationToken ct)
    {
        var prepped = isQuery ? texts.Select(t => _opt.QueryPrefix + t).ToList() : texts.ToList();
        var results = new float[texts.Count][];

        for (var batchStart = 0; batchStart < prepped.Count; batchStart += _opt.BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = prepped.Skip(batchStart).Take(_opt.BatchSize).ToList();
            var embedded = await Task.Run(() => EmbedBatch(batch), ct).ConfigureAwait(false);
            for (var i = 0; i < embedded.Length; i++) results[batchStart + i] = embedded[i];
        }
        return results;
    }

    private float[][] EmbedBatch(IReadOnlyList<string> batch)
    {
        var (input, mask, seqLen) = _tok.EncodeBatch(batch, _opt.MaxSequenceLength);
        var tokenTypes = new long[input.Length]; // all zeros
        long[] shape = [batch.Count, seqLen];

        using var idsVal = OrtValue.CreateTensorValueFromMemory(input, shape);
        using var maskVal = OrtValue.CreateTensorValueFromMemory(mask, shape);
        using var tttVal = OrtValue.CreateTensorValueFromMemory(tokenTypes, shape);
        var feeds = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
        {
            ["input_ids"] = idsVal,
            ["attention_mask"] = maskVal,
            ["token_type_ids"] = tttVal,
        };

        using var results = _session.Run(new RunOptions(), feeds, ["last_hidden_state"]);
        var hidden = results[0].GetTensorDataAsSpan<float>(); // [B, L, 768]

        var outputs = new float[batch.Count][];
        for (var b = 0; b < batch.Count; b++)
        {
            var vec = new float[Dimension];
            float maskSum = 0;
            for (var t = 0; t < seqLen; t++)
            {
                if (mask[(b * seqLen) + t] == 0) continue;
                maskSum += 1;
                var rowStart = ((b * seqLen) + t) * Dimension;
                for (var d = 0; d < Dimension; d++) vec[d] += hidden[rowStart + d];
            }
            if (maskSum > 0) for (var d = 0; d < Dimension; d++) vec[d] /= maskSum;

            // L2 normalize
            double sq = 0;
            for (var d = 0; d < Dimension; d++) sq += vec[d] * vec[d];
            var norm = (float)Math.Sqrt(sq);
            if (norm > 1e-9f) for (var d = 0; d < Dimension; d++) vec[d] /= norm;
            outputs[b] = vec;
        }
        return outputs;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _session.Dispose();
        _disposed = true;
    }
}
```

### Tests

Two kinds:
- **Unit** (always run): a `MockEmbedder : IEmbedder` returning deterministic vectors; used by ingest tests.
- **Integration** (skipped if `models/embedder/model.onnx` absent): loads real ONNX, asserts dimension = 768 and L2 norm ≈ 1.0 on a single string.

```csharp
// tests/AristaMcp.Embedding.Tests/OnnxEmbedderTests.cs
using AristaMcp.Embedding;
using FluentAssertions;
using Xunit;

namespace AristaMcp.Embedding.Tests;

public class OnnxEmbedderTests
{
    private static string? FindModelsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "models", "embedder", "model.onnx");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "models", "embedder");
            dir = dir.Parent;
        }
        return null;
    }

    [SkippableFact]
    public async Task EmbedsA768DimensionalUnitVector()
    {
        var modelsDir = FindModelsDir();
        Skip.If(modelsDir is null, "models/embedder/model.onnx not present; run scripts/fetch-models.ps1");

        using var embedder = new OnnxEmbedder(new EmbeddingOptions
        {
            ModelPath = Path.Combine(modelsDir!, "model.onnx"),
            VocabPath = Path.Combine(modelsDir!, "vocab.txt"),
        });

        var vecs = await embedder.EmbedAsync(["hello world"], isQuery: false, CancellationToken.None);
        vecs[0].Length.Should().Be(768);

        double norm = 0;
        foreach (var v in vecs[0]) norm += v * v;
        Math.Sqrt(norm).Should().BeApproximately(1.0, 1e-4);
    }
}
```

Uses `Xunit.SkippableFact` via the `Xunit.SkippableFact` package; we add that to CPM and the Embedding.Tests csproj.

---

## Task 2.3: Chunker

**Files:**
- Create: `src/AristaMcp.Core/Chunking/IChunker.cs`
- Create: `src/AristaMcp.Core/Chunking/SectionAwareChunker.cs`
- Create: `src/AristaMcp.Core/Chunking/Section.cs` (record: Title, Level, Content, PageStart, PageEnd)
- Create: `tests/AristaMcp.Core.Tests/Chunking/SectionAwareChunkerTests.cs`

Chunking rules (from spec):
- Target ~512 tokens, max 1200, overlap 64, min 40
- Embed-facing `Content` = `"{doc.title} > {section.title}\n\n{raw}"` prefix
- `RawContent` = unprefixed for display/BM25
- Chunk at section boundaries where feasible, otherwise split oversized sections with overlap
- Token count is approximate (word split ×1.3 heuristic); exact tokenization happens at embed time

Key test shapes:
- Single short section → 1 chunk
- Section of 2000 approx-tokens → ~4 chunks with overlap_tokens between them
- Very small section (<min) → merged with next sibling under same parent

---

## Task 2.4: Catalog + Document Loader

**Files:**
- Create: `src/AristaMcp.Core/Catalog/CatalogEntry.cs` (matches arista-docs/data/catalog.json entry shape)
- Create: `src/AristaMcp.Core/Catalog/CatalogReader.cs` (JSON deserialize + SHA256 of raw file)
- Create: `src/AristaMcp.Core/Catalog/DocumentLoader.cs` (read `{slug}.json` + `{slug}.md`, return domain model with Sections)

The `arista-docs` contract:
- Root: `catalog.json` — array of entries: `{ id, url, category, product, version, slug, title, md_path, json_path, pdf_sha256, … }`
- Per-doc JSON: `{slug}.json` with `toc`, `sections`, `title`, `image_names` (enriched in arista-docs v0.1.3)
- MD file: `{slug}.md` — possibly chunked with `{page}----` markers (EOS-User-Manual)

`DocumentLoader.LoadAsync(entry)` returns the `AristaDocument` domain model + a list of `Section` with `Content` and `PageStart`/`PageEnd`.

---

## Task 2.5: IngestRun repo

**Files:**
- Create: `src/AristaMcp.Data/Repositories/IIngestRunRepository.cs`
- Create: `src/AristaMcp.Data/Repositories/IngestRunRepository.cs`

Methods:
- `StartAsync(catalogSha256, ct)` → creates a row with `status='running'`, returns the entity
- `FinishAsync(id, status, docsTotal/Skipped/Upserted, chunksUpserted, errorMsg, ct)`
- `GetLastAsync(ct)` → latest row
- `GetLastSuccessfulCatalogSha256Async(ct)` → for incremental check

---

## Task 2.6: IngestService

**Files:**
- Create: `src/AristaMcp.Core/Ingest/IngestOptions.cs` (DryRun, Force, Category filter, Verbose)
- Create: `src/AristaMcp.Core/Ingest/IIngestProgress.cs` (simple progress-reporter interface for Spectre wiring)
- Create: `src/AristaMcp.Core/Ingest/IngestService.cs`
- Create: `tests/AristaMcp.Core.Tests/Ingest/IngestServiceTest.cs` (uses MockEmbedder + fixtures)

Flow:
1. Read `catalog.json`, compute SHA256.
2. If not `Force` and SHA256 == last successful run → return early, mark run `skipped`.
3. For each entry (filter by Category if set):
   a. Check `DocumentRepository.GetPdfSha256Async(id)` — if equal to entry's `pdf_sha256`, skip.
   b. Otherwise: `DocumentLoader.LoadAsync`, `Chunker.Chunk`, `Embedder.EmbedAsync`, `DocumentRepository.UpsertAsync`, `ChunkRepository.DeleteByDocumentAsync` + `BulkInsertAsync`.
4. `IngestRunRepository.FinishAsync` with aggregated counts.

Test harness creates 5 tiny docs on disk, drives the service with `MockEmbedder`, asserts:
- ≥30 chunks in DB
- Every chunk has non-null `bm25v` (trigger fired)
- Re-running without changes → 0 docs upserted (incremental)
- Touching one doc's SHA → exactly 1 doc re-ingested

---

## Task 2.7: CLI `ingest` verb

**Files:**
- Modify: `src/AristaMcp.Cli/Program.cs` — wire System.CommandLine root + `ingest` command
- Create: `src/AristaMcp.Cli/Commands/IngestCommand.cs`
- Create: `src/AristaMcp.Cli/Progress/SpectreIngestProgress.cs`
- Create: `tests/AristaMcp.Cli.Tests/IngestCommandTests.cs` (new project; or fold into AristaMcp.Server.Tests? → keep separate for CLI-specific tests)

`arista-mcp ingest` flags:
- `--catalog <path>` (default `../arista-docs/data/catalog.json`)
- `--force`
- `--dry-run`
- `--category <name>`
- `--verbose`
- `--models <dir>` (embedder/reranker paths)

Wire DI: `IServiceCollection` registers `AristaDbContext`, `NpgsqlDataSource`, repos, `OnnxEmbedder`, `IngestService`. Options bound from env (`ARISTA_MCP__*`) + JSON file if present.

---

## Task 2.8: Sprint 2 gate + tag

1. `dotnet build` — clean
2. `ARISTA_MCP_TEST_CS=Host=…` `dotnet test` — green
3. Fixture ingest:
   - Create `tests/AristaMcp.Core.Tests/Fixtures/fake-catalog/` with 5 tiny MD + JSON docs
   - Run `IngestServiceTest.IngestsFiveDocs_ProducesAtLeast30Chunks` against the shared postgres
4. Update `CLAUDE.md` with Sprint 2 additions (embedder/chunker/ingest conventions)
5. `git tag sprint-2-review`

## Gate checklist

- [ ] All src builds clean
- [ ] All tests green (Core + Data + Embedding + Cli + any E2E)
- [ ] Fixture ingest produces ≥30 chunks with halfvec + bm25v populated
- [ ] Incremental re-ingest skips unchanged docs
- [ ] `arista-mcp ingest --help` renders
- [ ] `sprint-2-review` tag exists
