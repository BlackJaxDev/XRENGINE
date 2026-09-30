using System.Net;
using System.Security.Cryptography;

namespace XREngine.Networking;

/// <summary>Established, endpoint-bound managed UDP association. Access is serialized by its owner.</summary>
public sealed class ManagedUdpAssociation : IDisposable
{
    public ManagedUdpAssociation(Guid associationId, ManagedAdmissionIdentity identity, IPEndPoint endpoint, byte[] sendKey, byte[] receiveKey)
    {
        AssociationId = associationId;
        Identity = identity;
        Endpoint = endpoint;
        SendKey = sendKey;
        ReceiveKey = receiveKey;
    }

    public Guid AssociationId { get; }
    public ManagedAdmissionIdentity Identity { get; }
    public IPEndPoint Endpoint { get; }
    public byte[] SendKey { get; }
    public byte[] ReceiveKey { get; }
    public ulong NextSendCounter { get; private set; }
    public ManagedUdpReplayWindow ReceiveReplay { get; } = new();
    public bool Closed { get; private set; }

    public bool TryNextSendCounter(out ulong counter)
    {
        counter = 0;
        if (Closed || NextSendCounter == ulong.MaxValue)
            return false;
        counter = ++NextSendCounter;
        return true;
    }

    public void Dispose()
    {
        if (Closed)
            return;
        Closed = true;
        CryptographicOperations.ZeroMemory(SendKey);
        CryptographicOperations.ZeroMemory(ReceiveKey);
    }
}
