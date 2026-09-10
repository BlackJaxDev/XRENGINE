namespace XREngine.Rendering.Vulkan;

/// <summary>Frozen GPU address and exact ownership of one native shading parameter allocation.</summary>
internal readonly record struct VulkanAdvancedNativeShadingRootBinding(
    VulkanFrameDataSlice Slice,
    ulong ParameterAddress,
    ulong NativeBufferGeneration,
    ulong SlotResetEpoch)
{
    internal bool IsValid => Slice.IsValid && Slice.Length == 64 &&
        ParameterAddress != 0 && (ParameterAddress & 15UL) == 0 &&
        NativeBufferGeneration != 0 && SlotResetEpoch != 0;
}
