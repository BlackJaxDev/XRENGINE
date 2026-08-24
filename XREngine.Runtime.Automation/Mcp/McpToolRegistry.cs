namespace XREngine.Runtime.Automation.Mcp;

/// <summary>Case-insensitive registry assembled from independent runtime and editor bundles.</summary>
public sealed class McpToolRegistry
{
    private readonly Dictionary<string, McpToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<McpToolDefinition> Tools => _tools.Values;

    public void Register(IMcpToolBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        foreach (McpToolDefinition tool in bundle.GetTools())
        {
            if (!_tools.TryAdd(tool.Name, tool))
                throw new InvalidOperationException($"MCP tool '{tool.Name}' is already registered.");
        }
    }

    public bool TryGet(string name, out McpToolDefinition? tool) => _tools.TryGetValue(name, out tool);
}
