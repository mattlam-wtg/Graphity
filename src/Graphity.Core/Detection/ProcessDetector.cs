using Graphity.Core.Graph;

namespace Graphity.Core.Detection;

public sealed class ProcessDetector
{
    private const int MaxDepth = 10;
    private const int MaxBranching = 4;
    private const double MinConfidence = 0.5;
    private const int MinSteps = 3;
    private const int MaxProcesses = 75;

    public void DetectProcesses(KnowledgeGraph graph, IReadOnlyList<EntryPointScorer.ScoredEntry> entryPoints)
    {
        var allTraces = new List<List<string>>();

        // 1. BFS from each entry point
        foreach (var entry in entryPoints)
        {
            var traces = TraceFromEntry(graph, entry.NodeId);
            allTraces.AddRange(traces);
        }

        // 2. Deduplication
        allTraces = RemoveSubsets(allTraces);
        allTraces = KeepLongestPerPair(allTraces);

        // 3. Limit to MaxProcesses, prioritize by length
        var topTraces = allTraces.OrderByDescending(t => t.Count).Take(MaxProcesses).ToList();

        // 4. Create Process nodes + STEP_IN_PROCESS edges
        for (int i = 0; i < topTraces.Count; i++)
        {
            var trace = topTraces[i];
            var entryNode = graph.GetNode(trace[0]);
            var terminalNode = graph.GetNode(trace[^1]);
            var label = $"{entryNode?.Name ?? "?"} → {terminalNode?.Name ?? "?"}";

            var processId = $"Process:{i}:{label}";
            var processNode = new GraphNode
            {
                Id = processId,
                Name = label,
                Type = NodeType.Process,
                Language = "computed",
            };
            processNode.Properties["stepCount"] = trace.Count;
            processNode.Properties["entryPointId"] = trace[0];
            processNode.Properties["terminalId"] = trace[^1];
            processNode.Properties["processType"] = DetermineProcessType(graph, trace);

            graph.AddNode(processNode);

            for (int step = 0; step < trace.Count; step++)
            {
                var edge = new GraphRelationship
                {
                    Id = $"StepInProcess:{processId}->{trace[step]}#{step}",
                    SourceId = trace[step],
                    TargetId = processId,
                    Type = EdgeType.StepInProcess,
                    Step = step + 1, // 1-indexed
                    Reason = "process trace",
                };
                graph.AddEdge(edge);
            }
        }
    }

    internal List<List<string>> TraceFromEntry(KnowledgeGraph graph, string entryId)
    {
        var traces = new List<List<string>>();
        var queue = new Queue<(List<string> path, HashSet<string> visited)>();
        queue.Enqueue((new List<string> { entryId }, new HashSet<string> { entryId }));

        while (queue.Count > 0)
        {
            var (path, visited) = queue.Dequeue();
            if (path.Count > MaxDepth) continue;

            var current = path[^1];
            var callees = graph.GetOutgoingEdges(current)
                .Where(e => e.Type == EdgeType.Calls && e.Confidence >= MinConfidence && !visited.Contains(e.TargetId))
                .OrderByDescending(e => e.Confidence)
                .Take(MaxBranching)
                .ToList();

            if (callees.Count == 0)
            {
                if (path.Count >= MinSteps) traces.Add(path);
                continue;
            }

            foreach (var callee in callees)
            {
                var newPath = new List<string>(path) { callee.TargetId };
                var newVisited = new HashSet<string>(visited) { callee.TargetId };
                queue.Enqueue((newPath, newVisited));
            }

            // Also save the current path if it's long enough (in case branches are dead ends)
            if (path.Count >= MinSteps) traces.Add(path);
        }

        return traces;
    }

    internal static List<List<string>> RemoveSubsets(List<List<string>> traces)
    {
        // Convert each trace to a HashSet for subset checking
        var traceSets = traces.Select(t => new HashSet<string>(t)).ToList();
        var result = new List<List<string>>();

        for (int i = 0; i < traces.Count; i++)
        {
            bool isSubset = false;
            for (int j = 0; j < traces.Count; j++)
            {
                if (i == j) continue;
                if (traces[i].Count >= traces[j].Count) continue;
                if (traceSets[i].IsSubsetOf(traceSets[j]))
                {
                    isSubset = true;
                    break;
                }
            }
            if (!isSubset) result.Add(traces[i]);
        }

        return result;
    }

    internal static List<List<string>> KeepLongestPerPair(List<List<string>> traces)
    {
        var bestPerPair = new Dictionary<(string entry, string terminal), List<string>>();

        foreach (var trace in traces)
        {
            if (trace.Count == 0) continue;
            var key = (trace[0], trace[^1]);
            if (!bestPerPair.TryGetValue(key, out var existing) || trace.Count > existing.Count)
            {
                bestPerPair[key] = trace;
            }
        }

        return bestPerPair.Values.ToList();
    }

    internal static string DetermineProcessType(KnowledgeGraph graph, List<string> trace)
    {
        // Check if trace members span multiple communities
        var communities = new HashSet<string>();
        foreach (var nodeId in trace)
        {
            var memberOfEdges = graph.GetOutgoingEdges(nodeId)
                .Where(e => e.Type == EdgeType.MemberOf);
            foreach (var edge in memberOfEdges)
            {
                communities.Add(edge.TargetId);
            }
        }

        if (communities.Count > 1) return "cross-community";
        if (communities.Count == 1) return "intra-community";
        return "unclassified";
    }
}
