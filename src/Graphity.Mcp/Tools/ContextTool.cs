using System.ComponentModel;
using System.Text;
using Graphity.Core.Graph;
using ModelContextProtocol.Server;

namespace Graphity.Mcp.Tools;

[McpServerToolType]
public class ContextTool
{
    private readonly GraphService _service;

    public ContextTool(GraphService service) => _service = service;

    [McpServerTool(Name = "context"), Description("Get 360-degree view of a symbol: callers, callees, inheritance, file location, and relationships.")]
    public async Task<string> GetContext(
        [Description("Symbol name or ID (e.g., 'UserService', 'Method:MyApp.UserService.GetUser')")] string name,
        [Description("Include source code content in response")] bool include_content = false)
    {
        try
        {
            _service.EnsureInitialized();
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }

        // 1. Resolve symbol — try exact ID first, then search by name
        var node = await _service.Adapter.GetNodeAsync(name);
        if (node is null)
        {
            // Search for the symbol by name
            var results = _service.SearchIndex.Search(name, 10);
            if (results.Count == 0)
                return $"Symbol '{name}' not found. Try query('{name}') to search more broadly.";

            // Disambiguation: prefer Class > Interface > Struct > Method > other
            var best = results
                .OrderBy(r => GetTypePriority(r.Type))
                .ThenByDescending(r => r.Score)
                .First();

            node = await _service.Adapter.GetNodeAsync(best.NodeId);
            if (node is null)
                return $"Symbol '{name}' found in search index but not in graph database. Re-index may be needed.";

            // If multiple matches, note disambiguation
            if (results.Count > 1)
            {
                var otherMatches = results.Where(r => r.NodeId != best.NodeId).Take(3);
                // We'll note these at the end
            }
        }

        var sb = new StringBuilder();

        // 2. Symbol header
        sb.AppendLine($"Symbol: {node.Name}");
        sb.AppendLine($"  Type:      {node.Type}");
        if (node.FullName != null) sb.AppendLine($"  Full name: {node.FullName}");
        if (node.FilePath != null) sb.AppendLine($"  File:      {node.FilePath}");
        if (node.StartLine.HasValue)
            sb.AppendLine($"  Lines:     {node.StartLine}-{node.EndLine}");
        sb.AppendLine($"  ID:        {node.Id}");
        sb.AppendLine();

        // 3. Get incoming edges (callers, importers, extenders)
        var incoming = await _service.Adapter.GetEdgesToAsync(node.Id);
        if (incoming.Count > 0)
        {
            sb.AppendLine("Incoming references (who depends on this):");
            var grouped = incoming.GroupBy(e => e.Type);
            foreach (var group in grouped.OrderBy(g => g.Key.ToString()))
            {
                foreach (var edge in group.Take(15))
                {
                    var sourceNode = await _service.Adapter.GetNodeAsync(edge.SourceId);
                    var sourceName = sourceNode?.Name ?? edge.SourceId;
                    var sourceType = sourceNode?.Type.ToString() ?? "?";
                    sb.AppendLine($"  [{edge.Type}] {sourceName} ({sourceType})");
                }
                if (group.Count() > 15)
                    sb.AppendLine($"  ... and {group.Count() - 15} more {group.Key} references");
            }
            sb.AppendLine();
        }

        // 4. Get outgoing edges (callees, imports, base types)
        var outgoing = await _service.Adapter.GetEdgesFromAsync(node.Id);
        if (outgoing.Count > 0)
        {
            sb.AppendLine("Outgoing references (what this depends on):");
            var grouped = outgoing.GroupBy(e => e.Type);
            foreach (var group in grouped.OrderBy(g => g.Key.ToString()))
            {
                foreach (var edge in group.Take(15))
                {
                    var targetNode = await _service.Adapter.GetNodeAsync(edge.TargetId);
                    var targetName = targetNode?.Name ?? edge.TargetId;
                    var targetType = targetNode?.Type.ToString() ?? "?";
                    sb.AppendLine($"  [{edge.Type}] {targetName} ({targetType})");
                }
                if (group.Count() > 15)
                    sb.AppendLine($"  ... and {group.Count() - 15} more {group.Key} references");
            }
            sb.AppendLine();
        }

        // 5. Summary
        sb.AppendLine($"Summary: {incoming.Count} incoming, {outgoing.Count} outgoing references");

        // 6. Include content if requested
        if (include_content && !string.IsNullOrEmpty(node.Content))
        {
            sb.AppendLine();
            sb.AppendLine("Source:");
            sb.AppendLine("```");
            sb.AppendLine(node.Content);
            sb.AppendLine("```");
        }

        // 7. Next-step hints
        sb.AppendLine();
        sb.AppendLine("Next steps:");
        sb.AppendLine($"  - Use impact('{node.Name}') to see what breaks if you modify this symbol");
        if (incoming.Count > 5)
            sb.AppendLine($"  - This symbol has {incoming.Count} dependents — consider impact analysis before changes");

        return sb.ToString();
    }

    private static int GetTypePriority(NodeType type) => type switch
    {
        NodeType.Class => 0,
        NodeType.Interface => 1,
        NodeType.Struct => 2,
        NodeType.Record => 3,
        NodeType.Enum => 4,
        NodeType.Method => 5,
        NodeType.Constructor => 6,
        NodeType.Property => 7,
        NodeType.Field => 8,
        _ => 9,
    };
}
