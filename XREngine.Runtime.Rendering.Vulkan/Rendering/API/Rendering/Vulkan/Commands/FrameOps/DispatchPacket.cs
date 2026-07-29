namespace XREngine.Rendering.Vulkan;

internal readonly record struct DispatchPacket(
    int OpIndex,
    int ProgramIdentity,
    uint GroupsX,
    uint GroupsY,
    uint GroupsZ,
    ulong StructuralSignature,
    ulong FrameDataSignature);
