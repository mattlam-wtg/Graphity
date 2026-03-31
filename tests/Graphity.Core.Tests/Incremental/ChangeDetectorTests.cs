using Graphity.Core.Incremental;

namespace Graphity.Core.Tests.Incremental;

public class ChangeDetectorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ChangeDetector _detector = new();

    public ChangeDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "graphity-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            // Force-remove read-only files (git objects)
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }

    private bool RunGitCommand(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = _tempDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null) return false;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(10000);
        return process.ExitCode == 0;
    }

    private void InitGitRepo()
    {
        RunGitCommand("init");
        RunGitCommand("config user.email test@test.com");
        RunGitCommand("config user.name Test");
    }

    [Fact]
    public void GetCurrentCommitHash_ReturnsHash_InGitRepo()
    {
        InitGitRepo();
        File.WriteAllText(Path.Combine(_tempDir, "file.cs"), "// test");
        RunGitCommand("add .");
        RunGitCommand("commit -m init");

        var hash = _detector.GetCurrentCommitHash(_tempDir);

        Assert.NotNull(hash);
        Assert.Equal(40, hash.Length);
        Assert.Matches("^[0-9a-f]+$", hash);
    }

    [Fact]
    public void CanDoIncremental_ReturnsFalse_WhenNoLastCommit()
    {
        Assert.False(_detector.CanDoIncremental(_tempDir, null));
        Assert.False(_detector.CanDoIncremental(_tempDir, ""));
    }

    [Fact]
    public void DetectChanges_ReturnsNull_WhenNoLastCommit()
    {
        var result = _detector.DetectChanges(_tempDir, null);
        Assert.Null(result);
    }

    [Fact]
    public void DetectChanges_ReturnsNull_ForNonGitDirectory()
    {
        var result = _detector.DetectChanges(_tempDir, "abc123");
        Assert.Null(result);
    }

    [Fact]
    public void GetCurrentCommitHash_ReturnsNull_ForNonGitDirectory()
    {
        var hash = _detector.GetCurrentCommitHash(_tempDir);
        Assert.Null(hash);
    }

    [Fact]
    public void CanDoIncremental_ReturnsFalse_ForInvalidCommit()
    {
        InitGitRepo();
        File.WriteAllText(Path.Combine(_tempDir, "file.cs"), "// test");
        RunGitCommand("add .");
        RunGitCommand("commit -m init");

        Assert.False(_detector.CanDoIncremental(_tempDir, "0000000000000000000000000000000000000000"));
    }

    [Fact]
    public void CanDoIncremental_ReturnsTrue_ForValidCommit()
    {
        InitGitRepo();
        File.WriteAllText(Path.Combine(_tempDir, "file.cs"), "// test");
        RunGitCommand("add .");
        RunGitCommand("commit -m init");

        var hash = _detector.GetCurrentCommitHash(_tempDir);
        Assert.True(_detector.CanDoIncremental(_tempDir, hash));
    }

    [Fact]
    public void DetectChanges_ReturnsEmptyChangeSet_WhenNoChanges()
    {
        InitGitRepo();
        File.WriteAllText(Path.Combine(_tempDir, "file.cs"), "// test");
        RunGitCommand("add .");
        RunGitCommand("commit -m init");

        var hash = _detector.GetCurrentCommitHash(_tempDir);
        var changes = _detector.DetectChanges(_tempDir, hash);

        Assert.NotNull(changes);
        Assert.Empty(changes.Added);
        Assert.Empty(changes.Modified);
        Assert.Empty(changes.Deleted);
    }

    [Fact]
    public void DetectChanges_DetectsAddedFile()
    {
        InitGitRepo();
        File.WriteAllText(Path.Combine(_tempDir, "file.cs"), "// test");
        RunGitCommand("add .");
        RunGitCommand("commit -m init");
        var hash = _detector.GetCurrentCommitHash(_tempDir);

        // Add a new file and commit
        File.WriteAllText(Path.Combine(_tempDir, "new.cs"), "// new");
        RunGitCommand("add .");
        RunGitCommand("commit -m add-new");

        var changes = _detector.DetectChanges(_tempDir, hash);

        Assert.NotNull(changes);
        Assert.Contains("new.cs", changes.Added);
    }

    [Fact]
    public void DetectChanges_DetectsModifiedFile()
    {
        InitGitRepo();
        File.WriteAllText(Path.Combine(_tempDir, "file.cs"), "// test");
        RunGitCommand("add .");
        RunGitCommand("commit -m init");
        var hash = _detector.GetCurrentCommitHash(_tempDir);

        // Modify and commit
        File.WriteAllText(Path.Combine(_tempDir, "file.cs"), "// modified");
        RunGitCommand("add .");
        RunGitCommand("commit -m modify");

        var changes = _detector.DetectChanges(_tempDir, hash);

        Assert.NotNull(changes);
        Assert.Contains("file.cs", changes.Modified);
    }

    [Fact]
    public void DetectChanges_DetectsDeletedFile()
    {
        InitGitRepo();
        File.WriteAllText(Path.Combine(_tempDir, "file.cs"), "// test");
        File.WriteAllText(Path.Combine(_tempDir, "remove.cs"), "// remove");
        RunGitCommand("add .");
        RunGitCommand("commit -m init");
        var hash = _detector.GetCurrentCommitHash(_tempDir);

        // Delete and commit
        File.Delete(Path.Combine(_tempDir, "remove.cs"));
        RunGitCommand("add .");
        RunGitCommand("commit -m delete");

        var changes = _detector.DetectChanges(_tempDir, hash);

        Assert.NotNull(changes);
        Assert.Contains("remove.cs", changes.Deleted);
    }
}
