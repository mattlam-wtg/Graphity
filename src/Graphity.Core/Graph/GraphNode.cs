namespace Graphity.Core.Graph;

public sealed class GraphNode
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required NodeType Type { get; init; }
    public string? FullName { get; set; }
    public string? FilePath { get; set; }
    public int? StartLine { get; set; }
    public int? EndLine { get; set; }
    public bool IsExported { get; set; }
    public string Language { get; set; } = "csharp";
    public string? Content { get; set; }
    public Dictionary<string, object> Properties { get; } = new();
}
