using Graphity.Core.Graph;

namespace Graphity.Storage.Tests;

public class GraphQuerierTests
{
    private static string GetTempDbPath() => Path.Combine(Path.GetTempPath(), $"graphity_test_{Guid.NewGuid():N}.db");

    private static GraphNode MakeNode(string id, string name, NodeType type = NodeType.Method)
        => new() { Id = id, Name = name, Type = type };

    private static GraphRelationship MakeEdge(string id, string sourceId, string targetId, EdgeType type = EdgeType.Calls)
        => new() { Id = id, SourceId = sourceId, TargetId = targetId, Type = type };

    /// <summary>
    /// Builds a small graph: A -> B -> C (all CALLS edges)
    /// </summary>
    private static async Task<(LiteGraphAdapter adapter, GraphQuerier querier)> BuildTestGraphAsync(string dbPath)
    {
        var adapter = new LiteGraphAdapter(dbPath);
        await adapter.InitializeAsync("test-graph");

        await adapter.UpsertNodeAsync(MakeNode("A", "MethodA"));
        await adapter.UpsertNodeAsync(MakeNode("B", "MethodB"));
        await adapter.UpsertNodeAsync(MakeNode("C", "MethodC"));

        await adapter.UpsertEdgeAsync(MakeEdge("e1", "A", "B"));
        await adapter.UpsertEdgeAsync(MakeEdge("e2", "B", "C"));

        return (adapter, new GraphQuerier(adapter));
    }

    [Fact]
    public async Task FindCallers_returns_nodes_that_call_the_target()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var (adapter, querier) = await BuildTestGraphAsync(dbPath);
            using (adapter)
            {
                var callers = await querier.FindCallersAsync("B");
                Assert.Single(callers);
                Assert.Equal("A", callers[0].Id);
            }
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task FindCallees_returns_nodes_called_by_the_target()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var (adapter, querier) = await BuildTestGraphAsync(dbPath);
            using (adapter)
            {
                var callees = await querier.FindCalleesAsync("B");
                Assert.Single(callees);
                Assert.Equal("C", callees[0].Id);
            }
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task TraverseAsync_respects_maxDepth()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var (adapter, querier) = await BuildTestGraphAsync(dbPath);
            using (adapter)
            {
                // From A downstream with maxDepth=1 should only find B, not C.
                var result = await querier.TraverseAsync("A", TraversalDirection.Downstream, maxDepth: 1);
                Assert.True(result.ContainsKey(1));
                Assert.Single(result[1]);
                Assert.Equal("B", result[1][0].Id);
                Assert.False(result.ContainsKey(2));
            }
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task TraverseAsync_groups_by_depth_level_correctly()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var (adapter, querier) = await BuildTestGraphAsync(dbPath);
            using (adapter)
            {
                // From A downstream with maxDepth=3 should find B at depth 1, C at depth 2.
                var result = await querier.TraverseAsync("A", TraversalDirection.Downstream, maxDepth: 3);

                Assert.True(result.ContainsKey(1));
                Assert.Single(result[1]);
                Assert.Equal("B", result[1][0].Id);

                Assert.True(result.ContainsKey(2));
                Assert.Single(result[2]);
                Assert.Equal("C", result[2][0].Id);

                Assert.False(result.ContainsKey(3));
            }
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task TraverseAsync_handles_cycles()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var adapter = new LiteGraphAdapter(dbPath);
            await adapter.InitializeAsync("test-graph");

            await adapter.UpsertNodeAsync(MakeNode("X", "MethodX"));
            await adapter.UpsertNodeAsync(MakeNode("Y", "MethodY"));

            // Create a cycle: X -> Y -> X
            await adapter.UpsertEdgeAsync(MakeEdge("e1", "X", "Y"));
            await adapter.UpsertEdgeAsync(MakeEdge("e2", "Y", "X"));

            var querier = new GraphQuerier(adapter);
            using (adapter)
            {
                // Should terminate without infinite loop thanks to visited set.
                var result = await querier.TraverseAsync("X", TraversalDirection.Downstream, maxDepth: 10);

                // Y at depth 1, X is already visited so won't appear again.
                Assert.True(result.ContainsKey(1));
                Assert.Single(result[1]);
                Assert.Equal("Y", result[1][0].Id);
                Assert.False(result.ContainsKey(2));
            }
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }
}
