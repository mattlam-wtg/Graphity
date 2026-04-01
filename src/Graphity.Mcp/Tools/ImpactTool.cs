using System.ComponentModel;
using System.Text;
using Graphity.Core.Graph;
using Graphity.Storage;
using ModelContextProtocol.Server;

namespace Graphity.Mcp.Tools;

[McpServerToolType]
public class ImpactTool
{
    private readonly GraphService _service;

    public ImpactTool(GraphService service) => _service = service;

    [McpServerTool(Name = "impact"), Description("Analyze blast radius: what breaks if you modify this symbol. Returns depth-grouped affected symbols with risk level.")]
    public async Task<string> AnalyzeImpact(
        [Description("Symbol name or ID to analyze")] string target,
        [Description("Direction: 'upstream' (what calls this) or 'downstream' (what this calls). Default: upstream")] string direction = "upstream",
        [Description("Maximum traversal depth (1-5)")] int maxDepth = 3)
    {
        try
        {
            _service.EnsureInitialized();

            // Clamp depth
            maxDepth = Math.Clamp(maxDepth, 1, 5);

            // Resolve target symbol
            var node = await _service.Adapter.GetNodeAsync(target);
            if (node is null)
            {
                var results = _service.SearchIndex.Search(target, 5);
                if (results.Count == 0)
                    return $"Symbol '{target}' not found. Try query('{target}') to search first.";

                var best = results
                    .OrderBy(r => GetTypePriority(r.Type))
                    .ThenByDescending(r => r.Score)
                    .First();

                node = await _service.Adapter.GetNodeAsync(best.NodeId);
                if (node is null)
                    return $"Symbol '{target}' found in index but not in graph. Re-index may be needed.";
            }

            // Parse direction
            var traversalDir = direction.ToLowerInvariant() switch
            {
                "upstream" => TraversalDirection.Upstream,
                "downstream" => TraversalDirection.Downstream,
                _ => TraversalDirection.Upstream,
            };

            // Traverse the graph
            var depthMap = await _service.Querier.TraverseAsync(
                node.Id, traversalDir, maxDepth, ct: default);

            if (depthMap.Count == 0)
            {
                var dirLabel = traversalDir == TraversalDirection.Upstream ? "dependents" : "dependencies";
                return $"No {dirLabel} found for '{node.Name}' ({node.Type}).\n\nThis symbol appears to be a leaf node with no {dirLabel}.";
            }

            var sb = new StringBuilder();
            var directionLabel = traversalDir == TraversalDirection.Upstream ? "upstream (dependents)" : "downstream (dependencies)";
            sb.AppendLine($"Impact analysis for: {node.Name} ({node.Type})");
            sb.AppendLine($"Direction: {directionLabel}");
            sb.AppendLine($"Max depth: {maxDepth}");
            sb.AppendLine();

            // Calculate risk level
            var d1Count = depthMap.GetValueOrDefault(1)?.Count ?? 0;
            var totalAffected = depthMap.Values.Sum(list => list.Count);
            var risk = CalculateRisk(d1Count, totalAffected);

            sb.AppendLine($"Risk level: {risk}");
            sb.AppendLine($"Total affected: {totalAffected} symbols");
            sb.AppendLine();

            // Group by depth with impact labels
            foreach (var (depth, nodes) in depthMap.OrderBy(kv => kv.Key))
            {
                var label = GetDepthLabel(depth);
                sb.AppendLine($"Depth {depth} — {label} ({nodes.Count} symbols):");

                // Group by type for readability
                var byType = nodes.GroupBy(n => n.Type).OrderBy(g => g.Key.ToString());
                foreach (var typeGroup in byType)
                {
                    foreach (var affected in typeGroup.Take(10))
                    {
                        var filePart = affected.FilePath != null ? $" in {Path.GetFileName(affected.FilePath)}" : "";
                        sb.AppendLine($"  [{affected.Type}] {affected.Name}{filePart}");
                    }
                    if (typeGroup.Count() > 10)
                        sb.AppendLine($"  ... and {typeGroup.Count() - 10} more {typeGroup.Key} items");
                }
                sb.AppendLine();
            }

            // Next-step hints
            sb.AppendLine("Next steps:");
            if (d1Count > 0)
                sb.AppendLine($"  - Review depth-1 items first (WILL BREAK on modification)");
            sb.AppendLine($"  - Use context(<name>) on any affected symbol for detailed references");
            if (traversalDir == TraversalDirection.Upstream)
                sb.AppendLine($"  - Use impact('{node.Name}', direction: 'downstream') to see dependencies");

            return sb.ToString();
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error analyzing impact for '{target}': {ex.Message}";
        }
    }

    private static string CalculateRisk(int d1Count, int totalAffected)
    {
        if (d1Count > 20) return "CRITICAL — more than 20 direct dependents";
        if (d1Count > 10) return "HIGH — more than 10 direct dependents";
        if (d1Count > 0) return $"MEDIUM — {d1Count} direct dependents";
        if (totalAffected > 0) return "LOW — no direct dependents, but transitive impact exists";
        return "NONE — no affected symbols";
    }

    private static string GetDepthLabel(int depth) => depth switch
    {
        1 => "WILL BREAK",
        2 => "LIKELY AFFECTED",
        _ => "MAY NEED TESTING",
    };

    private static int GetTypePriority(NodeType type) => type switch
    {
        NodeType.Class => 0,
        NodeType.Interface => 1,
        NodeType.Struct => 2,
        NodeType.Record => 3,
        NodeType.Method => 5,
        _ => 9,
    };
}
