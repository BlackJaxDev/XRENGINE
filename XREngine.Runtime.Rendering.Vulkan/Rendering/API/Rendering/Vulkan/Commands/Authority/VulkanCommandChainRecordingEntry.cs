using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One frozen command-chain queue entry. The queue itself stays compact and
/// contains only native execution state plus indices into the frame-owned
/// prepared and cold streams; managed chain authority never crosses workers.
/// </summary>
internal struct VulkanCommandChainRecordingEntry
{
    private const uint NeedsRecordingMask = 1u;

    public int PreparedChainIndex;
    public int ColdDataIndex;
    public CommandBuffer SecondaryBuffer;
    public int WorkerIndex;
    public uint Flags;

    public bool NeedsRecording
    {
        readonly get => (Flags & NeedsRecordingMask) != 0;
        set => Flags = value ? Flags | NeedsRecordingMask : Flags & ~NeedsRecordingMask;
    }

    public static int SizeInBytes => Unsafe.SizeOf<VulkanCommandChainRecordingEntry>();
}
