using System.ComponentModel;
using System.Text;
using Graphity.Core.Graph;
using ModelContextProtocol.Server;

namespace Graphity.Mcp.Tools;

[McpServerToolType]
public class QueryTool
{
    private readonly GraphService _service;

    public QueryTool(GraphService service) => _service = service;

    [McpServerTool(Name = "query"), Description("Search the code knowledge graph using keyword search. Returns matching symbols grouped by file.")]
    public string Query(
        [Description("Search query (e.g., 'UserService', 'authentication', 'database connection')")] string query,
        [Description("Maximum number of results to return")] int limit = 20)
    {
        try
        {
            _service.EnsureInitialized();
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }

        var results = _service.SearchIndex.Search(query, limit);
        if (results.Count == 0)
            return $"No results found for '{query}'.\n\nHint: Try broader terms, or check list_repos() to confirm the index exists.";

        var sb = new StringBuilder();
        sb.AppendLine($"Search results for '{query}' ({results.Count} matches):");
        sb.AppendLine();

        // Group by file
        var grouped = results
            .GroupBy(r => r.FilePath ?? "(unknown file)")
            .OrderByDescending(g => g.Sum(r => r.Score));

        foreach (var group in grouped)
        {
            sb.AppendLine($"File: {group.Key}");
            foreach (var result in group.OrderByDescending(r => r.Score))
            {
                var typeLabel = FormatNodeType(result.Type);
                sb.AppendLine($"  {typeLabel} {result.Name}  (id: {result.NodeId}, score: {result.Score:F2})");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Next steps:");
        sb.AppendLine("  - Use context(<name>) on a specific symbol for callers, callees, and inheritance");
        sb.AppendLine("  - Use impact(<name>) to analyze blast radius before making changes");

        return sb.ToString();
    }

    private static string FormatNodeType(NodeType type) => type switch
    {
        NodeType.Class => "[class]",
        NodeType.Interface => "[interface]",
        NodeType.Method => "[method]",
        NodeType.Property => "[property]",
        NodeType.Field => "[field]",
        NodeType.Namespace => "[namespace]",
        NodeType.Enum => "[enum]",
        NodeType.Struct => "[struct]",
        NodeType.Record => "[record]",
        NodeType.Constructor => "[ctor]",
        NodeType.File => "[file]",
        NodeType.Delegate => "[delegate]",
        _ => $"[{type.ToString().ToLowerInvariant()}]",
    };
}
