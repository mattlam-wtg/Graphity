using Graphity.Core.Graph;

namespace Graphity.Core.Ingestion;

public sealed class FileScanner
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", "packages",
        "TestResults", "debug", "release", ".nuget", "artifacts"
    };

    private static readonly Dictionary<string, string> ExtensionToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".js"] = "javascript",
        [".jsx"] = "javascript",
        [".sql"] = "sql",
        [".csproj"] = "config",
        [".json"] = "config",
        [".config"] = "config",
        [".props"] = "config",
        [".targets"] = "config",
    };

    public record ScannedFile(string FullPath, string RelativePath, string Language, long Size);

    public IReadOnlyList<ScannedFile> Scan(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        var results = new List<ScannedFile>();
        var gitignorePatterns = LoadGitignore(root);

        ScanDirectory(root, root, results, gitignorePatterns);
        return results;
    }

    public AnalyzerResult CreateFileAndFolderNodes(string rootPath, IReadOnlyList<ScannedFile> files)
    {
        var result = new AnalyzerResult();
        var folders = new HashSet<string>();
        var emittedEdges = new HashSet<string>();

        foreach (var file in files)
        {
            var fileNode = new GraphNode
            {
                Id = $"File:{file.RelativePath}",
                Name = Path.GetFileName(file.RelativePath),
                Type = NodeType.File,
                FilePath = file.RelativePath,
                Language = file.Language,
            };
            fileNode.Properties["size"] = file.Size;
            result.Nodes.Add(fileNode);

            // Create folder nodes up the tree
            var dir = Path.GetDirectoryName(file.RelativePath);
            while (!string.IsNullOrEmpty(dir))
            {
                if (folders.Add(dir))
                {
                    result.Nodes.Add(new GraphNode
                    {
                        Id = $"Folder:{dir}",
                        Name = Path.GetFileName(dir),
                        Type = NodeType.Folder,
                        FilePath = dir,
                        Language = "none",
                    });
                }

                var parent = Path.GetDirectoryName(dir);
                if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(dir))
                {
                    var edgeId = $"Contains:{parent}->{dir}";
                    if (emittedEdges.Add(edgeId))
                    {
                        result.Edges.Add(new GraphRelationship
                        {
                            Id = edgeId,
                            SourceId = $"Folder:{parent}",
                            TargetId = $"Folder:{dir}",
                            Type = EdgeType.Contains,
                            Reason = "directory structure",
                        });
                    }
                }
                dir = parent;
            }

            // Folder CONTAINS File edge
            var fileDir = Path.GetDirectoryName(file.RelativePath);
            if (!string.IsNullOrEmpty(fileDir))
            {
                var fileEdgeId = $"Contains:{fileDir}->{file.RelativePath}";
                if (emittedEdges.Add(fileEdgeId))
                {
                    result.Edges.Add(new GraphRelationship
                    {
                        Id = fileEdgeId,
                        SourceId = $"Folder:{fileDir}",
                        TargetId = $"File:{file.RelativePath}",
                        Type = EdgeType.Contains,
                        Reason = "directory structure",
                    });
                }
            }
        }

        return result;
    }

    private void ScanDirectory(string dir, string root, List<ScannedFile> results, HashSet<string> gitignorePatterns)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var ext = Path.GetExtension(file);
            if (!ExtensionToLanguage.TryGetValue(ext, out var language)) continue;

            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsGitignored(relativePath, gitignorePatterns)) continue;

            var info = new FileInfo(file);
            results.Add(new ScannedFile(file, relativePath, language, info.Length));
        }

        foreach (var subDir in Directory.EnumerateDirectories(dir))
        {
            var dirName = Path.GetFileName(subDir);
            if (IgnoredDirectories.Contains(dirName)) continue;
            if (dirName.StartsWith('.')) continue;

            var relativePath = Path.GetRelativePath(root, subDir).Replace('\\', '/');
            if (IsGitignored(relativePath + "/", gitignorePatterns)) continue;

            ScanDirectory(subDir, root, results, gitignorePatterns);
        }
    }

    private static HashSet<string> LoadGitignore(string root)
    {
        var patterns = new HashSet<string>();
        var gitignorePath = Path.Combine(root, ".gitignore");
        if (!File.Exists(gitignorePath)) return patterns;

        foreach (var line in File.ReadLines(gitignorePath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
            patterns.Add(trimmed);
        }
        return patterns;
    }

    private static bool IsGitignored(string relativePath, HashSet<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            // Simple gitignore matching - directory patterns and prefix matching
            var p = pattern.TrimEnd('/');
            if (relativePath.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)) return true;
            if (relativePath.Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
            // Check if any path segment matches
            var segments = relativePath.Split('/');
            if (segments.Any(s => s.Equals(p, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }
}
