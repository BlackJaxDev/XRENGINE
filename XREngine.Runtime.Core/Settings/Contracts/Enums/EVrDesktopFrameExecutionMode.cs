namespace XREngine;

/// <summary>
/// Selects how a recording desktop view executes while VR is active.
/// </summary>
public enum EVrDesktopFrameExecutionMode
{
    /// <summary>The render thread records, submits and presents the desktop frame after the eyes.</summary>
    Inline,

    /// <summary>
    /// A background render lane records the desktop frame while the render thread
    /// prepares the next XR frame. Submit and present happen one XR frame later.
    /// </summary>
    WorkerRecord,

    /// <summary>Reserved for a separate desktop render domain with its own window ownership.</summary>
    SeparateDomain,
}
