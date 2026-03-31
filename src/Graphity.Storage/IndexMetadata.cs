using System.Text.Json;

namespace Graphity.Storage;

public sealed class IndexMetadata
{
    public string RepoName { get; set; } = "";
    public string RepoPath { get; set; } = "";
    public DateTime IndexedAtUtc { get; set; }
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public string? CommitHash { get; set; }

    public void Save(string metadataPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(metadataPath, json);
    }

    public static IndexMetadata? Load(string metadataPath)
    {
        if (!File.Exists(metadataPath))
            return null;
        var json = File.ReadAllText(metadataPath);
        return JsonSerializer.Deserialize<IndexMetadata>(json);
    }
}
