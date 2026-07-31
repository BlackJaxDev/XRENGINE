namespace XREngine.AgentOrchestration;

/// <summary>
/// Stable failure categories returned by orchestration and broker APIs.
/// </summary>
public enum AgentFailureCategory
{
    Validation,
    Authentication,
    ModelUnavailable,
    ModelSubstitution,
    ProviderRateLimit,
    ProviderError,
    Transport,
    ToolDiscovery,
    ToolDenied,
    ToolError,
    ToolOutputTooLarge,
    BudgetExceeded,
    MutationEvidenceMissing,
    Cancelled,
    Internal,
}
