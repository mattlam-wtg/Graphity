using System.CommandLine;
using System.Diagnostics;

using Graphity.Core.Analyzers.CSharp;
using Graphity.Core.Ingestion;
using Graphity.Search;
using Graphity.Storage;

var rootCommand = new RootCommand("Graphity — code knowledge graph tool");

var pathArg = new Argument<string>("path") { DefaultValueFactory = _ => ".", Description = "Path to solution or directory" };
var analyzeCommand = new Command("analyze", "Index a codebase into a knowledge graph");
analyzeCommand.Add(pathArg);
rootCommand.Add(analyzeCommand);

var mcpCommand = new Command("mcp", "Start MCP server");
rootCommand.Add(mcpCommand);

var statusCommand = new Command("status", "Show index status");
rootCommand.Add(statusCommand);

var cleanCommand = new Command("clean", "Delete index data");
rootCommand.Add(cleanCommand);

analyzeCommand.SetAction(async (parseResult, ct) =>
{
    var path = parseResult.GetValue(pathArg)!;
    var sw = Stopwatch.StartNew();
    var fullPath = Path.GetFullPath(path);
    Console.WriteLine($"Indexing {fullPath}...");

    var pipeline = new Pipeline();
    pipeline.RegisterAnalyzer(new RoslynAnalyzer());

    var graph = await pipeline.RunAsync(fullPath, ct);
    Console.WriteLine($"  Found {graph.Nodes.Count} nodes, {graph.Edges.Count} edges");

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
        EdgeCount = graph.Edges.Count
    };
    metadata.Save(StoragePaths.GetMetadataPath(fullPath));

    // Build and save search index
    var index = new Bm25Index();
    index.BuildIndex(graph.Nodes.Values);
    index.Save(dataDir);
    Console.WriteLine($"  Search index: {index.DocumentCount} documents indexed");

    sw.Stop();
    Console.WriteLine($"  Indexed in {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"  Data saved to {dataDir}");
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
    var repoPath = Path.GetFullPath(".");
    await Graphity.Mcp.GraphityMcpServer.RunAsync(repoPath, ct);
});

var parseResult = rootCommand.Parse(args);
return await parseResult.InvokeAsync();
