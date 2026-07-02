---
description: Search the Arista Networks documentation corpus via the arista-mcp MCP server.
argument-hint: <free-text query>
---

Use the `arista-mcp` MCP server to answer the user's question about Arista
hardware, EOS features, CloudVision (CVP/CVaaS), CV-CUE, DMF, MSS, AVD,
CloudEOS, VeloCloud SD-WAN, campus switching, hardware replacement, or
anything else covered by the corpus.

User query: $ARGUMENTS

Steps:

1. Call the `mcp__arista-mcp__search_docs` tool with `query` set to the user
   query, `topK` 10.
2. If the user query mentions a specific Arista product family
   (for example, `EOS`, `CVP`, `DMF`, `CV-CUE`, `CVW`, `hardware`,
   `aboot`, `CVA`, `MSS`, `VeloCloud`, `CloudEOS`, `analytics`, `campus`,
   `AVD`), pass it in `product`.
3. If the user names an EOS version (for example, `4.22.0F`, `4.30.4M`),
   embed the version string in the `query` text — `search_docs` has no
   dedicated `version` argument; BM25 matches it against document
   titles like `EOS-4_32_0F-Manual`.
4. If the query maps to a category (`manual`, `reference`, `toi`
   — the TOI category holds the "Time-Of-Introduction" release notes),
   pass it in `category`.
5. Read the returned chunks. Quote the most relevant passages and cite the
   document title, slug, and page span.
6. If no chunk seems sufficient, call `mcp__arista-mcp__lookup_section` to
   pull the full section text, or `mcp__arista-mcp__list_documents` to
   broaden the lens.
7. Avoid speculation — every claim must trace back to a returned chunk.

## Follow-up

If a search returns weak hits, retry with a rephrased query — the
retriever expands ~20 Arista acronyms on first token (EVPN, VXLAN, MLAG,
BGP, OSPF, LACP, sFlow, SR, MSS, AVD, LANZ, VARP, VRRP, VRF, QoS, ACL,
TCAM, EOS, CVP, DMF) so acronyms don't need to be spelled out, but rare
product names benefit from the full form.
