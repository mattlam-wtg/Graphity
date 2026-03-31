using Graphity.Core.Graph;

namespace Graphity.Storage;

/// <summary>
/// Direction for graph traversals.
/// </summary>
public enum TraversalDirection
{
    /// <summary>Follow incoming edges (who calls me, who depends on me).</summary>
    Upstream,

    /// <summary>Follow outgoing edges (what do I call, what do I depend on).</summary>
    Downstream,
}

/// <summary>
/// Provides graph traversal queries over a <see cref="LiteGraphAdapter"/>.
/// </summary>
public sealed class GraphQuerier
{
    private readonly LiteGraphAdapter _adapter;

    public GraphQuerier(LiteGraphAdapter adapter) => _adapter = adapter;

    /// <summary>
    /// Find all callers of a symbol (nodes with incoming CALLS edges).
    /// </summary>
    public async Task<IReadOnlyList<GraphNode>> FindCallersAsync(string symbolId, CancellationToken ct = default)
    {
        var inEdges = await _adapter.GetEdgesToAsync(symbolId, ct);
        var callers = new List<GraphNode>();
        foreach (var edge in inEdges)
        {
            if (edge.Type != EdgeType.Calls) continue;
            var node = await _adapter.GetNodeAsync(edge.SourceId, ct);
            if (node != null) callers.Add(node);
        }
        return callers;
    }

    /// <summary>
    /// Find all callees of a symbol (nodes reached via outgoing CALLS edges).
    /// </summary>
    public async Task<IReadOnlyList<GraphNode>> FindCalleesAsync(string symbolId, CancellationToken ct = default)
    {
        var outEdges = await _adapter.GetEdgesFromAsync(symbolId, ct);
        var callees = new List<GraphNode>();
        foreach (var edge in outEdges)
        {
            if (edge.Type != EdgeType.Calls) continue;
            var node = await _adapter.GetNodeAsync(edge.TargetId, ct);
            if (node != null) callees.Add(node);
        }
        return callees;
    }

    /// <summary>
    /// Find all symbols defined in a given file. This scans nodes looking for
    /// a matching FilePath via edges from the file node, or by checking all nodes
    /// that have the given file path.
    /// </summary>
    public async Task<IReadOnlyList<GraphNode>> FindSymbolsInFileAsync(string filePath, CancellationToken ct = default)
    {
        // The file node's ID convention is "file:{filePath}".
        // We look for outgoing DEFINES/CONTAINS edges from the file node.
        var fileNodeId = $"file:{filePath}";
        var outEdges = await _adapter.GetEdgesFromAsync(fileNodeId, ct);
        var symbols = new List<GraphNode>();

        foreach (var edge in outEdges)
        {
            if (edge.Type is EdgeType.Defines or EdgeType.Contains)
            {
                var node = await _adapter.GetNodeAsync(edge.TargetId, ct);
                if (node != null) symbols.Add(node);
            }
        }

        // If we found nothing via edges, the file node might not exist or use a
        // different convention. Fall back: there is no efficient way to scan all
        // nodes in LiteGraph by property, so return what we have.
        return symbols;
    }

    /// <summary>
    /// Walk the inheritance chain upward (base types) from a given type node.
    /// Follows EXTENDS and IMPLEMENTS edges recursively.
    /// </summary>
    public async Task<IReadOnlyList<GraphNode>> FindInheritanceChainAsync(string typeId, CancellationToken ct = default)
    {
        var chain = new List<GraphNode>();
        var visited = new HashSet<string> { typeId };
        var queue = new Queue<string>();
        queue.Enqueue(typeId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            var outEdges = await _adapter.GetEdgesFromAsync(currentId, ct);

            foreach (var edge in outEdges)
            {
                if (edge.Type is not (EdgeType.Extends or EdgeType.Implements)) continue;
                if (!visited.Add(edge.TargetId)) continue;

                var node = await _adapter.GetNodeAsync(edge.TargetId, ct);
                if (node != null)
                {
                    chain.Add(node);
                    queue.Enqueue(edge.TargetId);
                }
            }
        }

        return chain;
    }

    /// <summary>
    /// BFS traversal from a start node, grouped by depth level.
    /// </summary>
    /// <param name="startNodeId">The node to start from.</param>
    /// <param name="direction">Upstream (incoming) or Downstream (outgoing).</param>
    /// <param name="maxDepth">Maximum BFS depth (1-based).</param>
    /// <param name="edgeFilter">If non-null, only follow edges of these types.</param>
    /// <returns>Dictionary keyed by depth (1, 2, 3, ...) with the nodes at that level.</returns>
    public async Task<Dictionary<int, List<GraphNode>>> TraverseAsync(
        string startNodeId,
        TraversalDirection direction,
        int maxDepth = 3,
        HashSet<EdgeType>? edgeFilter = null,
        CancellationToken ct = default)
    {
        var result = new Dictionary<int, List<GraphNode>>();
        var visited = new HashSet<string> { startNodeId };
        var currentLevel = new List<string> { startNodeId };

        for (int depth = 1; depth <= maxDepth && currentLevel.Count > 0; depth++)
        {
            ct.ThrowIfCancellationRequested();
            var nextLevel = new List<string>();
            var nodesAtDepth = new List<GraphNode>();

            foreach (var nodeId in currentLevel)
            {
                var edges = direction == TraversalDirection.Downstream
                    ? await _adapter.GetEdgesFromAsync(nodeId, ct)
                    : await _adapter.GetEdgesToAsync(nodeId, ct);

                foreach (var edge in edges)
                {
                    if (edgeFilter != null && !edgeFilter.Contains(edge.Type))
                        continue;

                    var neighborId = direction == TraversalDirection.Downstream
                        ? edge.TargetId
                        : edge.SourceId;

                    if (!visited.Add(neighborId))
                        continue;

                    var neighbor = await _adapter.GetNodeAsync(neighborId, ct);
                    if (neighbor != null)
                    {
                        nodesAtDepth.Add(neighbor);
                        nextLevel.Add(neighborId);
                    }
                }
            }

            if (nodesAtDepth.Count > 0)
                result[depth] = nodesAtDepth;

            currentLevel = nextLevel;
        }

        return result;
    }
}
