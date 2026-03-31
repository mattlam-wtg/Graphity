using Graphity.Core.Graph;

namespace Graphity.Core.Detection;

public sealed class EntryPointScorer
{
    public record ScoredEntry(string NodeId, string Name, double Score);

    public IReadOnlyList<ScoredEntry> ScoreEntryPoints(KnowledgeGraph graph, int topN = 200)
    {
        var results = new List<ScoredEntry>();

        var candidates = graph.Nodes.Values.Where(n =>
            n.Type is NodeType.Method or NodeType.Function or NodeType.Constructor);

        foreach (var node in candidates)
        {
            if (IsTestFile(node.FilePath)) continue;

            var callers = graph.GetIncomingEdges(node.Id).Count(e => e.Type == EdgeType.Calls);
            var callees = graph.GetOutgoingEdges(node.Id).Count(e => e.Type == EdgeType.Calls);

            // Skip leaf functions (no outgoing calls)
            if (callees == 0) continue;

            // Base score
            double score = (double)callees / (callers + 1);

            // Export multiplier
            if (node.IsExported) score *= 2.0;

            // Name pattern multipliers
            score *= GetNameMultiplier(node.Name);

            results.Add(new ScoredEntry(node.Id, node.Name, score));
        }

        return results.OrderByDescending(x => x.Score).Take(topN).ToList();
    }

    internal static double GetNameMultiplier(string name)
    {
        // Bonus: Handle*, Execute*, Process*, Controller, Main -> 1.5x
        if (name.StartsWith("Handle", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Execute", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Process", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Controller", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Main", StringComparison.OrdinalIgnoreCase))
            return 1.5;

        // Penalty: Get*, Set*, Is*, Helper, Util, Format, Parse -> 0.3x
        if (name.StartsWith("Get", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Set", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Is", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Helper", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Util", StringComparison.OrdinalIgnoreCase))
            return 0.3;

        return 1.0;
    }

    internal static bool IsTestFile(string? filePath)
    {
        if (filePath == null) return false;
        return filePath.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
               filePath.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
               filePath.Contains(".Test.", StringComparison.OrdinalIgnoreCase) ||
               filePath.Contains(".Tests.", StringComparison.OrdinalIgnoreCase);
    }
}
