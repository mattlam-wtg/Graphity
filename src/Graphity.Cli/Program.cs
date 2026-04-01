using System.CommandLine;
using System.Diagnostics;

using Graphity.Cli.Commands;
using Graphity.Core.Analyzers.CSharp;
using Graphity.Core.Analyzers.Sql;
using Graphity.Core.Analyzers.TypeScript;
using Graphity.Core.Incremental;
using Graphity.Core.Ingestion;
using Graphity.Search;
using Graphity.Storage;

var rootCommand = new RootCommand("Graphity — code knowledge graph tool");

var pathArg = new Argument<string>("path") { DefaultValueFactory = _ => ".", Description = "Path to solution or directory" };
var skipEmbeddingsOption = new Option<bool>("--skip-embeddings") { Description = "Skip generating semantic embeddings" };
var verboseOption = new Option<bool>("--verbose") { Description = "Show detailed progress including per-file analysis" };

var analyzeCommand = new Command("analyze", "Index a codebase into a knowledge graph");
analyzeCommand.Add(pathArg);
analyzeCommand.Add(skipEmbeddingsOption);
analyzeCommand.Add(verboseOption);
rootCommand.Add(analyzeCommand);

var mcpCommand = new Command("mcp", "Start MCP server");
rootCommand.Add(mcpCommand);

var statusCommand = new Command("status", "Show index status");
rootCommand.Add(statusCommand);

var cleanCommand = new Command("clean", "Delete index data");
rootCommand.Add(cleanCommand);

var setupCommand = new Command("setup", "Auto-configure MCP for detected editors");
rootCommand.Add(setupCommand);

analyzeCommand.SetAction(async (parseResult, ct) =>
{
    var path = parseResult.GetValue(pathArg)!;
    var skipEmbeddings = parseResult.GetValue(skipEmbeddingsOption);
    var verbose = parseResult.GetValue(verboseOption);
    var sw = Stopwatch.StartNew();
    var fullPath = Path.GetFullPath(path);
    Console.WriteLine($"Indexing {fullPath}...");

    try
    {
        // Check for existing metadata to enable incremental indexing
        var existingMetadata = IndexMetadata.Load(StoragePaths.GetMetadataPath(fullPath));
        var lastCommitHash = existingMetadata?.CommitHash;

        var pipeline = new Pipeline();
        pipeline.RegisterAnalyzer(new RoslynAnalyzer());
        pipeline.RegisterAnalyzer(new TypeScriptAnalyzer());
        pipeline.RegisterAnalyzer(new SqlAnalyzer());

        // Wire up progress reporting
        pipeline.OnProgress = (phase, pct) =>
        {
            Console.WriteLine($"  [{pct:P0}] {phase}...");
        };

        if (verbose)
        {
            pipeline.OnVerbose = msg => Console.WriteLine(msg);
        }

        var (graph, wasIncremental) = await pipeline.RunSmartAsync(fullPath, lastCommitHash, ct);

        if (wasIncremental)
            Console.WriteLine($"  Incremental update applied");

        Console.WriteLine($"  Found {graph.Nodes.Count:N0} nodes, {graph.Edges.Count:N0} edges");

        // Detect current git commit hash
        var changeDetector = new ChangeDetector();
        var commitHash = changeDetector.GetCurrentCommitHash(fullPath);

        // Ensure data directory exists
        var dataDir = StoragePaths.GetDataDirectory(fullPath);
        Directory.CreateDirectory(dataDir);

        // Save metadata
        var metadata = new IndexMetadata
        {
            RepoName = graph.RepoName,
            RepoPath = graph.RepoPath,
            IndexedAtUtc = graph.IndexedAt,
            NodeCount = graph.Nodes.Count,
            EdgeCount = graph.Edges.Count,
            CommitHash = commitHash
        };
        metadata.Save(StoragePaths.GetMetadataPath(fullPath));

        // Persist graph to LiteGraph database for traversal queries
        var dbPath = StoragePaths.GetDatabasePath(fullPath);
        using (var adapter = new LiteGraphAdapter(dbPath))
        {
            await adapter.InitializeAsync(graph.RepoName, ct);
            foreach (var node in graph.Nodes.Values)
                await adapter.UpsertNodeAsync(node, ct);
            foreach (var edge in graph.Edges.Values)
                await adapter.UpsertEdgeAsync(edge, ct);
            Console.WriteLine($"  Graph database: {adapter.NodeCount} nodes, {adapter.EdgeCount} edges");
        }

        // Build and save search index
        var index = new Bm25Index();
        index.BuildIndex(graph.Nodes.Values);
        index.Save(dataDir);
        Console.WriteLine($"  Search index: {index.DocumentCount} documents indexed");

        // Build and save embedding index for hybrid search
        if (!skipEmbeddings)
        {
            using var embedder = new OnnxEmbedder();
            var hybrid = new HybridSearch(index, embedder);
            hybrid.BuildEmbeddingIndex(graph.Nodes.Values);
            hybrid.Save(dataDir);
            var modelNote = embedder.IsModelAvailable ? "ONNX model" : "hash-based fallback";
            Console.WriteLine($"  Embeddings: {hybrid.EmbeddingCount} vectors ({modelNote})");
        }

        sw.Stop();
        Console.WriteLine($"  Indexed in {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  Data saved to {dataDir}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        if (verbose)
            Console.Error.WriteLine(ex.StackTrace);
        Environment.ExitCode = 1;
    }
});

statusCommand.SetAction(_ =>
{
    var fullPath = Path.GetFullPath(".");
    var metadataPath = StoragePaths.GetMetadataPath(fullPath);
    var metadata = IndexMetadata.Load(metadataPath);

    if (metadata is null)
    {
        Console.WriteLine("No index found in the current directory.");
        Console.WriteLine($"  Run 'graphity analyze' to create one.");
        return;
    }

    var age = DateTime.UtcNow - metadata.IndexedAtUtc;
    var stale = age.TotalHours > 24 ? " (stale — more than 24h old)" : "";

    Console.WriteLine($"Graphity Index Status");
    Console.WriteLine($"  Repository:  {metadata.RepoName}");
    Console.WriteLine($"  Path:        {metadata.RepoPath}");
    Console.WriteLine($"  Indexed at:  {metadata.IndexedAtUtc:u}{stale}");
    Console.WriteLine($"  Nodes:       {metadata.NodeCount}");
    Console.WriteLine($"  Edges:       {metadata.EdgeCount}");
    if (!string.IsNullOrEmpty(metadata.CommitHash))
        Console.WriteLine($"  Commit:      {metadata.CommitHash[..Math.Min(12, metadata.CommitHash.Length)]}");

    // Staleness detection: compare stored commit to current HEAD
    if (!string.IsNullOrEmpty(metadata.CommitHash))
    {
        try
        {
            var changeDetector = new ChangeDetector();
            var currentHead = changeDetector.GetCurrentCommitHash(fullPath);

            if (currentHead != null && currentHead != metadata.CommitHash)
            {
                var changes = changeDetector.DetectChanges(fullPath, metadata.CommitHash);
                if (changes != null)
                {
                    var totalChanged = changes.Added.Count + changes.Modified.Count + changes.Deleted.Count;
                    Console.WriteLine();
                    Console.WriteLine($"  Warning: Index is out of date");
                    Console.WriteLine($"  HEAD:        {currentHead[..Math.Min(12, currentHead.Length)]}");
                    Console.WriteLine($"  Files changed since last index: {totalChanged}");
                    Console.WriteLine($"    Added: {changes.Added.Count}, Modified: {changes.Modified.Count}, Deleted: {changes.Deleted.Count}");
                    Console.WriteLine($"  Run 'graphity analyze' to update.");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine($"  Warning: Index may be out of date (HEAD has moved)");
                    Console.WriteLine($"  Run 'graphity analyze' to update.");
                }
            }
            else if (currentHead == metadata.CommitHash)
            {
                Console.WriteLine($"  Status:      Up to date with HEAD");
            }
        }
        catch
        {
            // Not a git repo or git not available — skip staleness check
        }
    }
});

cleanCommand.SetAction(_ =>
{
    var fullPath = Path.GetFullPath(".");
    var dataDir = StoragePaths.GetDataDirectory(fullPath);

    if (!Directory.Exists(dataDir))
    {
        Console.WriteLine("No .graphity directory found. Nothing to clean.");
        return;
    }

    Directory.Delete(dataDir, recursive: true);
    Console.WriteLine($"Deleted {dataDir}");
});

mcpCommand.SetAction(async (_, ct) =>
{
    try
    {
        var repoPath = Path.GetFullPath(".");
        await Graphity.Mcp.GraphityMcpServer.RunAsync(repoPath, ct);
    }
    catch (OperationCanceledException)
    {
        // Normal shutdown
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"MCP server error: {ex.Message}");
        Environment.ExitCode = 1;
    }
});

setupCommand.SetAction(_ =>
{
    try
    {
        SetupCommand.Run();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Setup error: {ex.Message}");
        Environment.ExitCode = 1;
    }
});

var parseResult = rootCommand.Parse(args);
return await parseResult.InvokeAsync();
