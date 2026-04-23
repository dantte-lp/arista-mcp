# Observability — traces via OpenTelemetry

arista-mcp emits distributed-tracing spans via the `AristaMcp` `ActivitySource`.
Export is opt-in: set an OTLP endpoint env var and spans flow; leave it unset
and the pipeline runs with zero exporter overhead.

## Quick start with Jaeger

Bring up Jaeger alongside the postgres container:

```bash
podman compose -f docker/compose.yaml -f docker/compose.otel.yaml up -d
```

Run the server with the OTLP endpoint pointing at Jaeger:

```bash
ARISTA_MCP__Otel__Endpoint=http://localhost:4317 \
  dotnet run --project src/AristaMcp.Cli -- serve --transport http --port 8080
```

Open <http://localhost:16686>, pick Service `arista-mcp`, hit **Find Traces**.
Issue one `tools/call search_docs` from an MCP client and the span tree shows
up within a second.

## What's instrumented

Spans:

| Operation                   | Parent      | Where                             |
|-----------------------------|-------------|-----------------------------------|
| `search.hybrid`             | root        | `HybridRetriever.SearchAsync`     |
| `search.embed`              | search.hybrid | cache miss → ONNX inference     |
| `search.dense`              | search.hybrid | pgvector HNSW scan              |
| `search.sparse`             | search.hybrid | vchord_bm25 scan                |
| `search.rerank`             | search.hybrid | cross-encoder pass              |
| `ingest.document`           | root        | `IngestService` per-doc loop      |
| `ingest.subbatch`           | ingest.document | sub-batch embed+BulkInsert    |

Tags (OTel attribute names, dotted lowercase):

- `arista.query.length`, `arista.cache.hit`
- `arista.category`, `arista.product`
- `arista.dense.hits`, `arista.sparse.hits`
- `arista.rerank.topn`, `arista.rerank.adaptive`
- `arista.doc.id`, `arista.doc.slug`, `arista.chunk.count`
- `arista.subbatch.index`, `arista.subbatch.total`

## Configuration

Two env vars are recognised:

- `ARISTA_MCP__Otel__Endpoint` — arista-specific. Set to e.g.
  `http://localhost:4317` to send OTLP/gRPC to Jaeger.
- `OTEL_EXPORTER_OTLP_ENDPOINT` — OTel spec standard. If neither is set,
  `OtelConfig.IsEnabled()` returns false and no OpenTelemetry services are
  registered — the `ActivitySource` stays headless.

Setting either one triggers registration. The arista-specific var takes
precedence when both are set.

Other standard OTel vars (honoured by the exporter itself):

- `OTEL_EXPORTER_OTLP_HEADERS` — e.g. `x-api-key=…` for cloud OTLP endpoints
- `OTEL_EXPORTER_OTLP_PROTOCOL` — `grpc` (default) or `http/protobuf`
- `OTEL_EXPORTER_OTLP_TIMEOUT` — milliseconds

## Source-name stability

`AristaMcp` (see `AristaActivity.SourceName`) is the stable contract.
`AristaActivity.Version` bumps only when tag schemas break — adding a new
attribute key is backward-compatible. Downstream dashboards / alerting
queries can rely on the source name and operation strings.

Unknown `arista.*` tags in future versions should not crash consumers that
filter on the listed set.
