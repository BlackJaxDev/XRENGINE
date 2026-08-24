namespace XREngine.Rendering;

/// <summary>
/// Describes whether the command stream containing a GPU fence reached backend submission.
/// This is intentionally separate from <see cref="EGpuFenceStatus"/>, which describes GPU
/// execution completion after a submission has been accepted.
/// </summary>
public enum EGpuFenceSubmissionStatus
{
    /// <summary>The backend has not yet accepted the command stream for submission.</summary>
    AwaitingSubmission,

    /// <summary>The backend accepted the command stream for submission.</summary>
    Submitted,

    /// <summary>The command stream was abandoned or its submission failed.</summary>
    Failed,
}
