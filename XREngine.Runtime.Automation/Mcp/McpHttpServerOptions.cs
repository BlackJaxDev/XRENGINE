namespace XREngine.Runtime.Automation.Mcp;

/// <summary>Configuration for an editor-free HTTP MCP endpoint.</summary>
public sealed record McpHttpServerOptions
{
    public required int Port { get; init; }
    public string ServerName { get; init; } = "XREngine.Runtime";
    public string ServerVersion { get; init; } = "1";
    public string? SessionToken { get; init; }
    public bool AllowMutations { get; init; }
    public int MaxIdempotencyEntries { get; init; } = 512;
    public Func<object>? StatusProvider { get; init; }
    public Action? ShutdownRequested { get; init; }
}
