using Graphity.Core.Analyzers.CSharp;
using Graphity.Core.Graph;
using Graphity.Core.Ingestion;

namespace Graphity.Core.Tests.Analyzers;

public class CSharpConfigParserTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly CSharpConfigParser _parser = new();

    public CSharpConfigParserTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "graphity_config_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private FileScanner.ScannedFile CreateConfigFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_tempRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        var info = new FileInfo(fullPath);
        return new FileScanner.ScannedFile(fullPath, relativePath, "config", info.Length);
    }

    [Fact]
    public void ParseConfigs_Csproj_CreatesNuGetPackageNodesAndReferencesEdges()
    {
        var csprojContent = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <PackageReference Include="xunit" Version="2.9.0" />
              </ItemGroup>
            </Project>
            """;
        var file = CreateConfigFile("src/MyProject.csproj", csprojContent);

        var result = _parser.ParseConfigs(_tempRoot, new[] { file });

        // Should have: 1 ConfigFile node + 2 NuGetPackage nodes
        var configNodes = result.Nodes.Where(n => n.Type == NodeType.ConfigFile).ToList();
        Assert.Single(configNodes);
        Assert.Equal("MyProject.csproj", configNodes[0].Name);

        var packageNodes = result.Nodes.Where(n => n.Type == NodeType.NuGetPackage).ToList();
        Assert.Equal(2, packageNodes.Count);
        Assert.Contains(packageNodes, n => n.Name == "Newtonsoft.Json");
        Assert.Contains(packageNodes, n => n.Name == "xunit");

        // Check version property
        var newtonsoftNode = packageNodes.First(n => n.Name == "Newtonsoft.Json");
        Assert.Equal("13.0.3", newtonsoftNode.Properties["version"]);

        // Check REFERENCES_PACKAGE edges
        var refEdges = result.Edges.Where(e => e.Type == EdgeType.ReferencesPackage).ToList();
        Assert.Equal(2, refEdges.Count);
        Assert.All(refEdges, e => Assert.Equal($"File:{file.RelativePath}", e.SourceId));
    }

    [Fact]
    public void ParseConfigs_AppSettingsJson_CreatesConfigFileAndConfigSectionNodes()
    {
        var jsonContent = """
            {
              "Logging": {
                "LogLevel": {
                  "Default": "Information"
                }
              },
              "ConnectionStrings": {
                "DefaultConnection": "Server=localhost"
              },
              "AppName": "TestApp"
            }
            """;
        var file = CreateConfigFile("appsettings.json", jsonContent);

        var result = _parser.ParseConfigs(_tempRoot, new[] { file });

        var configNodes = result.Nodes.Where(n => n.Type == NodeType.ConfigFile).ToList();
        Assert.Single(configNodes);
        Assert.Equal("appsettings.json", configNodes[0].Name);

        var sectionNodes = result.Nodes.Where(n => n.Type == NodeType.ConfigSection).ToList();
        Assert.Equal(3, sectionNodes.Count);
        Assert.Contains(sectionNodes, n => n.Name == "Logging");
        Assert.Contains(sectionNodes, n => n.Name == "ConnectionStrings");
        Assert.Contains(sectionNodes, n => n.Name == "AppName");

        // String value should be stored
        var appNameNode = sectionNodes.First(n => n.Name == "AppName");
        Assert.Equal("TestApp", appNameNode.Properties["value"]);

        // Contains edges from config file to sections
        var containsEdges = result.Edges.Where(e => e.Type == EdgeType.Contains).ToList();
        Assert.Equal(3, containsEdges.Count);
    }

    [Fact]
    public void ParseConfigs_MalformedCsproj_DoesNotThrow()
    {
        var file = CreateConfigFile("src/Bad.csproj", "this is not valid xml <<<<");

        var result = _parser.ParseConfigs(_tempRoot, new[] { file });

        Assert.Empty(result.Nodes);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void ParseConfigs_MalformedJson_DoesNotThrow()
    {
        var file = CreateConfigFile("appsettings.json", "{ invalid json }}}");

        var result = _parser.ParseConfigs(_tempRoot, new[] { file });

        Assert.Empty(result.Nodes);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void ParseConfigs_NonConfigJson_IsIgnored()
    {
        // Only appsettings*.json files are parsed as app settings
        var file = CreateConfigFile("tsconfig.json", """{ "compilerOptions": {} }""");

        var result = _parser.ParseConfigs(_tempRoot, new[] { file });

        Assert.Empty(result.Nodes);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void ParseConfigs_CsprojWithNoPackages_CreatesOnlyConfigFileNode()
    {
        var csprojContent = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;
        var file = CreateConfigFile("src/Empty.csproj", csprojContent);

        var result = _parser.ParseConfigs(_tempRoot, new[] { file });

        Assert.Single(result.Nodes);
        Assert.Equal(NodeType.ConfigFile, result.Nodes[0].Type);
        Assert.Empty(result.Edges);
    }
}
