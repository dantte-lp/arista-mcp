# Архитектура

`arista-mcp` — слоистое .NET 10 приложение с намеренно узкой
поверхностью: одно решение, шесть проектов, одна БД, два MCP-транспорта.

## Карта слоёв

```mermaid
flowchart TB
  subgraph cli["AristaMcp.Cli"]
    CMD["System.CommandLine 2.0<br/>ingest · serve · bench ·<br/>curate-triples · validate-bench-queries"]
  end

  subgraph server["AristaMcp.Server"]
    HOST["ServerHosting<br/><i>общий DI</i>"]
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
    MIG[Миграции]
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

### Строгое правило зависимостей

```
Cli → Server → Core ← Embedding, Data
```

Core **не ссылается** на `Data`, `Embedding` или `Server`. Тесты
могут ссылаться на любой слой. Правило форсится через project-файлы,
а не только конвенцией.

### Зачем нужно разделение

- Core владеет доменным словарём (records, settings) и алгоритмами
  без внешних зависимостей (раскрытие запроса, чанкинг, контракты
  retrieval). Можно менять downstream-проекты без правки доменного
  кода.
- Embedding изолирован, чтобы зависимость от ONNX Runtime (и
  опционально — CUDA runtime) не попадала в весь граф сборки.
  Флаг `-p:UseGpuOnnx=true` переключает на GPU-пакет **только** в
  этом проекте.
- Data содержит все детали SQL / EF Core. `HybridRetriever` живёт в
  Server, потому что выполняет сырой Npgsql — Core не может без
  протяжки Npgsql во всех потребителей.

## Хостинг — два транспорта, один DI-граф

```mermaid
flowchart LR
  subgraph serve["arista-mcp serve"]
    SEL{--transport}
    SEL -->|stdio| H1[StdioHost]
    SEL -->|http| H2[HttpHost]
  end

  ADI["ServerHosting.AddAristaMcpServices<br/>единственный источник истины по DI"]

  H1 -->|Host builder + логи в stderr| ADI
  H2 -->|WebApplication + MapMcp| ADI

  ADI -->|Singleton| S1[IEmbedder]
  ADI -->|Singleton| S2[IReranker]
  ADI -->|Singleton| S3[IHydeExpander]
  ADI -->|Singleton| S4[IHybridRetriever]
  ADI -->|Scoped| S5[Репозитории]
```

Общий DI-метод гарантирует, что фикс в проводке никогда не попадёт
только в один из транспортов.

## Runtime-последовательность — типичный вызов `search_docs`

```mermaid
sequenceDiagram
  autonumber
  participant C as Claude / HTTP-клиент
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
  HY-->>R: HydeResult (raw при disabled)
  par параллельно
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
  RR-->>R: оценённые чанки
  R-->>T: SearchResponse (результаты + диагностика)
  T-->>C: JSON-RPC результат
```

Dense-эмбеддинг и sparse-SQL идут параллельно через `Task.WhenAll`.
RRF-фьюжн и реранк дешёвые — остаются на основной задаче.

## Data-слой — эскиз схемы

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

- `embedding` использует pgvector `halfvec(768)` — вдвое меньше
  `vector(768)` при пренебрежимой потере recall. HNSW-индекс с
  `halfvec_cosine_ops`.
- `bm25v` заполняется триггером PostgreSQL, установленным через
  `tokenizer_catalog.create_custom_model_tokenizer_and_trigger` — в
  колонку не пишем напрямую.

## Версии стека

По состоянию v0.1.4, из `Directory.Packages.props`:

| Пакет                                   | Версия   |
|-----------------------------------------|----------|
| .NET SDK                                | 10.0.201 |
| ModelContextProtocol                    | 1.2.0    |
| EF Core + Npgsql.EFCore + Pgvector.EFCore | 9.0.15 / 9.0.4 / 0.3.0 (держим ради Pgvector) |
| Microsoft.ML.OnnxRuntime                | 1.24.4   |
| Microsoft.ML.Tokenizers                 | 2.0.0    |
| System.CommandLine                      | 2.0.6    |
| PostgreSQL                              | 18 (образ tensorchord/vchord-suite) |
| pgvector / vchord / vchord_bm25 / pg_tokenizer | 0.8.2 / 1.1.1 / 0.3.0 / 0.1.1 |

## Дальше

- [retrieval.md](retrieval.md) — каждый этап пайплайна поиска подробно.
- [getting-started.md](getting-started.md) — как поднять стек.
- [../../CLAUDE.md](../../CLAUDE.md) — операционные нюансы по спринтам.
