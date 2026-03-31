using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Graphity.Core.Graph;
using Graphity.Core.Ingestion;

namespace Graphity.Core.Analyzers.CSharp;

public sealed class RoslynAnalyzer : ISolutionAnalyzer
{
    private static bool _msBuildRegistered;
    private static readonly object _lock = new();

    public string Language => "csharp";
    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".cs" };

    private static void EnsureMSBuildRegistered()
    {
        if (_msBuildRegistered) return;
        lock (_lock)
        {
            if (_msBuildRegistered) return;
            MSBuildLocator.RegisterDefaults();
            _msBuildRegistered = true;
        }
    }

    public async Task<AnalyzerResult> AnalyzeSolutionAsync(string solutionPath, CancellationToken ct = default)
    {
        EnsureMSBuildRegistered();
        var result = new AnalyzerResult();
        var repoRoot = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

        using var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(_ => { /* log but don't crash */ });

        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);

        foreach (var project in solution.Projects)
        {
            if (ct.IsCancellationRequested) break;

            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            foreach (var document in project.Documents)
            {
                if (ct.IsCancellationRequested) break;
                if (document.FilePath is null) continue;

                try
                {
                    var syntaxTree = await document.GetSyntaxTreeAsync(ct);
                    if (syntaxTree is null) continue;

                    var semanticModel = compilation.GetSemanticModel(syntaxTree);
                    var root = await syntaxTree.GetRootAsync(ct);
                    var relativePath = Path.GetRelativePath(repoRoot, document.FilePath).Replace('\\', '/');

                    var walker = new SymbolWalker(semanticModel, relativePath, result);
                    walker.Visit(root);
                }
                catch
                {
                    // Skip documents that fail to analyze
                }
            }
        }

        return result;
    }

    public Task<AnalyzerResult> AnalyzeAsync(string filePath, string repoRoot, CancellationToken ct = default)
    {
        // Single-file analysis is not the primary path for C# — solution analysis is preferred
        return Task.FromResult(AnalyzerResult.Empty);
    }
}

internal sealed class SymbolWalker : CSharpSyntaxWalker
{
    private readonly SemanticModel _model;
    private readonly string _filePath;
    private readonly AnalyzerResult _result;
    private readonly string _fileNodeId;
    private readonly HashSet<string> _emittedNodeIds = new();
    private readonly HashSet<string> _emittedEdgeIds = new();
    private int _callEdgeCounter;

    // Track current containing context
    private string? _currentTypeId;
    private string? _currentMethodId;

    public SymbolWalker(SemanticModel model, string filePath, AnalyzerResult result)
    {
        _model = model;
        _filePath = filePath;
        _result = result;
        _fileNodeId = $"File:{filePath}";
    }

    // --- Namespace declarations ---

    public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        VisitNamespaceCore(node, node.Name.ToString());
        base.VisitNamespaceDeclaration(node);
    }

    public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        VisitNamespaceCore(node, node.Name.ToString());
        base.VisitFileScopedNamespaceDeclaration(node);
    }

    private void VisitNamespaceCore(BaseNamespaceDeclarationSyntax node, string nsName)
    {
        var symbol = _model.GetDeclaredSymbol(node);
        var fullName = symbol?.ToDisplayString() ?? nsName;
        var nodeId = $"Namespace:{fullName}";

        AddNode(nodeId, nsName, NodeType.Namespace, fullName, node, isExported: true);

        // File CONTAINS Namespace
        AddEdge(EdgeType.Contains, _fileNodeId, nodeId, "file contains namespace");
    }

    // --- Type declarations ---

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        VisitTypeDeclarationCore(node, NodeType.Class, () => base.VisitClassDeclaration(node));
    }

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        VisitTypeDeclarationCore(node, NodeType.Interface, () => base.VisitInterfaceDeclaration(node));
    }

    public override void VisitStructDeclaration(StructDeclarationSyntax node)
    {
        VisitTypeDeclarationCore(node, NodeType.Struct, () => base.VisitStructDeclaration(node));
    }

    public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var nodeType = node.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)
            ? NodeType.Struct
            : NodeType.Record;
        VisitTypeDeclarationCore(node, nodeType, () => base.VisitRecordDeclaration(node));
    }

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        var symbol = _model.GetDeclaredSymbol(node);
        if (symbol is null) { base.VisitEnumDeclaration(node); return; }

        var fullName = symbol.ToDisplayString();
        var nodeId = $"Enum:{fullName}";
        var isExported = symbol.DeclaredAccessibility == Accessibility.Public;

        AddNode(nodeId, symbol.Name, NodeType.Enum, fullName, node, isExported);
        AddContainsEdgeForType(symbol, nodeId);

        var prevType = _currentTypeId;
        _currentTypeId = nodeId;
        base.VisitEnumDeclaration(node);
        _currentTypeId = prevType;
    }

    public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        var symbol = _model.GetDeclaredSymbol(node);
        if (symbol is null) { base.VisitDelegateDeclaration(node); return; }

        var fullName = symbol.ToDisplayString();
        var nodeId = $"Delegate:{fullName}";
        var isExported = symbol.DeclaredAccessibility == Accessibility.Public;

        AddNode(nodeId, symbol.Name, NodeType.Delegate, fullName, node, isExported);
        AddContainsEdgeForType(symbol, nodeId);

        base.VisitDelegateDeclaration(node);
    }

    private void VisitTypeDeclarationCore(TypeDeclarationSyntax node, NodeType nodeType, Action visitBase)
    {
        var symbol = _model.GetDeclaredSymbol(node);
        if (symbol is null)
        {
            visitBase();
            return;
        }

        var fullName = symbol.ToDisplayString();
        var nodeId = $"{nodeType}:{fullName}";
        var isExported = symbol.DeclaredAccessibility == Accessibility.Public;

        var graphNode = AddNode(nodeId, symbol.Name, nodeType, fullName, node, isExported);

        // Mark partial classes
        if (graphNode is not null && symbol.DeclaringSyntaxReferences.Length > 1)
        {
            graphNode.Properties["isPartial"] = true;
        }

        AddContainsEdgeForType(symbol, nodeId);

        // EXTENDS — base type (skip System.Object and System.ValueType)
        if (symbol.BaseType is { } baseType
            && baseType.SpecialType != SpecialType.System_Object
            && baseType.SpecialType != SpecialType.System_ValueType)
        {
            var baseId = $"{MapTypeKind(baseType)}:{baseType.ToDisplayString()}";
            AddEdge(EdgeType.Extends, nodeId, baseId, "inherits");
        }

        // IMPLEMENTS — direct interfaces only
        foreach (var iface in symbol.Interfaces)
        {
            var ifaceId = $"Interface:{iface.ToDisplayString()}";
            AddEdge(EdgeType.Implements, nodeId, ifaceId, "implements");
        }

        var prevType = _currentTypeId;
        _currentTypeId = nodeId;
        visitBase();
        _currentTypeId = prevType;
    }

    // --- Member declarations ---

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _model.GetDeclaredSymbol(node);
        if (symbol is null) { base.VisitMethodDeclaration(node); return; }

        var fullName = symbol.ToDisplayString();
        var nodeId = $"Method:{fullName}";
        var isExported = symbol.DeclaredAccessibility == Accessibility.Public;

        var methodNode = AddNode(nodeId, symbol.Name, NodeType.Method, fullName, node, isExported);

        if (methodNode is not null && symbol.IsExtensionMethod)
        {
            methodNode.Properties["isExtension"] = true;
        }

        if (_currentTypeId is not null)
        {
            AddEdge(EdgeType.Defines, _currentTypeId, nodeId, "defines method");
            AddEdge(EdgeType.HasMethod, _currentTypeId, nodeId, "has method");
        }

        // OVERRIDES
        if (symbol.IsOverride && symbol.OverriddenMethod is { } overridden)
        {
            var overriddenId = $"Method:{overridden.ToDisplayString()}";
            AddEdge(EdgeType.Overrides, nodeId, overriddenId, "overrides");
        }

        var prevMethod = _currentMethodId;
        _currentMethodId = nodeId;
        base.VisitMethodDeclaration(node);
        _currentMethodId = prevMethod;
    }

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var symbol = _model.GetDeclaredSymbol(node);
        if (symbol is null) { base.VisitConstructorDeclaration(node); return; }

        var fullName = symbol.ToDisplayString();
        var nodeId = $"Constructor:{fullName}";
        var isExported = symbol.DeclaredAccessibility == Accessibility.Public;

        AddNode(nodeId, symbol.Name, NodeType.Constructor, fullName, node, isExported);

        if (_currentTypeId is not null)
        {
            AddEdge(EdgeType.Defines, _currentTypeId, nodeId, "defines constructor");
        }

        var prevMethod = _currentMethodId;
        _currentMethodId = nodeId;
        base.VisitConstructorDeclaration(node);
        _currentMethodId = prevMethod;
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var symbol = _model.GetDeclaredSymbol(node);
        if (symbol is null) { base.VisitPropertyDeclaration(node); return; }

        var fullName = symbol.ToDisplayString();
        var nodeId = $"Property:{fullName}";
        var isExported = symbol.DeclaredAccessibility == Accessibility.Public;

        AddNode(nodeId, symbol.Name, NodeType.Property, fullName, node, isExported);

        if (_currentTypeId is not null)
        {
            AddEdge(EdgeType.Defines, _currentTypeId, nodeId, "defines property");
            AddEdge(EdgeType.HasProperty, _currentTypeId, nodeId, "has property");
        }

        base.VisitPropertyDeclaration(node);
    }

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        foreach (var variable in node.Declaration.Variables)
        {
            var symbol = _model.GetDeclaredSymbol(variable);
            if (symbol is not IFieldSymbol fieldSymbol) continue;

            var fullName = fieldSymbol.ToDisplayString();
            var nodeId = $"Field:{fullName}";
            var isExported = fieldSymbol.DeclaredAccessibility == Accessibility.Public;

            AddNode(nodeId, fieldSymbol.Name, NodeType.Field, fullName, variable, isExported);

            if (_currentTypeId is not null)
            {
                AddEdge(EdgeType.Defines, _currentTypeId, nodeId, "defines field");
                AddEdge(EdgeType.HasField, _currentTypeId, nodeId, "has field");
            }
        }

        base.VisitFieldDeclaration(node);
    }

    public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
    {
        foreach (var variable in node.Declaration.Variables)
        {
            var symbol = _model.GetDeclaredSymbol(variable);
            if (symbol is not IEventSymbol eventSymbol) continue;

            var fullName = eventSymbol.ToDisplayString();
            var nodeId = $"Field:{fullName}";
            var isExported = eventSymbol.DeclaredAccessibility == Accessibility.Public;

            AddNode(nodeId, eventSymbol.Name, NodeType.Field, fullName, variable, isExported);

            if (_currentTypeId is not null)
            {
                AddEdge(EdgeType.Defines, _currentTypeId, nodeId, "defines event");
                AddEdge(EdgeType.HasField, _currentTypeId, nodeId, "has event field");
            }
        }

        base.VisitEventFieldDeclaration(node);
    }

    // --- Local functions ---

    public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var symbol = _model.GetDeclaredSymbol(node);
        if (symbol is not null)
        {
            var fullName = symbol.ToDisplayString();
            var nodeId = $"Method:{fullName}";
            var isExported = false; // local functions are never exported

            AddNode(nodeId, symbol.Name, NodeType.Method, fullName, node, isExported);

            if (_currentMethodId is not null)
            {
                AddEdge(EdgeType.Defines, _currentMethodId, nodeId, "defines local function");
            }
        }

        base.VisitLocalFunctionStatement(node);
    }

    // --- Invocation expressions (CALLS edges) ---

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (_currentMethodId is not null)
        {
            try
            {
                var symbolInfo = _model.GetSymbolInfo(node);
                if (symbolInfo.Symbol is IMethodSymbol targetMethod)
                {
                    var targetId = $"Method:{targetMethod.ToDisplayString()}";
                    var edgeId = $"Calls:{_currentMethodId}->{targetId}#{Interlocked.Increment(ref _callEdgeCounter)}";
                    AddEdgeWithId(edgeId, EdgeType.Calls, _currentMethodId, targetId, "method invocation", 0.9);
                }
                else if (symbolInfo.CandidateSymbols.Length > 0)
                {
                    // Unresolved but has candidates — use first candidate with lower confidence
                    var candidate = symbolInfo.CandidateSymbols[0];
                    if (candidate is IMethodSymbol candidateMethod)
                    {
                        var targetId = $"Method:{candidateMethod.ToDisplayString()}";
                        var edgeId = $"Calls:{_currentMethodId}->{targetId}#{Interlocked.Increment(ref _callEdgeCounter)}";
                        AddEdgeWithId(edgeId, EdgeType.Calls, _currentMethodId, targetId, "candidate invocation", 0.5);
                    }
                }
            }
            catch
            {
                // Skip unresolvable invocations
            }
        }

        base.VisitInvocationExpression(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        if (_currentMethodId is not null)
        {
            try
            {
                var symbolInfo = _model.GetSymbolInfo(node);
                if (symbolInfo.Symbol is IMethodSymbol ctor)
                {
                    var targetId = $"Constructor:{ctor.ToDisplayString()}";
                    var edgeId = $"Calls:{_currentMethodId}->{targetId}#{Interlocked.Increment(ref _callEdgeCounter)}";
                    AddEdgeWithId(edgeId, EdgeType.Calls, _currentMethodId, targetId, "object creation", 0.9);
                }
                else if (symbolInfo.CandidateSymbols.Length > 0 && symbolInfo.CandidateSymbols[0] is IMethodSymbol candidateCtor)
                {
                    var targetId = $"Constructor:{candidateCtor.ToDisplayString()}";
                    var edgeId = $"Calls:{_currentMethodId}->{targetId}#{Interlocked.Increment(ref _callEdgeCounter)}";
                    AddEdgeWithId(edgeId, EdgeType.Calls, _currentMethodId, targetId, "candidate object creation", 0.5);
                }
            }
            catch
            {
                // Skip unresolvable creations
            }
        }

        base.VisitObjectCreationExpression(node);
    }

    // --- Using directives (IMPORTS edges) ---

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        if (node.Name is not null)
        {
            var nsName = node.Name.ToString();
            var targetId = $"Namespace:{nsName}";
            AddEdge(EdgeType.Imports, _fileNodeId, targetId, "using directive");
        }

        base.VisitUsingDirective(node);
    }

    // --- Helpers ---

    private GraphNode? AddNode(string id, string name, NodeType type, string fullName, SyntaxNode node, bool isExported)
    {
        if (!_emittedNodeIds.Add(id)) return null;

        var lineSpan = node.GetLocation().GetLineSpan();

        var graphNode = new GraphNode
        {
            Id = id,
            Name = name,
            Type = type,
            FullName = fullName,
            FilePath = _filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            IsExported = isExported,
            Language = "csharp",
        };
        _result.Nodes.Add(graphNode);
        return graphNode;
    }

    private void AddEdge(EdgeType type, string sourceId, string targetId, string reason)
    {
        var edgeId = $"{type}:{sourceId}->{targetId}";
        if (!_emittedEdgeIds.Add(edgeId)) return;

        _result.Edges.Add(new GraphRelationship
        {
            Id = edgeId,
            SourceId = sourceId,
            TargetId = targetId,
            Type = type,
            Reason = reason,
        });
    }

    private void AddEdgeWithId(string edgeId, EdgeType type, string sourceId, string targetId, string reason, double confidence)
    {
        if (!_emittedEdgeIds.Add(edgeId)) return;

        _result.Edges.Add(new GraphRelationship
        {
            Id = edgeId,
            SourceId = sourceId,
            TargetId = targetId,
            Type = type,
            Confidence = confidence,
            Reason = reason,
        });
    }

    private void AddContainsEdgeForType(INamedTypeSymbol symbol, string nodeId)
    {
        // If nested type, CONTAINS from parent type
        if (symbol.ContainingType is { } containingType)
        {
            var parentId = $"{MapTypeKind(containingType)}:{containingType.ToDisplayString()}";
            AddEdge(EdgeType.Contains, parentId, nodeId, "nested type");
        }
        // Otherwise CONTAINS from namespace
        else if (symbol.ContainingNamespace is { IsGlobalNamespace: false } ns)
        {
            EnsureNamespaceNode(ns);
            var nsId = $"Namespace:{ns.ToDisplayString()}";
            AddEdge(EdgeType.Contains, nsId, nodeId, "namespace contains type");
        }
        // Also add File CONTAINS type
        else
        {
            AddEdge(EdgeType.Contains, _fileNodeId, nodeId, "file contains type");
        }
    }

    private void EnsureNamespaceNode(INamespaceSymbol ns)
    {
        var fullName = ns.ToDisplayString();
        var nodeId = $"Namespace:{fullName}";
        if (_emittedNodeIds.Add(nodeId))
        {
            _result.Nodes.Add(new GraphNode
            {
                Id = nodeId,
                Name = ns.Name,
                Type = NodeType.Namespace,
                FullName = fullName,
                FilePath = _filePath,
                IsExported = true,
                Language = "csharp",
            });
        }
    }

    private static NodeType MapTypeKind(INamedTypeSymbol symbol) => symbol.TypeKind switch
    {
        TypeKind.Interface => NodeType.Interface,
        TypeKind.Struct => NodeType.Struct,
        TypeKind.Enum => NodeType.Enum,
        TypeKind.Delegate => NodeType.Delegate,
        _ => NodeType.Class,
    };
}
