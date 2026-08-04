namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private sealed class CameraPoseReuseState
        {
            public ulong RawPoseGeneration;
            public ulong ReplayGeneration = 1;
            public ulong LastObservedFrame;
            public bool SettleInvalidationPending;
        }

    }
}
