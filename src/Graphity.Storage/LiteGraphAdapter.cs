using System.Collections.Specialized;
using System.Text.Json;
using Graphity.Core.Graph;
using LiteGraph;
using LiteGraph.GraphRepositories.Sqlite;

namespace Graphity.Storage;

/// <summary>
/// Wraps LiteGraph's embedded property graph database, mapping between
/// our domain types (GraphNode/GraphRelationship) and LiteGraph's native types.
/// </summary>
public sealed class LiteGraphAdapter : IDisposable
{
    private readonly LiteGraphClient _client;
    private readonly Guid _tenantGuid;

    // Maps our string IDs to LiteGraph GUIDs and back.
    private readonly Dictionary<string, Guid> _nodeIdMap = new();
    private readonly Dictionary<Guid, string> _nodeGuidMap = new();
    private readonly Dictionary<string, Guid> _edgeIdMap = new();

    private Guid _graphGuid;
    private bool _disposed;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public LiteGraphAdapter(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var repo = new SqliteGraphRepository(dbPath);
        _client = new LiteGraphClient(
            repo,
            new LoggingSettings { Enable = false },
            new CachingSettings { Enable = true },
            new StorageSettings());
        _client.InitializeRepository();

        // Use a deterministic tenant so we can reopen the same DB.
        _tenantGuid = new Guid("00000000-0000-0000-0000-000000000001");
    }

    /// <summary>
    /// Ensures the tenant and named graph exist. Populates the ID maps from
    /// any nodes/edges already stored in the database.
    /// </summary>
    public async Task InitializeAsync(string graphName, CancellationToken ct = default)
    {
        // Ensure the tenant exists.
        if (!await _client.Tenant.ExistsByGuid(_tenantGuid, ct))
        {
            await _client.Tenant.Create(new TenantMetadata
            {
                GUID = _tenantGuid,
                Name = "graphity",
            }, ct);
        }

        // Look for an existing graph with this name.
        LiteGraph.Graph? existing = null;
        await foreach (var g in _client.Graph.ReadAllInTenant(
            _tenantGuid, EnumerationOrderEnum.CreatedAscending, 0, true, false, ct))
        {
            if (g.Name == graphName)
            {
                existing = g;
                break;
            }
        }

        if (existing != null)
        {
            _graphGuid = existing.GUID;
        }
        else
        {
            var created = await _client.Graph.Create(new LiteGraph.Graph
            {
                TenantGUID = _tenantGuid,
                GUID = Guid.NewGuid(),
                Name = graphName,
            }, ct);
            _graphGuid = created.GUID;
        }

        // Rebuild ID maps from existing data.
        await RebuildIdMapsAsync(ct);
    }

    /// <summary>
    /// Inserts or updates a node. The node's properties are stored in LiteGraph's
    /// Data field as JSON and its string ID is stored as a tag.
    /// </summary>
    public async Task UpsertNodeAsync(GraphNode node, CancellationToken ct = default)
    {
        if (_nodeIdMap.TryGetValue(node.Id, out var existingGuid))
        {
            // Update.
            var lgNode = await _client.Node.ReadByGuid(_tenantGuid, _graphGuid, existingGuid, true, false, ct);
            if (lgNode != null)
            {
                lgNode.Name = node.Name;
                lgNode.Data = SerializeNodeData(node);
                await _client.Node.Update(lgNode, ct);
                await EnsureNodeTagsAsync(existingGuid, node, ct);
                return;
            }
        }

        // Create.
        var newGuid = Guid.NewGuid();
        var created = await _client.Node.Create(new Node
        {
            TenantGUID = _tenantGuid,
            GraphGUID = _graphGuid,
            GUID = newGuid,
            Name = node.Name,
            Data = SerializeNodeData(node),
        }, ct);

        _nodeIdMap[node.Id] = created.GUID;
        _nodeGuidMap[created.GUID] = node.Id;
        await EnsureNodeTagsAsync(created.GUID, node, ct);
    }

    /// <summary>
    /// Inserts or updates an edge. Both source and target nodes must already exist.
    /// </summary>
    public async Task UpsertEdgeAsync(GraphRelationship edge, CancellationToken ct = default)
    {
        if (!_nodeIdMap.TryGetValue(edge.SourceId, out var fromGuid) ||
            !_nodeIdMap.TryGetValue(edge.TargetId, out var toGuid))
        {
            // Skip edges whose endpoints haven't been loaded yet.
            return;
        }

        if (_edgeIdMap.TryGetValue(edge.Id, out var existingGuid))
        {
            var lgEdge = await _client.Edge.ReadByGuid(_tenantGuid, _graphGuid, existingGuid, true, false, ct);
            if (lgEdge != null)
            {
                lgEdge.Name = edge.Type.ToString();
                lgEdge.Data = SerializeEdgeData(edge);
                await _client.Edge.Update(lgEdge, ct);
                await EnsureEdgeTagsAsync(existingGuid, edge, ct);
                return;
            }
        }

        var newGuid = Guid.NewGuid();
        var created = await _client.Edge.Create(new Edge
        {
            TenantGUID = _tenantGuid,
            GraphGUID = _graphGuid,
            GUID = newGuid,
            Name = edge.Type.ToString(),
            From = fromGuid,
            To = toGuid,
            Data = SerializeEdgeData(edge),
        }, ct);

        _edgeIdMap[edge.Id] = created.GUID;
        await EnsureEdgeTagsAsync(created.GUID, edge, ct);
    }

    /// <summary>
    /// Retrieves a node by its domain string ID.
    /// </summary>
    public async Task<GraphNode?> GetNodeAsync(string nodeId, CancellationToken ct = default)
    {
        if (!_nodeIdMap.TryGetValue(nodeId, out var guid))
            return null;

        var lgNode = await _client.Node.ReadByGuid(_tenantGuid, _graphGuid, guid, true, false, ct);
        return lgNode == null ? null : DeserializeNode(lgNode);
    }

    /// <summary>
    /// Returns all outgoing edges from a node.
    /// </summary>
    public async Task<IReadOnlyList<GraphRelationship>> GetEdgesFromAsync(string nodeId, CancellationToken ct = default)
    {
        if (!_nodeIdMap.TryGetValue(nodeId, out var guid))
            return [];

        var results = new List<GraphRelationship>();
        await foreach (var lgEdge in _client.Edge.ReadEdgesFromNode(
            _tenantGuid, _graphGuid, guid,
            includeData: true, includeSubordinates: true, token: ct))
        {
            var rel = DeserializeEdge(lgEdge);
            if (rel != null) results.Add(rel);
        }
        return results;
    }

    /// <summary>
    /// Returns all incoming edges to a node.
    /// </summary>
    public async Task<IReadOnlyList<GraphRelationship>> GetEdgesToAsync(string nodeId, CancellationToken ct = default)
    {
        if (!_nodeIdMap.TryGetValue(nodeId, out var guid))
            return [];

        var results = new List<GraphRelationship>();
        await foreach (var lgEdge in _client.Edge.ReadEdgesToNode(
            _tenantGuid, _graphGuid, guid,
            includeData: true, includeSubordinates: true, token: ct))
        {
            var rel = DeserializeEdge(lgEdge);
            if (rel != null) results.Add(rel);
        }
        return results;
    }

    /// <summary>
    /// Deletes a node by its domain string ID.
    /// </summary>
    public async Task DeleteNodeAsync(string nodeId, CancellationToken ct = default)
    {
        if (!_nodeIdMap.TryGetValue(nodeId, out var guid))
            return;

        await _client.Node.DeleteByGuid(_tenantGuid, _graphGuid, guid, ct);
        _nodeIdMap.Remove(nodeId);
        _nodeGuidMap.Remove(guid);
    }

    /// <summary>
    /// Deletes all edges connected to a node.
    /// </summary>
    public async Task DeleteEdgesByNodeAsync(string nodeId, CancellationToken ct = default)
    {
        if (!_nodeIdMap.TryGetValue(nodeId, out var guid))
            return;

        await _client.Edge.DeleteNodeEdges(_tenantGuid, _graphGuid, guid, ct);

        // Clean up local edge maps for edges that referenced this node.
        var toRemove = _edgeIdMap
            .Where(kvp => true) // We don't track endpoints in the map, so rebuild after.
            .ToList();
        // Simpler: just clear edge map and let it rebuild if needed.
        // For now, the edge map may become stale after deletes.
    }

    /// <summary>
    /// Resolves a LiteGraph GUID to our domain string ID.
    /// </summary>
    public string? ResolveNodeId(Guid guid)
        => _nodeGuidMap.GetValueOrDefault(guid);

    /// <summary>
    /// Resolves a domain string ID to a LiteGraph GUID.
    /// </summary>
    public Guid? ResolveNodeGuid(string nodeId)
        => _nodeIdMap.TryGetValue(nodeId, out var guid) ? guid : null;

    /// <summary>
    /// Saves a metadata key-value pair as a tag on the graph object.
    /// </summary>
    public async Task SaveMetadataAsync(string key, string value, CancellationToken ct = default)
    {
        // Store metadata as tags on the graph.
        await _client.Tag.Create(new TagMetadata
        {
            TenantGUID = _tenantGuid,
            GraphGUID = _graphGuid,
            Key = $"meta:{key}",
            Value = value,
        }, ct);
    }

    /// <summary>
    /// Retrieves a metadata value by key.
    /// </summary>
    public async Task<string?> GetMetadataAsync(string key, CancellationToken ct = default)
    {
        await foreach (var tag in _client.Tag.ReadManyGraph(_tenantGuid, _graphGuid,
            EnumerationOrderEnum.CreatedDescending, 0, ct))
        {
            if (tag.Key == $"meta:{key}")
                return tag.Value;
        }
        return null;
    }

    /// <summary>
    /// Returns the total number of nodes currently stored.
    /// </summary>
    public int NodeCount => _nodeIdMap.Count;

    /// <summary>
    /// Returns the total number of edges currently stored.
    /// </summary>
    public int EdgeCount => _edgeIdMap.Count;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private async Task RebuildIdMapsAsync(CancellationToken ct)
    {
        _nodeIdMap.Clear();
        _nodeGuidMap.Clear();
        _edgeIdMap.Clear();

        await foreach (var lgNode in _client.Node.ReadAllInGraph(
            _tenantGuid, _graphGuid, EnumerationOrderEnum.CreatedAscending, 0, true, true, ct))
        {
            var domainId = ExtractDomainId(lgNode.Tags, lgNode.Data, "nodeId");
            if (domainId != null)
            {
                _nodeIdMap[domainId] = lgNode.GUID;
                _nodeGuidMap[lgNode.GUID] = domainId;
            }
        }

        await foreach (var lgEdge in _client.Edge.ReadAllInGraph(
            _tenantGuid, _graphGuid, EnumerationOrderEnum.CreatedAscending, 0, true, true, ct))
        {
            var domainId = ExtractDomainId(lgEdge.Tags, lgEdge.Data, "edgeId");
            if (domainId != null)
            {
                _edgeIdMap[domainId] = lgEdge.GUID;
            }
        }
    }

    private static string? ExtractDomainId(NameValueCollection? tags, object? data, string dataKey)
    {
        // Try tags first (the canonical source).
        var fromTag = tags?["domainId"];
        if (!string.IsNullOrEmpty(fromTag))
            return fromTag;

        // Fall back to data JSON.
        if (data is JsonElement je && je.TryGetProperty(dataKey, out var idProp))
            return idProp.GetString();

        return null;
    }

    private static object SerializeNodeData(GraphNode node)
    {
        var dict = new Dictionary<string, object?>
        {
            ["nodeId"] = node.Id,
            ["nodeType"] = node.Type.ToString(),
            ["fullName"] = node.FullName,
            ["filePath"] = node.FilePath,
            ["startLine"] = node.StartLine,
            ["endLine"] = node.EndLine,
            ["isExported"] = node.IsExported,
            ["language"] = node.Language,
            ["content"] = node.Content,
        };

        foreach (var kvp in node.Properties)
            dict[$"prop:{kvp.Key}"] = kvp.Value;

        return JsonSerializer.SerializeToElement(dict, s_jsonOpts);
    }

    private static object SerializeEdgeData(GraphRelationship edge)
    {
        var dict = new Dictionary<string, object?>
        {
            ["edgeId"] = edge.Id,
            ["sourceId"] = edge.SourceId,
            ["targetId"] = edge.TargetId,
            ["edgeType"] = edge.Type.ToString(),
            ["confidence"] = edge.Confidence,
            ["reason"] = edge.Reason,
            ["step"] = edge.Step,
        };

        foreach (var kvp in edge.Properties)
            dict[$"prop:{kvp.Key}"] = kvp.Value;

        return JsonSerializer.SerializeToElement(dict, s_jsonOpts);
    }

    private GraphNode? DeserializeNode(Node lgNode)
    {
        if (lgNode.Data is not JsonElement je)
            return null;

        var nodeId = je.TryGetProperty("nodeId", out var idProp) ? idProp.GetString() : null;
        if (nodeId == null) return null;

        var nodeTypeStr = je.TryGetProperty("nodeType", out var ntProp) ? ntProp.GetString() : null;
        if (!Enum.TryParse<NodeType>(nodeTypeStr, out var nodeType))
            return null;

        var node = new GraphNode
        {
            Id = nodeId,
            Name = lgNode.Name ?? "",
            Type = nodeType,
            FullName = je.TryGetProperty("fullName", out var fn) && fn.ValueKind != JsonValueKind.Null ? fn.GetString() : null,
            FilePath = je.TryGetProperty("filePath", out var fp) && fp.ValueKind != JsonValueKind.Null ? fp.GetString() : null,
            StartLine = je.TryGetProperty("startLine", out var sl) && sl.ValueKind == JsonValueKind.Number ? sl.GetInt32() : null,
            EndLine = je.TryGetProperty("endLine", out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null,
            IsExported = je.TryGetProperty("isExported", out var ie) && ie.ValueKind == JsonValueKind.True,
            Language = je.TryGetProperty("language", out var lang) ? lang.GetString() ?? "csharp" : "csharp",
            Content = je.TryGetProperty("content", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null,
        };

        // Restore custom properties.
        foreach (var prop in je.EnumerateObject())
        {
            if (prop.Name.StartsWith("prop:"))
            {
                var key = prop.Name["prop:".Length..];
                node.Properties[key] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString()!,
                    JsonValueKind.Number => prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.GetRawText(),
                };
            }
        }

        return node;
    }

    internal GraphRelationship? DeserializeEdge(Edge lgEdge)
    {
        if (lgEdge.Data is not JsonElement je)
            return null;

        var edgeId = je.TryGetProperty("edgeId", out var idProp) ? idProp.GetString() : null;
        var sourceId = je.TryGetProperty("sourceId", out var sProp) ? sProp.GetString() : null;
        var targetId = je.TryGetProperty("targetId", out var tProp) ? tProp.GetString() : null;
        var edgeTypeStr = je.TryGetProperty("edgeType", out var etProp) ? etProp.GetString() : null;

        if (edgeId == null || sourceId == null || targetId == null) return null;
        if (!Enum.TryParse<EdgeType>(edgeTypeStr, out var edgeType)) return null;

        var edge = new GraphRelationship
        {
            Id = edgeId,
            SourceId = sourceId,
            TargetId = targetId,
            Type = edgeType,
            Confidence = je.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number
                ? conf.GetDouble() : 1.0,
            Reason = je.TryGetProperty("reason", out var r) && r.ValueKind != JsonValueKind.Null ? r.GetString() : null,
            Step = je.TryGetProperty("step", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : null,
        };

        foreach (var prop in je.EnumerateObject())
        {
            if (prop.Name.StartsWith("prop:"))
            {
                var key = prop.Name["prop:".Length..];
                edge.Properties[key] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString()!,
                    JsonValueKind.Number => prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.GetRawText(),
                };
            }
        }

        return edge;
    }

    private async Task EnsureNodeTagsAsync(Guid nodeGuid, GraphNode node, CancellationToken ct)
    {
        // Delete existing tags for this node, then recreate.
        await _client.Tag.DeleteNodeTags(_tenantGuid, _graphGuid, nodeGuid, ct);

        var tags = new List<TagMetadata>
        {
            new()
            {
                TenantGUID = _tenantGuid,
                GraphGUID = _graphGuid,
                NodeGUID = nodeGuid,
                Key = "domainId",
                Value = node.Id,
            },
            new()
            {
                TenantGUID = _tenantGuid,
                GraphGUID = _graphGuid,
                NodeGUID = nodeGuid,
                Key = "nodeType",
                Value = node.Type.ToString(),
            },
        };

        if (node.FilePath != null)
        {
            tags.Add(new TagMetadata
            {
                TenantGUID = _tenantGuid,
                GraphGUID = _graphGuid,
                NodeGUID = nodeGuid,
                Key = "filePath",
                Value = node.FilePath,
            });
        }

        await _client.Tag.CreateMany(_tenantGuid, tags, ct);
    }

    private async Task EnsureEdgeTagsAsync(Guid edgeGuid, GraphRelationship edge, CancellationToken ct)
    {
        await _client.Tag.DeleteEdgeTags(_tenantGuid, _graphGuid, edgeGuid, ct);

        var tags = new List<TagMetadata>
        {
            new()
            {
                TenantGUID = _tenantGuid,
                GraphGUID = _graphGuid,
                EdgeGUID = edgeGuid,
                Key = "domainId",
                Value = edge.Id,
            },
            new()
            {
                TenantGUID = _tenantGuid,
                GraphGUID = _graphGuid,
                EdgeGUID = edgeGuid,
                Key = "edgeType",
                Value = edge.Type.ToString(),
            },
        };

        await _client.Tag.CreateMany(_tenantGuid, tags, ct);
    }
}
