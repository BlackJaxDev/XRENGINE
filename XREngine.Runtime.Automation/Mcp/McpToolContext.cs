namespace XREngine.Runtime.Automation.Mcp;

/// <summary>Capability-scoped services supplied by an automation host.</summary>
public sealed class McpToolContext
{
    private readonly IReadOnlyDictionary<Type, object> _services;

    public McpToolContext(McpCapability capabilities, IReadOnlyDictionary<Type, object>? services = null)
    {
        Capabilities = capabilities;
        _services = services ?? new Dictionary<Type, object>();
    }

    public McpCapability Capabilities { get; }

    public T GetRequiredService<T>() where T : class
        => TryGetService(out T? service)
            ? service!
            : throw new InvalidOperationException($"MCP service '{typeof(T).Name}' is unavailable.");

    public bool TryGetService<T>(out T? service) where T : class
    {
        if (_services.TryGetValue(typeof(T), out object? value) && value is T typed)
        {
            service = typed;
            return true;
        }

        service = null;
        return false;
    }
}
