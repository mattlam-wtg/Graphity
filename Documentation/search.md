# Search & Embeddings

Graphity uses a hybrid search system combining keyword-based BM25 search with semantic vector search, fused via Reciprocal Rank Fusion (RRF).

## BM25 Full-Text Search

**File:** `src/Graphity.Search/Bm25Index.cs`

BM25 (Best Matching 25) is a probabilistic ranking function used in information retrieval. Graphity implements it from scratch for full control over tokenization and scoring.

### Parameters

| Parameter | Value | Purpose |
|-----------|-------|---------|
| k1 | 1.2 | Term frequency saturation |
| b | 0.75 | Document length normalization |

### Tokenization

The tokenizer is designed for code identifiers and applies these transformations in order:

1. **Lowercase** the entire input
2. **Split on non-alphanumeric characters** (dots, underscores, hyphens, etc.)
3. **Split camelCase** — `getUserById` becomes `["get", "user", "by", "id"]`
4. **Filter tokens shorter than 2 characters**

This is critical for code search because developers rarely search for exact identifiers — they search for partial names like "user service" which should match `UserService`, `IUserService`, and `getUserById`.

The tokenizer is shared between `Bm25Index` and `OnnxEmbedder` (via `Bm25Index.Tokenize()`) to ensure consistent text representation.

### What Gets Indexed

Each `GraphNode` is indexed with a searchable text representation combining:
- Node name
- Fully qualified name
- File path
- Node type

### Persistence

The BM25 index is serialized to JSON (`bm25-index.json`) and loaded on MCP server startup. The index includes all documents, term frequencies, and IDF values needed for scoring.

---

## Semantic Embeddings

**File:** `src/Graphity.Search/OnnxEmbedder.cs`

### ONNX Model

Graphity is designed to use the `all-MiniLM-L6-v2` ONNX model for generating 384-dimensional semantic embeddings. The model path is `~/.graphity/models/all-MiniLM-L6-v2.onnx`.

**Current state:** The ONNX model must be manually placed at the model path. When unavailable, Graphity automatically falls back to hash-based embeddings (see below).

### Hash-Based Fallback

When the ONNX model is not available, `OnnxEmbedder` uses a deterministic hash-based embedding scheme:

1. Tokenize the input text (using the same tokenizer as BM25)
2. For each token, compute a hash-based 384-dimensional vector
3. Sum all token vectors
4. L2-normalize the result

This provides consistent, deterministic embeddings that capture token overlap between queries and documents. While not as semantically rich as a trained neural model, hash embeddings still improve search quality over keyword-only search because they handle:
- Partial token matches across different identifier formats
- Consistent similarity scoring for related terms

### Node Text Generation

The `NodeToText()` method creates a searchable text representation for each node:

```
{Type}: {Name}
Full name: {FullName}
File: {FilePath}
{Content (first 500 chars)}
```

This representation is used for both embedding generation and BM25 indexing.

---

## Hybrid Search (RRF Fusion)

**File:** `src/Graphity.Search/HybridSearch.cs`

Hybrid search combines BM25 and semantic search results using Reciprocal Rank Fusion:

### Algorithm

1. Run BM25 search and semantic (embedding) search independently
2. For each result set, assign a rank (1 = best)
3. Compute RRF score: `score = 1 / (k + rank)` where `k = 60`
4. Sum RRF scores for items appearing in both result sets
5. Sort by combined score (descending)

### Why RRF?

RRF is preferred over score normalization because:
- BM25 scores and cosine similarity scores are on completely different scales
- RRF only uses rank positions, making it scale-invariant
- Items in both rankings get naturally boosted (summed scores)
- Items in only one ranking still rank reasonably

### Semantic Search Implementation

Semantic search uses brute-force k-NN with cosine similarity:

1. Embed the query text using the same embedder (ONNX or hash fallback)
2. Compute cosine similarity against all stored embeddings
3. Filter by minimum threshold (0.3)
4. Return top-k results

The cosine similarity computation includes a guard against zero-norm vectors to prevent NaN scores.

### Persistence

Embeddings are stored in a binary file (`embeddings.bin`) containing:
- Number of embeddings (int32)
- For each embedding: node ID (length-prefixed string) + 384 float32 values

The BM25 index is stored separately in `bm25-index.json`.

---

## Search in MCP Tools

The `QueryTool` uses hybrid search when embeddings are available, falling back to BM25-only:

```
if hybrid search has embeddings → use HybridSearch.Search()
else → use Bm25Index.Search()
```

The `ContextTool` and `ImpactTool` also use BM25 search for symbol name resolution when an exact node ID match is not found.
