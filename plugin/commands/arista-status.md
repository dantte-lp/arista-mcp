---
description: Report the health of the arista-mcp server and ingest store.
---

Call `mcp__arista-mcp__get_status` to read the server's health, document
count, chunk count, and the summary of the last ingest run. Render the
result as a short report with the document and chunk totals plus the
status, timing, and counts of the last run. Note any obvious anomalies —
zero documents, a failed last run, or an `error` field.
