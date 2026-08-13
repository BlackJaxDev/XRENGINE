namespace XREngine.Runtime.Automation.Mcp;

/// <summary>Permission required to invoke a runtime automation tool.</summary>
public enum McpPermissionLevel
{
    ReadOnly,
    Mutating,
    Destructive,
}
