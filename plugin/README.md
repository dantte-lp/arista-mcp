# arista-mcp Claude Code plugin

Wires the `arista-mcp` MCP server into Claude Code together with two slash
commands and one skill that enforce a ground-everything-in-search policy
for Arista questions.

## Layout

```text
plugin/
├── .claude-plugin/plugin.json   # Manifest
├── .mcp.json                    # MCP server registration
├── commands/
│   ├── arista-search.md         # /arista-search <query>
│   └── arista-status.md         # /arista-status
└── skills/
    └── arista-search/SKILL.md
```

## Prerequisites

- The `arista-mcp` binary on `PATH`. Either install one of the release
  artefacts from the GitHub Releases page or build from source via
  `dotnet publish`.
- A populated PostgreSQL database that the binary can reach, plus the
  ONNX models on disk (see `docs/en/getting-started.md`).
- Two environment variables exported before launching Claude Code:
  - `ARISTA_MCP__ConnectionString`
  - `ARISTA_MCP__ModelsDir`

Optional (production INT8 / fine-tune reranker):

- `ARISTA_MCP__RerankerDir` — override for the reranker directory.

## Install — via Claude Code marketplace (recommended)

```
/plugin marketplace add https://github.com/dantte-lp/arista-mcp
/plugin install arista-mcp
```

Then set the two env vars in the shell that launches `claude`, or add
them into `~/.claude.json` under the `mcpServers.arista-mcp.env` block.

## Install — via one-shot script (CLI, no Claude Code REPL)

```bash
# From a fresh release tarball:
gh release download v0.3.1 -R dantte-lp/arista-mcp \
  -p 'arista-mcp-v0.3.1-linux-x64.tar.gz'
tar -xzf arista-mcp-v0.3.1-linux-x64.tar.gz
cd arista-mcp-v0.3.1-linux-x64
sudo bash scripts/install.sh          # binary → /usr/local/bin,
                                      # models fetch, PG bootstrap,
                                      # register in Claude + Codex,
                                      # copy skills into ~/.codex/skills
```

The script is idempotent — running it again against an existing install
only refreshes files it needs to change.

## What each entry does

- **`/arista-search <query>`** — slash command that runs `search_docs`
  with the right filters based on the phrasing.
- **`/arista-status`** — reports document / chunk counts and last ingest
  run status.
- **`arista-search` skill** — auto-activates whenever the model detects
  an Arista-question intent (EOS / EVPN / CVP / MSS / hardware / …),
  forcing the model to ground every claim in a returned chunk.

## MCP tools exposed by the server

| Tool | Purpose |
|---|---|
| `search_docs` | Hybrid dense + BM25 + rerank |
| `lookup_section` | Full text of a named section across its chunks |
| `list_documents` | Filter documents by category / product / version |
| `get_document` | Full metadata + chunk count for one document |
| `get_status` | Chunk / document counts and last ingest-run summary |

The skill is designed so that these tools are called in the right
sequence without the user having to name them.
