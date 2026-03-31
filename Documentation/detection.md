# Detection: Communities & Processes

Graphity performs two types of graph-level analysis at index time: **community detection** (clustering code into functional areas) and **process detection** (tracing execution flows). These are computed after all analyzers have run and produce additional nodes and edges in the knowledge graph.

## Community Detection

**File:** `src/Graphity.Core/Detection/CommunityDetector.cs`
**Algorithm:** Louvain modularity optimization

### Purpose

Community detection groups related symbols into functional clusters — analogous to "modules" or "feature areas" in a codebase. For example, it might identify clusters like "Authentication", "Order Processing", or "Data Access".

### Algorithm

The [Louvain method](https://en.wikipedia.org/wiki/Louvain_method) optimizes [modularity](https://en.wikipedia.org/wiki/Modularity_(networks)) — a measure of how densely connected nodes within communities are compared to random expectation.

**Steps:**

1. **Build undirected adjacency graph** from `Calls`, `Extends`, and `Implements` edges with confidence >= 0.5
2. **Initialize** each symbol as its own community
3. **Iterative optimization:** For each node, compute the modularity gain of moving it to each neighboring community. Move it to the community with the highest positive gain. Repeat until no further improvement (or max 10 passes, or 60-second timeout).
4. **Create Community nodes** for each cluster with >= 2 members
5. **Create MemberOf edges** from each symbol to its community

### Large-Graph Optimization

For graphs with more than 10,000 symbols, a performance optimization filters out degree-1 nodes (nodes with only one connection) before running the algorithm. These nodes are typically leaf functions that don't contribute meaningful community structure.

### Community Labeling

Communities are labeled heuristically:
1. **Most common folder name** among members (e.g., "Services", "Controllers")
2. **Common name prefix** if no dominant folder (e.g., "User" for UserService, UserRepository, UserController)
3. **Fallback:** `Cluster_N` where N is the community index

### Cohesion Score

Each community gets a cohesion score (0.0–1.0) representing the fraction of edges that stay within the community. A cohesion of 0.8 means 80% of all edges from community members connect to other community members — indicating a tightly coupled module.

---

## Entry Point Scoring

**File:** `src/Graphity.Core/Detection/EntryPointScorer.cs`

### Purpose

Identifies methods and functions that are likely "entry points" — the starting points of meaningful execution flows (API controllers, message handlers, main methods, etc.).

### Scoring Formula

```
score = baseScore * exportMultiplier * nameMultiplier
```

| Factor | Calculation |
|--------|-------------|
| **Base score** | `calleeCount / (callerCount + 1)` — high outgoing calls, few incoming callers |
| **Export multiplier** | 2.0x if the symbol is public/exported, 1.0x otherwise |
| **Name bonus** (1.5x) | Symbols named `Handle*`, `Execute*`, `*Controller`, or `Main` |
| **Name penalty** (0.3x) | Symbols named `Get*`, `Set*`, `*Helper`, or `*Util` |

### Exclusions

The scorer excludes:
- **Leaf functions** — functions with no outgoing calls (they're not entry points, they're terminals)
- **Test files** — files in test directories are excluded from entry point consideration
- Node types other than `Method`, `Function`, and `Constructor`

---

## Process Detection

**File:** `src/Graphity.Core/Detection/ProcessDetector.cs`

### Purpose

Traces execution flows from entry points through call chains, producing `Process` nodes that represent end-to-end paths like "LoginController.Login → AuthService.Authenticate → UserRepository.FindByEmail → Database".

### Algorithm

1. **Select top entry points** from the scored candidates
2. **BFS traversal** from each entry point, following `Calls` edges:
   - Maximum depth: 10 steps
   - Maximum branching factor: 4 callees per node (prevents explosion)
   - Minimum confidence: 0.5 on edges
   - Minimum path length: 3 steps (filters trivial 2-step traces)
3. **Deduplication** (two passes):
   - Pass 1: Remove traces that are subsets of longer traces
   - Pass 2: For each unique entry→terminal pair, keep only the longest trace
4. **Limit** to 75 processes total, prioritized by length

### Output

For each detected process:
- A `Process` node is created with metadata (step count, entry point, terminal)
- `StepInProcess` edges connect each participating symbol to the process, with 1-indexed `Step` values

### Example

Given this call chain:
```
OrderController.CreateOrder → OrderService.ProcessOrder → InventoryService.CheckStock → OrderRepository.Save
```

The process detector creates:
- 1 `Process` node: "CreateOrder → Save"
- 4 `StepInProcess` edges: OrderController (step 1), OrderService (step 2), InventoryService (step 3), OrderRepository (step 4)

### Edge Direction Convention

`StepInProcess` edges point from the **symbol** to the **process** (Symbol → Process), consistent with the schema convention where `MemberOf` edges also point from member to group.
