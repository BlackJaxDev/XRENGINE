namespace XREngine.Networking;

/// <summary>Fixed-width authenticated wrapper around an inner realtime datagram.</summary>
public readonly record struct ManagedUdpEnvelopeHeader(
    ManagedUdpMessageKind Kind,
    ManagedUdpDirection Direction,
    Guid SessionId,
    Guid Generation,
    Guid AssociationId,
    long CredentialEpoch,
    ulong Counter)
{
    public const int UnsignedHeaderLength = 80;
    public const int TagLength = 32;
    public const int TotalHeaderLength = UnsignedHeaderLength + TagLength;
    public const byte Version = 1;
}

