using Graphity.Core.Graph;
using Graphity.Search;

namespace Graphity.Search.Tests;

public class Bm25IndexTests
{
    private static GraphNode MakeNode(string id, string name, NodeType type = NodeType.Class, string? filePath = null, string? content = null)
        => new()
        {
            Id = id,
            Name = name,
            Type = type,
            FilePath = filePath,
            Content = content,
        };

    [Fact]
    public void BuildIndex_and_Search_finds_matching_node_by_name()
    {
        var index = new Bm25Index();
        var nodes = new[]
        {
            MakeNode("1", "UserService"),
            MakeNode("2", "OrderRepository"),
            MakeNode("3", "PaymentGateway"),
        };

        index.BuildIndex(nodes);

        var results = index.Search("UserService");
        Assert.NotEmpty(results);
        Assert.Equal("1", results[0].NodeId);
    }

    [Fact]
    public void Search_returns_results_ordered_by_relevance()
    {
        var index = new Bm25Index();
        var nodes = new[]
        {
            MakeNode("1", "Logger", content: "handles payment processing and payment validation"),
            MakeNode("2", "PaymentService", content: "payment payment payment core payment logic"),
            MakeNode("3", "OrderService", content: "creates orders"),
        };

        index.BuildIndex(nodes);

        var results = index.Search("payment");
        Assert.True(results.Count >= 2);
        // The node with more "payment" occurrences should score higher.
        Assert.Equal("2", results[0].NodeId);
    }

    [Fact]
    public void Search_with_no_matches_returns_empty()
    {
        var index = new Bm25Index();
        index.BuildIndex(new[]
        {
            MakeNode("1", "UserService"),
        });

        var results = index.Search("zzzznotfound");
        Assert.Empty(results);
    }

    [Fact]
    public void CamelCase_tokenization_finds_UserService_when_searching_user()
    {
        var index = new Bm25Index();
        index.BuildIndex(new[]
        {
            MakeNode("1", "UserService"),
            MakeNode("2", "OrderService"),
        });

        var results = index.Search("user");
        Assert.NotEmpty(results);
        Assert.Equal("1", results[0].NodeId);
    }

    [Fact]
    public void Search_respects_limit_parameter()
    {
        var index = new Bm25Index();
        var nodes = Enumerable.Range(1, 50)
            .Select(i => MakeNode($"n{i}", $"Service{i}", content: "common shared keyword"))
            .ToArray();

        index.BuildIndex(nodes);

        var results = index.Search("common", limit: 5);
        Assert.Equal(5, results.Count);
    }

    [Fact]
    public void Save_and_Load_roundtrip_preserves_index()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bm25test_{Guid.NewGuid():N}");
        try
        {
            var index = new Bm25Index();
            index.BuildIndex(new[]
            {
                MakeNode("1", "UserService", NodeType.Class, "/src/UserService.cs"),
                MakeNode("2", "OrderRepository", NodeType.Class, "/src/OrderRepository.cs"),
            });

            index.Save(tempDir);

            var loaded = Bm25Index.Load(tempDir);
            Assert.Equal(index.DocumentCount, loaded.DocumentCount);

            // Search should work identically on the loaded index.
            var originalResults = index.Search("UserService");
            var loadedResults = loaded.Search("UserService");
            Assert.Equal(originalResults.Count, loadedResults.Count);
            Assert.Equal(originalResults[0].NodeId, loadedResults[0].NodeId);
            Assert.Equal(originalResults[0].Score, loadedResults[0].Score, precision: 10);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
