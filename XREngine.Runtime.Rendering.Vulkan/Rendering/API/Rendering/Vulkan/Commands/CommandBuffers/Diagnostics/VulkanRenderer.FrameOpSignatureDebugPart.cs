namespace XREngine.Rendering.Vulkan;

internal readonly record struct FrameOpSignatureDebugPart(
    int OpIndex,
    string OpType,
    string Component,
    ulong Signature,
    string Detail);
