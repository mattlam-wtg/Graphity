using Graphity.Core.Graph;

namespace Graphity.Storage.Tests;

public class LiteGraphAdapterTests
{
    private static string GetTempDbPath() => Path.Combine(Path.GetTempPath(), $"graphity_test_{Guid.NewGuid():N}.db");

    private static GraphNode MakeNode(string id, string name, NodeType type = NodeType.Class)
        => new() { Id = id, Name = name, Type = type, FilePath = $"/src/{name}.cs" };

    private static GraphRelationship MakeEdge(string id, string sourceId, string targetId, EdgeType type = EdgeType.Calls)
        => new() { Id = id, SourceId = sourceId, TargetId = targetId, Type = type };

    [Fact]
    public async Task UpsertNode_and_GetNode_roundtrip()
    {
        var dbPath = GetTempDbPath();
        try
        {
            using var adapter = new LiteGraphAdapter(dbPath);
            await adapter.InitializeAsync("test-graph");

            var node = MakeNode("n1", "UserService");
            await adapter.UpsertNodeAsync(node);

            var retrieved = await adapter.GetNodeAsync("n1");
            Assert.NotNull(retrieved);
            Assert.Equal("n1", retrieved.Id);
            Assert.Equal("UserService", retrieved.Name);
            Assert.Equal(NodeType.Class, retrieved.Type);
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task UpsertEdge_and_GetEdgesFrom_GetEdgesTo_roundtrip()
    {
        var dbPath = GetTempDbPath();
        try
        {
            using var adapter = new LiteGraphAdapter(dbPath);
            await adapter.InitializeAsync("test-graph");

            await adapter.UpsertNodeAsync(MakeNode("n1", "Caller"));
            await adapter.UpsertNodeAsync(MakeNode("n2", "Callee"));
            await adapter.UpsertEdgeAsync(MakeEdge("e1", "n1", "n2", EdgeType.Calls));

            var fromEdges = await adapter.GetEdgesFromAsync("n1");
            Assert.Single(fromEdges);
            Assert.Equal("n1", fromEdges[0].SourceId);
            Assert.Equal("n2", fromEdges[0].TargetId);
            Assert.Equal(EdgeType.Calls, fromEdges[0].Type);

            var toEdges = await adapter.GetEdgesToAsync("n2");
            Assert.Single(toEdges);
            Assert.Equal("n1", toEdges[0].SourceId);
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task DeleteNode_removes_the_node()
    {
        var dbPath = GetTempDbPath();
        try
        {
            using var adapter = new LiteGraphAdapter(dbPath);
            await adapter.InitializeAsync("test-graph");

            await adapter.UpsertNodeAsync(MakeNode("n1", "ToDelete"));
            Assert.NotNull(await adapter.GetNodeAsync("n1"));

            await adapter.DeleteNodeAsync("n1");
            Assert.Null(await adapter.GetNodeAsync("n1"));
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task Multiple_nodes_can_be_stored_and_retrieved()
    {
        var dbPath = GetTempDbPath();
        try
        {
            using var adapter = new LiteGraphAdapter(dbPath);
            await adapter.InitializeAsync("test-graph");

            await adapter.UpsertNodeAsync(MakeNode("n1", "Service1"));
            await adapter.UpsertNodeAsync(MakeNode("n2", "Service2"));
            await adapter.UpsertNodeAsync(MakeNode("n3", "Service3"));

            Assert.Equal(3, adapter.NodeCount);
            Assert.NotNull(await adapter.GetNodeAsync("n1"));
            Assert.NotNull(await adapter.GetNodeAsync("n2"));
            Assert.NotNull(await adapter.GetNodeAsync("n3"));
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task Metadata_save_and_get_roundtrip()
    {
        var dbPath = GetTempDbPath();
        try
        {
            using var adapter = new LiteGraphAdapter(dbPath);
            await adapter.InitializeAsync("test-graph");

            await adapter.SaveMetadataAsync("version", "1.0");
            var value = await adapter.GetMetadataAsync("version");
            Assert.Equal("1.0", value);
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
