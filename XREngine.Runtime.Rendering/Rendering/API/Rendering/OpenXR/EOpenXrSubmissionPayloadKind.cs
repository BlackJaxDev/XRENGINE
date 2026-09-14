namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Describes the retained payload categories carried by a submission.</summary>
[System.Flags]
public enum EOpenXrSubmissionPayloadKind : byte
{
    None = 0,
    Eye0 = 1,
    Eye1 = 2,
    Publish = 4,
    Preview = 8,
    OtherTemporary = 16,
}
