using Graphity.Core.Detection;
using Graphity.Core.Graph;

namespace Graphity.Core.Tests.Detection;

public class CommunityDetectorTests
{
    private static GraphNode MakeNode(string id, NodeType type = NodeType.Method, string? filePath = null)
        => new() { Id = id, Name = id, Type = type, FilePath = filePath };

    private static GraphRelationship MakeEdge(string source, string target, EdgeType type = EdgeType.Calls, double confidence = 1.0)
        => new() { Id = $"{source}->{target}", SourceId = source, TargetId = target, Type = type, Confidence = confidence };

    [Fact]
    public void ConnectedNodes_GetGroupedIntoCommunities()
    {
        var graph = new KnowledgeGraph();
        // Create a tight cluster of 4 nodes all connected
        for (int i = 0; i < 4; i++)
            graph.AddNode(MakeNode($"A{i}"));

        graph.AddEdge(MakeEdge("A0", "A1"));
        graph.AddEdge(MakeEdge("A0", "A2"));
        graph.AddEdge(MakeEdge("A0", "A3"));
        graph.AddEdge(MakeEdge("A1", "A2"));
        graph.AddEdge(MakeEdge("A1", "A3"));
        graph.AddEdge(MakeEdge("A2", "A3"));

        var detector = new CommunityDetector();
        detector.DetectCommunities(graph);

        var communityNodes = graph.GetNodesByType(NodeType.Community).ToList();
        Assert.Single(communityNodes);

        // All 4 nodes should be members
        var memberEdges = graph.Edges.Values.Where(e => e.Type == EdgeType.MemberOf).ToList();
        Assert.Equal(4, memberEdges.Count);
    }

    [Fact]
    public void DisconnectedGroups_FormSeparateCommunities()
    {
        var graph = new KnowledgeGraph();

        // Group 1: 3 connected nodes
        for (int i = 0; i < 3; i++)
            graph.AddNode(MakeNode($"G1_{i}"));
        graph.AddEdge(MakeEdge("G1_0", "G1_1"));
        graph.AddEdge(MakeEdge("G1_0", "G1_2"));
        graph.AddEdge(MakeEdge("G1_1", "G1_2"));

        // Group 2: 3 connected nodes (no connection to group 1)
        for (int i = 0; i < 3; i++)
            graph.AddNode(MakeNode($"G2_{i}"));
        graph.AddEdge(MakeEdge("G2_0", "G2_1"));
        graph.AddEdge(MakeEdge("G2_0", "G2_2"));
        graph.AddEdge(MakeEdge("G2_1", "G2_2"));

        var detector = new CommunityDetector();
        detector.DetectCommunities(graph);

        var communityNodes = graph.GetNodesByType(NodeType.Community).ToList();
        Assert.Equal(2, communityNodes.Count);
    }

    [Fact]
    public void Singletons_AreExcluded()
    {
        var graph = new KnowledgeGraph();
        // Two isolated nodes - no edges between eligible types
        graph.AddNode(MakeNode("lonely1"));
        graph.AddNode(MakeNode("lonely2"));

        var detector = new CommunityDetector();
        detector.DetectCommunities(graph);

        var communityNodes = graph.GetNodesByType(NodeType.Community).ToList();
        Assert.Empty(communityNodes);
    }

    [Fact]
    public void CommunityLabels_UseFolderNames()
    {
        var graph = new KnowledgeGraph();
        // 3 nodes all in same folder
        graph.AddNode(MakeNode("A", filePath: "src/Services/A.cs"));
        graph.AddNode(MakeNode("B", filePath: "src/Services/B.cs"));
        graph.AddNode(MakeNode("C", filePath: "src/Services/C.cs"));
        graph.AddEdge(MakeEdge("A", "B"));
        graph.AddEdge(MakeEdge("A", "C"));
        graph.AddEdge(MakeEdge("B", "C"));

        var detector = new CommunityDetector();
        detector.DetectCommunities(graph);

        var communityNodes = graph.GetNodesByType(NodeType.Community).ToList();
        Assert.Single(communityNodes);
        Assert.Equal("Services", communityNodes[0].Name);
    }

    [Fact]
    public void EmptyGraph_ProducesNoCommunities()
    {
        var graph = new KnowledgeGraph();

        var detector = new CommunityDetector();
        detector.DetectCommunities(graph);

        var communityNodes = graph.GetNodesByType(NodeType.Community).ToList();
        Assert.Empty(communityNodes);
    }

    [Fact]
    public void SmallCommunities_UnderThreshold_AreExcluded()
    {
        var graph = new KnowledgeGraph();
        // Only 2 connected nodes - below MinCommunitySize of 3
        graph.AddNode(MakeNode("X"));
        graph.AddNode(MakeNode("Y"));
        graph.AddEdge(MakeEdge("X", "Y"));

        var detector = new CommunityDetector();
        detector.DetectCommunities(graph);

        var communityNodes = graph.GetNodesByType(NodeType.Community).ToList();
        Assert.Empty(communityNodes);
    }

    [Fact]
    public void NonEligibleNodeTypes_AreSkipped()
    {
        var graph = new KnowledgeGraph();
        // File and Folder nodes should not be part of communities
        graph.AddNode(MakeNode("f1", NodeType.File));
        graph.AddNode(MakeNode("f2", NodeType.File));
        graph.AddNode(MakeNode("f3", NodeType.File));
        graph.AddEdge(MakeEdge("f1", "f2"));
        graph.AddEdge(MakeEdge("f1", "f3"));
        graph.AddEdge(MakeEdge("f2", "f3"));

        var detector = new CommunityDetector();
        detector.DetectCommunities(graph);

        var communityNodes = graph.GetNodesByType(NodeType.Community).ToList();
        Assert.Empty(communityNodes);
    }
}
