using Graphity.Core.Graph;
using Graphity.Search;
using Graphity.Storage;

namespace Graphity.Mcp;

public sealed class GraphServiceConfig
{
    public string? RepoPath { get; init; }
}

/// <summary>
/// Shared service that lazily loads the graph database, BM25 search index,
/// and metadata for the configured repository.
/// </summary>
public sealed class GraphService : IDisposable
{
    private readonly GraphServiceConfig _config;
    private LiteGraphAdapter? _adapter;
    private GraphQuerier? _querier;
    private Bm25Index? _searchIndex;
    private IndexMetadata? _metadata;
    private bool _initialized;
    private readonly object _lock = new();

    public GraphService(GraphServiceConfig config) => _config = config;

    public string RepoPath => _config.RepoPath ?? Directory.GetCurrentDirectory();

    public void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            var repoPath = RepoPath;
            var dataDir = StoragePaths.GetDataDirectory(repoPath);
            var metadataPath = StoragePaths.GetMetadataPath(repoPath);
            var dbPath = StoragePaths.GetDatabasePath(repoPath);

            if (!Directory.Exists(dataDir))
                throw new InvalidOperationException(
                    $"No .graphity index found at '{repoPath}'. Run 'graphity analyze' first.");

            // Load metadata
            _metadata = IndexMetadata.Load(metadataPath);

            // Load BM25 search index
            var bm25Path = Path.Combine(dataDir, "bm25-index.json");
            if (File.Exists(bm25Path))
                _searchIndex = Bm25Index.Load(dataDir);
            else
                _searchIndex = new Bm25Index();

            // Open LiteGraph database
            if (File.Exists(dbPath))
            {
                _adapter = new LiteGraphAdapter(dbPath);
                var graphName = _metadata?.RepoName ?? Path.GetFileName(repoPath);
                _adapter.InitializeAsync(graphName).GetAwaiter().GetResult();
                _querier = new GraphQuerier(_adapter);
            }

            _initialized = true;
        }
    }

    public bool IsInitialized => _initialized;

    public GraphQuerier Querier
    {
        get
        {
            EnsureInitialized();
            return _querier ?? throw new InvalidOperationException("Graph database not available.");
        }
    }

    public Bm25Index SearchIndex
    {
        get
        {
            EnsureInitialized();
            return _searchIndex ?? throw new InvalidOperationException("Search index not available.");
        }
    }

    public LiteGraphAdapter Adapter
    {
        get
        {
            EnsureInitialized();
            return _adapter ?? throw new InvalidOperationException("Graph database not available.");
        }
    }

    public IndexMetadata? Metadata
    {
        get
        {
            EnsureInitialized();
            return _metadata;
        }
    }

    public void Dispose() => _adapter?.Dispose();
}
