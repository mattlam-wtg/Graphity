using System.Text.Json;
using System.Text.RegularExpressions;

using Graphity.Core.Graph;

namespace Graphity.Search;

public sealed partial class Bm25Index
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly Dictionary<string, DocumentEntry> _documents = new();
    private readonly Dictionary<string, Dictionary<string, int>> _invertedIndex = new(); // term -> (docId -> termFreq)
    private double _avgDocLength;

    public int DocumentCount => _documents.Count;

    public void BuildIndex(IEnumerable<GraphNode> nodes)
    {
        _documents.Clear();
        _invertedIndex.Clear();

        foreach (var node in nodes)
        {
            var text = BuildSearchableText(node);
            var tokens = Tokenize(text);
            var termFreqs = new Dictionary<string, int>();
            foreach (var token in tokens)
            {
                termFreqs.TryGetValue(token, out var count);
                termFreqs[token] = count + 1;
            }

            var entry = new DocumentEntry(node.Id, node.Name, node.Type, node.FilePath, tokens.Count, termFreqs);
            _documents[node.Id] = entry;

            foreach (var (term, freq) in termFreqs)
            {
                if (!_invertedIndex.TryGetValue(term, out var postings))
                {
                    postings = new Dictionary<string, int>();
                    _invertedIndex[term] = postings;
                }
                postings[node.Id] = freq;
            }
        }

        _avgDocLength = _documents.Count > 0
            ? _documents.Values.Average(d => d.Length)
            : 0;
    }

    public IReadOnlyList<SearchResult> Search(string query, int limit = 20)
    {
        if (_documents.Count == 0)
            return [];

        var queryTerms = Tokenize(query);
        if (queryTerms.Count == 0)
            return [];

        var scores = new Dictionary<string, double>();
        int n = _documents.Count;

        foreach (var term in queryTerms.Distinct())
        {
            if (!_invertedIndex.TryGetValue(term, out var postings))
                continue;

            int docFreq = postings.Count;
            double idf = Math.Log((n - docFreq + 0.5) / (docFreq + 0.5) + 1.0);

            foreach (var (docId, tf) in postings)
            {
                var doc = _documents[docId];
                double numerator = tf * (K1 + 1.0);
                double denominator = tf + K1 * (1.0 - B + B * doc.Length / _avgDocLength);
                double score = idf * numerator / denominator;

                scores.TryGetValue(docId, out var existing);
                scores[docId] = existing + score;
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(limit)
            .Select(kv =>
            {
                var doc = _documents[kv.Key];
                return new SearchResult(doc.NodeId, doc.Name, doc.Type, doc.FilePath, kv.Value);
            })
            .ToList();
    }

    public void Save(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "bm25-index.json");
        var data = new Bm25SerializedData
        {
            AvgDocLength = _avgDocLength,
            Documents = _documents.Values.Select(d => new SerializedDocument
            {
                NodeId = d.NodeId,
                Name = d.Name,
                Type = d.Type.ToString(),
                FilePath = d.FilePath,
                Length = d.Length,
                TermFrequencies = d.TermFrequencies
            }).ToList()
        };
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
        File.WriteAllText(path, json);
    }

    public static Bm25Index Load(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "bm25-index.json");
        var json = File.ReadAllText(path);
        var data = JsonSerializer.Deserialize<Bm25SerializedData>(json)
            ?? throw new InvalidOperationException("Failed to deserialize BM25 index.");

        var index = new Bm25Index();
        index._avgDocLength = data.AvgDocLength;

        foreach (var doc in data.Documents)
        {
            var type = Enum.Parse<NodeType>(doc.Type);
            var entry = new DocumentEntry(doc.NodeId, doc.Name, type, doc.FilePath, doc.Length, doc.TermFrequencies);
            index._documents[doc.NodeId] = entry;

            foreach (var (term, freq) in doc.TermFrequencies)
            {
                if (!index._invertedIndex.TryGetValue(term, out var postings))
                {
                    postings = new Dictionary<string, int>();
                    index._invertedIndex[term] = postings;
                }
                postings[doc.NodeId] = freq;
            }
        }

        return index;
    }

    private static string BuildSearchableText(GraphNode node)
    {
        var parts = new List<string>(5);
        if (!string.IsNullOrEmpty(node.Name)) parts.Add(node.Name);
        if (!string.IsNullOrEmpty(node.FullName)) parts.Add(node.FullName);
        if (!string.IsNullOrEmpty(node.FilePath)) parts.Add(Path.GetFileName(node.FilePath));
        parts.Add(node.Type.ToString());
        if (!string.IsNullOrEmpty(node.Content)) parts.Add(node.Content);
        return string.Join(" ", parts);
    }

    internal static List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        // Split camelCase/PascalCase then split on non-alphanumeric
        var expanded = CamelCaseRegex().Replace(text, "$1 $2");
        var tokens = NonAlphanumericRegex().Split(expanded);

        return tokens
            .Select(t => t.ToLowerInvariant().Trim())
            .Where(t => t.Length >= 2)
            .ToList();
    }

    [GeneratedRegex(@"([a-z])([A-Z])")]
    private static partial Regex CamelCaseRegex();

    [GeneratedRegex(@"[^a-zA-Z0-9]+")]
    private static partial Regex NonAlphanumericRegex();
}

public record SearchResult(string NodeId, string Name, NodeType Type, string? FilePath, double Score);

internal record DocumentEntry(string NodeId, string Name, NodeType Type, string? FilePath, int Length, Dictionary<string, int> TermFrequencies);

internal sealed class Bm25SerializedData
{
    public double AvgDocLength { get; set; }
    public List<SerializedDocument> Documents { get; set; } = [];
}

internal sealed class SerializedDocument
{
    public string NodeId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? FilePath { get; set; }
    public int Length { get; set; }
    public Dictionary<string, int> TermFrequencies { get; set; } = new();
}
