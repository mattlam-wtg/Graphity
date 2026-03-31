using Graphity.Core.Graph;
using Graphity.Core.Ingestion;

namespace Graphity.Core.Tests.Ingestion;

public class FileScannerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FileScanner _scanner = new();

    public FileScannerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "graphity_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private string CreateFile(string relativePath, string content = "// content")
    {
        var fullPath = Path.Combine(_tempRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    [Fact]
    public void Scan_FindsAllExpectedFiles_WithCorrectLanguages()
    {
        CreateFile("src/Program.cs");
        CreateFile("src/app.ts");
        CreateFile("db/init.sql");
        CreateFile("config/settings.json");
        CreateFile("src/MyProject.csproj");

        var files = _scanner.Scan(_tempRoot);

        Assert.Equal(5, files.Count);
        Assert.Contains(files, f => f.RelativePath == "src/Program.cs" && f.Language == "csharp");
        Assert.Contains(files, f => f.RelativePath == "src/app.ts" && f.Language == "typescript");
        Assert.Contains(files, f => f.RelativePath == "db/init.sql" && f.Language == "sql");
        Assert.Contains(files, f => f.RelativePath == "config/settings.json" && f.Language == "config");
        Assert.Contains(files, f => f.RelativePath == "src/MyProject.csproj" && f.Language == "config");
    }

    [Fact]
    public void Scan_IgnoresUnsupportedExtensions()
    {
        CreateFile("src/readme.md");
        CreateFile("src/image.png");
        CreateFile("src/Program.cs");

        var files = _scanner.Scan(_tempRoot);

        Assert.Single(files);
        Assert.Equal("csharp", files[0].Language);
    }

    [Fact]
    public void Scan_RespectsIgnoredDirectories()
    {
        CreateFile("src/Program.cs");
        CreateFile("bin/Debug/output.cs");
        CreateFile("obj/Debug/temp.cs");
        CreateFile("node_modules/lib/index.js");
        CreateFile(".git/objects/hash.cs");

        var files = _scanner.Scan(_tempRoot);

        Assert.Single(files);
        Assert.Equal("src/Program.cs", files[0].RelativePath);
    }

    [Fact]
    public void Scan_RespectsGitignorePatterns()
    {
        File.WriteAllText(Path.Combine(_tempRoot, ".gitignore"), "dist\nbuild/\n");
        CreateFile("src/Program.cs");
        CreateFile("dist/bundle.js");
        CreateFile("build/output.cs");

        var files = _scanner.Scan(_tempRoot);

        Assert.Single(files);
        Assert.Equal("src/Program.cs", files[0].RelativePath);
    }

    [Fact]
    public void Scan_ReturnsCorrectFileSize()
    {
        var content = "namespace Test { }";
        CreateFile("src/Test.cs", content);

        var files = _scanner.Scan(_tempRoot);

        Assert.Single(files);
        Assert.Equal(content.Length, files[0].Size);
    }

    [Fact]
    public void CreateFileAndFolderNodes_CreatesFileNodes()
    {
        CreateFile("src/Program.cs");
        var files = _scanner.Scan(_tempRoot);

        var result = _scanner.CreateFileAndFolderNodes(_tempRoot, files);

        var fileNodes = result.Nodes.Where(n => n.Type == NodeType.File).ToList();
        Assert.Single(fileNodes);
        Assert.Equal("Program.cs", fileNodes[0].Name);
        Assert.Equal("File:src/Program.cs", fileNodes[0].Id);
        Assert.Equal("csharp", fileNodes[0].Language);
    }

    [Fact]
    public void CreateFileAndFolderNodes_CreatesFolderNodes()
    {
        CreateFile("src/sub/Program.cs");
        var files = _scanner.Scan(_tempRoot);

        var result = _scanner.CreateFileAndFolderNodes(_tempRoot, files);

        var folderNodes = result.Nodes.Where(n => n.Type == NodeType.Folder).ToList();
        Assert.Equal(2, folderNodes.Count);
        // Path.GetDirectoryName uses OS-specific separators, so normalize for comparison
        var folderIds = folderNodes.Select(f => f.Id.Replace('\\', '/')).ToList();
        Assert.Contains("Folder:src/sub", folderIds);
        Assert.Contains("Folder:src", folderIds);
    }

    [Fact]
    public void CreateFileAndFolderNodes_CreatesContainsEdges()
    {
        CreateFile("src/Program.cs");
        var files = _scanner.Scan(_tempRoot);

        var result = _scanner.CreateFileAndFolderNodes(_tempRoot, files);

        var containsEdges = result.Edges.Where(e => e.Type == EdgeType.Contains).ToList();
        // Should have folder->file edge
        Assert.Contains(containsEdges, e =>
            e.SourceId == "Folder:src" && e.TargetId == "File:src/Program.cs");
    }

    [Fact]
    public void CreateFileAndFolderNodes_NoDuplicateFolderNodes()
    {
        CreateFile("src/A.cs");
        CreateFile("src/B.cs");
        var files = _scanner.Scan(_tempRoot);

        var result = _scanner.CreateFileAndFolderNodes(_tempRoot, files);

        var folderNodes = result.Nodes.Where(n => n.Type == NodeType.Folder).ToList();
        Assert.Single(folderNodes); // Only one "src" folder node
    }
}
