namespace XREngine.Runtime.Automation.Mcp;

internal static class McpCapabilityNames
{
    public static string Format(McpCapability capabilities)
        => string.Join(", ", Enum.GetValues<McpCapability>()
            .Where(value => value != McpCapability.None && capabilities.HasFlag(value))
            .Select(static value => value.ToString()));
}
