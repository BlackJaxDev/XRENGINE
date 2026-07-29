using System.Numerics;

namespace XREngine.Rendering.Vulkan;

internal struct VulkanImGuiCommandSnapshot
{
    public Vector4 ClipRect;
    public nint TextureId;
    public uint ElemCount;
    public uint IdxOffset;
    public uint VtxOffset;
    public bool HasUserCallback;
}
