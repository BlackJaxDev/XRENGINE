namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Identifies the authored command shape of an OpenXR submission.</summary>
public enum EOpenXrSubmissionShape : byte
{
    Unknown,
    SingleEye,
    PairedEyes,
    PairedEyesParallel,
    EyeWithPublish,
    PairedEyesWithPublish,
    PreviewCopy,
    MirrorPublish,
    MirrorTextureCopy,
    DiagnosticClear,
}
