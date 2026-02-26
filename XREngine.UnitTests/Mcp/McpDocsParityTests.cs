using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Text;
using XREngine.Editor.Mcp;

namespace XREngine.UnitTests.Mcp;

[TestFixture]
public class McpDocsParityTests
{
    private const string StartMarker = "<!-- MCP_TOOL_TABLE:START -->";
    private const string EndMarker = "<!-- MCP_TOOL_TABLE:END -->";

    [Test]
    public void McpServerDoc_ToolTableMatchesRuntimeRegistry()
    {
        string repoRoot = FindRepositoryRoot();
        string docPath = Path.Combine(repoRoot, "docs", "features", "mcp-server.md");

        File.Exists(docPath).ShouldBeTrue($"Expected MCP docs file at {docPath}");

        string docText = File.ReadAllText(docPath);
        int start = docText.IndexOf(StartMarker, StringComparison.Ordinal);
        int end = docText.IndexOf(EndMarker, StringComparison.Ordinal);

        start.ShouldBeGreaterThanOrEqualTo(0, "Missing MCP docs start marker.");
        end.ShouldBeGreaterThan(start, "Missing MCP docs end marker.");

        string actual = docText[(start + StartMarker.Length)..end].Trim();
        string expected = BuildExpectedTable();

        actual.ShouldBe(expected,
            "MCP docs tool table is out of sync with runtime registry. Run `pwsh Tools/Reports/generate_mcp_docs.ps1`.");
    }

    private static string BuildExpectedTable()
    {
        var tools = McpToolRegistry.Tools
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new
            {
                Name = t.Name,
                Description = (t.Description ?? string.Empty).Trim().Replace("|", "\\|")
            })
            .ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("| Tool | Description |");
        sb.AppendLine("|------|-------------|");
        foreach (var tool in tools)
            sb.AppendLine($"| `{tool.Name}` | {tool.Description} |");

        return sb.ToString().TrimEnd();
    }

    private static string FindRepositoryRoot()
    {
        string current = Path.GetFullPath(AppContext.BaseDirectory);

        while (true)
        {
            if (File.Exists(Path.Combine(current, "XRENGINE.sln")))
                return current;

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
                break;

            current = parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root containing XRENGINE.sln.");
    }
}
