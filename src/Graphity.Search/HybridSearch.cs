using Graphity.Core.Graph;

namespace Graphity.Search;

/// <summary>
/// Reciprocal Rank Fusion (RRF) of BM25 keyword search and semantic embedding search.
/// </summary>
public sealed class HybridSearch
{
    private readonly Bm25Index _bm25;
    private readonly OnnxEmbedder _embedder;
    private readonly Dictionary<string, float[]> _nodeEmbeddings = new();
    private readonly Dictionary<string, (string Name, NodeType Type, string? FilePath)> _nodeInfo = new();
    private const int RrfK = 60;

    public HybridSearch(Bm25Index bm25, OnnxEmbedder embedder)
    {
        _bm25 = bm25;
        _embedder = embedder;
    }

    public int EmbeddingCount => _nodeEmbeddings.Count;

    public void BuildEmbeddingIndex(IEnumerable<GraphNode> nodes)
    {
        foreach (var node in nodes)
        {
            var text = OnnxEmbedder.NodeToText(node);
            _nodeEmbeddings[node.Id] = _embedder.Embed(text);
            _nodeInfo[node.Id] = (node.Name, node.Type, node.FilePath);
        }
    }

    public IReadOnlyList<SearchResult> Search(string query, int limit = 20)
    {
        // 1. BM25 results
        var bm25Results = _bm25.Search(query, limit * 2);

        // 2. Semantic results (embed query, brute-force k-NN)
        var queryEmb = _embedder.Embed(query);
        var semanticResults = _nodeEmbeddings
            .Select(kv => (id: kv.Key, score: CosineSimilarity(queryEmb, kv.Value)))
            .OrderByDescending(x => x.score)
            .Take(limit * 2)
            .Where(x => x.score > 0.3f)
            .Select(x =>
            {
                var info = _nodeInfo.GetValueOrDefault(x.id);
                return new SearchResult(x.id, info.Name ?? "", info.Type, info.FilePath, x.score);
            })
            .ToList();

        // 3. RRF fusion
        var scores = new Dictionary<string, (double Score, SearchResult Result)>();

        for (int i = 0; i < bm25Results.Count; i++)
        {
            var r = bm25Results[i];
            scores[r.NodeId] = (1.0 / (RrfK + i + 1), r);
        }

        for (int i = 0; i < semanticResults.Count; i++)
        {
            var r = semanticResults[i];
            var rrfScore = 1.0 / (RrfK + i + 1);
            if (scores.TryGetValue(r.NodeId, out var existing))
                scores[r.NodeId] = (existing.Score + rrfScore, existing.Result);
            else
                scores[r.NodeId] = (rrfScore, r);
        }

        return scores.Values
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Result with { Score = x.Score })
            .ToList();
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length && i < b.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0) return 0f;
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    /// <summary>
    /// Save the embedding index to a binary file.
    /// Format: [int count] then for each entry: [int idLen][byte[] idUtf8][float[384] embedding]
    /// </summary>
    public void Save(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "embeddings.bin");

        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        writer.Write(_nodeEmbeddings.Count);
        foreach (var (id, embedding) in _nodeEmbeddings)
        {
            var idBytes = System.Text.Encoding.UTF8.GetBytes(id);
            writer.Write(idBytes.Length);
            writer.Write(idBytes);
            foreach (var val in embedding)
                writer.Write(val);

            // Write node info
            var info = _nodeInfo.GetValueOrDefault(id);
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(info.Name ?? "");
            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);
            writer.Write((int)info.Type);
            var filePathBytes = System.Text.Encoding.UTF8.GetBytes(info.FilePath ?? "");
            writer.Write(filePathBytes.Length);
            writer.Write(filePathBytes);
        }
    }

    /// <summary>
    /// Load the embedding index from a binary file.
    /// </summary>
    public void Load(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "embeddings.bin");
        if (!File.Exists(path)) return;

        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs);

        var count = reader.ReadInt32();
        for (int n = 0; n < count; n++)
        {
            var idLen = reader.ReadInt32();
            var idBytes = reader.ReadBytes(idLen);
            var id = System.Text.Encoding.UTF8.GetString(idBytes);

            var embedding = new float[384];
            for (int i = 0; i < 384; i++)
                embedding[i] = reader.ReadSingle();

            _nodeEmbeddings[id] = embedding;

            // Read node info
            var nameLen = reader.ReadInt32();
            var nameBytes = reader.ReadBytes(nameLen);
            var name = System.Text.Encoding.UTF8.GetString(nameBytes);
            var type = (NodeType)reader.ReadInt32();
            var filePathLen = reader.ReadInt32();
            var filePathBytes = reader.ReadBytes(filePathLen);
            var filePath = filePathLen > 0 ? System.Text.Encoding.UTF8.GetString(filePathBytes) : null;

            _nodeInfo[id] = (name, type, filePath);
        }
    }
}
