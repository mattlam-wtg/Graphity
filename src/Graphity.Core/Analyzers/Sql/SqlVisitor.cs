using Microsoft.SqlServer.TransactSql.ScriptDom;
using Graphity.Core.Graph;
using Graphity.Core.Ingestion;

namespace Graphity.Core.Analyzers.Sql;

internal sealed class SqlVisitor : TSqlFragmentVisitor
{
    private readonly string _relativePath;
    private readonly string _fileNodeId;
    private readonly AnalyzerResult _result;
    private readonly HashSet<string> _emittedNodeIds = new();
    private readonly HashSet<string> _emittedEdgeIds = new();

    public SqlVisitor(string relativePath, string fileNodeId, AnalyzerResult result)
    {
        _relativePath = relativePath;
        _fileNodeId = fileNodeId;
        _result = result;
    }

    // --- CREATE TABLE ---

    public override void Visit(CreateTableStatement node)
    {
        var tableName = GetObjectName(node.SchemaObjectName);
        if (tableName is null) return;

        var tableNodeId = $"Table:{tableName}";
        AddNode(tableNodeId, tableName, NodeType.Table, node);
        AddEdge(EdgeType.Defines, _fileNodeId, tableNodeId, "file defines table");

        // Extract columns
        if (node.Definition?.ColumnDefinitions is not null)
        {
            foreach (var col in node.Definition.ColumnDefinitions)
            {
                var colName = col.ColumnIdentifier?.Value;
                if (colName is null) continue;

                var colNodeId = $"Column:{tableName}.{colName}";
                var colNode = AddNode(colNodeId, colName, NodeType.Column, col);
                if (colNode is not null)
                {
                    if (col.DataType is not null)
                    {
                        colNode.Properties["dataType"] = FragmentToString(col.DataType);
                    }
                    colNode.Properties["nullable"] = !col.Constraints.Any(c => c is NullableConstraintDefinition { Nullable: false });
                }

                AddEdge(EdgeType.Contains, tableNodeId, colNodeId, "table contains column");
            }
        }

        // Extract foreign key constraints from table definition
        if (node.Definition?.TableConstraints is not null)
        {
            foreach (var constraint in node.Definition.TableConstraints)
            {
                if (constraint is ForeignKeyConstraintDefinition fk)
                {
                    VisitForeignKey(fk, tableName);
                }
            }
        }

        // Also check column-level constraints for inline FK definitions
        if (node.Definition?.ColumnDefinitions is not null)
        {
            foreach (var col in node.Definition.ColumnDefinitions)
            {
                foreach (var constraint in col.Constraints)
                {
                    if (constraint is ForeignKeyConstraintDefinition fk)
                    {
                        VisitForeignKey(fk, tableName);
                    }
                }
            }
        }

        base.Visit(node);
    }

    private void VisitForeignKey(ForeignKeyConstraintDefinition fk, string fromTableName)
    {
        var refTableName = GetObjectName(fk.ReferenceTableName);
        if (refTableName is null) return;

        var fkName = fk.ConstraintIdentifier?.Value ?? $"FK_{fromTableName}_{refTableName}";
        var fkNodeId = $"ForeignKey:{fkName}";

        AddNode(fkNodeId, fkName, NodeType.ForeignKey, fk);

        var fromTableId = $"Table:{fromTableName}";
        var toTableId = $"Table:{refTableName}";

        AddEdge(EdgeType.Contains, fromTableId, fkNodeId, "table has foreign key");
        AddEdge(EdgeType.ForeignKeyTo, fkNodeId, toTableId, $"references {refTableName}");
    }

    // --- CREATE VIEW ---

    public override void Visit(CreateViewStatement node)
    {
        var viewName = GetObjectName(node.SchemaObjectName);
        if (viewName is null) return;

        var viewNodeId = $"View:{viewName}";
        AddNode(viewNodeId, viewName, NodeType.View, node);
        AddEdge(EdgeType.Defines, _fileNodeId, viewNodeId, "file defines view");

        // Find referenced tables in the view body
        var tableRefs = new TableReferenceCollector();
        node.SelectStatement?.Accept(tableRefs);

        foreach (var tableName in tableRefs.Tables)
        {
            var tableNodeId = $"Table:{tableName}";
            AddEdge(EdgeType.ReferencesTable, viewNodeId, tableNodeId, $"view references {tableName}");
        }

        base.Visit(node);
    }

    // --- CREATE PROCEDURE ---

    public override void Visit(CreateProcedureStatement node)
    {
        var procName = GetObjectName(node.ProcedureReference?.Name);
        if (procName is null) return;

        var procNodeId = $"StoredProcedure:{procName}";
        var procNode = AddNode(procNodeId, procName, NodeType.StoredProcedure, node);
        AddEdge(EdgeType.Defines, _fileNodeId, procNodeId, "file defines stored procedure");

        // Extract parameters
        if (procNode is not null && node.Parameters is { Count: > 0 })
        {
            var paramNames = node.Parameters.Select(p => p.VariableName?.Value).Where(n => n is not null).ToList();
            procNode.Properties["parameters"] = string.Join(", ", paramNames);
        }

        // Find referenced tables in procedure body
        var tableRefs = new TableReferenceCollector();
        foreach (var statement in node.StatementList?.Statements ?? Enumerable.Empty<TSqlStatement>())
        {
            statement.Accept(tableRefs);
        }

        foreach (var tableName in tableRefs.Tables)
        {
            var tableNodeId = $"Table:{tableName}";
            AddEdge(EdgeType.ReferencesTable, procNodeId, tableNodeId, $"procedure references {tableName}");
        }

        base.Visit(node);
    }

    // --- CREATE FUNCTION ---

    public override void Visit(CreateFunctionStatement node)
    {
        var funcName = GetObjectName(node.Name);
        if (funcName is null) return;

        var funcNodeId = $"StoredProcedure:{funcName}";
        var funcNode = AddNode(funcNodeId, funcName, NodeType.StoredProcedure, node);
        AddEdge(EdgeType.Defines, _fileNodeId, funcNodeId, "file defines function");

        if (funcNode is not null)
        {
            funcNode.Properties["isFunction"] = true;
        }

        // Extract parameters
        if (funcNode is not null && node.Parameters is { Count: > 0 })
        {
            var paramNames = node.Parameters.Select(p => p.VariableName?.Value).Where(n => n is not null).ToList();
            funcNode.Properties["parameters"] = string.Join(", ", paramNames);
        }

        // Find referenced tables in function body
        var tableRefs = new TableReferenceCollector();
        if (node.ReturnType is SelectFunctionReturnType selectReturn)
        {
            selectReturn.SelectStatement?.Accept(tableRefs);
        }
        foreach (var statement in node.StatementList?.Statements ?? Enumerable.Empty<TSqlStatement>())
        {
            statement.Accept(tableRefs);
        }

        foreach (var tableName in tableRefs.Tables)
        {
            var tableNodeId = $"Table:{tableName}";
            AddEdge(EdgeType.ReferencesTable, funcNodeId, tableNodeId, $"function references {tableName}");
        }

        base.Visit(node);
    }

    // --- CREATE INDEX ---

    public override void Visit(CreateIndexStatement node)
    {
        var indexName = node.Name?.Value;
        var tableName = GetObjectName(node.OnName);
        if (indexName is null || tableName is null) return;

        var indexNodeId = $"Index:{indexName}";
        var indexNode = AddNode(indexNodeId, indexName, NodeType.Index, node);
        AddEdge(EdgeType.Defines, _fileNodeId, indexNodeId, "file defines index");

        var tableNodeId = $"Table:{tableName}";
        AddEdge(EdgeType.Contains, tableNodeId, indexNodeId, "table has index");

        if (indexNode is not null && node.Columns is { Count: > 0 })
        {
            var colNames = node.Columns.Select(c => c.Column?.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value)
                .Where(n => n is not null)
                .ToList();
            indexNode.Properties["columns"] = string.Join(", ", colNames);
        }

        base.Visit(node);
    }

    // --- CREATE TRIGGER ---

    public override void Visit(CreateTriggerStatement node)
    {
        var triggerName = GetObjectName(node.Name);
        var tableName = GetObjectName(node.TriggerObject?.Name);
        if (triggerName is null) return;

        var triggerNodeId = $"Trigger:{triggerName}";
        AddNode(triggerNodeId, triggerName, NodeType.Trigger, node);
        AddEdge(EdgeType.Defines, _fileNodeId, triggerNodeId, "file defines trigger");

        if (tableName is not null)
        {
            var tableNodeId = $"Table:{tableName}";
            AddEdge(EdgeType.Contains, tableNodeId, triggerNodeId, "table has trigger");
        }

        base.Visit(node);
    }

    // --- Helpers ---

    private static string? GetObjectName(SchemaObjectName? schemaObjectName)
    {
        if (schemaObjectName is null) return null;
        var identifiers = schemaObjectName.Identifiers;
        if (identifiers is null || identifiers.Count == 0) return null;

        // Use the last identifier as the name (skip schema/database prefixes for node ID simplicity)
        return identifiers[^1].Value;
    }

    private GraphNode? AddNode(string id, string name, NodeType type, TSqlFragment fragment)
    {
        if (!_emittedNodeIds.Add(id)) return null;

        var node = new GraphNode
        {
            Id = id,
            Name = name,
            Type = type,
            FullName = id,
            FilePath = _relativePath,
            StartLine = fragment.StartLine,
            EndLine = fragment.ScriptTokenStream is not null && fragment.LastTokenIndex >= 0
                ? fragment.ScriptTokenStream[fragment.LastTokenIndex].Line
                : fragment.StartLine,
            Language = "sql",
        };
        _result.Nodes.Add(node);
        return node;
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

    private static string FragmentToString(TSqlFragment fragment)
    {
        // Build the text from the fragment's tokens
        var parts = new List<string>();
        for (var i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
        {
            parts.Add(fragment.ScriptTokenStream[i].Text);
        }
        return string.Join("", parts).Trim();
    }
}

/// <summary>
/// Collects all NamedTableReference nodes from a SQL fragment.
/// </summary>
internal sealed class TableReferenceCollector : TSqlFragmentVisitor
{
    public HashSet<string> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public override void Visit(NamedTableReference node)
    {
        var name = node.SchemaObject?.Identifiers?.LastOrDefault()?.Value;
        if (name is not null)
        {
            Tables.Add(name);
        }
        base.Visit(node);
    }
}
