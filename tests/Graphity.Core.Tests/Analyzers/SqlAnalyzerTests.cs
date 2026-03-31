using Graphity.Core.Analyzers.Sql;
using Graphity.Core.Graph;

namespace Graphity.Core.Tests.Analyzers;

public class SqlAnalyzerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly SqlAnalyzer _analyzer = new();

    public SqlAnalyzerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "graphity_sql_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private string CreateFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_tempRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    [Fact]
    public async Task ExtractsCreateTableWithColumns()
    {
        var filePath = CreateFile("db/tables.sql", """
            CREATE TABLE Users (
                Id INT NOT NULL,
                Name NVARCHAR(100) NOT NULL,
                Email NVARCHAR(255) NULL
            );
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var tableNodes = result.Nodes.Where(n => n.Type == NodeType.Table).ToList();
        Assert.Single(tableNodes);
        Assert.Equal("Users", tableNodes[0].Name);
        Assert.Equal("sql", tableNodes[0].Language);

        var columnNodes = result.Nodes.Where(n => n.Type == NodeType.Column).ToList();
        Assert.Equal(3, columnNodes.Count);
        Assert.Contains(columnNodes, c => c.Name == "Id");
        Assert.Contains(columnNodes, c => c.Name == "Name");
        Assert.Contains(columnNodes, c => c.Name == "Email");

        // Check Contains edges from table to columns
        var containsEdges = result.Edges.Where(e => e.Type == EdgeType.Contains && e.SourceId == "Table:Users").ToList();
        Assert.Equal(3, containsEdges.Count);
    }

    [Fact]
    public async Task ExtractsCreateViewWithTableReferences()
    {
        var filePath = CreateFile("db/views.sql", """
            CREATE VIEW ActiveUsers AS
            SELECT u.Id, u.Name, o.OrderCount
            FROM Users u
            JOIN Orders o ON u.Id = o.UserId
            WHERE u.IsActive = 1;
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var viewNodes = result.Nodes.Where(n => n.Type == NodeType.View).ToList();
        Assert.Single(viewNodes);
        Assert.Equal("ActiveUsers", viewNodes[0].Name);

        var refEdges = result.Edges.Where(e => e.Type == EdgeType.ReferencesTable).ToList();
        Assert.Equal(2, refEdges.Count);
        Assert.Contains(refEdges, e => e.TargetId == "Table:Users");
        Assert.Contains(refEdges, e => e.TargetId == "Table:Orders");
    }

    [Fact]
    public async Task ExtractsCreateProcedureWithTableReferences()
    {
        var filePath = CreateFile("db/procs.sql", """
            CREATE PROCEDURE GetUserOrders
                @UserId INT,
                @Status NVARCHAR(50)
            AS
            BEGIN
                SELECT o.Id, o.Total
                FROM Orders o
                WHERE o.UserId = @UserId AND o.Status = @Status;
            END;
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var procNodes = result.Nodes.Where(n => n.Type == NodeType.StoredProcedure).ToList();
        Assert.Single(procNodes);
        Assert.Equal("GetUserOrders", procNodes[0].Name);

        var refEdges = result.Edges.Where(e => e.Type == EdgeType.ReferencesTable).ToList();
        Assert.Single(refEdges);
        Assert.Equal("Table:Orders", refEdges[0].TargetId);
    }

    [Fact]
    public async Task ExtractsForeignKeyConstraints()
    {
        var filePath = CreateFile("db/fk.sql", """
            CREATE TABLE Orders (
                Id INT NOT NULL,
                UserId INT NOT NULL,
                CONSTRAINT FK_Orders_Users FOREIGN KEY (UserId) REFERENCES Users(Id)
            );
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var fkNodes = result.Nodes.Where(n => n.Type == NodeType.ForeignKey).ToList();
        Assert.Single(fkNodes);
        Assert.Equal("FK_Orders_Users", fkNodes[0].Name);

        var fkEdges = result.Edges.Where(e => e.Type == EdgeType.ForeignKeyTo).ToList();
        Assert.Single(fkEdges);
        Assert.Equal("Table:Users", fkEdges[0].TargetId);
    }

    [Fact]
    public async Task ExtractsCreateIndex()
    {
        var filePath = CreateFile("db/indexes.sql", """
            CREATE INDEX IX_Users_Email ON Users (Email);
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var indexNodes = result.Nodes.Where(n => n.Type == NodeType.Index).ToList();
        Assert.Single(indexNodes);
        Assert.Equal("IX_Users_Email", indexNodes[0].Name);

        // Index should be contained by the table
        var containsEdges = result.Edges.Where(e => e.Type == EdgeType.Contains && e.SourceId == "Table:Users").ToList();
        Assert.Single(containsEdges);
        Assert.Equal("Index:IX_Users_Email", containsEdges[0].TargetId);
    }

    [Fact]
    public async Task HandlesMalformedSqlGracefully()
    {
        var filePath = CreateFile("db/bad.sql", """
            THIS IS NOT VALID SQL AT ALL ;;;
            CREATE TABL broken (
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        // Should not throw, may return empty or partial results
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ExtractsCreateFunction()
    {
        var filePath = CreateFile("db/funcs.sql", """
            CREATE FUNCTION GetUserName(@UserId INT)
            RETURNS NVARCHAR(100)
            AS
            BEGIN
                DECLARE @Name NVARCHAR(100);
                SELECT @Name = Name FROM Users WHERE Id = @UserId;
                RETURN @Name;
            END;
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var funcNodes = result.Nodes.Where(n => n.Type == NodeType.StoredProcedure).ToList();
        Assert.Single(funcNodes);
        Assert.Equal("GetUserName", funcNodes[0].Name);

        var refEdges = result.Edges.Where(e => e.Type == EdgeType.ReferencesTable).ToList();
        Assert.Single(refEdges);
        Assert.Equal("Table:Users", refEdges[0].TargetId);
    }

    [Fact]
    public async Task ExtractsCreateTrigger()
    {
        var filePath = CreateFile("db/triggers.sql", """
            CREATE TRIGGER trg_Users_Insert
            ON Users
            AFTER INSERT
            AS
            BEGIN
                INSERT INTO AuditLog (Action) VALUES ('Insert');
            END;
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var triggerNodes = result.Nodes.Where(n => n.Type == NodeType.Trigger).ToList();
        Assert.Single(triggerNodes);
        Assert.Equal("trg_Users_Insert", triggerNodes[0].Name);

        // Should have Contains edge from table to trigger
        var containsEdges = result.Edges.Where(e => e.Type == EdgeType.Contains && e.SourceId == "Table:Users").ToList();
        Assert.Single(containsEdges);
    }

    [Fact]
    public void SupportedExtensionsContainsSql()
    {
        Assert.Contains(".sql", _analyzer.SupportedExtensions);
    }

    [Fact]
    public async Task AllNodesSetsLanguageToSql()
    {
        var filePath = CreateFile("db/schema.sql", """
            CREATE TABLE Products (
                Id INT NOT NULL,
                Name NVARCHAR(200)
            );
            CREATE INDEX IX_Products_Name ON Products (Name);
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        Assert.All(result.Nodes, n => Assert.Equal("sql", n.Language));
    }
}
