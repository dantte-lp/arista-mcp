---
description: Run a one-shot hybrid search against arista-mcp (for quick quality testing).
---

Execute a search query through the arista-mcp CLI against the running local database.

**Query:** `$ARGUMENTS`

**Run:**

```bash
dotnet run --project src/AristaMcp.Cli -- search "$ARGUMENTS" --limit 10 --with-diagnostics
```

**Report:**
- Top 10 results as a markdown table: `title | section | page_start | score`
- Full `SearchDiagnostics` block (dense_hits, sparse_hits, after_rrf, after_rerank, timings)
- Flag anything suspicious: all scores clustered tightly (poor separation), zero sparse hits (bm25 index empty?), rerank_ms > 1000ms (GPU offloaded?).
