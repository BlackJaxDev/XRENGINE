namespace XREngine.Rendering;

/// <summary>
/// Stable diagnostic views decoded from the final visibility-buffer resources.
/// </summary>
public enum EAdvancedVisibilityDebugView : uint
{
    Disabled = 0u,
    RawPayloadWords = 1u,
    DrawId = 2u,
    PrimitiveId = 3u,
    MaterialId = 4u,
    ShadingKernelId = 5u,
    SelectionId = 6u,
    EarlyLateOrigin = 7u,
    InvalidPayload = 8u,
    Depth = 9u,
}
