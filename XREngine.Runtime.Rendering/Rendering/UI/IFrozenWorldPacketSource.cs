namespace XREngine.Rendering.UI;

/// <summary>Provides a retained packet to one world collect generation.</summary>
internal interface IFrozenWorldPacketSource
{
    object PacketKey { get; }
    bool IsInWorld(IRuntimeWorldContext expectedWorld);

    /// <summary>Returns false when an eligible source has no complete packet.</summary>
    bool TryAcquireCollectPacket(IRuntimeWorldContext expectedWorld,
        out UIPublication? packet);
}
