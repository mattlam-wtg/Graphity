# CLI Reference

Graphity is distributed as a .NET global tool with the command name `graphity`.

## Commands

### `graphity analyze [path]`

Index a codebase into a knowledge graph.

**Arguments:**

| Name | Default | Description |
|------|---------|-------------|
| `path` | `.` (current directory) | Path to solution file or directory to index |

**Options:**

| Flag | Description |
|------|-------------|
| `--skip-embeddings` | Skip generating semantic embeddings. Results in keyword-only search (BM25). Faster indexing. |
| `--verbose` | Show detailed progress including per-file analysis output and analyzer diagnostics. |

**Examples:**

```bash
# Index the current directory
graphity analyze

# Index a specific solution
graphity analyze /path/to/MySolution.sln

# Fast index without embeddings
graphity analyze --skip-embeddings

# Verbose output for debugging
graphity analyze --verbose
```

**What it does:**

1. Scans the file tree, creating File and Folder nodes
2. Runs language analyzers (Roslyn for C#, regex for TS/JS, ScriptDom for SQL)
3. Parses config files (.csproj, appsettings.json, web.config)
4. Runs community detection (Louvain clustering)
5. Traces execution flows (entry point scoring + BFS)
6. Builds and saves BM25 search index
7. Builds and saves semantic embeddings (unless `--skip-embeddings`)
8. Saves metadata (node/edge counts, commit hash)

**Output location:** `.graphity/` directory at the analyzed path root.

**Incremental indexing:** If a previous index exists with a commit hash, the analyze command checks git history to determine if an incremental update is possible. This is automatic — no flag needed.

---

### `graphity mcp`

Start the MCP server using stdio transport.

**No arguments or options.**

This starts a long-running process that communicates via stdin/stdout using the MCP JSON-RPC protocol. It exposes 4 tools (`query`, `context`, `impact`, `list_repos`) and 2 resources (`graphity://repo/context`, `graphity://repo/schema`).

The server loads the index from the `.graphity/` directory in the current working directory. If no index exists, tools will return an error message telling the user to run `graphity analyze` first.

**Usage:**

```bash
cd /path/to/your/project
graphity mcp
```

Typically, you don't run this command directly — your editor invokes it automatically via MCP configuration (see `graphity setup`).

---

### `graphity status`

Show index status for the current directory.

**No arguments or options.**

Displays:
- Repository name and path
- Index timestamp (with age)
- Node and edge counts
- Git commit hash at index time
- Staleness warning if the index is behind HEAD or older than 24 hours

**Examples:**

```
Graphity Index Status
  Repository:  MyProject
  Path:        C:\src\MyProject
  Indexed at:  2026-04-01 09:30:00Z
  Nodes:       1234
  Edges:       5678
  Commit:      abc123def456
  Status:      Up to date with HEAD
```

If the index is out of date:

```
  Warning: Index is out of date
  HEAD:        def789abc012
  Files changed since last index: 12
    Added: 2, Modified: 8, Deleted: 2
  Run 'graphity analyze' to update.
```

---

### `graphity clean`

Delete all indexed data for the current directory.

**No arguments or options.**

Removes the `.graphity/` directory and all its contents. This is non-destructive to source code — only the index files are deleted. You can regenerate them with `graphity analyze`.

---

### `graphity setup`

Auto-configure MCP for detected editors.

**No arguments or options.**

Detects installed editors and adds Graphity's MCP server configuration:

| Editor | Detection | Config File |
|--------|-----------|-------------|
| Claude Code | `~/.claude/` directory exists | `~/.claude/settings.json` |
| VS Code | `%APPDATA%/Code` or `~/.vscode` exists | `.vscode/mcp.json` (workspace) |
| Cursor | `%APPDATA%/Cursor` or `~/.cursor` exists | `~/.cursor/mcp.json` |

For each detected editor:
1. Checks if `graphity` is already configured (skips if so)
2. Shows the target file path
3. Prompts for confirmation (`[Y/n]`)
4. Safely merges the configuration into the existing file

The MCP configuration added is:

```json
{
  "graphity": {
    "command": "graphity",
    "args": ["mcp"],
    "env": {}
  }
}
```

If no editors are detected, the command prints the manual configuration JSON for reference.

---

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Error (message printed to stderr) |

## Environment

Graphity requires:
- .NET 10 SDK (for building/installing)
- .NET 10 runtime (for execution)
- Git (optional, for incremental indexing and staleness detection)
- MSBuild (for C# solution analysis — typically comes with .NET SDK or Visual Studio)
