using Graphity.Core.Graph;
using Graphity.Search;

namespace Graphity.Search.Tests;

public class HybridSearchTests
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

    private static (Bm25Index bm25, HybridSearch hybrid) BuildHybrid(GraphNode[] nodes)
    {
        var bm25 = new Bm25Index();
        bm25.BuildIndex(nodes);

        using var embedder = new OnnxEmbedder();
        var hybrid = new HybridSearch(bm25, embedder);
        hybrid.BuildEmbeddingIndex(nodes);

        return (bm25, hybrid);
    }

    [Fact]
    public void Search_returns_results_combining_BM25_and_semantic()
    {
        var nodes = new[]
        {
            MakeNode("1", "UserService", content: "handles user authentication and login"),
            MakeNode("2", "OrderRepository", content: "manages order persistence"),
            MakeNode("3", "PaymentGateway", content: "processes payments via external API"),
        };

        var (_, hybrid) = BuildHybrid(nodes);

        var results = hybrid.Search("user authentication");
        Assert.NotEmpty(results);
        // UserService should be the top result since it matches both keyword and semantic
        Assert.Equal("1", results[0].NodeId);
    }

    [Fact]
    public void RRF_boosts_items_appearing_in_both_rankings()
    {
        var nodes = new[]
        {
            MakeNode("1", "PaymentService", content: "payment processing logic with validation"),
            MakeNode("2", "Logger", content: "payment log entries for auditing"),
            MakeNode("3", "OrderService", content: "creates and manages orders"),
        };

        var (_, hybrid) = BuildHybrid(nodes);

        var results = hybrid.Search("payment");
        Assert.NotEmpty(results);

        // PaymentService should rank first — it matches both BM25 (keyword "payment" in name + content)
        // and semantic (payment-related content), getting boosted by RRF fusion
        Assert.Equal("1", results[0].NodeId);
    }

    [Fact]
    public void Works_when_only_BM25_has_results()
    {
        // Create nodes where semantic similarity will be very low for the query
        var nodes = new[]
        {
            MakeNode("1", "XyzService", content: "xyz unique unrelated content"),
            MakeNode("2", "AbcHelper", content: "abc totally different things"),
        };

        var bm25 = new Bm25Index();
        bm25.BuildIndex(nodes);

        using var embedder = new OnnxEmbedder();
        var hybrid = new HybridSearch(bm25, embedder);
        hybrid.BuildEmbeddingIndex(nodes);

        // Search for a term that BM25 can match via exact keyword
        var results = hybrid.Search("xyz");
        Assert.NotEmpty(results);
        Assert.Equal("1", results[0].NodeId);
    }

    [Fact]
    public void Empty_query_returns_empty_results()
    {
        var nodes = new[]
        {
            MakeNode("1", "UserService"),
        };

        var (_, hybrid) = BuildHybrid(nodes);

        var results = hybrid.Search("");
        Assert.Empty(results);
    }

    [Fact]
    public void Search_respects_limit()
    {
        var nodes = Enumerable.Range(1, 30)
            .Select(i => MakeNode($"n{i}", $"Service{i}", content: "common shared keyword"))
            .ToArray();

        var (_, hybrid) = BuildHybrid(nodes);

        var results = hybrid.Search("common", limit: 5);
        Assert.True(results.Count <= 5);
    }

    [Fact]
    public void Save_and_Load_roundtrip_preserves_embeddings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"hybrid_test_{Guid.NewGuid():N}");
        try
        {
            var nodes = new[]
            {
                MakeNode("1", "UserService", NodeType.Class, "/src/UserService.cs"),
                MakeNode("2", "OrderRepository", NodeType.Class, "/src/OrderRepository.cs"),
            };

            var bm25 = new Bm25Index();
            bm25.BuildIndex(nodes);

            using var embedder = new OnnxEmbedder();
            var hybrid = new HybridSearch(bm25, embedder);
            hybrid.BuildEmbeddingIndex(nodes);

            hybrid.Save(tempDir);
            Assert.Equal(2, hybrid.EmbeddingCount);

            // Load into a new instance
            var hybrid2 = new HybridSearch(bm25, embedder);
            hybrid2.Load(tempDir);
            Assert.Equal(2, hybrid2.EmbeddingCount);

            // Search should work on the loaded instance
            var results = hybrid2.Search("UserService");
            Assert.NotEmpty(results);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
