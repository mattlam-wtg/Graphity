namespace Graphity.Core.Graph;

public sealed class GraphRelationship
{
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public required EdgeType Type { get; init; }
    public double Confidence { get; set; } = 1.0;
    public string? Reason { get; set; }
    public int? Step { get; set; }
    public Dictionary<string, object> Properties { get; } = new();
}
