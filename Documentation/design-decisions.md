# Design Decisions

This document explains the key technical choices made in Graphity and their rationale.

---

## 1. Roslyn for C# Instead of Tree-sitter

**Decision:** Use Microsoft Roslyn (Microsoft.CodeAnalysis) for C# analysis instead of Tree-sitter.

**Rationale:** Graphity was inspired by [GitNexus](https://github.com/abhigyanpatwari/GitNexus), which uses Tree-sitter for all 14 languages it supports. Tree-sitter provides fast, syntax-level AST parsing — but for C#, this means:

- No cross-file type resolution (imports/using directives can't be resolved)
- No overload resolution (`GetUser(int)` vs `GetUser(string)`)
- No partial class merging
- No extension method tracking
- Heuristic-only interface detection (relies on `I` prefix convention)
- Lower confidence call graphs (name matching only)

Roslyn provides **full semantic analysis** out of the box:
- `SemanticModel.GetSymbolInfo()` resolves calls to exact method overloads
- `MSBuildWorkspace` resolves cross-project references
- `INamedTypeSymbol` provides complete inheritance chains
- Call confidence is 0.9 (semantic) vs 0.5 (name-only)

**Trade-off:** Roslyn requires loading the full solution, which is slower than Tree-sitter for initial indexing. This is mitigated by incremental indexing on subsequent runs.

---

## 2. Regex-Based TypeScript Parsing

**Decision:** Use regex patterns for TypeScript/JavaScript analysis instead of Tree-sitter.

**Rationale:** The plan called for Tree-sitter with `tree-sitter-typescript` grammar, but:

- .NET Tree-sitter bindings (`TreeSitter.DotNet`) are experimental and have native dependency issues across platforms
- Tree-sitter grammars require distributing platform-specific native binaries
- For v1 scope, the most common TypeScript patterns (imports, classes, functions, call expressions) are reliably extractable with regex

**What works well:**
- ES6 import/export detection
- Class declarations with heritage (extends/implements)
- Function declarations and arrow functions
- Call expression extraction with built-in keyword filtering

**Known limitations:**
- No cross-file import resolution
- No type inference for arrow function returns
- Deeply nested or unusual patterns may be missed
- Dynamic property access calls not detected

**Future:** If deeper TypeScript analysis is needed, Tree-sitter can be integrated once the .NET bindings stabilize, or a dedicated TypeScript language server could be used.

---

## 3. LiteGraph for Graph Storage

**Decision:** Use LiteGraph as the embedded graph database.

**Alternatives considered:**

| Database | Pros | Cons |
|----------|------|------|
| **LiteGraph** | Embedded .NET, MIT license, SQLite-backed, supports property graphs | Young project, some edge cases (caching bugs, API quirks) |
| **Neo4j** | Market leader, Cypher query language, mature | Requires separate server process, AGPL/commercial license |
| **KuzuDB** | High performance, Cypher support | Limited .NET bindings at the time |
| **LiteDB** | Mature .NET embedded DB | Document store, not native graph |

**Why LiteGraph:** Zero external dependencies (embedded), MIT licensed, .NET native, supports property graphs with tags. The fact that it's SQLite-backed means data files are portable and recoverable.

**Workarounds required:**
- String-to-GUID ID mapping (LiteGraph uses GUIDs internally)
- `CachingSettings.Enable = true` required (disabled caching causes NullReferenceException)
- `includeData: true` required on edge reads (data not returned by default)

---

## 4. BM25 from Scratch

**Decision:** Implement BM25 scoring from scratch instead of using an existing full-text search library.

**Rationale:**
- Custom tokenization is critical for code search (camelCase splitting, non-alphanumeric splitting)
- No .NET BM25 library offered the level of control needed
- BM25 is a well-documented algorithm (~100 lines of implementation)
- Custom tokenizer is shared with the embedding system for consistency

---

## 5. Hash-Based Embedding Fallback

**Decision:** Provide a deterministic hash-based embedding fallback when the ONNX model is unavailable.

**Rationale:** The ONNX model (`all-MiniLM-L6-v2`, ~90MB) is too large to bundle with the tool and requires manual download. Rather than making semantic search completely unavailable without the model, the hash-based fallback:

- Generates deterministic 384-dimensional vectors from token hashes
- Provides consistent similarity scoring for related identifiers
- Requires zero external files or network access
- Is sufficient for code search where identifier overlap is the primary signal

The fallback is not as semantically rich as a neural model (it can't understand that "authentication" and "login" are related), but it significantly improves over keyword-only search for code identifiers.

---

## 6. Louvain Instead of Leiden for Community Detection

**Decision:** Use the Louvain algorithm instead of Leiden for community detection.

**Rationale:** The plan referenced the Leiden algorithm (used by GitNexus via the Graphology JS library), but:

- No .NET Leiden implementation was available
- Louvain is simpler to implement and well-documented
- For codebases (typically < 100K symbols), the quality difference between Louvain and Leiden is negligible
- Louvain has a 60-second timeout and 10-pass limit to prevent runaway execution

**Note:** The implementation is labeled as Louvain in the code but uses a modularity optimization variant suitable for small-to-medium graphs.

---

## 7. Precomputed Intelligence Over Raw Graph Queries

**Decision:** Compute communities, processes, and entry point scores at index time rather than at query time.

**Rationale:** This is the core design principle borrowed from GitNexus. By precomputing structural intelligence:

- **MCP tools return complete context in a single call** — the AI agent doesn't need to chain 10 queries to understand one function
- **Token efficiency** — precomputed answers are compact compared to raw graph edges
- **Model democratization** — even smaller LLMs can use the tools effectively because the structural reasoning is already done

The trade-off is slightly longer indexing times, but this runs once (or incrementally) and the results are cached.

---

## 8. stdio Transport for MCP

**Decision:** Use stdio (standard input/output) for MCP communication.

**Rationale:** stdio is the standard MCP transport for CLI tools. It's the simplest to configure and works with all MCP-compatible editors:

- Claude Code reads from `~/.claude/settings.json`
- VS Code reads from `.vscode/mcp.json`
- Cursor reads from `~/.cursor/mcp.json`

HTTP/SSE transport would be needed for remote/cloud deployments but adds complexity with no benefit for local developer tools.

---

## 9. Single Repository Focus (v1)

**Decision:** Support single-repository indexing only (no multi-repo in v1).

**Rationale:** Multi-repo support (like GitNexus's global registry at `~/.gitnexus/registry.json`) adds significant complexity:

- Cross-repo symbol resolution
- Registry management and staleness per repo
- MCP server managing multiple database connections

For internal WiseTech use, developers typically work within a single solution at a time. The MCP server is started in the context of one repository, and tools query that repository.

---

## 10. .NET 10 Preview

**Decision:** Target .NET 10.0 (preview).

**Rationale:** Explicit user requirement. .NET 10 is the next LTS release and provides:

- Latest C# language features
- System.CommandLine 3.0.0-preview (stable enough for tooling)
- ModelContextProtocol NuGet compatibility

**Risk:** Preview SDKs may have breaking changes before GA. Mitigated by pinning package versions and using well-established APIs (Roslyn, MSBuild, etc.).

---

## 11. Confidence Scoring on Edges

**Decision:** Every edge in the graph carries a confidence score (0.0–1.0).

**Rationale:** Not all relationships are equally certain:

| Confidence | Source |
|------------|--------|
| 1.0 | Structural (Contains, Defines, Extends) — always correct |
| 0.9 | Roslyn-resolved calls — semantic model confirmed |
| 0.5 | Unresolved calls — name match only |
| 0.5+ | Minimum for community/process detection |

This allows downstream consumers (MCP tools, community detection, process tracing) to filter by quality. The `impact` tool inherently benefits because it traverses only edges that meet the confidence threshold.

---

## 12. Next-Step Hints in Tool Responses

**Decision:** Every MCP tool response includes contextual "next step" suggestions.

**Rationale:** AI agents work best when guided toward productive follow-up actions. Without hints, an agent might:

1. Call `query("authentication")` and get results
2. Not know that `context()` exists for deeper inspection
3. Miss the `impact()` tool entirely

With hints, the natural workflow emerges: `query` → `context` → `impact`. This is especially important for smaller models that can't infer the tool chain from descriptions alone.
