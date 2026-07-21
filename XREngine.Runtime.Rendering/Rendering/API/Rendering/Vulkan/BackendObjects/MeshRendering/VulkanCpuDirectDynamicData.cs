using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stable shader-facing dynamic record for Vulkan CPU-direct draws. Value-only changes update
/// bytes in the owning frame slot and do not change descriptor or command-buffer topology.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct VulkanCpuDirectDynamicData(
    Matrix4x4 ModelMatrix,
    Matrix4x4 PreviousModelMatrix,
    uint MaterialId,
    uint SkinningId,
    uint BlendshapeId,
    uint EditorId,
    uint Flags,
    uint PassMask,
    uint ViewId,
    uint TransformId)
{
    public static int Stride => Marshal.SizeOf<VulkanCpuDirectDynamicData>();
}
