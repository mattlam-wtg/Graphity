using Microsoft.SqlServer.TransactSql.ScriptDom;
using Graphity.Core.Graph;
using Graphity.Core.Ingestion;

namespace Graphity.Core.Analyzers.Sql;

public sealed class SqlAnalyzer : ILanguageAnalyzer
{
    public string Language => "sql";
    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".sql" };

    public async Task<AnalyzerResult> AnalyzeAsync(string filePath, string repoRoot, CancellationToken ct = default)
    {
        var result = new AnalyzerResult();
        var content = await File.ReadAllTextAsync(filePath, ct);
        var relativePath = Path.GetRelativePath(repoRoot, filePath).Replace('\\', '/');
        var fileNodeId = $"File:{relativePath}";

        TSqlFragment fragment;
        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: false);
            using var reader = new StringReader(content);
            fragment = parser.Parse(reader, out var errors);
            if (fragment is null) return result;
        }
        catch
        {
            // Malformed SQL — return empty result
            return result;
        }

        var visitor = new SqlVisitor(relativePath, fileNodeId, result);
        fragment.Accept(visitor);
        return result;
    }
}
