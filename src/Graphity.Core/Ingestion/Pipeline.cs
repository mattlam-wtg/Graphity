using Graphity.Core.Graph;
using Graphity.Core.Analyzers.CSharp;
using Graphity.Core.Detection;
using Graphity.Core.Incremental;

namespace Graphity.Core.Ingestion;

public sealed class Pipeline
{
    private readonly List<ILanguageAnalyzer> _analyzers = new();

    /// <summary>
    /// Optional progress callback: (phaseName, percentage 0.0-1.0).
    /// </summary>
    public Action<string, double>? OnProgress { get; set; }

    /// <summary>
    /// Optional callback for verbose per-file logging.
    /// </summary>
    public Action<string>? OnVerbose { get; set; }

    public void RegisterAnalyzer(ILanguageAnalyzer analyzer) => _analyzers.Add(analyzer);

    public async Task<KnowledgeGraph> RunAsync(string path, CancellationToken ct = default)
    {
        var graph = new KnowledgeGraph();
        var repoRoot = FindRepoRoot(path);
        graph.RepoPath = repoRoot;
        graph.RepoName = Path.GetFileName(repoRoot);
        graph.IndexedAt = DateTime.UtcNow;

        // Phase 1: Scan files
        OnProgress?.Invoke("Scanning files", 0.1);
        var scanner = new FileScanner();
        var files = scanner.Scan(repoRoot);
        var fileResult = scanner.CreateFileAndFolderNodes(repoRoot, files);
        MergeResult(graph, fileResult);
        OnVerbose?.Invoke($"  Scanned {files.Count} files, created {fileResult.Nodes.Count} nodes");

        // Phase 2: Find and analyze solutions
        OnProgress?.Invoke("Analyzing source code", 0.3);
        var slnFiles = Directory.GetFiles(repoRoot, "*.sln", SearchOption.TopDirectoryOnly);
        var slnxFiles = Directory.GetFiles(repoRoot, "*.slnx", SearchOption.TopDirectoryOnly);
        var solutions = slnFiles.Concat(slnxFiles).ToArray();

        foreach (var analyzer in _analyzers)
        {
            if (ct.IsCancellationRequested) break;

            var preNodes = graph.Nodes.Count;
            var preEdges = graph.Edges.Count;

            if (analyzer is ISolutionAnalyzer solAnalyzer && solutions.Length > 0)
            {
                foreach (var sln in solutions)
                {
                    if (ct.IsCancellationRequested) break;
                    OnVerbose?.Invoke($"  Analyzing solution: {Path.GetFileName(sln)}");
                    try
                    {
                        var result = await solAnalyzer.AnalyzeSolutionAsync(sln, ct);
                        MergeResult(graph, result);
                    }
                    catch (Exception ex)
                    {
                        OnVerbose?.Invoke($"  Warning: failed to analyze {Path.GetFileName(sln)}: {ex.Message}");
                    }
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
                    OnVerbose?.Invoke($"  Analyzing: {file.RelativePath}");
                    try
                    {
                        var result = await analyzer.AnalyzeAsync(file.FullPath, repoRoot, ct);
                        MergeResult(graph, result);
                    }
                    catch (Exception ex)
                    {
                        OnVerbose?.Invoke($"  Warning: skipped {file.RelativePath}: {ex.Message}");
                    }
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
                    OnVerbose?.Invoke($"  Analyzing: {file.RelativePath}");
                    try
                    {
                        var result = await analyzer.AnalyzeAsync(file.FullPath, repoRoot, ct);
                        MergeResult(graph, result);
                    }
                    catch (Exception ex)
                    {
                        OnVerbose?.Invoke($"  Warning: skipped {file.RelativePath}: {ex.Message}");
                    }
                }
            }

            OnVerbose?.Invoke($"  {analyzer.GetType().Name}: +{graph.Nodes.Count - preNodes} nodes, +{graph.Edges.Count - preEdges} edges");
        }

        // Phase 3: Parse config files
        OnProgress?.Invoke("Parsing config files", 0.5);
        var configFiles = files.Where(f => f.Language == "config").ToList();
        if (configFiles.Count > 0)
        {
            var configParser = new CSharpConfigParser();
            var configResult = configParser.ParseConfigs(repoRoot, configFiles);
            MergeResult(graph, configResult);
            OnVerbose?.Invoke($"  Parsed {configFiles.Count} config files");
        }

        // Phase 4: Community Detection
        OnProgress?.Invoke("Detecting communities", 0.7);
        var communityDetector = new CommunityDetector();
        communityDetector.DetectCommunities(graph);

        // Phase 5: Process Tracing
        OnProgress?.Invoke("Tracing processes", 0.8);
        var scorer = new EntryPointScorer();
        var entryPoints = scorer.ScoreEntryPoints(graph);
        var processDetector = new ProcessDetector();
        processDetector.DetectProcesses(graph, entryPoints);

        OnProgress?.Invoke("Complete", 1.0);

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

    public async Task<(KnowledgeGraph graph, bool wasIncremental)> RunSmartAsync(
        string path, string? lastCommitHash, CancellationToken ct = default)
    {
        var repoRoot = FindRepoRoot(path);
        var changeDetector = new ChangeDetector();

        if (changeDetector.CanDoIncremental(repoRoot, lastCommitHash))
        {
            var changes = changeDetector.DetectChanges(repoRoot, lastCommitHash);
            if (changes != null && (changes.Added.Count + changes.Modified.Count + changes.Deleted.Count) > 0)
            {
                // For incremental, we need to rebuild the in-memory graph from storage
                // For now, fall through to full reindex if we can't load existing graph
                // In a real implementation, we'd load from LiteGraph
            }
        }

        // Full reindex
        var graph = await RunAsync(path, ct);
        return (graph, false);
    }

    private static void MergeResult(KnowledgeGraph graph, AnalyzerResult result)
    {
        foreach (var node in result.Nodes) graph.AddNode(node);
        foreach (var edge in result.Edges) graph.AddEdge(edge);
    }
}
