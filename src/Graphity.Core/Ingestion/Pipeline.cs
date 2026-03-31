using Graphity.Core.Graph;
using Graphity.Core.Analyzers.CSharp;

namespace Graphity.Core.Ingestion;

public sealed class Pipeline
{
    private readonly List<ILanguageAnalyzer> _analyzers = new();

    public void RegisterAnalyzer(ILanguageAnalyzer analyzer) => _analyzers.Add(analyzer);

    public async Task<KnowledgeGraph> RunAsync(string path, CancellationToken ct = default)
    {
        var graph = new KnowledgeGraph();
        var repoRoot = FindRepoRoot(path);
        graph.RepoPath = repoRoot;
        graph.RepoName = Path.GetFileName(repoRoot);
        graph.IndexedAt = DateTime.UtcNow;

        // Phase 1: Scan files
        var scanner = new FileScanner();
        var files = scanner.Scan(repoRoot);
        var fileResult = scanner.CreateFileAndFolderNodes(repoRoot, files);
        MergeResult(graph, fileResult);

        // Phase 2: Find and analyze solutions
        var slnFiles = Directory.GetFiles(repoRoot, "*.sln", SearchOption.TopDirectoryOnly);
        var slnxFiles = Directory.GetFiles(repoRoot, "*.slnx", SearchOption.TopDirectoryOnly);
        var solutions = slnFiles.Concat(slnxFiles).ToArray();

        foreach (var analyzer in _analyzers)
        {
            if (ct.IsCancellationRequested) break;

            if (analyzer is ISolutionAnalyzer solAnalyzer && solutions.Length > 0)
            {
                foreach (var sln in solutions)
                {
                    if (ct.IsCancellationRequested) break;
                    var result = await solAnalyzer.AnalyzeSolutionAsync(sln, ct);
                    MergeResult(graph, result);
                }
            }
            else if (analyzer is ISolutionAnalyzer && solutions.Length == 0)
            {
                // No solution files found — fall back to file-by-file analysis
                var langFiles = files
                    .Where(f => analyzer.SupportedExtensions.Contains(
                        Path.GetExtension(f.FullPath).ToLowerInvariant()))
                    .ToList();

                foreach (var file in langFiles)
                {
                    if (ct.IsCancellationRequested) break;
                    var result = await analyzer.AnalyzeAsync(file.FullPath, repoRoot, ct);
                    MergeResult(graph, result);
                }
            }
            else
            {
                // File-by-file analysis for non-solution analyzers
                var langFiles = files
                    .Where(f => analyzer.SupportedExtensions.Contains(Path.GetExtension(f.FullPath)))
                    .ToList();

                foreach (var file in langFiles)
                {
                    if (ct.IsCancellationRequested) break;
                    var result = await analyzer.AnalyzeAsync(file.FullPath, repoRoot, ct);
                    MergeResult(graph, result);
                }
            }
        }

        // Phase 3: Parse config files
        var configFiles = files.Where(f => f.Language == "config").ToList();
        if (configFiles.Count > 0)
        {
            var configParser = new CSharpConfigParser();
            var configResult = configParser.ParseConfigs(repoRoot, configFiles);
            MergeResult(graph, configResult);
        }

        return graph;
    }

    private static string FindRepoRoot(string path)
    {
        // If path is a .sln/.slnx file, use its directory
        if (File.Exists(path) && (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                                  || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
        {
            return Path.GetDirectoryName(Path.GetFullPath(path))!;
        }

        return Path.GetFullPath(path);
    }

    private static void MergeResult(KnowledgeGraph graph, AnalyzerResult result)
    {
        foreach (var node in result.Nodes) graph.AddNode(node);
        foreach (var edge in result.Edges) graph.AddEdge(edge);
    }
}
