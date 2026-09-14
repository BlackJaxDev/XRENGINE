namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Runtime-neutral disposition returned for a tracked OpenXR submission.</summary>
public enum EOpenXrSubmissionDisposition : byte
{
    NotSubmitted,
    SubmittedIncomplete,
    Completed,
}
