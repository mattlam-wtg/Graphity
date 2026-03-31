# Architecture

This document describes the overall architecture of Graphity, including the solution structure, project responsibilities, and data flow from source code to MCP tool responses.

## Solution Structure

```
Graphity/
├── Graphity.slnx                    # Solution file (.NET modern format)
├── .gitignore
│
├── src/
│   ├── Graphity.Cli/                # CLI entry point (dotnet global tool)
│   ├── Graphity.Core/               # Code analysis engine
│   ├── Graphity.Mcp/                # MCP server and tools
│   ├── Graphity.Search/             # BM25 + semantic search
│   └── Graphity.Storage/            # LiteGraph database adapter
│
├── tests/
│   ├── Graphity.Core.Tests/         # 73 tests
│   ├── Graphity.Search.Tests/       # 21 tests
│   └── Graphity.Storage.Tests/      # 12 tests
│
└── Documentation/                   # This documentation
```

## Project Dependency Graph

```
Graphity.Cli
  ├── Graphity.Core
  ├── Graphity.Storage  ──► Graphity.Core
  ├── Graphity.Search   ──► Graphity.Core, Graphity.Storage
  └── Graphity.Mcp      ──► Graphity.Core, Graphity.Storage, Graphity.Search
```

All projects target **.NET 10.0** (preview).

## Project Responsibilities

### Graphity.Cli

The command-line entry point, packaged as a [dotnet global tool](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools). Uses [System.CommandLine](https://learn.microsoft.com/en-us/dotnet/standard/commandline/) (v3.0.0-preview) for argument parsing.

**Key files:**
- `Program.cs` — Defines 5 CLI commands: `analyze`, `mcp`, `status`, `clean`, `setup`
- `Commands/SetupCommand.cs` — Auto-configures MCP for Claude Code, VS Code, and Cursor

**Responsibilities:**
- Parse CLI arguments and dispatch to the appropriate handler
- Orchestrate the full analyze pipeline (scan → parse → detect → index → save)
- Start the MCP server process
- Display progress, status, and diagnostics

### Graphity.Core

The core analysis engine. Contains all language analyzers, the in-memory graph model, community/process detection, and the ingestion pipeline.

**Directory structure:**
```
Graphity.Core/
├── Graph/                   # Data model
│   ├── GraphSchema.cs       # NodeType and EdgeType enums
│   ├── GraphNode.cs         # Node data class
│   ├── GraphRelationship.cs # Edge data class
│   └── KnowledgeGraph.cs    # Thread-safe in-memory graph
│
├── Ingestion/               # Pipeline orchestration
│   ├── Pipeline.cs          # Main orchestrator (scan → parse → detect)
│   ├── ILanguageAnalyzer.cs # Pluggable analyzer interface
│   ├── AnalyzerResult.cs    # Nodes + edges returned by analyzers
│   └── FileScanner.cs       # File tree walker with .gitignore support
│
├── Analyzers/               # Language-specific parsers
│   ├── CSharp/
│   │   ├── RoslynAnalyzer.cs       # Roslyn semantic analysis
│   │   └── CSharpConfigParser.cs   # .csproj, appsettings, web.config
│   ├── TypeScript/
│   │   └── TypeScriptAnalyzer.cs   # Regex-based TS/JS extraction
│   └── Sql/
│       ├── SqlAnalyzer.cs          # ScriptDom entry point
│       └── SqlVisitor.cs           # TSqlFragmentVisitor impl
│
├── Detection/               # Graph analysis algorithms
│   ├── CommunityDetector.cs    # Louvain modularity clustering
│   ├── EntryPointScorer.cs     # Heuristic entry point scoring
│   └── ProcessDetector.cs      # BFS execution flow tracing
│
└── Incremental/             # Change detection
    ├── ChangeDetector.cs       # Git diff parsing
    └── IncrementalPipeline.cs  # Selective re-analysis
```

### Graphity.Storage

Persistence layer wrapping [LiteGraph](https://github.com/litegraphdb/LiteGraph), an embedded .NET property graph database backed by SQLite.

**Key files:**
- `LiteGraphAdapter.cs` — CRUD wrapper with string-to-GUID ID mapping
- `GraphLoader.cs` — Bulk loads the in-memory `KnowledgeGraph` into LiteGraph
- `GraphQuerier.cs` — Graph traversal queries (callers, callees, inheritance, BFS)
- `StoragePaths.cs` — Conventional file paths (`.graphity/`, `graph.db`, etc.)
- `IndexMetadata.cs` — JSON-serialized index metadata (repo name, counts, commit hash)

### Graphity.Search

Search and retrieval layer combining keyword and semantic search.

**Key files:**
- `Bm25Index.cs` — Full BM25 implementation with camelCase-aware tokenization
- `OnnxEmbedder.cs` — ONNX model inference with hash-based fallback
- `HybridSearch.cs` — Reciprocal Rank Fusion (RRF) combining BM25 and semantic scores

### Graphity.Mcp

MCP server exposing the knowledge graph to AI agents via stdio transport.

**Key files:**
- `GraphityMcpServer.cs` — Host builder with MCP server registration
- `GraphService.cs` — Lazy-initialized shared service (DI singleton)
- `Tools/QueryTool.cs` — Hybrid search tool
- `Tools/ContextTool.cs` — 360-degree symbol view
- `Tools/ImpactTool.cs` — Blast radius analysis
- `Tools/ListReposTool.cs` — Indexed repository listing
- `Resources/GraphityResources.cs` — Repo context and schema resources

## Data Flow

### Indexing Pipeline

```
Source Code Files
       │
       ▼
  FileScanner           ── Walks file tree, creates File/Folder nodes
       │
       ▼
  Language Analyzers    ── Parse source code into nodes + edges
  ├── RoslynAnalyzer      (C# via MSBuildWorkspace + SemanticModel)
  ├── TypeScriptAnalyzer  (TS/JS via regex patterns)
  ├── SqlAnalyzer         (SQL via TSql160Parser)
  └── CSharpConfigParser  (.csproj, appsettings.json, web.config)
       │
       ▼
  CommunityDetector     ── Louvain clustering → Community nodes + MemberOf edges
       │
       ▼
  EntryPointScorer      ── Score methods/functions as potential entry points
       │
       ▼
  ProcessDetector       ── BFS from entry points → Process nodes + StepInProcess edges
       │
       ▼
  KnowledgeGraph        ── In-memory graph (ConcurrentDictionary-backed)
       │
       ├──► Bm25Index.BuildIndex()    → bm25-index.json
       ├──► HybridSearch.BuildEmbeddingIndex() → embeddings.bin
       └──► IndexMetadata.Save()      → metadata.json
```

### MCP Query Flow

```
AI Agent (Claude Code, Copilot, etc.)
       │
       │  stdio (JSON-RPC)
       ▼
  MCP Server (GraphityMcpServer)
       │
       ▼
  GraphService.EnsureInitialized()
  ├── Loads BM25 index from disk
  ├── Loads embeddings from disk
  └── Opens LiteGraph database
       │
       ▼
  MCP Tool (query / context / impact / list_repos)
       │
       ├── HybridSearch or Bm25Index  (for symbol lookup)
       ├── LiteGraphAdapter            (for node/edge retrieval)
       └── GraphQuerier                (for graph traversal)
       │
       ▼
  Formatted text response with next-step hints
```

## Thread Safety

The `KnowledgeGraph` class uses `ConcurrentDictionary` for node and edge storage, with `lock` on `HashSet`-based adjacency lists for edge lookups. This allows safe concurrent reads during MCP tool execution.

`GraphService` uses double-checked locking for lazy initialization, ensuring the database and search indexes are loaded exactly once.

## Key NuGet Dependencies

| Package | Version | Project | Purpose |
|---------|---------|---------|---------|
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | 5.3.0 | Core | Roslyn C# analysis |
| `Microsoft.CodeAnalysis.Workspaces.MSBuild` | 5.3.0 | Core | Load .sln/.csproj |
| `Microsoft.Build.Locator` | 1.11.2 | Core | Find MSBuild installation |
| `Microsoft.SqlServer.TransactSql.ScriptDom` | 170.191.0 | Core | SQL parsing |
| `LiteGraph` | 5.0.2 | Storage | Embedded property graph DB |
| `ModelContextProtocol` | 1.2.0 | Mcp | MCP server SDK |
| `Microsoft.Extensions.Hosting` | 10.0.5 | Mcp | Host builder for MCP server |
| `Microsoft.ML.OnnxRuntime` | 1.24.4 | Search | ONNX model inference |
| `System.CommandLine` | 3.0.0-preview | Cli | CLI argument parsing |
