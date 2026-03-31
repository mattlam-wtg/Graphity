using Graphity.Core.Graph;

namespace Graphity.Core.Ingestion;

public sealed class AnalyzerResult
{
    public List<GraphNode> Nodes { get; } = new();
    public List<GraphRelationship> Edges { get; } = new();

    public static AnalyzerResult Empty => new();

    public void Merge(AnalyzerResult other)
    {
        Nodes.AddRange(other.Nodes);
        Edges.AddRange(other.Edges);
    }
}
