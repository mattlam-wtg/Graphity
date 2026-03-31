using Graphity.Core.Detection;
using Graphity.Core.Graph;

namespace Graphity.Core.Tests.Detection;

public class ProcessDetectorTests
{
    private static GraphNode MakeNode(string id, string? name = null, NodeType type = NodeType.Method)
        => new() { Id = id, Name = name ?? id, Type = type };

    private static GraphRelationship MakeCallEdge(string source, string target, double confidence = 1.0)
        => new() { Id = $"call:{source}->{target}", SourceId = source, TargetId = target, Type = EdgeType.Calls, Confidence = confidence };

    [Fact]
    public void CreatesProcessNodes_FromEntryPoints()
    {
        var graph = new KnowledgeGraph();
        // Chain: A -> B -> C -> D (4 steps, >= MinSteps of 3)
        graph.AddNode(MakeNode("A"));
        graph.AddNode(MakeNode("B"));
        graph.AddNode(MakeNode("C"));
        graph.AddNode(MakeNode("D"));
        graph.AddEdge(MakeCallEdge("A", "B"));
        graph.AddEdge(MakeCallEdge("B", "C"));
        graph.AddEdge(MakeCallEdge("C", "D"));

        var entryPoints = new List<EntryPointScorer.ScoredEntry>
        {
            new("A", "A", 1.0),
        };

        var detector = new ProcessDetector();
        detector.DetectProcesses(graph, entryPoints);

        var processNodes = graph.GetNodesByType(NodeType.Process).ToList();
        Assert.NotEmpty(processNodes);
        Assert.Contains(processNodes, p => p.Name.Contains("A") && p.Name.Contains("D"));
    }

    [Fact]
    public void ShortTraces_UnderMinSteps_AreExcluded()
    {
        var graph = new KnowledgeGraph();
        // Chain: A -> B (only 2 steps, less than MinSteps of 3)
        graph.AddNode(MakeNode("A"));
        graph.AddNode(MakeNode("B"));
        graph.AddEdge(MakeCallEdge("A", "B"));

        var entryPoints = new List<EntryPointScorer.ScoredEntry>
        {
            new("A", "A", 1.0),
        };

        var detector = new ProcessDetector();
        detector.DetectProcesses(graph, entryPoints);

        var processNodes = graph.GetNodesByType(NodeType.Process).ToList();
        Assert.Empty(processNodes);
    }

    [Fact]
    public void Cycles_DoNotCauseInfiniteLoops()
    {
        var graph = new KnowledgeGraph();
        // Cycle: A -> B -> C -> A, but also C -> D for a valid trace
        graph.AddNode(MakeNode("A"));
        graph.AddNode(MakeNode("B"));
        graph.AddNode(MakeNode("C"));
        graph.AddNode(MakeNode("D"));
        graph.AddEdge(MakeCallEdge("A", "B"));
        graph.AddEdge(MakeCallEdge("B", "C"));
        graph.AddEdge(MakeCallEdge("C", "A")); // cycle
        graph.AddEdge(MakeCallEdge("C", "D")); // exit from cycle

        var entryPoints = new List<EntryPointScorer.ScoredEntry>
        {
            new("A", "A", 1.0),
        };

        var detector = new ProcessDetector();
        detector.DetectProcesses(graph, entryPoints); // Should not hang

        var processNodes = graph.GetNodesByType(NodeType.Process).ToList();
        Assert.NotEmpty(processNodes);
    }

    [Fact]
    public void ProcessLimit_Of75_IsRespected()
    {
        var graph = new KnowledgeGraph();
        // Create many entry points with long chains to exceed limit
        var entryPoints = new List<EntryPointScorer.ScoredEntry>();

        for (int i = 0; i < 100; i++)
        {
            var prefix = $"chain{i}";
            for (int j = 0; j < 4; j++)
            {
                graph.AddNode(MakeNode($"{prefix}_n{j}"));
                if (j > 0)
                    graph.AddEdge(MakeCallEdge($"{prefix}_n{j - 1}", $"{prefix}_n{j}"));
            }
            entryPoints.Add(new EntryPointScorer.ScoredEntry($"{prefix}_n0", $"{prefix}_n0", 1.0));
        }

        var detector = new ProcessDetector();
        detector.DetectProcesses(graph, entryPoints);

        var processNodes = graph.GetNodesByType(NodeType.Process).ToList();
        Assert.True(processNodes.Count <= 75);
    }

    [Fact]
    public void StepInProcess_Edges_HaveCorrectStepNumbers()
    {
        var graph = new KnowledgeGraph();
        // Chain: A -> B -> C -> D
        graph.AddNode(MakeNode("A"));
        graph.AddNode(MakeNode("B"));
        graph.AddNode(MakeNode("C"));
        graph.AddNode(MakeNode("D"));
        graph.AddEdge(MakeCallEdge("A", "B"));
        graph.AddEdge(MakeCallEdge("B", "C"));
        graph.AddEdge(MakeCallEdge("C", "D"));

        var entryPoints = new List<EntryPointScorer.ScoredEntry>
        {
            new("A", "A", 1.0),
        };

        var detector = new ProcessDetector();
        detector.DetectProcesses(graph, entryPoints);

        var stepEdges = graph.Edges.Values
            .Where(e => e.Type == EdgeType.StepInProcess)
            .OrderBy(e => e.Step)
            .ToList();

        Assert.NotEmpty(stepEdges);

        // Find the edges for a process that includes A->B->C->D
        var processNode = graph.GetNodesByType(NodeType.Process).First();
        var processSteps = stepEdges.Where(e => e.TargetId == processNode.Id).OrderBy(e => e.Step).ToList();

        Assert.Equal(4, processSteps.Count);
        Assert.Equal(1, processSteps[0].Step);
        Assert.Equal(2, processSteps[1].Step);
        Assert.Equal(3, processSteps[2].Step);
        Assert.Equal(4, processSteps[3].Step);
        Assert.Equal("A", processSteps[0].SourceId);
        Assert.Equal("D", processSteps[3].SourceId);
    }

    [Fact]
    public void RemoveSubsets_RemovesSmallDuplicates()
    {
        var traces = new List<List<string>>
        {
            new() { "A", "B", "C", "D" },
            new() { "A", "B", "C" }, // subset of first
            new() { "X", "Y", "Z" },
        };

        var result = ProcessDetector.RemoveSubsets(traces);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, t => t.Count == 4);
        Assert.Contains(result, t => t[0] == "X");
    }

    [Fact]
    public void KeepLongestPerPair_KeepsOnlyLongest()
    {
        var traces = new List<List<string>>
        {
            new() { "A", "B", "D" },
            new() { "A", "B", "C", "D" }, // same entry/terminal, longer
        };

        var result = ProcessDetector.KeepLongestPerPair(traces);

        Assert.Single(result);
        Assert.Equal(4, result[0].Count);
    }
}
