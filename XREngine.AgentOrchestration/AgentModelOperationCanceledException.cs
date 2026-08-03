namespace XREngine.AgentOrchestration;

/// <summary>
/// Cancellation that retains safe metadata for an accepted provider response.
/// </summary>
public sealed class AgentModelOperationCanceledException : OperationCanceledException
{
    public AgentModelOperationCanceledException(
        AgentProviderAttemptDiagnostic providerAttempt,
        OperationCanceledException innerException,
        CancellationToken cancellationToken)
        : base("The provider model request was cancelled.", innerException, cancellationToken)
    {
        ProviderAttempt = providerAttempt;
    }

    public AgentProviderAttemptDiagnostic ProviderAttempt { get; }
}
