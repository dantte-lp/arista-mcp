# Security policy

## Supported versions

Security fixes are backported only to the most recent minor release.

| Version  | Supported         |
|----------|-------------------|
| 0.2.x    | Yes (current)     |
| 0.1.x    | Best-effort — patches will land if the fix is isolated |
| < 0.1    | No                |

## Reporting a vulnerability

**Do not open a public issue** for security-sensitive reports.

Preferred channel: GitHub private security advisory —
<https://github.com/dantte-lp/arista-mcp/security/advisories/new>.

Include:

- Affected version(s) and commit hash if known.
- Reproducer (minimal — a query payload, a config snippet).
- Expected impact: data exposure, auth bypass, DoS, RCE.
- Any PoC code / logs that help triage.

## Response targets

| Severity   | Acknowledge within | Fix target          | Public disclosure |
|------------|--------------------|----------------------|-------------------|
| Critical   | 24 h               | 7 days               | Coordinated       |
| High       | 72 h               | 30 days              | Coordinated       |
| Medium     | 7 d                | 90 days              | After patch ships |
| Low        | 14 d               | Next scheduled release | With release notes |

Severity follows [CVSS 3.1](https://www.first.org/cvss/v3.1/specification-document)
intuition, not strict scoring — use your judgement.

## Scope

### In scope

- The .NET server code under `src/` (retrieval, embedder, reranker,
  HTTP + stdio transports, CLI).
- Docker Compose manifest under `docker/` (PostgreSQL + optional LLM sidecar).
- Scripts under `scripts/`.

### Out of scope (report upstream)

- **ONNX models** — `snowflake-arctic-embed-m-v1.5`,
  `cross-encoder/ms-marco-MiniLM-L6-v2`, `BAAI/bge-reranker-*`,
  `Qwen/Qwen2.5-*`. Report to the respective upstream projects.
- **PostgreSQL extensions** — `pgvector`, `vchord`, `vchord_bm25`,
  `pg_tokenizer`. Report to the respective upstream projects.
- **.NET SDK / ONNX Runtime / EF Core / Npgsql / Pgvector.EFCore /
  Microsoft.ML.Tokenizers / ModelContextProtocol** — report to
  the vendor and we will track the bump.

### Known-accepted risks (NOT vulnerabilities)

- Default connection string carries a hard-coded local-dev password
  (`arista / arista`). Flagged by SonarAnalyzer S2068, suppression
  documented in `AristaMcpSettings.cs`. Production deployments MUST
  override `ARISTA_MCP__ConnectionString`.
- The MCP HTTP transport is stateless and ships without authentication;
  local-only is the intended deployment. Put it behind a reverse proxy
  with mTLS or an auth sidecar if you want to expose it.
- The HyDE path (disabled by default) trusts the LLM sidecar response
  is well-formed. Malformed responses are caught and fall back to raw
  query; malicious responses would only affect dense-retrieval ranking,
  not data integrity.

## Disclosure philosophy

We prefer coordinated disclosure. If a public disclosure is necessary
(e.g. to warn operators of active exploitation), we will publish a
GHSA advisory simultaneously with the fix.

No bug-bounty program yet — acknowledgement in the advisory only.
