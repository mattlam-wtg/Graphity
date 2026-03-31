using Graphity.Core.Graph;
using Graphity.Search;

namespace Graphity.Search.Tests;

public class OnnxEmbedderTests
{
    [Fact]
    public void HashEmbed_produces_consistent_embeddings_for_same_input()
    {
        var embedding1 = OnnxEmbedder.HashEmbed("UserService");
        var embedding2 = OnnxEmbedder.HashEmbed("UserService");

        Assert.Equal(embedding1.Length, embedding2.Length);
        for (int i = 0; i < embedding1.Length; i++)
            Assert.Equal(embedding1[i], embedding2[i]);
    }

    [Fact]
    public void HashEmbed_produces_different_embeddings_for_different_inputs()
    {
        var embedding1 = OnnxEmbedder.HashEmbed("UserService");
        var embedding2 = OnnxEmbedder.HashEmbed("PaymentGateway");

        Assert.Equal(embedding1.Length, embedding2.Length);

        // At least some dimensions should differ
        var differences = 0;
        for (int i = 0; i < embedding1.Length; i++)
        {
            if (MathF.Abs(embedding1[i] - embedding2[i]) > 1e-6f)
                differences++;
        }

        Assert.True(differences > 0, "Embeddings for different inputs should differ");
    }

    [Fact]
    public void HashEmbed_produces_L2_normalized_embeddings()
    {
        var embedding = OnnxEmbedder.HashEmbed("some code symbol with multiple tokens");

        var magnitude = MathF.Sqrt(embedding.Sum(x => x * x));
        Assert.InRange(magnitude, 0.99f, 1.01f);
    }

    [Fact]
    public void HashEmbed_returns_384_dimensions_by_default()
    {
        var embedding = OnnxEmbedder.HashEmbed("test");
        Assert.Equal(384, embedding.Length);
    }

    [Fact]
    public void NodeToText_generates_readable_text_from_node()
    {
        var node = new GraphNode
        {
            Id = "test-id",
            Name = "UserService",
            Type = NodeType.Class,
            FullName = "MyApp.Services.UserService",
            FilePath = "/src/UserService.cs",
            Content = "public class UserService { }"
        };

        var text = OnnxEmbedder.NodeToText(node);

        Assert.Contains("Class", text);
        Assert.Contains("UserService", text);
        Assert.Contains("MyApp.Services.UserService", text);
        Assert.Contains("in /src/UserService.cs", text);
        Assert.Contains("public class UserService", text);
    }

    [Fact]
    public void NodeToText_handles_node_with_minimal_fields()
    {
        var node = new GraphNode
        {
            Id = "minimal",
            Name = "Foo",
            Type = NodeType.Method,
        };

        var text = OnnxEmbedder.NodeToText(node);

        Assert.Contains("Method", text);
        Assert.Contains("Foo", text);
    }

    [Fact]
    public void NodeToText_truncates_long_content()
    {
        var longContent = new string('x', 1000);
        var node = new GraphNode
        {
            Id = "long",
            Name = "BigClass",
            Type = NodeType.Class,
            Content = longContent,
        };

        var text = OnnxEmbedder.NodeToText(node);

        // Content should be truncated to 500 chars
        Assert.DoesNotContain(longContent, text);
        Assert.True(text.Length < longContent.Length);
    }

    [Fact]
    public void IsModelAvailable_is_false_when_no_model_file_exists()
    {
        using var embedder = new OnnxEmbedder("/nonexistent/path/model.onnx");
        Assert.False(embedder.IsModelAvailable);
    }

    [Fact]
    public void EmbedBatch_returns_embeddings_for_all_inputs()
    {
        using var embedder = new OnnxEmbedder();
        var results = embedder.EmbedBatch(["hello", "world", "test"]);

        Assert.Equal(3, results.Length);
        foreach (var emb in results)
            Assert.Equal(384, emb.Length);
    }
}
