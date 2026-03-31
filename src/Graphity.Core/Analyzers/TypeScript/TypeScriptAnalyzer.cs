using System.Text.RegularExpressions;
using Graphity.Core.Graph;
using Graphity.Core.Ingestion;

namespace Graphity.Core.Analyzers.TypeScript;

public sealed partial class TypeScriptAnalyzer : ILanguageAnalyzer
{
    public string Language => "typescript";
    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".ts", ".tsx", ".js", ".jsx" };

    public async Task<AnalyzerResult> AnalyzeAsync(string filePath, string repoRoot, CancellationToken ct = default)
    {
        var result = new AnalyzerResult();
        var content = await File.ReadAllTextAsync(filePath, ct);
        var relativePath = Path.GetRelativePath(repoRoot, filePath).Replace('\\', '/');
        var fileNodeId = $"File:{relativePath}";

        ExtractImports(content, relativePath, fileNodeId, result);
        ExtractClasses(content, relativePath, fileNodeId, result);
        ExtractInterfaces(content, relativePath, fileNodeId, result);
        ExtractFunctions(content, relativePath, fileNodeId, result);
        ExtractEnums(content, relativePath, fileNodeId, result);
        ExtractCalls(content, relativePath, result);

        return result;
    }

    private static int GetLineNumber(string content, int position)
    {
        var line = 1;
        for (var i = 0; i < position && i < content.Length; i++)
        {
            if (content[i] == '\n') line++;
        }
        return line;
    }

    private static bool IsExported(string content, int matchIndex)
    {
        // Look backwards from the match to see if it's preceded by 'export'
        var preceding = content[..matchIndex].TrimEnd();
        return preceding.EndsWith("export") || preceding.EndsWith("export default");
    }

    // --- Imports ---

    [GeneratedRegex(@"import\s+(?:\{([^}]+)\}|(\w+))\s+from\s+['""]([^'""]+)['""]", RegexOptions.Multiline)]
    private static partial Regex ImportEs6Regex();

    [GeneratedRegex(@"import\s+\*\s+as\s+(\w+)\s+from\s+['""]([^'""]+)['""]", RegexOptions.Multiline)]
    private static partial Regex ImportNamespaceRegex();

    [GeneratedRegex(@"require\(['""]([^'""]+)['""]\)", RegexOptions.Multiline)]
    private static partial Regex RequireRegex();

    private static void ExtractImports(string content, string relativePath, string fileNodeId, AnalyzerResult result)
    {
        foreach (Match match in ImportEs6Regex().Matches(content))
        {
            var modulePath = match.Groups[3].Value;
            var targetId = $"Module:{modulePath}";
            var line = GetLineNumber(content, match.Index);

            if (match.Groups[1].Success)
            {
                // Named imports: import { A, B } from 'module'
                var names = match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                foreach (var name in names)
                {
                    var cleanName = name.Contains(" as ") ? name.Split(" as ")[0].Trim() : name.Trim();
                    result.Edges.Add(new GraphRelationship
                    {
                        Id = $"Imports:{fileNodeId}->{targetId}:{cleanName}",
                        SourceId = fileNodeId,
                        TargetId = targetId,
                        Type = EdgeType.Imports,
                        Reason = $"imports {cleanName} from {modulePath}",
                    });
                }
            }
            else if (match.Groups[2].Success)
            {
                // Default import: import Foo from 'module'
                result.Edges.Add(new GraphRelationship
                {
                    Id = $"Imports:{fileNodeId}->{targetId}:default",
                    SourceId = fileNodeId,
                    TargetId = targetId,
                    Type = EdgeType.Imports,
                    Reason = $"imports default from {modulePath}",
                });
            }
        }

        foreach (Match match in ImportNamespaceRegex().Matches(content))
        {
            var alias = match.Groups[1].Value;
            var modulePath = match.Groups[2].Value;
            var targetId = $"Module:{modulePath}";

            result.Edges.Add(new GraphRelationship
            {
                Id = $"Imports:{fileNodeId}->{targetId}:*",
                SourceId = fileNodeId,
                TargetId = targetId,
                Type = EdgeType.Imports,
                Reason = $"imports * as {alias} from {modulePath}",
            });
        }

        foreach (Match match in RequireRegex().Matches(content))
        {
            var modulePath = match.Groups[1].Value;
            var targetId = $"Module:{modulePath}";

            result.Edges.Add(new GraphRelationship
            {
                Id = $"Imports:{fileNodeId}->{targetId}:require",
                SourceId = fileNodeId,
                TargetId = targetId,
                Type = EdgeType.Imports,
                Reason = $"requires {modulePath}",
            });
        }
    }

    // --- Classes ---

    [GeneratedRegex(@"(?:export\s+)?(?:default\s+)?(?:abstract\s+)?class\s+(\w+)(?:\s+extends\s+(\w+))?(?:\s+implements\s+([\w,\s]+))?", RegexOptions.Multiline)]
    private static partial Regex ClassRegex();

    private static void ExtractClasses(string content, string relativePath, string fileNodeId, AnalyzerResult result)
    {
        foreach (Match match in ClassRegex().Matches(content))
        {
            var name = match.Groups[1].Value;
            var nodeId = $"Class:{relativePath}:{name}";
            var line = GetLineNumber(content, match.Index);
            var exported = IsExported(content, match.Index) ||
                           match.Value.TrimStart().StartsWith("export");

            result.Nodes.Add(new GraphNode
            {
                Id = nodeId,
                Name = name,
                Type = NodeType.Class,
                FullName = $"{relativePath}:{name}",
                FilePath = relativePath,
                StartLine = line,
                IsExported = exported,
                Language = "typescript",
            });

            result.Edges.Add(new GraphRelationship
            {
                Id = $"Defines:{fileNodeId}->{nodeId}",
                SourceId = fileNodeId,
                TargetId = nodeId,
                Type = EdgeType.Defines,
                Reason = "file defines class",
            });

            // Extends
            if (match.Groups[2].Success)
            {
                var baseName = match.Groups[2].Value;
                result.Edges.Add(new GraphRelationship
                {
                    Id = $"Extends:{nodeId}->Class:{baseName}",
                    SourceId = nodeId,
                    TargetId = $"Class:{baseName}",
                    Type = EdgeType.Extends,
                    Reason = $"extends {baseName}",
                    Confidence = 0.8,
                });
            }

            // Implements
            if (match.Groups[3].Success)
            {
                var ifaces = match.Groups[3].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                foreach (var iface in ifaces)
                {
                    result.Edges.Add(new GraphRelationship
                    {
                        Id = $"Implements:{nodeId}->Interface:{iface}",
                        SourceId = nodeId,
                        TargetId = $"Interface:{iface}",
                        Type = EdgeType.Implements,
                        Reason = $"implements {iface}",
                        Confidence = 0.8,
                    });
                }
            }
        }
    }

    // --- Interfaces ---

    [GeneratedRegex(@"(?:export\s+)?interface\s+(\w+)(?:\s+extends\s+([\w,\s]+))?", RegexOptions.Multiline)]
    private static partial Regex InterfaceRegex();

    private static void ExtractInterfaces(string content, string relativePath, string fileNodeId, AnalyzerResult result)
    {
        foreach (Match match in InterfaceRegex().Matches(content))
        {
            var name = match.Groups[1].Value;
            var nodeId = $"Interface:{relativePath}:{name}";
            var line = GetLineNumber(content, match.Index);
            var exported = IsExported(content, match.Index) ||
                           match.Value.TrimStart().StartsWith("export");

            result.Nodes.Add(new GraphNode
            {
                Id = nodeId,
                Name = name,
                Type = NodeType.Interface,
                FullName = $"{relativePath}:{name}",
                FilePath = relativePath,
                StartLine = line,
                IsExported = exported,
                Language = "typescript",
            });

            result.Edges.Add(new GraphRelationship
            {
                Id = $"Defines:{fileNodeId}->{nodeId}",
                SourceId = fileNodeId,
                TargetId = nodeId,
                Type = EdgeType.Defines,
                Reason = "file defines interface",
            });

            // Extends
            if (match.Groups[2].Success)
            {
                var bases = match.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                foreach (var baseName in bases)
                {
                    result.Edges.Add(new GraphRelationship
                    {
                        Id = $"Extends:{nodeId}->Interface:{baseName}",
                        SourceId = nodeId,
                        TargetId = $"Interface:{baseName}",
                        Type = EdgeType.Extends,
                        Reason = $"extends {baseName}",
                        Confidence = 0.8,
                    });
                }
            }
        }
    }

    // --- Functions ---

    [GeneratedRegex(@"(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex FunctionDeclRegex();

    [GeneratedRegex(@"(?:export\s+)?(?:const|let|var)\s+(\w+)\s*=\s*(?:async\s+)?(?:\(.*?\)|[a-zA-Z_]\w*)\s*=>", RegexOptions.Singleline)]
    private static partial Regex ArrowFunctionRegex();

    private static void ExtractFunctions(string content, string relativePath, string fileNodeId, AnalyzerResult result)
    {
        var emittedIds = new HashSet<string>();

        foreach (Match match in FunctionDeclRegex().Matches(content))
        {
            var name = match.Groups[1].Value;
            var nodeId = $"Function:{relativePath}:{name}";
            if (!emittedIds.Add(nodeId)) continue;

            var line = GetLineNumber(content, match.Index);
            var exported = IsExported(content, match.Index) ||
                           match.Value.TrimStart().StartsWith("export");

            result.Nodes.Add(new GraphNode
            {
                Id = nodeId,
                Name = name,
                Type = NodeType.Function,
                FullName = $"{relativePath}:{name}",
                FilePath = relativePath,
                StartLine = line,
                IsExported = exported,
                Language = "typescript",
            });

            result.Edges.Add(new GraphRelationship
            {
                Id = $"Defines:{fileNodeId}->{nodeId}",
                SourceId = fileNodeId,
                TargetId = nodeId,
                Type = EdgeType.Defines,
                Reason = "file defines function",
            });
        }

        foreach (Match match in ArrowFunctionRegex().Matches(content))
        {
            var name = match.Groups[1].Value;
            var nodeId = $"Function:{relativePath}:{name}";
            if (!emittedIds.Add(nodeId)) continue;

            var line = GetLineNumber(content, match.Index);
            var exported = IsExported(content, match.Index) ||
                           match.Value.TrimStart().StartsWith("export");

            result.Nodes.Add(new GraphNode
            {
                Id = nodeId,
                Name = name,
                Type = NodeType.Function,
                FullName = $"{relativePath}:{name}",
                FilePath = relativePath,
                StartLine = line,
                IsExported = exported,
                Language = "typescript",
            });

            result.Edges.Add(new GraphRelationship
            {
                Id = $"Defines:{fileNodeId}->{nodeId}",
                SourceId = fileNodeId,
                TargetId = nodeId,
                Type = EdgeType.Defines,
                Reason = "file defines arrow function",
            });
        }
    }

    // --- Enums ---

    [GeneratedRegex(@"(?:export\s+)?(?:const\s+)?enum\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex EnumRegex();

    private static void ExtractEnums(string content, string relativePath, string fileNodeId, AnalyzerResult result)
    {
        foreach (Match match in EnumRegex().Matches(content))
        {
            var name = match.Groups[1].Value;
            var nodeId = $"Enum:{relativePath}:{name}";
            var line = GetLineNumber(content, match.Index);
            var exported = IsExported(content, match.Index) ||
                           match.Value.TrimStart().StartsWith("export");

            result.Nodes.Add(new GraphNode
            {
                Id = nodeId,
                Name = name,
                Type = NodeType.Enum,
                FullName = $"{relativePath}:{name}",
                FilePath = relativePath,
                StartLine = line,
                IsExported = exported,
                Language = "typescript",
            });

            result.Edges.Add(new GraphRelationship
            {
                Id = $"Defines:{fileNodeId}->{nodeId}",
                SourceId = fileNodeId,
                TargetId = nodeId,
                Type = EdgeType.Defines,
                Reason = "file defines enum",
            });
        }
    }

    // --- Calls ---

    [GeneratedRegex(@"new\s+(\w+)\s*\(", RegexOptions.Multiline)]
    private static partial Regex NewCallRegex();

    [GeneratedRegex(@"(\w+)\.(\w+)\s*\(", RegexOptions.Multiline)]
    private static partial Regex MethodCallRegex();

    [GeneratedRegex(@"(?<!\w)(\w+)\s*\(", RegexOptions.Multiline)]
    private static partial Regex FunctionCallRegex();

    private static readonly HashSet<string> BuiltInKeywords = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "return", "throw", "typeof", "instanceof",
        "new", "delete", "void", "import", "export", "from", "require", "function", "class",
        "interface", "enum", "const", "let", "var", "async", "await", "yield", "super", "this",
        "else", "do", "try", "finally", "case", "break", "continue", "with", "debugger",
    };

    private static void ExtractCalls(string content, string relativePath, AnalyzerResult result)
    {
        var callCounter = 0;

        // Collect all function/class node IDs that were defined in this file
        var definedFunctions = result.Nodes
            .Where(n => n.Type == NodeType.Function && n.FilePath == relativePath)
            .Select(n => n.Id)
            .ToHashSet();

        foreach (Match match in NewCallRegex().Matches(content))
        {
            var className = match.Groups[1].Value;
            if (BuiltInKeywords.Contains(className)) continue;

            var sourceId = FindContainingFunction(result, relativePath, GetLineNumber(content, match.Index));
            if (sourceId is null) continue;

            result.Edges.Add(new GraphRelationship
            {
                Id = $"Calls:{sourceId}->Class:{className}#{callCounter++}",
                SourceId = sourceId,
                TargetId = $"Class:{className}",
                Type = EdgeType.Calls,
                Reason = $"new {className}()",
                Confidence = 0.7,
            });
        }

        // Collect method call names to avoid duplicating them as plain function calls
        var methodCallNames = new HashSet<(int position, string name)>();
        foreach (Match match in MethodCallRegex().Matches(content))
        {
            methodCallNames.Add((match.Index + match.Groups[1].Length + 1, match.Groups[2].Value));
        }

        foreach (Match match in FunctionCallRegex().Matches(content))
        {
            var funcName = match.Groups[1].Value;
            if (BuiltInKeywords.Contains(funcName)) continue;

            // Skip if this match is actually the method part of a method call (obj.method())
            if (methodCallNames.Contains((match.Index, funcName))) continue;

            var sourceId = FindContainingFunction(result, relativePath, GetLineNumber(content, match.Index));
            if (sourceId is null) continue;

            // Don't emit self-references for the function definition line itself
            if (sourceId == $"Function:{relativePath}:{funcName}") continue;

            result.Edges.Add(new GraphRelationship
            {
                Id = $"Calls:{sourceId}->Function:{funcName}#{callCounter++}",
                SourceId = sourceId,
                TargetId = $"Function:{funcName}",
                Type = EdgeType.Calls,
                Reason = $"calls {funcName}()",
                Confidence = 0.6,
            });
        }
    }

    private static string? FindContainingFunction(AnalyzerResult result, string relativePath, int line)
    {
        // Find the function that contains this line (closest preceding function start)
        GraphNode? best = null;
        foreach (var node in result.Nodes)
        {
            if (node.FilePath != relativePath) continue;
            if (node.Type is not (NodeType.Function or NodeType.Method)) continue;
            if (node.StartLine is null || node.StartLine > line) continue;
            if (best is null || node.StartLine > best.StartLine)
                best = node;
        }
        return best?.Id;
    }
}
