using Graphity.Core.Graph;
using Graphity.Core.Incremental;
using Graphity.Core.Ingestion;

namespace Graphity.Core.Tests.Incremental;

public class IncrementalPipelineTests : IDisposable
{
    private readonly string _tempDir;

    public IncrementalPipelineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "graphity-incr-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    private static GraphNode MakeNode(string id, NodeType type = NodeType.Class, string? filePath = null)
        => new() { Id = id, Name = id, Type = type, FilePath = filePath, Language = "csharp" };

    private static GraphRelationship MakeEdge(string id, string source, string target, EdgeType type = EdgeType.Calls)
        => new() { Id = id, SourceId = source, TargetId = target, Type = type };

    private KnowledgeGraph BuildGraph(params (GraphNode node, GraphRelationship? edge)[] items)
    {
        var graph = new KnowledgeGraph { RepoPath = _tempDir };
        foreach (var (node, edge) in items)
        {
            graph.AddNode(node);
            if (edge != null) graph.AddEdge(edge);
        }
        return graph;
    }

    [Fact]
    public async Task DeletedFiles_HaveNodesRemoved()
    {
        var graph = new KnowledgeGraph { RepoPath = _tempDir };
        graph.AddNode(MakeNode("Class:A", filePath: "src/A.cs"));
        graph.AddNode(MakeNode("Class:B", filePath: "src/B.cs"));
        graph.AddEdge(MakeEdge("e1", "Class:A", "Class:B"));

        var changes = new ChangeDetector.ChangeSet(
            Added: [],
            Modified: [],
            Deleted: ["src/A.cs"],
            CurrentCommitHash: "abc123");

        var pipeline = new IncrementalPipeline();
        await pipeline.RunIncrementalAsync(graph, changes);

        Assert.DoesNotContain("Class:A", graph.Nodes.Keys);
        Assert.Contains("Class:B", graph.Nodes.Keys);
        // Edge from deleted node should be removed
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task ModifiedFiles_AreReanalyzed()
    {
        // Create a real .cs file in the temp dir for re-analysis
        var srcDir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "A.cs"), "namespace Test; public class AModified { }");

        var graph = new KnowledgeGraph { RepoPath = _tempDir };
        graph.AddNode(MakeNode("Class:OldA", filePath: "src/A.cs"));
        graph.AddNode(MakeNode("Class:B", filePath: "src/B.cs"));

        var changes = new ChangeDetector.ChangeSet(
            Added: [],
            Modified: ["src/A.cs"],
            Deleted: [],
            CurrentCommitHash: "abc123");

        var pipeline = new IncrementalPipeline();
        // No analyzer registered so old nodes are removed but no new code nodes added
        // (file/folder nodes will be created though)
        await pipeline.RunIncrementalAsync(graph, changes);

        // Old node from modified file should be removed
        Assert.DoesNotContain("Class:OldA", graph.Nodes.Keys);
        // Unmodified file's node should remain
        Assert.Contains("Class:B", graph.Nodes.Keys);
        // File node should be recreated for the modified file
        Assert.Contains("File:src/A.cs", graph.Nodes.Keys);
    }

    [Fact]
    public async Task AddedFiles_GetNewNodes()
    {
        var srcDir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "New.cs"), "namespace Test; public class New { }");

        var graph = new KnowledgeGraph { RepoPath = _tempDir };
        graph.AddNode(MakeNode("Class:Existing", filePath: "src/Existing.cs"));

        var changes = new ChangeDetector.ChangeSet(
            Added: ["src/New.cs"],
            Modified: [],
            Deleted: [],
            CurrentCommitHash: "abc123");

        var pipeline = new IncrementalPipeline();
        await pipeline.RunIncrementalAsync(graph, changes);

        // Existing node should remain
        Assert.Contains("Class:Existing", graph.Nodes.Keys);
        // New file node should be created
        Assert.Contains("File:src/New.cs", graph.Nodes.Keys);
    }

    [Fact]
    public async Task UnchangedNodes_ArePreserved()
    {
        var srcDir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Changed.cs"), "// changed");

        var graph = new KnowledgeGraph { RepoPath = _tempDir };
        graph.AddNode(MakeNode("Class:Unchanged1", filePath: "src/Unchanged1.cs"));
        graph.AddNode(MakeNode("Class:Unchanged2", filePath: "src/Unchanged2.cs"));
        graph.AddNode(MakeNode("Class:Changed", filePath: "src/Changed.cs"));
        graph.AddEdge(MakeEdge("e1", "Class:Unchanged1", "Class:Unchanged2"));

        var changes = new ChangeDetector.ChangeSet(
            Added: [],
            Modified: ["src/Changed.cs"],
            Deleted: [],
            CurrentCommitHash: "abc123");

        var pipeline = new IncrementalPipeline();
        await pipeline.RunIncrementalAsync(graph, changes);

        // Unchanged nodes and edges should be fully preserved
        Assert.Contains("Class:Unchanged1", graph.Nodes.Keys);
        Assert.Contains("Class:Unchanged2", graph.Nodes.Keys);
        Assert.Contains("e1", graph.Edges.Keys);
        // Changed file's old node should be removed
        Assert.DoesNotContain("Class:Changed", graph.Nodes.Keys);
    }
}
