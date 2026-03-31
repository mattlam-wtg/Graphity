using System.Text.Json;
using System.Xml.Linq;
using Graphity.Core.Graph;
using Graphity.Core.Ingestion;

namespace Graphity.Core.Analyzers.CSharp;

public sealed class CSharpConfigParser
{
    private readonly HashSet<string> _emittedPackageIds = new();

    public AnalyzerResult ParseConfigs(string repoRoot, IReadOnlyList<FileScanner.ScannedFile> configFiles)
    {
        var result = new AnalyzerResult();

        foreach (var file in configFiles)
        {
            var ext = Path.GetExtension(file.FullPath).ToLowerInvariant();
            var fileName = Path.GetFileName(file.FullPath).ToLowerInvariant();

            try
            {
                if (ext is ".csproj" or ".props" or ".targets")
                    ParseCsproj(file, result);
                else if (fileName.StartsWith("appsettings") && ext == ".json")
                    ParseAppSettings(file, result);
                else if (fileName is "web.config" or "app.config")
                    ParseXmlConfig(file, result);
            }
            catch
            {
                // Skip malformed config files
            }
        }

        return result;
    }

    private void ParseCsproj(FileScanner.ScannedFile file, AnalyzerResult result)
    {
        var doc = XDocument.Load(file.FullPath);
        var fileNodeId = $"File:{file.RelativePath}";

        // Create a ConfigFile node for the project file
        result.Nodes.Add(new GraphNode
        {
            Id = $"ConfigFile:{file.RelativePath}",
            Name = Path.GetFileName(file.FullPath),
            Type = NodeType.ConfigFile,
            FilePath = file.RelativePath,
            Language = "xml",
        });

        // Extract PackageReference elements
        var packageRefs = doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference");

        foreach (var pkgRef in packageRefs)
        {
            var packageName = pkgRef.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(packageName)) continue;

            var version = pkgRef.Attribute("Version")?.Value ?? "unknown";
            var packageId = $"NuGetPackage:{packageName}";

            // Create NuGetPackage node only if not already emitted
            if (_emittedPackageIds.Add(packageId))
            {
                var packageNode = new GraphNode
                {
                    Id = packageId,
                    Name = packageName,
                    Type = NodeType.NuGetPackage,
                    Language = "nuget",
                };
                packageNode.Properties["version"] = version;
                result.Nodes.Add(packageNode);
            }

            // REFERENCES_PACKAGE edge from project file to package
            result.Edges.Add(new GraphRelationship
            {
                Id = $"ReferencesPackage:{fileNodeId}->{packageId}",
                SourceId = fileNodeId,
                TargetId = packageId,
                Type = EdgeType.ReferencesPackage,
                Reason = $"PackageReference Version={version}",
            });
        }
    }

    private static void ParseAppSettings(FileScanner.ScannedFile file, AnalyzerResult result)
    {
        var json = File.ReadAllText(file.FullPath);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var configNodeId = $"ConfigFile:{file.RelativePath}";
        result.Nodes.Add(new GraphNode
        {
            Id = configNodeId,
            Name = Path.GetFileName(file.FullPath),
            Type = NodeType.ConfigFile,
            FilePath = file.RelativePath,
            Language = "json",
        });

        // Create ConfigSection nodes for top-level keys
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var sectionId = $"ConfigSection:{file.RelativePath}:{property.Name}";
            var sectionNode = new GraphNode
            {
                Id = sectionId,
                Name = property.Name,
                Type = NodeType.ConfigSection,
                FilePath = file.RelativePath,
                Language = "json",
            };

            // Store the value kind as a property
            sectionNode.Properties["valueKind"] = property.Value.ValueKind.ToString();
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                sectionNode.Properties["value"] = property.Value.GetString()!;
            }

            result.Nodes.Add(sectionNode);

            result.Edges.Add(new GraphRelationship
            {
                Id = $"Contains:{configNodeId}->{sectionId}",
                SourceId = configNodeId,
                TargetId = sectionId,
                Type = EdgeType.Contains,
                Reason = "config section",
            });
        }
    }

    private static void ParseXmlConfig(FileScanner.ScannedFile file, AnalyzerResult result)
    {
        var doc = XDocument.Load(file.FullPath);
        var configNodeId = $"ConfigFile:{file.RelativePath}";

        result.Nodes.Add(new GraphNode
        {
            Id = configNodeId,
            Name = Path.GetFileName(file.FullPath),
            Type = NodeType.ConfigFile,
            FilePath = file.RelativePath,
            Language = "xml",
        });

        // Extract appSettings keys
        var appSettings = doc.Descendants()
            .Where(e => e.Name.LocalName == "appSettings")
            .SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "add"));

        foreach (var setting in appSettings)
        {
            var key = setting.Attribute("key")?.Value;
            if (string.IsNullOrWhiteSpace(key)) continue;

            var value = setting.Attribute("value")?.Value ?? "";
            var sectionId = $"ConfigSection:{file.RelativePath}:appSettings:{key}";

            var sectionNode = new GraphNode
            {
                Id = sectionId,
                Name = key,
                Type = NodeType.ConfigSection,
                FilePath = file.RelativePath,
                Language = "xml",
            };
            sectionNode.Properties["value"] = value;
            sectionNode.Properties["section"] = "appSettings";
            result.Nodes.Add(sectionNode);

            result.Edges.Add(new GraphRelationship
            {
                Id = $"Contains:{configNodeId}->{sectionId}",
                SourceId = configNodeId,
                TargetId = sectionId,
                Type = EdgeType.Contains,
                Reason = "appSettings key",
            });
        }

        // Extract connectionStrings
        var connectionStrings = doc.Descendants()
            .Where(e => e.Name.LocalName == "connectionStrings")
            .SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "add"));

        foreach (var connStr in connectionStrings)
        {
            var name = connStr.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var sectionId = $"ConfigSection:{file.RelativePath}:connectionStrings:{name}";
            var providerName = connStr.Attribute("providerName")?.Value ?? "";

            var sectionNode = new GraphNode
            {
                Id = sectionId,
                Name = name,
                Type = NodeType.ConfigSection,
                FilePath = file.RelativePath,
                Language = "xml",
            };
            sectionNode.Properties["section"] = "connectionStrings";
            sectionNode.Properties["providerName"] = providerName;
            result.Nodes.Add(sectionNode);

            result.Edges.Add(new GraphRelationship
            {
                Id = $"Contains:{configNodeId}->{sectionId}",
                SourceId = configNodeId,
                TargetId = sectionId,
                Type = EdgeType.Contains,
                Reason = "connectionString",
            });
        }
    }
}
