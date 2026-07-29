namespace XREngine.Rendering.Vulkan;

internal readonly record struct DrawPacket(
    int OpIndex,
    int RendererIdentity,
    int MeshIdentity,
    int MaterialIdentity,
    int ProgramIdentity,
    uint InstanceCount,
    bool Transparent,
    ulong StructuralSignature,
    ulong FrameDataSignature);
