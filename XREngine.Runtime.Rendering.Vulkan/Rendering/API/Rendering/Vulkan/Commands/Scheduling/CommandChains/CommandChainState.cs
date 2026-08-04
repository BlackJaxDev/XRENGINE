namespace XREngine.Rendering.Vulkan;

internal enum CommandChainState
{
    Unrecorded,
    Reused,
    FrameDataRefreshed,
    Recorded,
    NotReady,
}
