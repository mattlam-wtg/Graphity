using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Graphity.Mcp;

public static class GraphityMcpServer
{
    public static async Task RunAsync(string? repoPath = null, CancellationToken ct = default)
    {
        var builder = Host.CreateApplicationBuilder();

        // Suppress noisy console logging — MCP uses stdio
        builder.Logging.ClearProviders();

        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new()
            {
                Name = "graphity",
                Version = "1.0.0",
            };
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly()
        .WithResourcesFromAssembly();

        // Register our services
        builder.Services.AddSingleton(new GraphServiceConfig { RepoPath = repoPath });
        builder.Services.AddSingleton<GraphService>();

        var app = builder.Build();
        await app.RunAsync(ct);
    }
}
