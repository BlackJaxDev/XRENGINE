using System.Text;
using XREngine.Editor.Mcp;

const string StartMarker = "<!-- MCP_TOOL_TABLE:START -->";
const string EndMarker = "<!-- MCP_TOOL_TABLE:END -->";

string repoRoot = FindRepositoryRoot(Directory.GetCurrentDirectory());
string docPath = Path.Combine(repoRoot, "docs", "features", "mcp-server.md");

if (!File.Exists(docPath))
{
    Console.Error.WriteLine($"MCP docs file not found: {docPath}");
    return 2;
}

string original = File.ReadAllText(docPath);
int start = original.IndexOf(StartMarker, StringComparison.Ordinal);
int end = original.IndexOf(EndMarker, StringComparison.Ordinal);
if (start < 0 || end < 0 || end <= start)
{
    Console.Error.WriteLine("MCP docs markers not found or invalid. Expected MCP_TOOL_TABLE start/end markers.");
    return 3;
}

string table = BuildToolTableMarkdown();
int contentStart = start + StartMarker.Length;
string updated = string.Concat(
    original.AsSpan(0, contentStart),
    Environment.NewLine,
    Environment.NewLine,
    table,
    Environment.NewLine,
    original.AsSpan(end));

if (string.Equals(original, updated, StringComparison.Ordinal))
{
    Console.WriteLine("MCP docs already up to date.");
    return 0;
}

File.WriteAllText(docPath, updated);
Console.WriteLine($"Updated MCP tool table in {docPath}");
return 0;

static string BuildToolTableMarkdown()
{
    var tools = McpToolRegistry.Tools
        .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
        .Select(t => new
        {
            Name = t.Name,
            Description = EscapePipes(string.IsNullOrWhiteSpace(t.Description) ? string.Empty : t.Description.Trim())
        })
        .ToArray();

    var sb = new StringBuilder();
    sb.AppendLine("| Tool | Description |");
    sb.AppendLine("|------|-------------|");

    foreach (var tool in tools)
        sb.AppendLine($"| `{tool.Name}` | {tool.Description} |");

    return sb.ToString().TrimEnd();
}

static string EscapePipes(string value) => value.Replace("|", "\\|");

static string FindRepositoryRoot(string startDirectory)
{
    string current = Path.GetFullPath(startDirectory);
    while (true)
    {
        if (File.Exists(Path.Combine(current, "XRENGINE.sln")))
            return current;

        string? parent = Directory.GetParent(current)?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
            throw new DirectoryNotFoundException("Unable to locate repository root containing XRENGINE.sln.");

        current = parent;
    }
}
