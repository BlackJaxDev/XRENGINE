namespace XREngine.AgentOrchestration;

/// <summary>
/// Provider-neutral function tool metadata.
/// </summary>
public sealed record AgentToolDefinition
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string InputSchemaJson { get; init; } = """{"type":"object","properties":{}}""";

    public bool IsReadOnly { get; init; } = true;

    public bool IsDestructive { get; init; }
}
