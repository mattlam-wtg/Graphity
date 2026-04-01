using System.Text.Json;
using System.Text.Json.Nodes;

namespace Graphity.Cli.Commands;

/// <summary>
/// Auto-configures MCP server entries for common editors (Claude Code, VS Code, Cursor).
/// </summary>
public static class SetupCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Run()
    {
        Console.WriteLine("Graphity Setup — configuring MCP for detected editors\n");

        var configured = false;

        if (TryConfigureClaudeCode())
            configured = true;

        if (TryConfigureVsCode())
            configured = true;

        if (TryConfigureCursor())
            configured = true;

        if (!configured)
        {
            Console.WriteLine("No supported editors detected.");
            Console.WriteLine("  Supported: Claude Code (~/.claude), VS Code, Cursor");
            Console.WriteLine("\n  You can manually add the MCP config:");
            Console.WriteLine("    {");
            Console.WriteLine("      \"mcpServers\": {");
            Console.WriteLine("        \"graphity\": {");
            Console.WriteLine("          \"command\": \"graphity\",");
            Console.WriteLine("          \"args\": [\"mcp\"],");
            Console.WriteLine("          \"env\": {}");
            Console.WriteLine("        }");
            Console.WriteLine("      }");
            Console.WriteLine("    }");
        }
    }

    private static bool TryConfigureClaudeCode()
    {
        var claudeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude");

        if (!Directory.Exists(claudeDir))
            return false;

        Console.WriteLine("[Claude Code] Detected ~/.claude directory");

        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude.json");
        return ConfigureMcpInJsonFile(settingsPath, "mcpServers", "  Claude Code");
    }

    private static bool TryConfigureVsCode()
    {
        // Check for VS Code by looking for the .vscode directory or code executable
        var vscodeDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode"),
        };

        var detected = vscodeDirs.Any(Directory.Exists);
        if (!detected) return false;

        Console.WriteLine("[VS Code] Detected VS Code installation");

        // Write to workspace-level .vscode/mcp.json
        var workspaceVsCode = Path.Combine(Directory.GetCurrentDirectory(), ".vscode");
        Directory.CreateDirectory(workspaceVsCode);
        var mcpPath = Path.Combine(workspaceVsCode, "mcp.json");
        return ConfigureMcpInJsonFile(mcpPath, "servers", "  VS Code");
    }

    private static bool TryConfigureCursor()
    {
        var cursorDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor"),
        };

        var detected = cursorDirs.Any(Directory.Exists);
        if (!detected) return false;

        Console.WriteLine("[Cursor] Detected Cursor installation");

        var cursorDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor");
        Directory.CreateDirectory(cursorDir);
        var mcpPath = Path.Combine(cursorDir, "mcp.json");
        return ConfigureMcpInJsonFile(mcpPath, "mcpServers", "  Cursor");
    }

    private static bool ConfigureMcpInJsonFile(string filePath, string serversKey, string editorLabel)
    {
        try
        {
            JsonObject root;
            if (File.Exists(filePath))
            {
                var existing = File.ReadAllText(filePath);
                root = JsonNode.Parse(existing)?.AsObject() ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            // Check if graphity is already configured
            if (root[serversKey] is JsonObject servers && servers["graphity"] != null)
            {
                Console.WriteLine($"{editorLabel}: graphity already configured in {filePath}");
                return true;
            }

            // Build the MCP server entry
            var graphityEntry = new JsonObject
            {
                ["command"] = "graphity",
                ["args"] = new JsonArray("mcp"),
                ["env"] = new JsonObject()
            };

            if (root[serversKey] is not JsonObject)
            {
                root[serversKey] = new JsonObject();
            }

            root[serversKey]!.AsObject()["graphity"] = graphityEntry;

            Console.WriteLine($"{editorLabel}: Will write to {filePath}");
            Console.Write($"{editorLabel}: Proceed? [Y/n] ");
            var response = Console.ReadLine()?.Trim();
            if (response != null && response.Length > 0
                && !response.Equals("y", StringComparison.OrdinalIgnoreCase)
                && !response.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{editorLabel}: Skipped");
                return true; // Editor was detected even if user skipped
            }

            File.WriteAllText(filePath, root.ToJsonString(JsonOptions));
            Console.WriteLine($"{editorLabel}: Configured successfully\n");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{editorLabel}: Failed to configure — {ex.Message}");
            return true; // Editor was detected
        }
    }
}
