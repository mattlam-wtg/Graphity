using Graphity.Core.Graph;

namespace Graphity.Storage;

/// <summary>
/// Bulk loads an in-memory <see cref="KnowledgeGraph"/> into a LiteGraph database
/// via <see cref="LiteGraphAdapter"/>.
/// </summary>
public sealed class GraphLoader
{
    /// <summary>
    /// Persists all nodes, edges, and metadata from the in-memory graph.
    /// </summary>
    public async Task LoadAsync(KnowledgeGraph graph, LiteGraphAdapter adapter, CancellationToken ct = default)
    {
        await adapter.InitializeAsync(graph.RepoName, ct);

        // Load all nodes first (edges depend on nodes existing).
        foreach (var node in graph.Nodes.Values)
        {
            ct.ThrowIfCancellationRequested();
            await adapter.UpsertNodeAsync(node, ct);
        }

        // Load all edges.
        foreach (var edge in graph.Edges.Values)
        {
            ct.ThrowIfCancellationRequested();
            await adapter.UpsertEdgeAsync(edge, ct);
        }

        // Save metadata.
        await adapter.SaveMetadataAsync("indexedAt", graph.IndexedAt.ToString("O"), ct);
        await adapter.SaveMetadataAsync("repoPath", graph.RepoPath, ct);
        await adapter.SaveMetadataAsync("repoName", graph.RepoName, ct);
        await adapter.SaveMetadataAsync("nodeCount", graph.Nodes.Count.ToString(), ct);
        await adapter.SaveMetadataAsync("edgeCount", graph.Edges.Count.ToString(), ct);
    }
}
