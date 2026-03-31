using System.ComponentModel;
using System.Text;
using Graphity.Storage;
using ModelContextProtocol.Server;

namespace Graphity.Mcp.Tools;

[McpServerToolType]
public class ListReposTool
{
    private readonly GraphService _service;

    public ListReposTool(GraphService service) => _service = service;

    [McpServerTool(Name = "list_repos"), Description("List all indexed repositories with stats (node/edge counts, index age, staleness).")]
    public string ListRepos()
    {
        var sb = new StringBuilder();
        var repoPath = _service.RepoPath;
        var metadataPath = StoragePaths.GetMetadataPath(repoPath);
        var metadata = IndexMetadata.Load(metadataPath);

        if (metadata is null)
        {
            sb.AppendLine("No indexed repositories found.");
            sb.AppendLine();
            sb.AppendLine("Hint: Run 'graphity analyze <path>' to index a codebase first.");
            return sb.ToString();
        }

        var age = DateTime.UtcNow - metadata.IndexedAtUtc;
        var staleWarning = age.TotalHours > 24 ? " [STALE - re-index recommended]" : "";

        sb.AppendLine("Indexed Repositories:");
        sb.AppendLine("---");
        sb.AppendLine($"Name:       {metadata.RepoName}");
        sb.AppendLine($"Path:       {metadata.RepoPath}");
        sb.AppendLine($"Indexed:    {metadata.IndexedAtUtc:u} ({FormatAge(age)} ago){staleWarning}");
        sb.AppendLine($"Nodes:      {metadata.NodeCount:N0}");
        sb.AppendLine($"Edges:      {metadata.EdgeCount:N0}");
        sb.AppendLine();
        sb.AppendLine("Next steps:");
        sb.AppendLine("  - Use query() to search for symbols by keyword");
        sb.AppendLine("  - Use context() to get a 360-degree view of a symbol");

        return sb.ToString();
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 60) return $"{age.TotalMinutes:F0}m";
        if (age.TotalHours < 24) return $"{age.TotalHours:F1}h";
        return $"{age.TotalDays:F1}d";
    }
}
