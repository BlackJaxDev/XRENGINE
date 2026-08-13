namespace XREngine.Runtime.Automation.Mcp;

/// <summary>Result of one MCP tool invocation and optional work deferred until after serialization.</summary>
public sealed record McpToolResponse(
    string Message,
    object? Data = null,
    bool IsError = false,
    Func<Task>? AfterResponse = null,
    Task? SuspendTransportUntil = null);
