# Storage & Persistence

Graphity stores indexed data in a `.graphity/` directory at the root of the analyzed repository.

## Storage Layout

```
.graphity/
├── metadata.json      # Index metadata (repo name, counts, commit hash)
├── bm25-index.json    # BM25 full-text search index
└── embeddings.bin     # Semantic embedding vectors (384-dim, binary format)
```

**File:** `src/Graphity.Storage/StoragePaths.cs`

The `.graphity/` directory is automatically added to `.gitignore` by the project template. It should not be committed to version control — it's a local build artifact that can be regenerated with `graphity analyze`.

---

## Index Metadata

**File:** `src/Graphity.Storage/IndexMetadata.cs`

The `metadata.json` file stores:

```json
{
  "RepoName": "MyProject",
  "RepoPath": "/path/to/MyProject",
  "IndexedAtUtc": "2026-04-01T09:30:00Z",
  "NodeCount": 1234,
  "EdgeCount": 5678,
  "CommitHash": "abc123def456789..."
}
```

This metadata is used by:
- `graphity status` — to display index information and detect staleness
- `graphity analyze` — to determine if incremental indexing is possible
- `list_repos` MCP tool — to report repository stats to AI agents
- `graphity://repo/context` resource — to provide repo context

---

## LiteGraph Database

**File:** `src/Graphity.Storage/LiteGraphAdapter.cs`

[LiteGraph](https://github.com/litegraphdb/LiteGraph) is an embedded .NET property graph database backed by SQLite. Graphity uses it for persistent graph storage and traversal queries.

### ID Mapping

LiteGraph uses `Guid` identifiers internally, while Graphity uses human-readable string IDs (e.g., `"Class:MyApp.UserService"`). The `LiteGraphAdapter` maintains bidirectional dictionaries for mapping:

```
string ID  →  Guid  (for writes: node creation, edge creation)
Guid  →  string ID  (for reads: query results)
```

### Node Storage

Each `GraphNode` is stored as a LiteGraph node with:
- A `Guid` identifier (mapped from the string ID)
- A `Data` JSON payload containing all node properties
- Tags for queryability (node type, language, file path)

### Edge Storage

Each `GraphRelationship` is stored as a LiteGraph edge with:
- Source and target `Guid` identifiers
- A `Data` JSON payload containing edge properties (confidence, reason, step)
- Tags for the edge type

### Caching

LiteGraph's caching is enabled (`CachingSettings.Enable = true`). An earlier version had caching disabled, but this caused null reference exceptions in LiteGraph's internal LRU cache implementation.

### Data Retrieval

When retrieving edges, `LiteGraphAdapter` passes `includeData: true` and `includeSubordinates: true` to ensure the full edge payload (confidence, reason, etc.) is returned. Without these flags, LiteGraph returns edges without their `Data` field.

---

## Graph Loader

**File:** `src/Graphity.Storage/GraphLoader.cs`

Bulk loads an in-memory `KnowledgeGraph` into LiteGraph:

1. Creates (or opens) a named graph in LiteGraph
2. Iterates all nodes, calling `UpsertNodeAsync` for each
3. Iterates all edges, calling `UpsertEdgeAsync` for each
4. Saves metadata (repo name, path, indexed date) as graph-level tags

This is used during the `analyze` pipeline to persist the final graph.

---

## Graph Querier

**File:** `src/Graphity.Storage/GraphQuerier.cs`

Provides high-level graph traversal methods on top of `LiteGraphAdapter`:

### Methods

**`FindCallersAsync(symbolId)`**
Returns all nodes that have a `Calls` edge pointing to the given symbol.

**`FindCalleesAsync(symbolId)`**
Returns all nodes that the given symbol has `Calls` edges pointing to.

**`FindSymbolsInFileAsync(filePath)`**
Returns all nodes defined in a given file (by matching the `FilePath` property).

**`FindInheritanceChainAsync(typeId)`**
Walks `Extends` edges upward to build the full inheritance chain.

**`TraverseAsync(startNodeId, direction, maxDepth, edgeFilter)`**
General-purpose BFS traversal with:
- **Direction:** `Upstream` (follow incoming edges) or `Downstream` (follow outgoing edges)
- **Max depth:** Configurable (default 3)
- **Edge type filter:** Optional set of edge types to follow
- **Cycle detection:** Prevents infinite loops in cyclic graphs
- **Result grouping:** Returns a `Dictionary<int, List<GraphNode>>` mapping depth to nodes

This is the core method used by the `impact` MCP tool.

---

## Incremental Indexing

### Change Detection

**File:** `src/Graphity.Core/Incremental/ChangeDetector.cs`

Uses git commands to detect changes since the last indexed commit:

```bash
git diff --name-status <lastCommit> HEAD    # Committed changes
git status --porcelain                       # Uncommitted changes
```

Returns a `ChangeSet` with categorized file paths:
- **Added** — new files
- **Modified** — changed files
- **Deleted** — removed files
- **Renamed** — treated as delete + add

Handles non-git repositories gracefully (returns `null`, causing a full reindex fallback).

### Incremental Pipeline

**File:** `src/Graphity.Core/Incremental/IncrementalPipeline.cs`

When incremental indexing is possible:

1. **Remove** nodes and edges for deleted and modified files
2. **Re-analyze** added and modified files using the registered analyzers
3. **Recreate** file/folder nodes for affected paths
4. **Re-run** community detection and process tracing (these are global operations that must run on the full graph)

The `Pipeline.RunSmartAsync()` method auto-detects whether incremental is possible:

```csharp
var (graph, wasIncremental) = await pipeline.RunSmartAsync(path, lastCommitHash, ct);
```

If incremental indexing fails or isn't possible (no git repo, no previous commit hash), it falls back to a full reindex transparently.

### Staleness Detection

The `status` command compares the stored `CommitHash` to the current `git rev-parse HEAD`. If they differ, it shows the number of files that changed and recommends re-indexing.

The MCP `list_repos` tool also reports staleness (indexes older than 24 hours).
