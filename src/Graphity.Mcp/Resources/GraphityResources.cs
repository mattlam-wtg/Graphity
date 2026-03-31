using System.ComponentModel;
using System.Text;
using Graphity.Storage;
using ModelContextProtocol.Server;

namespace Graphity.Mcp.Resources;

[McpServerResourceType]
public class GraphityResources
{
    private readonly GraphService _service;

    public GraphityResources(GraphService service) => _service = service;

    [McpServerResource(
        UriTemplate = "graphity://repo/context",
        Name = "Repository Context"),
     Description("Stats, staleness, and available tools for the indexed repository")]
    public string GetRepoContext()
    {
        var repoPath = _service.RepoPath;
        var metadataPath = StoragePaths.GetMetadataPath(repoPath);
        var metadata = IndexMetadata.Load(metadataPath);

        if (metadata is null)
            return "No index found. Run 'graphity analyze' to create one.";

        var age = DateTime.UtcNow - metadata.IndexedAtUtc;
        var stale = age.TotalHours > 24;

        var sb = new StringBuilder();
        sb.AppendLine($"Repository: {metadata.RepoName}");
        sb.AppendLine($"Path: {metadata.RepoPath}");
        sb.AppendLine($"Indexed: {metadata.IndexedAtUtc:u} ({(stale ? "STALE" : "fresh")})");
        sb.AppendLine($"Nodes: {metadata.NodeCount:N0}");
        sb.AppendLine($"Edges: {metadata.EdgeCount:N0}");
        sb.AppendLine();
        sb.AppendLine("Available tools: query, context, impact, list_repos");
        return sb.ToString();
    }

    [McpServerResource(
        UriTemplate = "graphity://repo/schema",
        Name = "Graph Schema"),
     Description("Node and edge types available in the knowledge graph")]
    public string GetSchema()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Node types:");
        sb.AppendLine("  Code:     File, Folder, Namespace, Class, Interface, Struct, Record, Enum, Delegate");
        sb.AppendLine("  Members:  Method, Function, Constructor, Property, Field");
        sb.AppendLine("  Database: Table, View, StoredProcedure, Column, Index, Trigger, ForeignKey");
        sb.AppendLine("  Config:   ConfigFile, ConfigSection, NuGetPackage");
        sb.AppendLine("  Meta:     Community, Process");
        sb.AppendLine();
        sb.AppendLine("Edge types:");
        sb.AppendLine("  Structure:    Contains, Defines, HasMethod, HasProperty, HasField");
        sb.AppendLine("  References:   Imports, Calls, Extends, Implements, Overrides");
        sb.AppendLine("  Database:     ReferencesTable, ForeignKeyTo");
        sb.AppendLine("  Config:       ReferencesPackage, ConfiguredBy");
        sb.AppendLine("  Organization: MemberOf, StepInProcess");
        return sb.ToString();
    }
}
