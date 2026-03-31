using Graphity.Core.Graph;

namespace Graphity.Search;

public sealed class OnnxEmbedder : IDisposable
{
    private const int Dimensions = 384;
    private readonly bool _modelAvailable;

    public OnnxEmbedder(string? modelPath = null)
    {
        var path = modelPath ?? GetDefaultModelPath();
        _modelAvailable = false;

        if (File.Exists(path))
        {
            try
            {
                // ONNX inference deferred — model loading will be implemented
                // when the all-MiniLM-L6-v2.onnx model is available.
                // For now, we detect the file but still use hash-based fallback.
                _modelAvailable = false;
            }
            catch
            {
                _modelAvailable = false;
            }
        }
    }

    public bool IsModelAvailable => _modelAvailable;

    public float[] Embed(string text)
    {
        if (_modelAvailable)
        {
            // TODO: Run ONNX inference when model loading is implemented
            return HashEmbed(text);
        }

        return HashEmbed(text);
    }

    public float[][] EmbedBatch(string[] texts)
        => texts.Select(Embed).ToArray();

    /// <summary>
    /// Generate a text representation of a node suitable for embedding.
    /// </summary>
    public static string NodeToText(GraphNode node)
    {
        var parts = new List<string> { node.Type.ToString(), node.Name };
        if (node.FullName != null) parts.Add(node.FullName);
        if (node.FilePath != null) parts.Add($"in {node.FilePath}");
        if (node.Content != null) parts.Add(node.Content[..Math.Min(500, node.Content.Length)]);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Hash-based embedding fallback. Deterministic and works without an ONNX model.
    /// </summary>
    public static float[] HashEmbed(string text, int dimensions = Dimensions)
    {
        var tokens = Bm25Index.Tokenize(text);
        var embedding = new float[dimensions];

        foreach (var token in tokens)
        {
            var hash = token.GetHashCode(StringComparison.Ordinal);
            for (int i = 0; i < dimensions; i++)
                embedding[i] += MathF.Sin((hash + i) * 0.01f);
        }

        // L2 normalize
        var norm = MathF.Sqrt(embedding.Sum(x => x * x));
        if (norm > 0)
        {
            for (int i = 0; i < dimensions; i++)
                embedding[i] /= norm;
        }

        return embedding;
    }

    public static string GetDefaultModelPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".graphity", "models", "all-MiniLM-L6-v2.onnx");

    public void Dispose()
    {
        // Dispose ONNX session when model loading is implemented
    }
}
