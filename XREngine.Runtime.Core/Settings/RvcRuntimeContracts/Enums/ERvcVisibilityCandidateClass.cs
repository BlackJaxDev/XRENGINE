namespace XREngine;

/// <summary>
/// Visibility candidate classes used by the engine.
/// </summary>
public enum ERvcVisibilityCandidateClass
{
    /// <summary>
    /// Candidate that is stably visible.
    /// </summary>
    StableVisible,
    /// <summary>
    /// Candidate that was previously visible but can be rejected by HZB.
    /// </summary>
    PreviouslyVisibleHzbRejectable,
    /// <summary>
    /// Candidate that has newly become visible.
    /// </summary>
    NewlyVisible,
    /// <summary>
    /// Candidate with uncertain visibility.
    /// </summary>
    Uncertain,
    /// <summary>
    /// Candidate at the edge of the HZB.
    /// </summary>
    HzbEdge,
    /// <summary>
    /// Candidate with visibility disagreement across views.
    /// </summary>
    CrossViewDisagreement,
    /// <summary>
    /// Candidate that is a dynamic object.
    /// </summary>
    DynamicObject,
}
