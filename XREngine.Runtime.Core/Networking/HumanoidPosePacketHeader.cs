namespace XREngine.Networking;

/// <summary>
/// Fixed fields of a binary humanoid pose packet. The variable parts (source client id, entity
/// ids, and the quantized avatar payload) follow the fixed header on the wire.
/// </summary>
public struct HumanoidPosePacketHeader
{
    public Guid SessionId;
    public long ServerTickId;
    public long BaselineTickId;
    public double ServerTimestampUtc;
    public uint FrameSequence;
    public ushort BaselineSequence;
    public HumanoidPosePacketKind Kind;
    public NetworkAuthorityMode AuthorityMode;
    public NetworkReplicationChannel Channel;
    public ushort AvatarCount;
}
