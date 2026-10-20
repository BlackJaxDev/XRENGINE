namespace XREngine.Editor.Mcp;

/// <summary>Describes one bounded MCP request milestone.</summary>
public readonly record struct McpRequestMilestoneRecord(
    long RequestId,
    string JsonRpcId,
    string ToolName,
    string? TypeName,
    string? MethodName,
    string Milestone,
    double ElapsedMilliseconds,
    int ManagedThreadId,
    string? Detail);
