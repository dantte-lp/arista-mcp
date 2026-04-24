# Документация arista-mcp

Русскоязычный набор документации. Английский оригинал в
[`../en/`](../en/README.md).

Структура следует подходу [Diátaxis](https://diataxis.fr/):

| Страница                                  | Квадрант Diátaxis | Нужно…                                               |
|-------------------------------------------|-------------------|-------------------------------------------------------|
| [getting-started.md](getting-started.md)  | Tutorial          | Поднять стек на ноутбуке за 15 минут                  |
| [architecture.md](architecture.md)        | Explanation       | Понять, как устроены слои и процессы                  |
| [retrieval.md](retrieval.md)              | Explanation       | Понять, как запрос превращается в ранжированный ответ |
| [mcp-tools.md](mcp-tools.md)              | Reference         | Найти схемы входа / выхода MCP-инструментов           |
| [benchmarking.md](benchmarking.md)        | How-to            | Запускать и интерпретировать бенчи, расширять v2-набор |
| [development.md](development.md)          | How-to            | Собирать, тестировать, контрибьютить                  |
| [releasing.md](releasing.md)              | How-to            | Как делать релиз (CHANGELOG roll, тег, GitHub Release) |

Дополнительные документы (не переводились):

- [`../mcp-integration.md`](../mcp-integration.md) — настройка Claude Desktop / Claude Code / прямой JSON-RPC.
- [`../onnx-provider.md`](../onnx-provider.md) — переключение CPU ↔ GPU ONNX Runtime на этапе сборки.
- [`../otel.md`](../otel.md) — OpenTelemetry + Jaeger-сборка в compose.

## Статус проекта

- **v0.1.4** (2026-04-23) — выпущено. Стоковый cross-encoder реранкер на 111-запросном бенче.
- **v0.3.0 в работе** — расширенный 588-запросный chunk-ID бенч готов;
  цель — top-1 ≥ 95 % при serve на CPU.
- Полная история версий в [CHANGELOG.md](../../CHANGELOG.md) в корне,
  спринт-планы — в [`../superpowers/plans/`](../superpowers/plans/).
