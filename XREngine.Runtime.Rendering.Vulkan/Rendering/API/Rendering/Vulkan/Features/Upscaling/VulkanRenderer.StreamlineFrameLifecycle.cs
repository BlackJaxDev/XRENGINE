using System;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private void MarkDlssFrameGenerationPclMarker(NvidiaDlssManager.Native.StreamlinePclMarker marker)
        {
            if (!_streamlineFrameGenerationSwapchainActive)
                return;

            uint frameIndex = unchecked((uint)Math.Min(uint.MaxValue, _vkDebugFrameCounter));
            if (NvidiaDlssManager.Native.TryMarkFrameGenerationPclMarker(this, marker, frameIndex, out string failureReason))
                return;

            string message = $"NVIDIA DLSS frame generation failed to set Streamline PCL marker {marker}: {failureReason}";
            Debug.RenderingError(message);
            throw new InvalidOperationException(message);
        }
    }
}
