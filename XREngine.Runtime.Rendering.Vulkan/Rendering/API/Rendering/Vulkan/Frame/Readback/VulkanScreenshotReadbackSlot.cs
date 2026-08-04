using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reusable command/fence state for one bounded asynchronous screenshot readback.
/// </summary>
internal sealed class VulkanScreenshotReadbackSlot
{
    public int State;
    public int CallbackDelivered;
    public int ReservationReleased;
    public int WatchdogWarningLogged;
    public Buffer StagingBuffer;
    public DeviceMemory StagingMemory;
    public ulong RawByteCount;
    public CommandPool CommandPool;
    public CommandBuffer CommandBuffer;
    public Fence Fence;
    public Image ResolveImage;
    public VulkanMemoryAllocation ResolveAllocation;
    public Format ResolveFormat;
    public uint ResolveWidth;
    public uint ResolveHeight;
    public ulong ResolveByteCount;
    public int Width;
    public int Height;
    public Format SourceFormat;
    public bool WithTransparency;
    public bool UsedMultisampleResolve;
    public long SubmittedTimestamp;
    public long FenceSignaledTimestamp;
    public DateTimeOffset SubmittedAtUtc;
    public Action<ScreenshotReadbackResult>? Callback;
}
