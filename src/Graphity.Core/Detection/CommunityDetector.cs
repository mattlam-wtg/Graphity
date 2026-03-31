using System.Diagnostics;
using Graphity.Core.Graph;

namespace Graphity.Core.Detection;

public sealed class CommunityDetector
{
    private const int MaxPasses = 10;
    private const double MinConfidence = 0.5;
    private const int MinCommunitySize = 3;
    private const int LargeGraphThreshold = 10_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static readonly HashSet<NodeType> EligibleTypes = new()
    {
        NodeType.Method, NodeType.Function, NodeType.Class,
        NodeType.Interface, NodeType.Struct, NodeType.Record,
    };

    private static readonly HashSet<EdgeType> ConnectingEdgeTypes = new()
    {
        EdgeType.Calls, EdgeType.Extends, EdgeType.Implements,
    };

    public void DetectCommunities(KnowledgeGraph graph)
    {
        var sw = Stopwatch.StartNew();

        // 1. Build undirected adjacency from CALLS + EXTENDS + IMPLEMENTS edges (confidence >= 0.5)
        var adjacency = new Dictionary<string, HashSet<string>>();
        var degree = new Dictionary<string, int>();

        foreach (var edge in graph.Edges.Values)
        {
            if (!ConnectingEdgeTypes.Contains(edge.Type)) continue;
            if (edge.Confidence < MinConfidence) continue;

            var src = edge.SourceId;
            var tgt = edge.TargetId;

            // Only include edges between eligible nodes
            var srcNode = graph.GetNode(src);
            var tgtNode = graph.GetNode(tgt);
            if (srcNode == null || tgtNode == null) continue;
            if (!EligibleTypes.Contains(srcNode.Type) || !EligibleTypes.Contains(tgtNode.Type)) continue;

            if (!adjacency.ContainsKey(src)) adjacency[src] = new HashSet<string>();
            if (!adjacency.ContainsKey(tgt)) adjacency[tgt] = new HashSet<string>();

            adjacency[src].Add(tgt);
            adjacency[tgt].Add(src);
        }

        // 2. Compute degrees
        foreach (var (nodeId, neighbors) in adjacency)
        {
            degree[nodeId] = neighbors.Count;
        }

        // 3. Large-graph optimization: filter out degree-1 nodes
        if (adjacency.Count > LargeGraphThreshold)
        {
            var lowDegreeNodes = degree.Where(kv => kv.Value <= 1).Select(kv => kv.Key).ToList();
            foreach (var nodeId in lowDegreeNodes)
            {
                if (adjacency.TryGetValue(nodeId, out var neighbors))
                {
                    foreach (var neighbor in neighbors)
                    {
                        if (adjacency.TryGetValue(neighbor, out var neighborSet))
                        {
                            neighborSet.Remove(nodeId);
                            degree[neighbor] = neighborSet.Count;
                        }
                    }
                }
                adjacency.Remove(nodeId);
                degree.Remove(nodeId);
            }
        }

        if (adjacency.Count == 0) return;

        // 4. Run Louvain
        var nodeIds = adjacency.Keys.ToList();
        var nodeToCommunity = new Dictionary<string, int>();
        var communityMembers = new Dictionary<int, HashSet<string>>();
        int nextCommunity = 0;

        // a. Initialize: each node in its own community
        foreach (var nodeId in nodeIds)
        {
            var c = nextCommunity++;
            nodeToCommunity[nodeId] = c;
            communityMembers[c] = new HashSet<string> { nodeId };
        }

        int totalEdges = adjacency.Values.Sum(n => n.Count) / 2;
        if (totalEdges == 0) return;
        double m = totalEdges;

        // Precompute community total degrees
        var communityTotalDegree = new Dictionary<int, int>();
        foreach (var (comm, members) in communityMembers)
        {
            communityTotalDegree[comm] = members.Sum(n => degree.GetValueOrDefault(n));
        }

        // b-d. Iterate
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            if (sw.Elapsed > Timeout) break;

            bool anyMoved = false;

            foreach (var nodeId in nodeIds)
            {
                if (sw.Elapsed > Timeout) break;

                int currentComm = nodeToCommunity[nodeId];
                int ki = degree.GetValueOrDefault(nodeId);
                var neighbors = adjacency[nodeId];

                // Compute ki_in for current community
                int kiInCurrent = neighbors.Count(n => nodeToCommunity[n] == currentComm);

                // Remove node from its community for computation
                int sigmaTotWithout = communityTotalDegree.GetValueOrDefault(currentComm) - ki;

                // Compute gain of removing from current community
                double removeLoss = kiInCurrent / m - (double)sigmaTotWithout * ki / (2.0 * m * m);

                // Try each neighbor's community
                int bestComm = currentComm;
                double bestGain = 0;

                var neighborComms = new HashSet<int>();
                foreach (var neighbor in neighbors)
                {
                    neighborComms.Add(nodeToCommunity[neighbor]);
                }

                foreach (var targetComm in neighborComms)
                {
                    if (targetComm == currentComm) continue;

                    int kiInTarget = neighbors.Count(n => nodeToCommunity[n] == targetComm);
                    int sigmaTotTarget = communityTotalDegree.GetValueOrDefault(targetComm);

                    double moveGain = kiInTarget / m - (double)sigmaTotTarget * ki / (2.0 * m * m);
                    double deltaQ = moveGain - removeLoss;

                    if (deltaQ > bestGain)
                    {
                        bestGain = deltaQ;
                        bestComm = targetComm;
                    }
                }

                if (bestComm != currentComm)
                {
                    // Move node
                    communityMembers[currentComm].Remove(nodeId);
                    communityTotalDegree[currentComm] -= ki;

                    if (communityMembers[currentComm].Count == 0)
                    {
                        communityMembers.Remove(currentComm);
                        communityTotalDegree.Remove(currentComm);
                    }

                    nodeToCommunity[nodeId] = bestComm;
                    communityMembers[bestComm].Add(nodeId);
                    communityTotalDegree[bestComm] += ki;

                    anyMoved = true;
                }
            }

            if (!anyMoved) break;
        }

        // 5. Skip singleton communities and communities with < 3 members
        var validCommunities = communityMembers
            .Where(kv => kv.Value.Count >= MinCommunitySize)
            .ToList();

        // 6. Create Community nodes
        int communityIndex = 0;
        foreach (var (_, members) in validCommunities)
        {
            var label = ComputeCommunityLabel(graph, members, communityIndex);
            var processId = $"Community:{communityIndex}";

            // Compute cohesion: internal edges / possible edges
            int internalEdges = 0;
            foreach (var member in members)
            {
                if (adjacency.TryGetValue(member, out var neighbors))
                {
                    internalEdges += neighbors.Count(n => members.Contains(n));
                }
            }
            internalEdges /= 2; // undirected
            int possibleEdges = members.Count * (members.Count - 1) / 2;
            double cohesion = possibleEdges > 0 ? (double)internalEdges / possibleEdges : 0;

            var communityNode = new GraphNode
            {
                Id = processId,
                Name = label,
                Type = NodeType.Community,
                Language = "computed",
            };
            communityNode.Properties["cohesion"] = Math.Round(cohesion, 4);
            communityNode.Properties["symbolCount"] = members.Count;
            communityNode.Properties["keywords"] = ExtractKeywords(graph, members);

            graph.AddNode(communityNode);

            // 7. Create MEMBER_OF edges
            foreach (var memberId in members)
            {
                var edge = new GraphRelationship
                {
                    Id = $"MemberOf:{memberId}->{processId}",
                    SourceId = memberId,
                    TargetId = processId,
                    Type = EdgeType.MemberOf,
                    Reason = "community detection",
                };
                graph.AddEdge(edge);
            }

            communityIndex++;
        }
    }

    private static string ComputeCommunityLabel(KnowledgeGraph graph, HashSet<string> members, int index)
    {
        // Try most common folder
        var folders = new Dictionary<string, int>();
        foreach (var memberId in members)
        {
            var node = graph.GetNode(memberId);
            if (node?.FilePath == null) continue;
            var folder = Path.GetDirectoryName(node.FilePath)?.Replace('\\', '/');
            if (folder == null) continue;
            // Use the last segment of the folder path
            var parts = folder.Split('/');
            var segment = parts.Length > 0 ? parts[^1] : folder;
            if (!string.IsNullOrEmpty(segment))
            {
                folders.TryGetValue(segment, out var count);
                folders[segment] = count + 1;
            }
        }

        if (folders.Count > 0)
        {
            var bestFolder = folders.OrderByDescending(kv => kv.Value).First();
            if (bestFolder.Value >= members.Count / 2)
                return bestFolder.Key;
        }

        // Try common name prefix
        var names = members
            .Select(id => graph.GetNode(id)?.Name)
            .Where(n => n != null)
            .ToList();

        if (names.Count >= 2)
        {
            var prefix = CommonPrefix(names!);
            if (prefix.Length >= 3)
                return prefix;
        }

        return $"Cluster_{index}";
    }

    private static string CommonPrefix(List<string> names)
    {
        if (names.Count == 0) return string.Empty;
        var first = names[0];
        int len = first.Length;
        for (int i = 1; i < names.Count; i++)
        {
            len = Math.Min(len, names[i].Length);
            for (int j = 0; j < len; j++)
            {
                if (first[j] != names[i][j])
                {
                    len = j;
                    break;
                }
            }
        }
        return first[..len];
    }

    private static string ExtractKeywords(KnowledgeGraph graph, HashSet<string> members)
    {
        var words = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var memberId in members)
        {
            var node = graph.GetNode(memberId);
            if (node?.Name == null) continue;
            // Split PascalCase
            foreach (var word in SplitPascalCase(node.Name))
            {
                if (word.Length < 3) continue;
                words.TryGetValue(word, out var count);
                words[word] = count + 1;
            }
        }
        var topWords = words.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key);
        return string.Join(", ", topWords);
    }

    private static IEnumerable<string> SplitPascalCase(string name)
    {
        int start = 0;
        for (int i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                yield return name[start..i];
                start = i;
            }
        }
        yield return name[start..];
    }
}
