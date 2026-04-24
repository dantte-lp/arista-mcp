# Справочник MCP-инструментов

Сервер экспонирует пять инструментов в MCP-неймспейсе `arista`. Все
классы живут в `src/AristaMcp.Server/Tools/` как
`[McpServerToolType]` с ctor-DI.

| Инструмент            | Назначение                                                          |
|-----------------------|----------------------------------------------------------------------|
| [`search_docs`](#search_docs)       | Гибридный поиск — ранжированные чанки + опц. диагностика |
| [`lookup_section`](#lookup_section) | Полный текст именованной секции                         |
| [`list_documents`](#list_documents) | Фильтр документов по category / product                 |
| [`get_document`](#get_document)     | Полные метаданные + кол-во чанков для одного документа  |
| [`get_status`](#get_status)         | Health / статистика                                     |

Runtime-поток:

```mermaid
flowchart LR
  C[MCP-клиент] -->|tools/list| SRV[MCP-сервер]
  C -->|tools/call| SRV
  SRV --> SD[search_docs]
  SRV --> LS[lookup_section]
  SRV --> LD[list_documents]
  SRV --> GD[get_document]
  SRV --> GS[get_status]
  SD --> HR[HybridRetriever]
  LS --> REPO[ChunkRepository]
  LD --> REPO
  GD --> REPO
  GS --> REPO
```

## `search_docs`

Гибридный dense + sparse + rerank поиск.

**Вход**

| Поле              | Тип       | Дефолт  | Примечания                                              |
|-------------------|-----------|---------|----------------------------------------------------------|
| `query`           | string    | —       | Обязательный.                                            |
| `limit`           | int       | 5       | Размер финальной страницы. Ограничено 1–50.              |
| `category`        | string?   | null    | `manual`, `release-notes`, `kb`, `portal` и т.п.         |
| `product`         | string?   | null    | `eos`, `cvp`, `dmf`, `cva`, `cvw`, `hardware` и т.п.     |
| `candidatePoolSize` | int     | 50      | Пул на ranker перед RRF-фьюжном.                         |
| `rerankTopN`      | int       | 30      | Потолок глубины cross-encoder. Адаптивный floor = 10.    |
| `dedupPerSection` | bool      | false   | Выкинуть дубли чанков из одной секции.                   |
| `returnDiagnostics` | bool    | false   | Включить `SearchDiagnostics` в ответ.                    |

**Выход** — `SearchResponse`:

```jsonc
{
  "results": [
    {
      "chunkId": 12345,
      "documentId": "abc123",
      "documentTitle": "Arista Switch 7050X3 Series Data Sheet",
      "documentSlug": "7050X3-Datasheet",
      "category": "manual",
      "product": "hardware",
      "sectionTitle": "MLAG configuration",
      "sectionLevel": 2,
      "pageStart": 42,
      "pageEnd": 44,
      "content": "...",           // включает префикс "{doc} > {section}\n\n"
      "rawContent": "...",         // display-safe
      "rerankScore": 9.81,
      "denseSimilarity": 0.87,
      "bm25Score": 4.2
    }
  ],
  "diagnostics": {                 // только при returnDiagnostics=true
    "denseHits": 50,
    "sparseHits": 43,
    "afterRrf": 78,
    "afterRerank": 30,
    "embedMs": 11.4,
    "denseQueryMs": 7.3,
    "sparseQueryMs": 5.1,
    "rrfMs": 0.2,
    "rerankMs": 48.2,
    "totalMs": 73.1,
    "hydeMs": 0,
    "hydeHit": false,
    "hydeFallback": false
  }
}
```

**Пример**

```json
{
  "method": "tools/call",
  "params": {
    "name": "search_docs",
    "arguments": {
      "query": "MLAG peer-link configuration on 7050X3",
      "limit": 5,
      "product": "eos",
      "returnDiagnostics": true
    }
  }
}
```

**Паттерны запросов, которые работают хорошо**

- Естественно-языковые вопросы: *"Как настроить BGP EVPN type-5 routes?"*
- Одиночный концепт + платформа: *"OSPF single-area campus design"*
- Акронимы: раскрываются автоматически через `QueryExpander`.
- Model-номера: использовать фильтр `product` или включать SKU в
  запрос.

## `lookup_section`

Полный текст именованной секции по её чанкам.

**Вход**

| Поле               | Тип      | Дефолт  | Примечания                             |
|--------------------|----------|---------|-----------------------------------------|
| `documentId`       | string   | —       | Обязательный.                           |
| `sectionTitle`     | string   | —       | Регистронезависимый exact match.        |

**Выход**

```json
{
  "documentId": "abc123",
  "documentTitle": "...",
  "sectionTitle": "MLAG configuration",
  "content": "...",    // склеено по всем чанкам секции
  "pageStart": 42,
  "pageEnd": 44,
  "chunkCount": 3
}
```

## `list_documents`

Фильтр документов с опциональными предикатами category / product.

**Вход**

| Поле       | Тип     | Дефолт  | Примечания                              |
|------------|---------|---------|-----------------------------------------|
| `category` | string? | null    |                                          |
| `product`  | string? | null    |                                          |
| `limit`    | int     | 50      | Ограничено 1–500.                        |
| `offset`   | int     | 0       |                                          |

**Выход** — массив `{id, title, slug, category, product, pages, chunkCount}`.

## `get_document`

Полные метаданные + количество чанков для одного документа.

**Вход** — `{documentId: string}`.

**Выход** — `{id, url, title, slug, category, product, version, pages,
size_bytes, image_count, section_count, toc_count, tags, chunkCount,
downloadedAt, convertedAt}`.

## `get_status`

Операционный снимок.

**Выход**

```json
{
  "chunkCount": 59356,
  "documentCount": 2427,
  "lastIngestRun": {
    "startedAt": "2026-04-23T13:02:05Z",
    "finishedAt": "2026-04-23T13:27:38Z",
    "outcome": "success",
    "documentsSeen": 2427,
    "documentsUpserted": 2427,
    "chunksInserted": 59356
  },
  "embedderModel": "snowflake-arctic-embed-m-v1.5",
  "embedderVariant": "fp32",
  "rerankerFamily": "BertWordPiece",
  "serverVersion": "0.1.4"
}
```

## Обработка ошибок

Все инструменты возвращают MCP-стандартные error-пейлоады при сбое:

- `-32602` — invalid params (отсутствует `query`, отрицательный `limit`, …).
- `-32603` — internal error (БД недоступна, embedder model отсутствует).

`search_docs` gracefully деградирует, если модель реранкера отсутствует
(fallback на `NoopReranker`), так что неполный локальный сетап всё
равно возвращает *какой-то* результат, а не hard error.
