# Быстрый старт

Поднять стек на рабочем ноутбуке за ~15 минут.

## Требования

- **.NET SDK 10.0.201+** (пинятся через `global.json`).
- **Podman** с `podman compose`, либо Docker Compose v2. WSL2 или
  нативный Linux; macOS не тестировался.
- **PowerShell 7+** — `scripts/fetch-models.ps1` работает кросс-платформенно.
- **~5 GB свободного места** — ONNX-модели (~530 MB), данные PostgreSQL
  (1–3 GB для полного каталога), build output.
- Опционально: **CUDA 12+**, если хочется собирать с
  `-p:UseGpuOnnx=true` для GPU-ускоренного ingest.
- Опционально: каталог **`arista-docs`** рядом — нужен, чтобы гонять
  `ingest` на реальном контенте. Схема нормально работает пустой для
  smoke-тестов.

## 1 — PostgreSQL с vector + BM25 расширениями

```bash
podman compose -f docker/compose.yaml up -d postgres
```

Образ — `tensorchord/vchord-suite:pg18-latest`, с предустановленными
pgvector, vchord, vchord_bm25 и pg_tokenizer. Compose-файл
правильно прописывает `shared_preload_libraries`.

Проверить:

```bash
podman exec arista-mcp-postgres psql -U arista -d arista \
  -c "SELECT extversion FROM pg_extension WHERE extname='vector';"
```

### Нюанс Windows / Podman

Дефолтный Podman на WSL2 биндит опубликованные порты на `127.0.0.1`
внутри WSL-VM, недостижимо с Windows-хоста. Одноразовый фикс:

```powershell
podman machine stop
podman machine set --user-mode-networking
podman machine start
```

Альтернатива: `docker/compose.yaml` уже публикует PostgreSQL на
`0.0.0.0:5434` внутри WSL-дистра, так что адаптер `vEthernet (WSL)`
может до него достучаться.

## 2 — Скачать ONNX-модели

```bash
pwsh scripts/fetch-models.ps1
```

Качает в `models/`:

- `embedder/model.onnx` + `vocab.txt` — `snowflake-arctic-embed-m-v1.5`
  (~436 MB).
- `reranker/model.onnx` + `vocab.txt` — `ms-marco-MiniLM-L6-v2`
  (~91 MB).
- `llm/qwen2.5-1.5b-instruct-q4_k_m.gguf` — Qwen2.5-1.5B Q4_K_M для
  опционального HyDE (~1 GB).

Скрипт идемпотентный — повторный запуск пропускает файлы, которые
уже прошли проверку минимального размера.

## 3 — Схема

```bash
dotnet ef database update \
  --project src/AristaMcp.Data \
  --startup-project src/AristaMcp.Data
```

Ставит `documents`, `chunks` (с `halfvec(768)` embedding +
`bm25v bm25vector` + BM25-триггером), `ingest_runs`, HNSW
`halfvec_cosine_ops` и `idx_chunks_bm25`.

## 4 — Ingest

Указываем `arista-mcp` на catalog.json от `arista-docs` и отдаём
на чанкинг + эмбеддинг + upsert:

```bash
# Полный каталог — ~25 мин на 12-ядерном CPU
dotnet run --project src/AristaMcp.Cli -- ingest \
  --catalog ../arista-docs/catalog.json

# Или одну категорию для итерации
dotnet run --project src/AristaMcp.Cli -- ingest \
  --catalog ../arista-docs/catalog.json \
  --category avd
```

Флаги:

- `--force` — игнорировать incremental-skip по SHA256 и переингестить.
- `--dry-run` — вывести что бы переингестилось без записи.
- `--verbose` — прогресс на уровне чанков.

## 5 — Запустить MCP-сервер

### Stdio (Claude Desktop / Claude Code)

```bash
dotnet run --project src/AristaMcp.Cli -- serve --transport stdio
```

Добавить в `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "arista": {
      "command": "dotnet",
      "args": [
        "run", "--project", "/абсолютный/путь/arista-mcp/src/AristaMcp.Cli",
        "--no-build", "--",
        "serve", "--transport", "stdio"
      ]
    }
  }
}
```

Stdio пишет **логи только в stderr** — stdout это MCP-транспорт.

### HTTP (curl / сырые клиенты)

```bash
dotnet run --project src/AristaMcp.Cli -- serve --transport http --port 8080
```

Streamable HTTP на `http://127.0.0.1:8080/`, stateless.

```bash
curl -X POST http://127.0.0.1:8080/mcp \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

Ещё рецепты в [`../mcp-integration.md`](../mcp-integration.md).

## 6 — Smoke-тест retrieval

```bash
dotnet run --project src/AristaMcp.Cli -- bench \
  --queries tests/fixtures/bench-queries-v2.json \
  --limit 10 \
  --history tests/fixtures/bench-history.jsonl \
  --label my-first-bench
```

Ожидай top-1 около **90.82 %** на v2-наборе против стокового MiniLM
реранкера. Полная методология в [benchmarking.md](benchmarking.md).

## Справочник по конфигурации

Каждое свойство `AristaMcpSettings` можно переопределить переменной
окружения (префикс `ARISTA_MCP__`, `__` для вложенности) или
`arista-mcp.json` в рабочей директории. Частые override:

| Env var                                 | Дефолт                                                             |
|-----------------------------------------|--------------------------------------------------------------------|
| `ARISTA_MCP__ConnectionString`          | `Host=localhost;Port=5434;Database=arista;Username=arista;Password=arista` |
| `ARISTA_MCP__ModelsDir`                 | `models`                                                            |
| `ARISTA_MCP__EmbeddingVariant`          | `fp32` (`fp16` даёт 1.5–2× CPU ускорение при ≤1 pp nDCG)            |
| `ARISTA_MCP__Gpu`                       | `false`                                                             |
| `ARISTA_MCP__HttpPort`                  | `8080`                                                              |
| `ARISTA_MCP__Hyde__Enabled`             | `false` — `true` включает HyDE через llm-sidecar                    |
| `ARISTA_MCP__Hyde__Endpoint`            | `http://127.0.0.1:8090/v1/chat/completions`                         |
| `ARISTA_MCP__Otel__Endpoint`            | не задано — `http://localhost:4317` для OTLP-экспорта в Jaeger      |

## Изоляция тестовой и прод-БД

Интеграционный + E2E наборы используют отдельную `arista_test`.
`PgvectorFixture` создаёт её при первом использовании и **отказывается
работать против любой БД, имя которой не кончается на `_test`** —
guard спас нас от инцидента "release-gate wipes prod" в Sprint 5 и
намеренно строгий.

```bash
podman exec arista-mcp-postgres psql -U arista -d arista      -c "SELECT COUNT(*) FROM chunks;"
podman exec arista-mcp-postgres psql -U arista -d arista_test -c "SELECT COUNT(*) FROM chunks;"
```

## Дальше

- [architecture.md](architecture.md) — как устроены проекты и runtime.
- [retrieval.md](retrieval.md) — как запрос становится результатом.
- [mcp-tools.md](mcp-tools.md) — схемы и примеры пейлоадов по инструментам.
- [development.md](development.md) — сборка, тесты, вклад.
