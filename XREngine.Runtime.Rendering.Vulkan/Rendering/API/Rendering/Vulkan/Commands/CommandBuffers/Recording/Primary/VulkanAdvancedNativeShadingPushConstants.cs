using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>64-byte push ABI mirrored by AdvancedShadingInterface.glslinc.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly record struct VulkanAdvancedNativeShadingPushConstants(
    uint Width, uint Height, uint TilesX, uint TilesY,
    uint ViewIndex, uint ViewCount, uint KernelIndex, uint Flags,
    uint DepthSlices, uint MaxLightIndices, uint LightCount, uint MaxKernelTiles,
    Vector4 BackgroundColor);
