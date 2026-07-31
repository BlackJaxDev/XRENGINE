namespace XREngine.AgentOrchestration;

/// <summary>
/// Structured local-tool transport or policy exception.
/// </summary>
public sealed class AgentToolProviderException : Exception
{
    public AgentToolProviderException(
        AgentFailureCategory category,
        string message,
        string? diagnosticDetail = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Category = category;
        DiagnosticDetail = diagnosticDetail ?? string.Empty;
    }

    public AgentFailureCategory Category { get; }

    public string DiagnosticDetail { get; }
}
