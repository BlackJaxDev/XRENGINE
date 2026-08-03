using System.Threading;

namespace XREngine.Rendering.Vulkan;

internal sealed class CommandChainPacketOwner(ulong frameId) : IDisposable
{
    private int _retired;

    public ulong FrameId { get; } = frameId;
    public bool IsRetired => Volatile.Read(ref _retired) != 0;

    public void ThrowIfRetired()
    {
        if (IsRetired)
            throw new ObjectDisposedException(
                nameof(CommandChainPacketOwner),
                $"Command-chain packet memory for frame {FrameId} was used after retirement.");
    }

    public void Dispose()
        => Volatile.Write(ref _retired, 1);
}
