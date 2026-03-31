namespace Graphity.Core.Incremental;
using Graphity.Core.Graph;
using Graphity.Core.Ingestion;

public sealed class IncrementalPipeline
{
    private readonly List<ILanguageAnalyzer> _analyzers = new();

    public void RegisterAnalyzer(ILanguageAnalyzer analyzer) => _analyzers.Add(analyzer);

    public async Task<KnowledgeGraph> RunIncrementalAsync(
        KnowledgeGraph existingGraph,
        ChangeDetector.ChangeSet changes,
        CancellationToken ct = default)
    {
        var repoRoot = existingGraph.RepoPath;

        // 1. Remove nodes/edges from deleted and modified files
        foreach (var file in changes.Deleted.Concat(changes.Modified))
            existingGraph.RemoveNodesByFile(file);

        // 2. Re-analyze added + modified files
        var filesToAnalyze = changes.Added.Concat(changes.Modified).ToList();

        foreach (var relativePath in filesToAnalyze)
        {
            var fullPath = Path.GetFullPath(Path.Combine(repoRoot, relativePath));
            if (!File.Exists(fullPath)) continue;

            var ext = Path.GetExtension(fullPath).ToLowerInvariant();
            var analyzer = _analyzers.FirstOrDefault(a => a.SupportedExtensions.Contains(ext));
            if (analyzer == null) continue;

            try
            {
                var result = await analyzer.AnalyzeAsync(fullPath, repoRoot, ct);
                foreach (var node in result.Nodes) existingGraph.AddNode(node);
                foreach (var edge in result.Edges) existingGraph.AddEdge(edge);
            }
            catch { /* skip files that fail to analyze */ }
        }

        // 3. Recreate file/folder nodes for new files
        var scanner = new FileScanner();
        var newScanned = filesToAnalyze
            .Where(f => File.Exists(Path.GetFullPath(Path.Combine(repoRoot, f))))
            .Select(f =>
            {
                var full = Path.GetFullPath(Path.Combine(repoRoot, f));
                var info = new FileInfo(full);
                return new FileScanner.ScannedFile(full, f, DetectLanguage(f), info.Length);
            })
            .ToList();

        var fileResult = scanner.CreateFileAndFolderNodes(repoRoot, newScanned);
        foreach (var node in fileResult.Nodes) existingGraph.AddNode(node);
        foreach (var edge in fileResult.Edges) existingGraph.AddEdge(edge);

        existingGraph.IndexedAt = DateTime.UtcNow;
        return existingGraph;
    }

    private static string DetectLanguage(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".ts" or ".tsx" => "typescript",
        ".js" or ".jsx" => "javascript",
        ".sql" => "sql",
        ".json" or ".csproj" or ".config" or ".props" => "config",
        _ => "unknown"
    };
}
