using Graphity.Core.Graph;
using Graphity.Core.Ingestion;

namespace Graphity.Core.Tests.Ingestion;

public class AnalyzerResultTests
{
    [Fact]
    public void Merge_CombinesNodesAndEdgesFromBothResults()
    {
        var result1 = new AnalyzerResult();
        result1.Nodes.Add(new GraphNode { Id = "n1", Name = "A", Type = NodeType.Class });
        result1.Edges.Add(new GraphRelationship { Id = "e1", SourceId = "n1", TargetId = "n2", Type = EdgeType.Calls });

        var result2 = new AnalyzerResult();
        result2.Nodes.Add(new GraphNode { Id = "n2", Name = "B", Type = NodeType.Method });
        result2.Edges.Add(new GraphRelationship { Id = "e2", SourceId = "n2", TargetId = "n1", Type = EdgeType.Calls });

        result1.Merge(result2);

        Assert.Equal(2, result1.Nodes.Count);
        Assert.Equal(2, result1.Edges.Count);
        Assert.Contains(result1.Nodes, n => n.Id == "n1");
        Assert.Contains(result1.Nodes, n => n.Id == "n2");
        Assert.Contains(result1.Edges, e => e.Id == "e1");
        Assert.Contains(result1.Edges, e => e.Id == "e2");
    }

    [Fact]
    public void Empty_ReturnsEmptyResult()
    {
        var empty = AnalyzerResult.Empty;

        Assert.Empty(empty.Nodes);
        Assert.Empty(empty.Edges);
    }

    [Fact]
    public void Empty_ReturnsFreshInstanceEachTime()
    {
        var a = AnalyzerResult.Empty;
        var b = AnalyzerResult.Empty;
        Assert.NotSame(a, b);
    }
}
