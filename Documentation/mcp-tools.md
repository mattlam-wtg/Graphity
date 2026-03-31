# MCP Tools & Resources

Graphity exposes 4 tools and 2 resources via the [Model Context Protocol (MCP)](https://modelcontextprotocol.io/). These are the primary interface for AI agents to query the knowledge graph.

## Server Setup

The MCP server uses stdio transport (standard for CLI-based tools). It's started via:

```bash
graphity mcp
```

Under the hood, this uses `Microsoft.Extensions.Hosting` to build an application host with the `ModelContextProtocol` NuGet package. Tools and resources are auto-discovered from the assembly via `WithToolsFromAssembly()` and `WithResourcesFromAssembly()`.

**File:** `src/Graphity.Mcp/GraphityMcpServer.cs`

### Dependency Injection

All tools receive a `GraphService` singleton via constructor injection. `GraphService` lazily loads the LiteGraph database, BM25 index, and embedding index on first use. This means the MCP server starts quickly and only reads from disk when a tool is actually invoked.

---

## Tools

### `query` — Hybrid Search

**File:** `src/Graphity.Mcp/Tools/QueryTool.cs`

Search the knowledge graph using hybrid keyword + semantic search.

**Parameters:**

| Name | Type | Default | Description |
|------|------|---------|-------------|
| `query` | string | required | Search query (e.g., "UserService", "authentication") |
| `limit` | int | 20 | Maximum number of results |

**Behavior:**
1. Uses `HybridSearch` (BM25 + semantic) when embeddings are available
2. Falls back to `Bm25Index` (keyword-only) otherwise
3. Groups results by file path, sorted by relevance score
4. Each result shows: node type, name, ID, and score

**Example output:**
```
Search results for 'authentication' (5 matches):

File: src/Services/AuthService.cs
  [class] AuthService  (id: Class:MyApp.AuthService, score: 4.52)
  [method] Authenticate  (id: Method:MyApp.AuthService.Authenticate, score: 3.18)

File: src/Controllers/LoginController.cs
  [method] Login  (id: Method:MyApp.LoginController.Login, score: 2.91)

Next steps:
  - Use context(<name>) on a specific symbol for callers, callees, and inheritance
  - Use impact(<name>) to analyze blast radius before making changes
```

---

### `context` — 360-Degree Symbol View

**File:** `src/Graphity.Mcp/Tools/ContextTool.cs`

Get a complete view of a symbol: what it is, who depends on it, what it depends on.

**Parameters:**

| Name | Type | Default | Description |
|------|------|---------|-------------|
| `name` | string | required | Symbol name or full ID |
| `include_content` | bool | false | Include source code in response |

**Behavior:**
1. **Resolve symbol** — tries exact ID match first, then BM25 search with type-priority disambiguation (Class > Interface > Struct > Record > Enum > Method > Constructor > Property > Field)
2. **Symbol header** — type, full name, file path, line numbers
3. **Incoming references** — grouped by edge type (callers, importers, extenders, etc.)
4. **Outgoing references** — grouped by edge type (callees, imports, base types, etc.)
5. **Summary** — total incoming/outgoing count
6. **Source code** — if `include_content` is true
7. **Next-step hints** — suggests `impact()` for symbols with many dependents

**Example output:**
```
Symbol: UserService
  Type:      Class
  Full name: MyApp.Services.UserService
  File:      src/Services/UserService.cs
  Lines:     15-89
  ID:        Class:MyApp.Services.UserService

Incoming references (who depends on this):
  [Calls] UserController (Class)
  [Calls] OrderService (Class)
  [Implements] IUserService (Interface)

Outgoing references (what this depends on):
  [Calls] UserRepository (Class)
  [Extends] BaseService (Class)
  [Imports] System.Threading.Tasks (Namespace)

Summary: 3 incoming, 3 outgoing references

Next steps:
  - Use impact('UserService') to see what breaks if you modify this symbol
  - This symbol has 3 dependents — consider impact analysis before changes
```

---

### `impact` — Blast Radius Analysis

**File:** `src/Graphity.Mcp/Tools/ImpactTool.cs`

Analyze what breaks if you modify a symbol. Returns depth-grouped affected symbols with a risk level.

**Parameters:**

| Name | Type | Default | Description |
|------|------|---------|-------------|
| `target` | string | required | Symbol name or ID |
| `direction` | string | "upstream" | `"upstream"` (dependents) or `"downstream"` (dependencies) |
| `maxDepth` | int | 3 | Traversal depth (clamped to 1–5) |

**Behavior:**
1. **Resolve symbol** — same logic as `context`
2. **Graph traversal** — BFS from the target node, following edges in the specified direction
3. **Depth grouping:**
   - Depth 1: **WILL BREAK** — direct dependents that reference this symbol
   - Depth 2: **LIKELY AFFECTED** — one hop away from direct dependents
   - Depth 3+: **MAY NEED TESTING** — transitive impact
4. **Risk calculation:**

| Risk Level | Criteria |
|------------|----------|
| CRITICAL | More than 20 direct dependents (depth 1) |
| HIGH | More than 10 direct dependents |
| MEDIUM | 1–10 direct dependents |
| LOW | No direct dependents, but transitive impact exists |
| NONE | No affected symbols |

**Example output:**
```
Impact analysis for: UserService (Class)
Direction: upstream (dependents)
Max depth: 3

Risk level: MEDIUM — 3 direct dependents
Total affected: 7 symbols

Depth 1 — WILL BREAK (3 symbols):
  [Class] UserController in UserController.cs
  [Class] OrderService in OrderService.cs
  [Class] AdminService in AdminService.cs

Depth 2 — LIKELY AFFECTED (3 symbols):
  [Class] OrderController in OrderController.cs
  [Method] HandleOrder in OrderController.cs
  [Class] AdminController in AdminController.cs

Depth 3 — MAY NEED TESTING (1 symbols):
  [Class] ApiRouter in Startup.cs

Next steps:
  - Review depth-1 items first (WILL BREAK on modification)
  - Use context(<name>) on any affected symbol for detailed references
  - Use impact('UserService', direction: 'downstream') to see dependencies
```

---

### `list_repos` — List Indexed Repositories

**File:** `src/Graphity.Mcp/Tools/ListReposTool.cs`

List all indexed repositories with stats.

**Parameters:** None

**Behavior:**
Reads `IndexMetadata` from the current repository's `.graphity/metadata.json` and returns:
- Repository name and path
- Node and edge counts
- Index age and staleness warnings (>24 hours)
- Commit hash

---

## Resources

### `graphity://repo/context` — Repository Context

Returns high-level stats about the indexed repository, including node/edge counts, staleness indicator, and a list of available tools.

### `graphity://repo/schema` — Graph Schema

Returns the complete list of node types and edge types available in the knowledge graph, grouped by category (Code, Members, Database, Config, Meta for nodes; Structure, References, Database, Config, Organization for edges).

---

## Next-Step Hints

Every tool response includes contextual "next steps" that guide the AI agent toward productive follow-up queries:

| After this tool... | Hint |
|--------------------|------|
| `list_repos` | "Use query() to search the codebase" |
| `query` | "Use context() on a specific symbol" |
| `context` | "Use impact() if planning changes" |
| `impact` | "Review depth-1 items first" |

This pattern ensures AI agents can chain tool calls effectively without needing prior knowledge of the tool workflow.
