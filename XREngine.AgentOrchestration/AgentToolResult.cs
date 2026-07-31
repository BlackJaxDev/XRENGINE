namespace XREngine.AgentOrchestration;

/// <summary>
/// Result of one local tool invocation, including optional visual evidence.
/// </summary>
public sealed record AgentToolResult
{
    public string Content { get; init; } = string.Empty;

    public bool IsError { get; init; }

    public bool IsTruncated { get; init; }

    public string? ImageDataUri { get; init; }

    public string? ImagePath { get; init; }
}
