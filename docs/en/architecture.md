# Architecture

`arista-mcp` is a layered .NET 10 application with a deliberately small
surface area: one solution, six projects, one database, two MCP transports.

## Layer map

```mermaid
flowchart TB
  subgraph cli["AristaMcp.Cli"]
    CMD["System.CommandLine 2.0<br/>ingest · serve · bench ·<br/>curate-triples · validate-bench-queries"]
  end

  subgraph server["AristaMcp.Server"]
    HOST["ServerHosting<br/><i>shared DI</i>"]
    STDIO[StdioHost]
    HTTP["HttpHost<br/><i>MapMcp, Stateless</i>"]
    RETR[HybridRetriever]
    TOOLS["5 × [McpServerToolType]"]
  end

  subgraph core["AristaMcp.Core"]
    MODELS["Records<br/>ChunkResult · SearchResponse · SearchDiagnostics"]
    RET["QueryExpander ·<br/>IReranker · NoopReranker ·<br/>IHydeExpander · NoopHydeExpander"]
    CHK["IChunker · SectionAwareChunker"]
    LD["CatalogReader · DocumentLoader"]
    SET[AristaMcpSettings · ModelPaths · HydeSettings]
  end

  subgraph emb["AristaMcp.Embedding"]
    OEM[OnnxEmbedder]
    OR[OnnxReranker]
    XR[XlmRobertaOnnxReranker]
    TK[BertWordPieceTokenizer<br/>XlmRobertaRerankerTokenizer]
  end

  subgraph dat["AristaMcp.Data"]
    CTX["AristaDbContext<br/>EF Core 9"]
    REPO["DocumentRepository ·<br/>ChunkRepository · IngestRunRepository"]
    MIG[Migrations]
  end

  CMD --> HOST
  HOST --> STDIO
  HOST --> HTTP
  STDIO --> TOOLS
  HTTP --> TOOLS
  TOOLS --> RETR
  RETR --> RET
  RETR --> OEM
  RETR --> OR
  OR --> TK
  XR --> TK
  RETR --> CTX
  CTX --> REPO
  HOST --> SET
```

### Strict dependency rule

```
Cli → Server → Core ← Embedding, Data
```

Core has **no reference** to `Data`, `Embedding` or `Server`. Tests may reference
any layer. The rule is enforced by project files, not just convention.

### Why the split matters

- Core owns the domain vocabulary (records, settings) and the
  framework-free algorithms (query expansion, chunking, retrieval
  contracts). Swap any downstream project without editing domain code.
- Embedding is isolated so the ONNX Runtime dependency (and optionally
  the CUDA runtime) stays out of the rest of the build graph. The
  `-p:UseGpuOnnx=true` build switch picks the GPU package *only* in
  this project.
- Data holds every SQL / EF Core detail. `HybridRetriever` lives in
  Server because it issues raw Npgsql — Core couldn't without pulling
  Npgsql into every consumer.

## Hosting — two transports, one DI graph

```mermaid
flowchart LR
  subgraph serve["arista-mcp serve"]
    SEL{--transport}
    SEL -->|stdio| H1[StdioHost]
    SEL -->|http| H2[HttpHost]
  end

  ADI["ServerHosting.AddAristaMcpServices<br/>single source of DI truth"]

  H1 -->|Host builder + console logging to stderr| ADI
  H2 -->|WebApplication + MapMcp| ADI

  ADI -->|Singleton| S1[IEmbedder]
  ADI -->|Singleton| S2[IReranker]
  ADI -->|Singleton| S3[IHydeExpander]
  ADI -->|Singleton| S4[IHybridRetriever]
  ADI -->|Scoped| S5[Repositories]
```

Keeping the two hosts on a shared DI method means a bug fix in wiring
never lands in only one of the transports.

## Runtime sequence — a typical `search_docs` call

```mermaid
sequenceDiagram
  autonumber
  participant C as Claude / HTTP client
  participant T as search_docs tool
  participant R as HybridRetriever
  participant QE as QueryExpander
  participant HY as IHydeExpander
  participant E as OnnxEmbedder
  participant DB as PostgreSQL 18
  participant RR as IReranker

  C->>T: tools/call search_docs {query: "MLAG peer-link"}
  T->>R: SearchAsync(query, options)
  R->>QE: Expand(query)
  R->>HY: ExpandAsync(expanded.Expanded)
  HY-->>R: HydeResult (raw query if disabled)
  par parallel
    R->>E: EmbedAsync([denseQuery], isQuery: true)
    E-->>R: HalfVector[768]
  and
    R->>DB: SELECT ... ORDER BY bm25v <&> to_bm25query(...)
    DB-->>R: sparseRows
  end
  R->>DB: SELECT ... ORDER BY embedding <=> $1::halfvec
  DB-->>R: denseRows
  R->>R: Reciprocal Rank Fusion, k=60
  R->>RR: RerankAsync(query, top-N)
  RR-->>R: scored chunks
  R-->>T: SearchResponse (results + diagnostics)
  T-->>C: JSON-RPC result
```

Dense embedding and sparse SQL run in parallel via `Task.WhenAll`. RRF fusion
and rerank are cheap enough to stay on the main task.

## Data layer — schema sketch

```mermaid
erDiagram
  documents ||--o{ chunks : "1:N"
  ingest_runs ||--o{ documents : "logs"

  documents {
    text id PK
    text url
    text category
    text product
    text title
    text slug
    jsonb tags
    int pages
    int section_count
  }

  chunks {
    bigint id PK
    text document_id FK
    int chunk_index
    text content
    text raw_content
    text section_title
    halfvec embedding "halfvec(768)"
    bm25vector bm25v "populated by trigger"
  }

  ingest_runs {
    bigint id PK
    timestamptz started_at
    timestamptz finished_at
    text outcome
    int documents_seen
    int documents_upserted
    int chunks_inserted
  }
```

- `embedding` uses pgvector `halfvec(768)` — half the size of `vector(768)`
  at negligible recall cost. HNSW index with `halfvec_cosine_ops`.
- `bm25v` is populated by a postgres trigger from `tokenizer_catalog
  .create_custom_model_tokenizer_and_trigger` — you don't write to it
  directly.

## Tech stack version pins

As of v0.1.4, from `Directory.Packages.props`:

| Package                                 | Version  |
|-----------------------------------------|----------|
| .NET SDK                                | 10.0.201 |
| ModelContextProtocol                    | 1.2.0    |
| EF Core + Npgsql.EFCore + Pgvector.EFCore | 9.0.15 / 9.0.4 / 0.3.0 (held for Pgvector compat) |
| Microsoft.ML.OnnxRuntime                | 1.24.4   |
| Microsoft.ML.Tokenizers                 | 2.0.0    |
| System.CommandLine                      | 2.0.6    |
| PostgreSQL                              | 18 (tensorchord/vchord-suite image) |
| pgvector / vchord / vchord_bm25 / pg_tokenizer | 0.8.2 / 1.1.1 / 0.3.0 / 0.1.1 |

## Next

- [retrieval.md](retrieval.md) — every stage of the search pipeline in detail.
- [getting-started.md](getting-started.md) — bring the stack up.
- [../CLAUDE.md](../../CLAUDE.md) — operational gotchas per sprint.
