using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private static readonly HashSet<string> SceneDatabaseDeviceAddressBuffers = new(StringComparer.Ordinal)
    {
        "DrawMetadataBuffer",
        "TransformBuffer",
        "PrevTransformBuffer",
        "BoundsBuffer",
        "SkinningPaletteBuffer",
        "MaterialStateBuffer",
        "MaterialTable",
        "MaterialTextureHandleTable",
    };

    internal bool ShouldEnableDeviceAddressForSceneDatabaseBuffer(XRDataBuffer buffer)
    {
        if (!SupportsBufferDeviceAddress)
            return false;

        if (buffer.Target != EBufferTarget.ShaderStorageBuffer)
            return false;

        return SceneDatabaseDeviceAddressBuffers.Contains(buffer.AttributeName);
    }
}
