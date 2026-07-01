---
name: arista-search
description: Use this skill whenever the user asks a question that can be answered from Arista Networks documentation — EOS features, BGP / EVPN / VXLAN / MLAG configuration, CloudVision (CVP / CVaaS), CV-CUE, DMF, MSS, AVD, CloudEOS, VeloCloud SD-WAN, campus switching, hardware datasheets, cabling reference, TOI release notes, or hardware replacement procedures. Invoke the `mcp__arista-mcp__search_docs` MCP tool to retrieve grounded passages, quote them with citations, and refuse to speculate beyond what the tool returns.
---

# arista-search

The `arista-mcp` MCP server runs a hybrid retrieval pipeline (dense
`snowflake-arctic-embed-m-v1.5` plus BM25 through `vchord_bm25` plus a
BERT / XLM-R cross-encoder reranker) over the entire Arista
documentation corpus. Use it as the source of truth for any Arista
question.

## Decision tree

- The user asks an open-ended question about an Arista product → call
  `mcp__arista-mcp__search_docs`.
- The user wants a particular section verbatim → call
  `mcp__arista-mcp__lookup_section`.
- The user wants to enumerate documents in a category, product, or
  version → call `mcp__arista-mcp__list_documents`.
- The user asks for the metadata of a specific document (title, slug,
  chunk count) → call `mcp__arista-mcp__get_document`.

## search_docs usage

Always set `topK` to 10 by default; raise to 20 only if the first call
returns weak hits. Set `product`, `category`, or `version` whenever the
user mentions one — the SQL filter is cheap and improves precision.

| User signal | Argument |
|---|---|
| "EOS", any config-guide phrasing | `product: "eos"` |
| "CVP", "CloudVision Portal" | `product: "cvp"` |
| "CVaaS", "CloudVision as a Service" | `product: "cvp"` |
| "CV-CUE", "campus" wired/wireless | `product: "cv-cue"` |
| "DMF", "DANZ Monitoring Fabric" | `product: "dmf"` |
| "CVW", "CloudVision WiFi" | `product: "cvw"` |
| "MSS", "Multi-Domain Segmentation Services" | `product: "mss"` |
| "AVD", "Ansible AVD" | `product: "avd"` |
| "VeloCloud", "SD-WAN" | `product: "velocloud"` |
| "CloudEOS", "vEOS" | `product: "cloudeos"` |
| "Aboot" | `product: "aboot"` |
| "CVA", "CloudVision Appliance" | `product: "cva"` |
| "hardware", "datasheet", "cabling" | `product: "hardware"` |
| any manual title match | `category: "manual"` |
| TOI = time-of-introduction release notes | `category: "toi"` |
| API / reference material | `category: "reference"` |

The Arista corpus is English-only; user queries in Russian still work
(the embedder is multilingual-friendly), but expect slightly weaker
hits than English queries.

## Citation contract

Every claim must cite a chunk. Use the document title and the slug, plus
the page span when present:

```text
Cite: "EOS User Manual 4.32.0F", EOS-4_32_0F-Manual, p. 512–515
```

Refuse to fabricate. If the search returns nothing relevant, say so and
suggest the user broaden the query or drop the product filter.

## Diagnostics

Pass `withDiagnostics: true` only when the user asks why a search behaves
the way it does. The diagnostics reveal per-stage timings (embed, dense,
sparse, RRF, rerank) and counts (`DenseHits`, `SparseHits`, `AfterRrf`,
`AfterRerank`). The pipeline may adaptively force `rerank_top_n = 10`
when the RRF top-5 span is ≤ 0.02 — that is expected behaviour on
tight-cluster queries.

## Query rewriting

The server acronym-expands the query internally, so `EVPN`, `MLAG`,
`VXLAN`, `BGP`, `OSPF`, `LACP`, `sFlow`, `VARP`, `VRRP`, `VRF`, `QoS`,
`ACL`, `TCAM`, `EOS`, `CVP`, `DMF`, `CV-CUE`, `AVD`, `LANZ`, `MSS`, `SR`
do not need to be spelled out. Rare product codes (`7050X3`,
`21630-...`) may benefit from being paired with their family name.
