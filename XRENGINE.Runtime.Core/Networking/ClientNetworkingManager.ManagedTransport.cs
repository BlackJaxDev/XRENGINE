using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using XREngine.Input;
using XREngine.Networking;

namespace XREngine;

public partial class ClientNetworkingManager
{
    private static readonly TimeSpan ManagedHandshakeRetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ManagedHandshakeTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ManagedServerSilenceTimeout = TimeSpan.FromSeconds(12);
    private readonly object _managedTransportLock = new();
    private ManagedClientHandshakeState _managedHandshakeState;
    private ManagedAdmissionIdentity _managedIdentity;
    private byte[]? _managedRootKey;
    private byte[]? _managedJoinSendKey;
    private byte[]? _managedJoinReceiveKey;
    private byte[]? _managedHello;
    private byte[]? _managedClientNonce;
    private byte[]? _managedServerNonce;
    private byte[]? _managedCookie;
    private Guid _managedAssociationId;
    private long _managedChallengeExpiryUnixSeconds;
    private DateTime _managedHandshakeStartedUtc;
    private DateTime _managedLastHandshakeSendUtc;
    private DateTime _managedLastAuthenticatedServerUtc;
    private ManagedUdpAssociation? _managedAssociation;
    private string? _managedTransportFailure;

    /// <summary>Credential-free reason a managed handshake failed; null before failure.</summary>
    public string? ManagedTransportFailure { get { lock (_managedTransportLock) return _managedTransportFailure; } }

    private bool IsManagedTransportRequested
        => !string.IsNullOrWhiteSpace(AdmissionSecret)
            || !string.IsNullOrWhiteSpace(ReservationId)
            || WorkerGeneration.HasValue
            || CredentialEpoch != 0;

    private bool IsManagedTransportEstablished
    {
        get { lock (_managedTransportLock) return _managedHandshakeState == ManagedClientHandshakeState.Established; }
    }

    internal bool StartManagedTransportHandshake()
    {
        if (!IsManagedTransportRequested)
            return false;

        lock (_managedTransportLock)
        {
            DisposeManagedTransport_NoLock();
            _managedTransportFailure = null;
            if (!TryCreateManagedIdentity(out ManagedAdmissionIdentity identity, out string? error))
            {
                FailManagedHandshake_NoLock(error!);
                return true;
            }

            try
            {
                _managedIdentity = identity;
                _managedRootKey = ManagedUdpAuthentication.DeriveRootKey(AdmissionSecret!, identity);
                _managedJoinSendKey = ManagedUdpAuthentication.DeriveJoinKey(_managedRootKey, ManagedUdpDirection.ClientToServer);
                _managedJoinReceiveKey = ManagedUdpAuthentication.DeriveJoinKey(_managedRootKey, ManagedUdpDirection.ServerToClient);
                _managedClientNonce = RandomNumberGenerator.GetBytes(32);
                _managedHello = ManagedUdpHandshakeCodec.WriteHello(CreateManagedHelloRequest(identity), _managedClientNonce);
                _managedHandshakeStartedUtc = DateTime.UtcNow;
                _managedHandshakeState = ManagedClientHandshakeState.Hello;

                // The secret is never put in a handshake or inner join request. Clear the
                // public launch field once key material has been derived.
                AdmissionSecret = null;
                SessionToken = null;
                SendManagedHello_NoLock();
            }
            catch (Exception ex)
            {
                FailManagedHandshake_NoLock($"Unable to start managed UDP authentication: {ex.Message}");
            }
            return true;
        }
    }

    internal void TickManagedTransportHandshake()
    {
        string? terminalReason = null;
        lock (_managedTransportLock)
        {
            if (_managedHandshakeState is ManagedClientHandshakeState.None or ManagedClientHandshakeState.Failed)
                return;
            if (_managedHandshakeState == ManagedClientHandshakeState.Established)
            {
                if (DateTime.UtcNow - _managedLastAuthenticatedServerUtc <= ManagedServerSilenceTimeout)
                    return;
                terminalReason = "Authenticated server traffic timed out.";
                FailManagedHandshake_NoLock(terminalReason);
            }
            else if (DateTime.UtcNow - _managedHandshakeStartedUtc > ManagedHandshakeTimeout)
            {
                FailManagedHandshake_NoLock("Timed out waiting for managed UDP authentication.");
                return;
            }
            if (terminalReason is null && DateTime.UtcNow - _managedLastHandshakeSendUtc < ManagedHandshakeRetryInterval)
                return;

            if (terminalReason is null && _managedHandshakeState == ManagedClientHandshakeState.Hello)
                SendManagedHello_NoLock();
            else if (terminalReason is null && _managedHandshakeState == ManagedClientHandshakeState.Commit)
                SendManagedCommit_NoLock();
        }
        if (terminalReason is not null)
            QueueManagedTerminalCleanup(terminalReason);
    }

    internal void DisposeManagedTransport()
    {
        lock (_managedTransportLock)
            DisposeManagedTransport_NoLock();
    }

    protected override bool RequiresManagedUdpTransport => IsManagedTransportRequested;

    protected override bool TryUnwrapManagedDatagram(ReadOnlyMemory<byte> datagram, IPEndPoint sender, out ReadOnlyMemory<byte> innerDatagram)
    {
        innerDatagram = default;
        if (ServerIP is null || !ServerIP.Equals(sender)
            || !ManagedUdpEnvelope.TryRead(datagram.Span, out ManagedUdpEnvelopeHeader header, out ReadOnlySpan<byte> payload, out _)
            || header.Direction != ManagedUdpDirection.ServerToClient)
        {
            RecordBadSourceRejection();
            return false;
        }

        lock (_managedTransportLock)
        {
            if (_managedHandshakeState == ManagedClientHandshakeState.Hello && header.Kind == ManagedUdpMessageKind.Challenge)
                return TryHandleManagedChallenge_NoLock(datagram.Span, header, payload);

            if (_managedHandshakeState == ManagedClientHandshakeState.Commit && header.Kind == ManagedUdpMessageKind.Accept)
                return TryHandleManagedAccept_NoLock(datagram.Span, header, payload, sender, out innerDatagram);

            ManagedUdpAssociation? association = _managedAssociation;
            if (_managedHandshakeState != ManagedClientHandshakeState.Established
                || association is null || association.Closed
                || header.Kind is not (ManagedUdpMessageKind.Data or ManagedUdpMessageKind.Close)
                || header.AssociationId != association.AssociationId
                || header.SessionId != association.Identity.SessionId
                || header.Generation != association.Identity.Generation
                || header.CredentialEpoch != association.Identity.CredentialEpoch
                || header.Counter == 0)
            {
                RecordUnauthorizedRejection();
                return false;
            }
            if (!association.ReceiveReplay.CanAccept(header.Counter))
            {
                RecordReplayRejection();
                return false;
            }
            if (!ManagedUdpEnvelope.Verify(datagram.Span, association.ReceiveKey))
            {
                RecordBadMacRejection();
                return false;
            }
            string? closeReason = null;
            if (header.Kind == ManagedUdpMessageKind.Close)
            {
                if (payload.Length > 256 || !TryReadManagedCloseReason(payload, out closeReason))
                {
                    RecordUnauthorizedRejection();
                    return false;
                }
            }
            else if (!IsInnerFrk(payload))
            {
                RecordUnauthorizedRejection();
                return false;
            }
            if (!association.ReceiveReplay.Commit(header.Counter))
                return false;
            _managedLastAuthenticatedServerUtc = DateTime.UtcNow;
            if (header.Kind == ManagedUdpMessageKind.Close)
            {
                FailManagedHandshake_NoLock(closeReason!);
                QueueManagedTerminalCleanup(closeReason!);
                return false;
            }

            innerDatagram = payload.ToArray();
            return true;
        }
    }

    protected override byte[]? ProtectOutboundDatagram(byte[] innerDatagram, IPEndPoint target)
    {
        lock (_managedTransportLock)
        {
            if (!IsManagedTransportRequested)
                return innerDatagram;
            ManagedUdpAssociation? association = _managedAssociation;
            if (_managedHandshakeState != ManagedClientHandshakeState.Established || association is null || association.Closed
                || ServerIP is null || !ServerIP.Equals(target) || !association.TryNextSendCounter(out ulong counter))
            {
                return null;
            }

            return ManagedUdpEnvelope.Create(new ManagedUdpEnvelopeHeader(
                ManagedUdpMessageKind.Data,
                ManagedUdpDirection.ClientToServer,
                association.Identity.SessionId,
                association.Identity.Generation,
                association.AssociationId,
                association.Identity.CredentialEpoch,
                counter), innerDatagram, association.SendKey);
        }
    }

    private bool TryHandleManagedChallenge_NoLock(ReadOnlySpan<byte> datagram, ManagedUdpEnvelopeHeader header, ReadOnlySpan<byte> payload)
    {
        if (_managedJoinReceiveKey is null || _managedHello is null || _managedClientNonce is null
            || header.SessionId != _managedIdentity.SessionId || header.Generation != _managedIdentity.Generation
            || header.CredentialEpoch != _managedIdentity.CredentialEpoch || header.Counter != 0
            || !ManagedUdpEnvelope.Verify(datagram, _managedJoinReceiveKey)
            || !ManagedUdpHandshakeCodec.TryReadChallenge(payload, out byte[] clientNonce, out byte[] serverNonce, out Guid associationId, out byte[] helloHash, out long expiry, out byte[] cookie)
            || header.AssociationId != associationId
            || !CryptographicOperations.FixedTimeEquals(clientNonce, _managedClientNonce)
            || !CryptographicOperations.FixedTimeEquals(helloHash, SHA256.HashData(_managedHello))
            || associationId == Guid.Empty || expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            RecordBadMacRejection();
            return false;
        }

        _managedServerNonce = serverNonce;
        _managedAssociationId = associationId;
        _managedChallengeExpiryUnixSeconds = expiry;
        _managedCookie = cookie;
        _managedHandshakeState = ManagedClientHandshakeState.Commit;
        SendManagedCommit_NoLock();
        return false;
    }

    private bool TryHandleManagedAccept_NoLock(ReadOnlySpan<byte> datagram, ManagedUdpEnvelopeHeader header, ReadOnlySpan<byte> payload, IPEndPoint sender, out ReadOnlyMemory<byte> innerDatagram)
    {
        innerDatagram = default;
        if (_managedRootKey is null || _managedHello is null || _managedClientNonce is null || _managedServerNonce is null
            || header.SessionId != _managedIdentity.SessionId || header.Generation != _managedIdentity.Generation
            || header.CredentialEpoch != _managedIdentity.CredentialEpoch || header.AssociationId != _managedAssociationId || header.Counter != 1
            || !IsInnerFrk(payload))
        {
            RecordUnauthorizedRejection();
            return false;
        }

        byte[] transcriptHash = ManagedUdpAuthentication.DeriveTranscriptHash(_managedIdentity, _managedClientNonce, _managedServerNonce,
            _managedAssociationId, SHA256.HashData(_managedHello), _managedChallengeExpiryUnixSeconds, _managedCookie ?? []);
        byte[] sendKey = ManagedUdpAuthentication.DeriveTrafficKey(_managedRootKey, transcriptHash, ManagedUdpDirection.ClientToServer);
        byte[] receiveKey = ManagedUdpAuthentication.DeriveTrafficKey(_managedRootKey, transcriptHash, ManagedUdpDirection.ServerToClient);
        CryptographicOperations.ZeroMemory(transcriptHash);
        if (!ManagedUdpEnvelope.Verify(datagram, receiveKey))
        {
            CryptographicOperations.ZeroMemory(sendKey);
            CryptographicOperations.ZeroMemory(receiveKey);
            RecordBadMacRejection();
            return false;
        }

        ManagedUdpAssociation association = new(_managedAssociationId, _managedIdentity, sender, sendKey, receiveKey);
        if (!association.ReceiveReplay.Commit(header.Counter))
        {
            association.Dispose();
            return false;
        }
        _managedAssociation?.Dispose();
        _managedAssociation = association;
        _managedHandshakeState = ManagedClientHandshakeState.Established;
        _managedLastAuthenticatedServerUtc = DateTime.UtcNow;
        ZeroHandshakeKeys_NoLock();
        innerDatagram = payload.ToArray();
        return true;
    }

    private void SendManagedHello_NoLock()
        => SendManagedHandshake_NoLock(ManagedUdpMessageKind.Hello, Guid.Empty, _managedHello, _managedJoinSendKey);

    private void SendManagedCommit_NoLock()
    {
        if (_managedHello is null || _managedServerNonce is null || _managedCookie is null || _managedJoinSendKey is null)
            return;
        if (_managedChallengeExpiryUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            _managedHandshakeState = ManagedClientHandshakeState.Hello;
            SendManagedHello_NoLock();
            return;
        }
        byte[] commit = ManagedUdpHandshakeCodec.WriteCommit(_managedHello, _managedServerNonce, _managedAssociationId, _managedChallengeExpiryUnixSeconds, _managedCookie);
        try { SendManagedHandshake_NoLock(ManagedUdpMessageKind.Commit, _managedAssociationId, commit, _managedJoinSendKey); }
        finally { CryptographicOperations.ZeroMemory(commit); }
    }

    private void SendManagedHandshake_NoLock(ManagedUdpMessageKind kind, Guid associationId, byte[]? payload, byte[]? key)
    {
        if (payload is null || key is null || ServerIP is null || UdpSender is null)
            return;
        byte[] envelope = ManagedUdpEnvelope.Create(new ManagedUdpEnvelopeHeader(kind, ManagedUdpDirection.ClientToServer,
            _managedIdentity.SessionId, _managedIdentity.Generation, associationId, _managedIdentity.CredentialEpoch, 0), payload, key);
        try { UdpSender.Send(envelope, envelope.Length, ServerIP); }
        catch (SocketException) { }
        finally { CryptographicOperations.ZeroMemory(envelope); }
        _managedLastHandshakeSendUtc = DateTime.UtcNow;
    }

    private bool TryCreateManagedIdentity(out ManagedAdmissionIdentity identity, out string? error)
    {
        identity = default;
        error = null;
        if (string.IsNullOrWhiteSpace(AdmissionSecret) || SessionId is not Guid sessionId || sessionId == Guid.Empty
            || WorkerGeneration is not Guid generation || generation == Guid.Empty || string.IsNullOrWhiteSpace(ReservationId)
            || string.IsNullOrWhiteSpace(AccountId) || CredentialEpoch < 0)
        {
            error = "Managed UDP requires a session, worker generation, account, reservation, credential epoch, and admission secret.";
            return false;
        }
        identity = new ManagedAdmissionIdentity(sessionId, generation, ReservationId, EffectiveClientId, AccountId, CredentialEpoch, ResumeRequested ? 1 : 0);
        return true;
    }

    private PlayerJoinRequest CreateManagedHelloRequest(ManagedAdmissionIdentity identity)
        => new()
        {
            ClientId = identity.ClientId,
            DisplayName = Environment.UserName,
            BuildVersion = CurrentProtocolVersion,
            WorldName = ResolvePrimaryWorldInstance()?.TargetWorld?.Name,
            ClientWorldAsset = _localWorldAsset ??= CreateLocalWorldAsset(),
            SessionId = identity.SessionId,
            AccountId = identity.AccountId,
            ReservationId = identity.ReservationId,
            WorkerGeneration = identity.Generation,
            ResumeRequested = ResumeRequested,
            CredentialEpoch = identity.CredentialEpoch,
        };

    private void FailManagedHandshake_NoLock(string reason)
    {
        Debug.NetworkingWarning("[Client] Managed UDP authentication failed: {0}", reason);
        DisposeManagedTransport_NoLock();
        _managedHandshakeState = ManagedClientHandshakeState.Failed;
        _managedTransportFailure = reason;
    }

    private void DisposeManagedTransport_NoLock()
    {
        _managedAssociation?.Dispose();
        _managedAssociation = null;
        ZeroHandshakeKeys_NoLock();
        _managedHello = null;
        _managedClientNonce = null;
        _managedServerNonce = null;
        _managedCookie = null;
        _managedAssociationId = Guid.Empty;
        _managedChallengeExpiryUnixSeconds = 0;
        _managedLastAuthenticatedServerUtc = default;
        _managedHandshakeState = ManagedClientHandshakeState.None;
    }

    private void ZeroHandshakeKeys_NoLock()
    {
        Zero(_managedRootKey); _managedRootKey = null;
        Zero(_managedJoinSendKey); _managedJoinSendKey = null;
        Zero(_managedJoinReceiveKey); _managedJoinReceiveKey = null;
        Zero(_managedHello); _managedHello = null;
        Zero(_managedClientNonce); _managedClientNonce = null;
        Zero(_managedServerNonce); _managedServerNonce = null;
        Zero(_managedCookie); _managedCookie = null;
    }

    private static void Zero(byte[]? value)
    {
        if (value is not null)
            CryptographicOperations.ZeroMemory(value);
    }

    private static bool TryReadManagedCloseReason(ReadOnlySpan<byte> payload, out string reason)
    {
        reason = string.Empty;
        try
        {
            reason = new UTF8Encoding(false, true).GetString(payload);
            return !string.IsNullOrWhiteSpace(reason);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private void QueueManagedTerminalCleanup(string reason)
        => RuntimeNetworkingHostServices.Current.EnqueueSimulation(() => HandleManagedTerminalCleanup(reason));

    private void HandleManagedTerminalCleanup(string reason)
    {
        Debug.NetworkingWarning("[Client] Managed UDP session ended: {0}", reason);
        foreach (IPawnController? player in RuntimeNetworkingHostServices.Current.LocalPlayers)
            if (player?.PlayerInfo is { } playerInfo)
            {
                playerInfo.ServerIndex = -1;
                playerInfo.NetworkEntityId = NetworkEntityId.Empty;
                playerInfo.AuthorityLease = null;
            }
        _localServerIndices.Clear();
        ClearRemotePlayers();
        _assignmentReceived = false;
        _primaryAssignedServerPlayerIndex = -1;
        _primaryAssignedEntityId = NetworkEntityId.Empty;
        _activeSessionId = Guid.Empty;
        ClearPredictedInputs();
        ResetReplicationSynchronization();
        if (ServerIP is { } serverEndpoint)
            UnregisterUdpPeer(serverEndpoint);
    }
    private static bool IsInnerFrk(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 3 && bytes[0] == 0x46 && bytes[1] == 0x52 && bytes[2] == 0x4B;

    private enum ManagedClientHandshakeState : byte { None, Hello, Commit, Established, Failed }
}
