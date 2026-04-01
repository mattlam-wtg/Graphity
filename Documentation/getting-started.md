# Getting Started

This guide walks you through installing Graphity, indexing a codebase, and connecting it to your AI coding agent.

## Prerequisites

- **.NET 10 SDK** (preview) — [download](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Git** — required for incremental indexing and staleness detection
- A C# solution (`.sln` or `.slnx`), TypeScript project, or SQL scripts to index

## Installation

### From Source

```bash
# Clone the repository
git clone https://github.com/WiseTechGlobal/Graphity.git
cd Graphity

# Build and install as a global tool
dotnet pack src/Graphity.Cli/Graphity.Cli.csproj -c Release
dotnet tool install -g --add-source src/Graphity.Cli/bin/Release Graphity --version 1.0.0
```

### From NuGet (Internal)

```bash
dotnet tool install -g Graphity
```

After installation, the `graphity` command is available globally.

## Indexing a Codebase

Navigate to your project root (the directory containing your `.sln` file, or any directory with source files) and run:

```bash
cd /path/to/your/solution
graphity analyze
```

This will:

1. **Scan** the file tree, detecting languages and creating file/folder nodes
2. **Parse** source code using Roslyn (C#), regex-based analysis (TypeScript/JS), and ScriptDom (SQL)
3. **Parse config files** — `.csproj`, `appsettings.json`, `web.config`, `Directory.Build.props`
4. **Detect communities** — cluster code into functional areas using Louvain modularity optimization
5. **Trace execution flows** — identify entry points and trace call chains via BFS
6. **Build search indexes** — BM25 keyword index and semantic embedding index

Output is stored in a `.graphity/` directory at the project root:

```
.graphity/
  metadata.json      # Repo name, path, node/edge counts, commit hash
  bm25-index.json    # BM25 full-text search index
  embeddings.bin     # Semantic embedding vectors (384-dim)
```

### Options

| Flag | Description |
|------|-------------|
| `--skip-embeddings` | Skip generating semantic embeddings (faster, keyword-only search) |
| `--verbose` | Show detailed per-file progress and analyzer output |

```bash
# Fast index with keyword search only
graphity analyze --skip-embeddings

# Detailed output for debugging
graphity analyze --verbose
```

### What Gets Indexed

Graphity indexes the following file types:

| Language | Extensions | Parser |
|----------|------------|--------|
| C# | `.cs` | Roslyn (full semantic analysis via MSBuildWorkspace) |
| TypeScript | `.ts`, `.tsx` | Regex-based extraction |
| JavaScript | `.js`, `.jsx` | Regex-based extraction |
| SQL | `.sql` | Microsoft.SqlServer.TransactSql.ScriptDom |
| Config | `.csproj`, `.json`, `.config`, `.props`, `.targets` | Custom XML/JSON parsers |

Files in the following directories are automatically skipped: `bin`, `obj`, `node_modules`, `.git`, `.vs`, `TestResults`, `packages`, `.nuget`.

### Incremental Indexing

On subsequent runs, Graphity checks git history to determine if an incremental update is possible. If you've only changed a few files since the last index, it can selectively re-analyze only those files. The CLI auto-detects this:

```bash
# Will use incremental if possible, falls back to full
graphity analyze
```

## Starting the MCP Server

Once indexed, start the MCP server:

```bash
graphity mcp
```

This starts a stdio-based MCP server that AI agents connect to. The server exposes 4 tools and 2 resources (see [MCP Tools](mcp-tools.md)).

## Connecting to Your Editor

### Automatic Setup

Run `graphity setup` to auto-detect and configure supported editors:

```bash
graphity setup
```

This detects and configures:
- **Claude Code** — writes to `~/.claude/settings.json`
- **VS Code** — writes to `.vscode/mcp.json` in the current workspace
- **Cursor** — writes to `~/.cursor/mcp.json`

The command prompts for confirmation before writing to each file and safely merges with existing configuration.

### Manual Configuration

If automatic setup doesn't work for your editor, add this MCP server configuration manually:

```json
{
  "mcpServers": {
    "graphity": {
      "command": "graphity",
      "args": ["mcp"],
      "env": {}
    }
  }
}
```

The exact location depends on your editor:

| Editor | Config File |
|--------|-------------|
| Claude Code | `~/.claude/settings.json` under `mcpServers` |
| VS Code | `.vscode/mcp.json` under `servers` |
| Cursor | `~/.cursor/mcp.json` under `mcpServers` |

## Checking Index Status

```bash
graphity status
```

Shows repository name, node/edge counts, index age, and whether the index is stale (more than 24 hours old or behind the current git HEAD):

```
Graphity Index Status
  Repository:  MyProject
  Path:        /path/to/MyProject
  Indexed at:  2026-04-01 09:30:00Z
  Nodes:       1,234
  Edges:       5,678
  Commit:      abc123def456
  Status:      Up to date with HEAD
```

If the index is out of date, it shows the number of changed files and prompts you to re-index.

## Cleaning Up

```bash
graphity clean
```

Deletes the `.graphity/` directory and all indexed data. You can re-index at any time with `graphity analyze`.

## Typical Workflow

1. **Index once** when starting work on a project: `graphity analyze`
2. **Start the MCP server** in your editor or terminal: `graphity mcp`
3. **Ask your AI agent** structural questions:
   - "What calls UserService.GetUser?"
   - "What's the blast radius if I change the OrderRepository?"
   - "Find all code related to authentication"
4. **Re-index** when code changes significantly: `graphity analyze` (incremental when possible)
