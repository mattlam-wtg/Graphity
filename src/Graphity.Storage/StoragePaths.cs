namespace Graphity.Storage;

/// <summary>
/// Provides conventional paths for the .graphity data directory.
/// </summary>
public static class StoragePaths
{
    private const string DataDirName = ".graphity";
    private const string DatabaseFileName = "graph.db";
    private const string MetadataFileName = "metadata.json";

    /// <summary>
    /// Gets the .graphity data directory for a given repo root.
    /// </summary>
    public static string GetDataDirectory(string repoRoot)
        => Path.Combine(repoRoot, DataDirName);

    /// <summary>
    /// Gets the LiteGraph database file path.
    /// </summary>
    public static string GetDatabasePath(string repoRoot)
        => Path.Combine(GetDataDirectory(repoRoot), DatabaseFileName);

    /// <summary>
    /// Gets the metadata JSON file path.
    /// </summary>
    public static string GetMetadataPath(string repoRoot)
        => Path.Combine(GetDataDirectory(repoRoot), MetadataFileName);
}
