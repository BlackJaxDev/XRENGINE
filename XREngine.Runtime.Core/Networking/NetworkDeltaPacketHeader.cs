namespace XREngine.Networking;

/// <summary>Fixed fields of a binary replication-delta packet. Entity ids and the channel payload follow on the wire.</summary>
public struct NetworkDeltaPacketHeader
{
    public Guid SessionId;
    public long ServerTickId;
    public long BaselineTickId;
    public double ServerTimestampUtc;
    public uint DeltaSequence;
    public NetworkReplicationChannel Channel;
}
