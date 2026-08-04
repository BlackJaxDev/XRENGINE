namespace XREngine.AgentOrchestration;

/// <summary>
/// Structured provider exception used to drive bounded retry behavior.
/// </summary>
public sealed class AgentModelException : Exception
{
    public AgentModelException(
        AgentFailureCategory category,
        string message,
        bool retryable = false,
        int? providerStatus = null,
        TimeSpan? retryAfter = null,
        string? diagnosticDetail = null,
        AgentProviderAttemptDiagnostic? providerAttempt = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Category = category;
        Retryable = retryable;
        ProviderStatus = providerStatus;
        RetryAfter = retryAfter;
        DiagnosticDetail = diagnosticDetail ?? string.Empty;
        ProviderAttempt = providerAttempt;
    }

    public AgentFailureCategory Category { get; }

    public bool Retryable { get; }

    public int? ProviderStatus { get; }

    public TimeSpan? RetryAfter { get; }

    public string DiagnosticDetail { get; }

    public AgentProviderAttemptDiagnostic? ProviderAttempt { get; }

    public AgentModelException WithProviderAttempt(AgentProviderAttemptDiagnostic providerAttempt)
        => new(
            Category,
            Message,
            Retryable,
            ProviderStatus,
            RetryAfter,
            DiagnosticDetail,
            providerAttempt,
            this);
}
