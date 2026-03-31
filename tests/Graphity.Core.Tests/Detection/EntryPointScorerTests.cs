using Graphity.Core.Detection;
using Graphity.Core.Graph;

namespace Graphity.Core.Tests.Detection;

public class EntryPointScorerTests
{
    private static GraphNode MakeNode(string id, string name, NodeType type = NodeType.Method,
        bool isExported = false, string? filePath = null)
        => new() { Id = id, Name = name, Type = type, IsExported = isExported, FilePath = filePath };

    private static GraphRelationship MakeCallEdge(string source, string target)
        => new() { Id = $"call:{source}->{target}", SourceId = source, TargetId = target, Type = EdgeType.Calls };

    [Fact]
    public void HighCallee_LowCaller_ScoresHighest()
    {
        var graph = new KnowledgeGraph();

        // Node A: 3 callees, 0 callers -> score = 3/1 = 3.0
        graph.AddNode(MakeNode("A", "DoWork"));
        graph.AddNode(MakeNode("B", "Step1"));
        graph.AddNode(MakeNode("C", "Step2"));
        graph.AddNode(MakeNode("D", "Step3"));

        graph.AddEdge(MakeCallEdge("A", "B"));
        graph.AddEdge(MakeCallEdge("A", "C"));
        graph.AddEdge(MakeCallEdge("A", "D"));

        // Node E: 1 callee, 2 callers -> score = 1/3 = 0.33
        graph.AddNode(MakeNode("E", "LowScore"));
        graph.AddNode(MakeNode("F", "CallerF"));
        graph.AddNode(MakeNode("G", "CallerG"));
        graph.AddEdge(MakeCallEdge("E", "B"));
        graph.AddEdge(MakeCallEdge("F", "E"));
        graph.AddEdge(MakeCallEdge("G", "E"));
        // Give F and G outgoing calls so they don't get filtered
        graph.AddEdge(MakeCallEdge("F", "G"));
        graph.AddEdge(MakeCallEdge("G", "F"));

        var scorer = new EntryPointScorer();
        var results = scorer.ScoreEntryPoints(graph);

        var scoreA = results.First(r => r.NodeId == "A").Score;
        var scoreE = results.First(r => r.NodeId == "E").Score;
        Assert.True(scoreA > scoreE);
    }

    [Fact]
    public void PublicNodes_Get2xMultiplier()
    {
        var graph = new KnowledgeGraph();

        graph.AddNode(MakeNode("pub", "Run", isExported: true));
        graph.AddNode(MakeNode("priv", "Run", isExported: false));
        graph.AddNode(MakeNode("target", "Target"));

        graph.AddEdge(MakeCallEdge("pub", "target"));
        graph.AddEdge(MakeCallEdge("priv", "target"));

        var scorer = new EntryPointScorer();
        var results = scorer.ScoreEntryPoints(graph);

        var pubScore = results.First(r => r.NodeId == "pub").Score;
        var privScore = results.First(r => r.NodeId == "priv").Score;
        Assert.Equal(pubScore, privScore * 2.0, precision: 5);
    }

    [Fact]
    public void HandlePrefix_Gets1_5xBonus()
    {
        Assert.Equal(1.5, EntryPointScorer.GetNameMultiplier("HandleRequest"));
        Assert.Equal(1.5, EntryPointScorer.GetNameMultiplier("ExecuteCommand"));
        Assert.Equal(1.5, EntryPointScorer.GetNameMultiplier("ProcessData"));
        Assert.Equal(1.5, EntryPointScorer.GetNameMultiplier("Main"));
        Assert.Equal(1.5, EntryPointScorer.GetNameMultiplier("MyController"));
    }

    [Fact]
    public void GetPrefix_Gets0_3xPenalty()
    {
        Assert.Equal(0.3, EntryPointScorer.GetNameMultiplier("GetValue"));
        Assert.Equal(0.3, EntryPointScorer.GetNameMultiplier("SetName"));
        Assert.Equal(0.3, EntryPointScorer.GetNameMultiplier("IsValid"));
        Assert.Equal(0.3, EntryPointScorer.GetNameMultiplier("MyHelper"));
        Assert.Equal(0.3, EntryPointScorer.GetNameMultiplier("StringUtil"));
    }

    [Fact]
    public void LeafFunctions_NoCallees_AreExcluded()
    {
        var graph = new KnowledgeGraph();

        graph.AddNode(MakeNode("leaf", "LeafFunc"));
        graph.AddNode(MakeNode("caller", "CallerFunc"));
        graph.AddEdge(MakeCallEdge("caller", "leaf"));

        var scorer = new EntryPointScorer();
        var results = scorer.ScoreEntryPoints(graph);

        Assert.DoesNotContain(results, r => r.NodeId == "leaf");
    }

    [Fact]
    public void TestFiles_AreExcluded()
    {
        var graph = new KnowledgeGraph();

        graph.AddNode(MakeNode("testMethod", "TestRun", filePath: "src/tests/MyTests.cs"));
        graph.AddNode(MakeNode("target", "Target"));
        graph.AddEdge(MakeCallEdge("testMethod", "target"));

        var scorer = new EntryPointScorer();
        var results = scorer.ScoreEntryPoints(graph);

        Assert.DoesNotContain(results, r => r.NodeId == "testMethod");
    }

    [Fact]
    public void IsTestFile_DetectsVariousPatterns()
    {
        Assert.True(EntryPointScorer.IsTestFile("src/tests/Foo.cs"));
        Assert.True(EntryPointScorer.IsTestFile("src/test/Foo.cs"));
        Assert.True(EntryPointScorer.IsTestFile("My.Test.Project/Foo.cs"));
        Assert.True(EntryPointScorer.IsTestFile("My.Tests.Project/Foo.cs"));
        Assert.False(EntryPointScorer.IsTestFile("src/Services/Foo.cs"));
        Assert.False(EntryPointScorer.IsTestFile(null));
    }
}
