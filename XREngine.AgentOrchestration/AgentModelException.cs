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
        Exception? innerException = null,
        AgentModelTurnResult? partialResult = null)
        : base(message, innerException)
    {
        Category = category;
        Retryable = retryable;
        ProviderStatus = providerStatus;
        RetryAfter = retryAfter;
        DiagnosticDetail = diagnosticDetail ?? string.Empty;
        ProviderAttempt = providerAttempt;
        PartialResult = partialResult;
    }

    public AgentFailureCategory Category { get; }

    public bool Retryable { get; }

    public int? ProviderStatus { get; }

    public TimeSpan? RetryAfter { get; }

    public string DiagnosticDetail { get; }

    public AgentProviderAttemptDiagnostic? ProviderAttempt { get; }

    /// <summary>Observed text and usage from a failed turn. This is never an accepted proposal.</summary>
    public AgentModelTurnResult? PartialResult { get; }

    public AgentModelException WithProviderAttempt(
        AgentProviderAttemptDiagnostic providerAttempt,
        AgentModelTurnResult? partialResult = null)
        => new(
            Category,
            Message,
            Retryable,
            ProviderStatus,
            RetryAfter,
            DiagnosticDetail,
            providerAttempt,
            this,
            partialResult ?? PartialResult);
}
