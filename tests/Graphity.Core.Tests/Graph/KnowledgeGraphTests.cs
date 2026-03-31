using Graphity.Core.Graph;

namespace Graphity.Core.Tests.Graph;

public class KnowledgeGraphTests
{
    private readonly KnowledgeGraph _graph = new();

    private static GraphNode MakeNode(string id, NodeType type = NodeType.Class, string? filePath = null)
        => new() { Id = id, Name = id, Type = type, FilePath = filePath };

    private static GraphRelationship MakeEdge(string id, string source, string target, EdgeType type = EdgeType.Calls)
        => new() { Id = id, SourceId = source, TargetId = target, Type = type };

    [Fact]
    public void AddNode_Succeeds_And_CanBeRetrieved()
    {
        var node = MakeNode("n1");
        Assert.True(_graph.AddNode(node));
        Assert.Same(node, _graph.GetNode("n1"));
        Assert.Single(_graph.Nodes);
    }

    [Fact]
    public void AddNode_DuplicateId_ReturnsFalse()
    {
        _graph.AddNode(MakeNode("n1"));
        Assert.False(_graph.AddNode(MakeNode("n1")));
        Assert.Single(_graph.Nodes);
    }

    [Fact]
    public void AddEdge_Succeeds_And_ShowsInOutgoingAndIncoming()
    {
        _graph.AddNode(MakeNode("a"));
        _graph.AddNode(MakeNode("b"));
        var edge = MakeEdge("e1", "a", "b");

        Assert.True(_graph.AddEdge(edge));
        Assert.Single(_graph.Edges);

        var outgoing = _graph.GetOutgoingEdges("a").ToList();
        Assert.Single(outgoing);
        Assert.Equal("e1", outgoing[0].Id);

        var incoming = _graph.GetIncomingEdges("b").ToList();
        Assert.Single(incoming);
        Assert.Equal("e1", incoming[0].Id);
    }

    [Fact]
    public void AddEdge_DuplicateId_ReturnsFalse()
    {
        _graph.AddNode(MakeNode("a"));
        _graph.AddNode(MakeNode("b"));
        _graph.AddEdge(MakeEdge("e1", "a", "b"));

        Assert.False(_graph.AddEdge(MakeEdge("e1", "a", "b")));
        Assert.Single(_graph.Edges);
    }

    [Fact]
    public void GetOutgoingEdges_ReturnsCorrectEdges()
    {
        _graph.AddNode(MakeNode("a"));
        _graph.AddNode(MakeNode("b"));
        _graph.AddNode(MakeNode("c"));
        _graph.AddEdge(MakeEdge("e1", "a", "b"));
        _graph.AddEdge(MakeEdge("e2", "a", "c"));
        _graph.AddEdge(MakeEdge("e3", "b", "c")); // not from 'a'

        var outgoing = _graph.GetOutgoingEdges("a").ToList();
        Assert.Equal(2, outgoing.Count);
        Assert.Contains(outgoing, e => e.Id == "e1");
        Assert.Contains(outgoing, e => e.Id == "e2");
    }

    [Fact]
    public void GetIncomingEdges_ReturnsCorrectEdges()
    {
        _graph.AddNode(MakeNode("a"));
        _graph.AddNode(MakeNode("b"));
        _graph.AddNode(MakeNode("c"));
        _graph.AddEdge(MakeEdge("e1", "a", "c"));
        _graph.AddEdge(MakeEdge("e2", "b", "c"));
        _graph.AddEdge(MakeEdge("e3", "a", "b")); // not to 'c'

        var incoming = _graph.GetIncomingEdges("c").ToList();
        Assert.Equal(2, incoming.Count);
        Assert.Contains(incoming, e => e.Id == "e1");
        Assert.Contains(incoming, e => e.Id == "e2");
    }

    [Fact]
    public void GetOutgoingEdges_NoEdges_ReturnsEmpty()
    {
        _graph.AddNode(MakeNode("a"));
        Assert.Empty(_graph.GetOutgoingEdges("a"));
        Assert.Empty(_graph.GetOutgoingEdges("nonexistent"));
    }

    [Fact]
    public void GetNodesByType_FiltersCorrectly()
    {
        _graph.AddNode(MakeNode("c1", NodeType.Class));
        _graph.AddNode(MakeNode("c2", NodeType.Class));
        _graph.AddNode(MakeNode("m1", NodeType.Method));

        var classes = _graph.GetNodesByType(NodeType.Class).ToList();
        Assert.Equal(2, classes.Count);
        Assert.All(classes, n => Assert.Equal(NodeType.Class, n.Type));
    }

    [Fact]
    public void GetNodesByFile_FiltersCorrectly()
    {
        _graph.AddNode(MakeNode("n1", filePath: "src/A.cs"));
        _graph.AddNode(MakeNode("n2", filePath: "src/A.cs"));
        _graph.AddNode(MakeNode("n3", filePath: "src/B.cs"));

        var results = _graph.GetNodesByFile("src/A.cs").ToList();
        Assert.Equal(2, results.Count);
        Assert.All(results, n => Assert.Equal("src/A.cs", n.FilePath));
    }

    [Fact]
    public void RemoveNodesByFile_RemovesNodesAndAssociatedEdges()
    {
        _graph.AddNode(MakeNode("n1", filePath: "src/A.cs"));
        _graph.AddNode(MakeNode("n2", filePath: "src/A.cs"));
        _graph.AddNode(MakeNode("n3", filePath: "src/B.cs"));
        _graph.AddEdge(MakeEdge("e1", "n1", "n2"));
        _graph.AddEdge(MakeEdge("e2", "n1", "n3"));
        _graph.AddEdge(MakeEdge("e3", "n3", "n1"));

        _graph.RemoveNodesByFile("src/A.cs");

        Assert.Single(_graph.Nodes);
        Assert.Equal("n3", _graph.Nodes.Keys.Single());
        // All edges involving removed nodes should be gone
        Assert.Empty(_graph.Edges);
    }

    [Fact]
    public void RemoveNode_RemovesEdgesFromBothIndexes()
    {
        _graph.AddNode(MakeNode("a"));
        _graph.AddNode(MakeNode("b"));
        _graph.AddNode(MakeNode("c"));
        _graph.AddEdge(MakeEdge("e1", "a", "b"));
        _graph.AddEdge(MakeEdge("e2", "b", "c"));

        _graph.RemoveNode("b");

        Assert.Null(_graph.GetNode("b"));
        Assert.Empty(_graph.Edges);
        Assert.Empty(_graph.GetOutgoingEdges("a"));
        Assert.Empty(_graph.GetIncomingEdges("c"));
    }

    [Fact]
    public void RemoveNode_NonExistent_DoesNothing()
    {
        _graph.AddNode(MakeNode("a"));
        _graph.RemoveNode("nonexistent");
        Assert.Single(_graph.Nodes);
    }

    [Fact]
    public void Clear_EmptiesEverything()
    {
        _graph.AddNode(MakeNode("a"));
        _graph.AddNode(MakeNode("b"));
        _graph.AddEdge(MakeEdge("e1", "a", "b"));

        _graph.Clear();

        Assert.Empty(_graph.Nodes);
        Assert.Empty(_graph.Edges);
        Assert.Empty(_graph.GetOutgoingEdges("a"));
        Assert.Empty(_graph.GetIncomingEdges("b"));
    }
}
