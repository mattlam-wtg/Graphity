using System.Collections.Concurrent;

namespace Graphity.Core.Graph;

public sealed class KnowledgeGraph
{
    private readonly ConcurrentDictionary<string, GraphNode> _nodes = new();
    private readonly ConcurrentDictionary<string, GraphRelationship> _edges = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _outgoing = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _incoming = new();

    public IReadOnlyDictionary<string, GraphNode> Nodes => _nodes;
    public IReadOnlyDictionary<string, GraphRelationship> Edges => _edges;

    public string RepoPath { get; set; } = string.Empty;
    public string RepoName { get; set; } = string.Empty;
    public DateTime IndexedAt { get; set; }

    public bool AddNode(GraphNode node) => _nodes.TryAdd(node.Id, node);

    public bool AddEdge(GraphRelationship edge)
    {
        if (!_edges.TryAdd(edge.Id, edge)) return false;

        var outSet = _outgoing.GetOrAdd(edge.SourceId, _ => new HashSet<string>());
        lock (outSet) { outSet.Add(edge.Id); }

        var inSet = _incoming.GetOrAdd(edge.TargetId, _ => new HashSet<string>());
        lock (inSet) { inSet.Add(edge.Id); }

        return true;
    }

    public GraphNode? GetNode(string id) => _nodes.GetValueOrDefault(id);

    public IEnumerable<GraphRelationship> GetOutgoingEdges(string nodeId)
    {
        if (!_outgoing.TryGetValue(nodeId, out var edgeIds)) yield break;
        string[] snapshot;
        lock (edgeIds) { snapshot = edgeIds.ToArray(); }
        foreach (var eid in snapshot)
            if (_edges.TryGetValue(eid, out var edge)) yield return edge;
    }

    public IEnumerable<GraphRelationship> GetIncomingEdges(string nodeId)
    {
        if (!_incoming.TryGetValue(nodeId, out var edgeIds)) yield break;
        string[] snapshot;
        lock (edgeIds) { snapshot = edgeIds.ToArray(); }
        foreach (var eid in snapshot)
            if (_edges.TryGetValue(eid, out var edge)) yield return edge;
    }

    public IEnumerable<GraphNode> GetNodesByType(NodeType type)
        => _nodes.Values.Where(n => n.Type == type);

    public IEnumerable<GraphNode> GetNodesByFile(string filePath)
        => _nodes.Values.Where(n => n.FilePath == filePath);

    public void RemoveNodesByFile(string filePath)
    {
        var nodeIds = _nodes.Values.Where(n => n.FilePath == filePath).Select(n => n.Id).ToList();
        foreach (var nodeId in nodeIds)
            RemoveNode(nodeId);
    }

    public void RemoveNode(string nodeId)
    {
        if (!_nodes.TryRemove(nodeId, out _)) return;

        if (_outgoing.TryRemove(nodeId, out var outEdges))
        {
            string[] snapshot;
            lock (outEdges) { snapshot = outEdges.ToArray(); }
            foreach (var eid in snapshot) RemoveEdgeInternal(eid);
        }
        if (_incoming.TryRemove(nodeId, out var inEdges))
        {
            string[] snapshot;
            lock (inEdges) { snapshot = inEdges.ToArray(); }
            foreach (var eid in snapshot) RemoveEdgeInternal(eid);
        }
    }

    private void RemoveEdgeInternal(string edgeId)
    {
        if (!_edges.TryRemove(edgeId, out var edge)) return;
        if (_outgoing.TryGetValue(edge.SourceId, out var outSet))
            lock (outSet) { outSet.Remove(edgeId); }
        if (_incoming.TryGetValue(edge.TargetId, out var inSet))
            lock (inSet) { inSet.Remove(edgeId); }
    }

    public void Clear()
    {
        _nodes.Clear();
        _edges.Clear();
        _outgoing.Clear();
        _incoming.Clear();
    }
}
