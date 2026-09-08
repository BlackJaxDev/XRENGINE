namespace XREngine.Rendering;

/// <summary>
/// Describes whether an exact-output invocation reached backend authoring ownership.
/// </summary>
public enum ERenderOutputCompletionAuthoringDisposition
{
    /// <summary>No backend receipt was reserved, so the target has no writes from this invocation.</summary>
    RejectedBeforeAuthoring,

    /// <summary>The backend accepted authoring and returned a completion fence.</summary>
    AwaitingCompletion,

    /// <summary>Backend authoring began, but no fence can prove when its possible writes are safe.</summary>
    UnfencedAfterAuthoring,
}
