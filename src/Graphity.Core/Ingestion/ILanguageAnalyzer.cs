namespace Graphity.Core.Ingestion;

public interface ILanguageAnalyzer
{
    string Language { get; }
    IReadOnlySet<string> SupportedExtensions { get; }
    Task<AnalyzerResult> AnalyzeAsync(string filePath, string repoRoot, CancellationToken ct = default);
}

public interface ISolutionAnalyzer : ILanguageAnalyzer
{
    Task<AnalyzerResult> AnalyzeSolutionAsync(string solutionPath, CancellationToken ct = default);
}
