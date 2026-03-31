namespace Graphity.Core.Incremental;

public sealed class ChangeDetector
{
    public record ChangeSet(
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Modified,
        IReadOnlyList<string> Deleted,
        string CurrentCommitHash);

    public ChangeSet? DetectChanges(string repoPath, string? lastIndexedCommit)
    {
        if (string.IsNullOrEmpty(lastIndexedCommit)) return null;

        var currentCommit = GetCurrentCommitHash(repoPath);
        if (currentCommit == null) return null;
        if (currentCommit == lastIndexedCommit) return new ChangeSet([], [], [], currentCommit);

        // Run: git diff --name-status <lastCommit> HEAD
        var diffOutput = RunGit(repoPath, $"diff --name-status {lastIndexedCommit} HEAD");
        if (diffOutput == null) return null;

        var added = new List<string>();
        var modified = new List<string>();
        var deleted = new List<string>();

        foreach (var line in diffOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', 2);
            if (parts.Length < 2) continue;

            var status = parts[0].Trim();
            var filePath = parts[1].Trim().Replace('\\', '/');

            switch (status[0])
            {
                case 'A': added.Add(filePath); break;
                case 'M': modified.Add(filePath); break;
                case 'D': deleted.Add(filePath); break;
                case 'R': // Renamed
                    var renameParts = filePath.Split('\t', 2);
                    if (renameParts.Length == 2)
                    {
                        deleted.Add(renameParts[0].Trim());
                        added.Add(renameParts[1].Trim());
                    }
                    break;
            }
        }

        // Also check uncommitted changes
        var statusOutput = RunGit(repoPath, "status --porcelain");
        if (statusOutput != null)
        {
            foreach (var line in statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length < 4) continue;
                var xy = line[..2];
                var file = line[3..].Trim().Replace('\\', '/');

                if (xy.Contains('?')) added.Add(file);
                else if (xy.Contains('D')) deleted.Add(file);
                else modified.Add(file);
            }
        }

        return new ChangeSet(added.Distinct().ToList(), modified.Distinct().ToList(),
                            deleted.Distinct().ToList(), currentCommit);
    }

    public string? GetCurrentCommitHash(string repoPath)
    {
        var result = RunGit(repoPath, "rev-parse HEAD");
        return result?.Trim();
    }

    public bool CanDoIncremental(string repoPath, string? lastIndexedCommit)
    {
        if (string.IsNullOrEmpty(lastIndexedCommit)) return false;
        var check = RunGit(repoPath, $"cat-file -t {lastIndexedCommit}");
        return check?.Trim() == "commit";
    }

    private static string? RunGit(string workDir, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}
