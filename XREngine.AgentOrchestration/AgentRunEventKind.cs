namespace XREngine.AgentOrchestration;

/// <summary>
/// Identifies observable orchestration events without exposing provider event names.
/// </summary>
public enum AgentRunEventKind
{
    Status,
    TextDelta,
    ToolStarted,
    ToolCompleted,
    Usage,
    Retry,
    Diagnostic,
}
