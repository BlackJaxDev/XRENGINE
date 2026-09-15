namespace XREngine.Rendering;

/// <summary>One lock-consistent capture of the latest OpenXR view publication.</summary>
public readonly record struct RenderFrameViewSetPublicationSnapshot(
    ulong Revision,
    bool HasViewSet,
    RenderFrameViewSet ViewSet);
