# Graphity Documentation

Graphity is a .NET 10 developer tool that indexes codebases into knowledge graphs and exposes them via [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) tools for AI coding agents. It gives tools like Claude Code, GitHub Copilot, and Cursor structural awareness of your codebase — enabling them to answer questions like "what breaks if I change this?" or "what's the full call chain from this endpoint?" without crawling dozens of files.

## Documentation Index

| Document | Description |
|----------|-------------|
| [Getting Started](getting-started.md) | Installation, first index, connecting to your editor |
| [Architecture](architecture.md) | Solution structure, project layout, data flow |
| [Graph Schema](graph-schema.md) | Node types, edge types, properties, ID conventions |
| [Analyzers](analyzers.md) | How C#, TypeScript, and SQL code is parsed and indexed |
| [Search & Embeddings](search.md) | BM25, ONNX embeddings, hybrid search with RRF fusion |
| [Detection](detection.md) | Community clustering (Louvain) and execution flow tracing |
| [MCP Tools](mcp-tools.md) | The 4 MCP tools and 2 resources exposed to AI agents |
| [Storage](storage.md) | LiteGraph persistence, incremental indexing, metadata |
| [CLI Reference](cli-reference.md) | All commands, options, and configuration |
| [Design Decisions](design-decisions.md) | Key technical choices and their rationale |

## Quick Start

```bash
# Install as a global tool
dotnet tool install -g Graphity

# Index your codebase
cd /path/to/your/solution
graphity analyze

# Start the MCP server (AI agents connect via stdio)
graphity mcp

# Auto-configure your editor
graphity setup
```

See [Getting Started](getting-started.md) for detailed instructions.
