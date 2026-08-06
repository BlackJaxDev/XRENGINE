namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable logical resource access captured while lowering one operation.</summary>
internal readonly record struct FrameOpResourceUse(
    ulong ResourceId,
    ulong Version,
    EFrameOpResourceAccess Access);
