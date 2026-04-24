# arista-mcp documentation

English documentation set. Russian translation under
[`../ru/`](../ru/README.md).

Organised loosely along the [Diátaxis](https://diataxis.fr/) axes:

| Page                                      | Diátaxis quadrant | You want to…                                        |
|-------------------------------------------|-------------------|------------------------------------------------------|
| [getting-started.md](getting-started.md)  | Tutorial          | Bring the stack up on a laptop in 15 minutes          |
| [architecture.md](architecture.md)        | Explanation       | Understand how the layers + processes fit together    |
| [retrieval.md](retrieval.md)              | Explanation       | Understand how a query becomes a ranked answer        |
| [mcp-tools.md](mcp-tools.md)              | Reference         | Look up tool input / output schemas                   |
| [benchmarking.md](benchmarking.md)        | How-to            | Run / interpret benches, extend the v2 query set      |
| [development.md](development.md)          | How-to            | Build, test, contribute changes                       |
| [releasing.md](releasing.md)              | How-to            | Cut a release (CHANGELOG roll, tag, GitHub Release)   |

External, not re-written:

- [`../mcp-integration.md`](../mcp-integration.md) — full Claude Desktop / Claude Code / raw JSON-RPC setup.
- [`../onnx-provider.md`](../onnx-provider.md) — CPU vs GPU ONNX Runtime build switch.
- [`../otel.md`](../otel.md) — OpenTelemetry wiring + Jaeger compose.

## Project status

- **v0.1.4** (2026-04-23) — shipped. Stock cross-encoder reranker on the
  111-query bench set.
- **v0.3.0 in progress** — expanded 588-query chunk-ID bench landed; target
  top-1 ≥ 95 % on CPU-only serve.
- See the root [CHANGELOG.md](../../CHANGELOG.md) for version history and
  [`../superpowers/plans/`](../superpowers/plans/) for the sprint plans.
