namespace XREngine.Rendering.Vulkan;

internal sealed class CameraPoseReuseState
{
    public ulong RawPoseGeneration;
    public ulong ReplayGeneration = 1;
    public ulong LastObservedFrame;
    public bool SettleInvalidationPending;
}
