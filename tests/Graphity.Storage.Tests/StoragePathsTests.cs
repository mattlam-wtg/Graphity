namespace Graphity.Storage.Tests;

public class StoragePathsTests
{
    [Fact]
    public void GetDataDirectory_returns_graphity_subdirectory()
    {
        var repoRoot = "/some/repo";
        var dataDir = StoragePaths.GetDataDirectory(repoRoot);

        Assert.EndsWith(".graphity", dataDir);
        Assert.StartsWith(repoRoot, dataDir);
    }

    [Fact]
    public void GetDatabasePath_returns_path_under_graphity()
    {
        var repoRoot = "/some/repo";
        var dbPath = StoragePaths.GetDatabasePath(repoRoot);

        Assert.Contains(".graphity", dbPath);
        Assert.EndsWith("graph.db", dbPath);
        Assert.StartsWith(repoRoot, dbPath);
    }
}
