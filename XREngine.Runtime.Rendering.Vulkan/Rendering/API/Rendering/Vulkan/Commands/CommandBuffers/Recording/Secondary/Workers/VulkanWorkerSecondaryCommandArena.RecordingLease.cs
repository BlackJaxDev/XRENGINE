using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanWorkerSecondaryCommandArena
{
    internal readonly ref struct RecordingLease
    {
        private readonly VulkanWorkerSecondaryCommandArena? _arena;

        internal RecordingLease(VulkanWorkerSecondaryCommandArena? arena)
        {
            _arena = arena;
            _arena?.AcquireRecording();
        }

        public void Dispose()
            => _arena?.ReleaseRecording();
    }
}

