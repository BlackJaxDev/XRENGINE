using Silk.NET.Vulkan;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact prepared vertex-buffer source selected for one visibility draw. Static
/// geometry uses the shared canonical resident cache; GPU deformation uses a
/// generation-checked external native range.
/// </summary>
internal readonly record struct VulkanVisibilityPreparedVertexSource(
    VulkanFrameDataSlice CanonicalSlice,
    VulkanNativeBufferRange NativeRange,
    uint ElementStride)
{
    internal bool UsesNativeRange => NativeRange.IsValid;

    internal bool IsValid
        => ElementStride != 0u &&
           (CanonicalSlice.IsValid ^ NativeRange.IsValid);

    internal VkBufferHandle Buffer
        => UsesNativeRange ? NativeRange.Buffer : CanonicalSlice.Buffer;

    internal ulong Offset
        => UsesNativeRange ? NativeRange.Offset : CanonicalSlice.Offset;

    internal ulong Length
        => UsesNativeRange ? NativeRange.Length : CanonicalSlice.Length;

    internal ulong Generation
        => UsesNativeRange ? NativeRange.NativeGeneration : CanonicalSlice.Generation;

    internal bool Matches(VulkanVisibilityPreparedVertexSource other)
        => ElementStride == other.ElementStride &&
           CanonicalSlice.Matches(other.CanonicalSlice) &&
           NativeRange.Matches(other.NativeRange);

    internal bool TryValidate(
        VulkanResourceRuntime resources,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (!IsValid)
        {
            reason = "the prepared visibility vertex source is incomplete";
            return false;
        }

        if (!UsesNativeRange)
        {
            reason = "Ready";
            return true;
        }

        VulkanNativeBufferRange nativeRange = NativeRange;
        return resources.TryValidateNativeBufferRange(
            in nativeRange,
            out reason);
    }
}
