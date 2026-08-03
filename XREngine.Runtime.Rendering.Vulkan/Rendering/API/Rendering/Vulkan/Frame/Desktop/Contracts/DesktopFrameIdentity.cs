namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Captures the immutable identity assigned when one desktop Vulkan frame
/// attempt enters the renderer.
/// </summary>
internal readonly record struct DesktopFrameIdentity
{
    internal DesktopFrameIdentity(
        ulong frameNumber,
        int frameSlot,
        long startTimestamp,
        long activityPublicationToken)
    {
        FrameNumber = frameNumber;
        FrameSlot = frameSlot;
        StartTimestamp = startTimestamp;
        ActivityPublicationToken = activityPublicationToken;
    }

    internal ulong FrameNumber { get; }
    internal int FrameSlot { get; }
    internal long StartTimestamp { get; }
    internal long ActivityPublicationToken { get; }
}
