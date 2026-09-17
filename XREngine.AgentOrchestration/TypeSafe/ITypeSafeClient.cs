namespace XREngine.AgentOrchestration.TypeSafe;

/// <summary>
/// Client interface for evaluating System One decisions via TypeSafe's Jev model.
/// When unavailable (e.g. no API key configured or waiting on access), operations
/// return null gracefully so callers can fall back to deterministic logic.
/// </summary>
public interface ITypeSafeClient
{
    /// <summary>
    /// Gets whether a valid API key is present and the client is ready for evaluation calls.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets the target System One model identifier (defaults to "jev-latest").
    /// </summary>
    string TargetModel { get; }

    /// <summary>
    /// Evaluates application or context state against a set of typed questions.
    /// Returns null if the client is unavailable, unauthenticated, or the call fails non-fatally.
    /// </summary>
    /// <param name="state">The state object, string, or structured dictionary to evaluate.</param>
    /// <param name="questions">Map of question ID to typed question.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The evaluated response with typed answers, or null on fallback.</returns>
    Task<TypeSafeResponse?> EvaluateAsync(
        object state,
        IReadOnlyDictionary<string, TypeSafeQuestion> questions,
        CancellationToken cancellationToken = default);
}
