using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using XREngine.Networking;

namespace XREngine;

public partial class ServerNetworkingManager
{
    private static readonly TimeSpan ManagedChallengeLifetime = TimeSpan.FromSeconds(5);
    private readonly object _managedAssociationLock = new();
    private readonly Dictionary<Guid, ManagedUdpAssociation> _managedAssociations = [];
    private readonly Dictionary<Guid, ManagedUdpAssociation> _provisionalManagedAssociations = [];
    private readonly Dictionary<Guid, PendingManagedAdmission> _pendingManagedAdmissions = [];
    // Retains consumed credential identities until their grant expires. A later Commit
    // can retransmit only the exact active association's cached Accept.
    private readonly Dictionary<ManagedAdmissionIdentity, AcceptedAssociationBinding> _acceptedManagedIdentities = [];
    private readonly HashSet<Guid> _managedAcceptPending = [];
    private readonly Dictionary<Guid, byte[]> _managedAcceptEnvelopes = [];
    private readonly byte[] _managedEndpointCookieKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>Managed workers fail closed and accept only authenticated envelopes.</summary>
    public bool RequireManagedUdpTransport { get; set; }
    protected override bool RequiresManagedUdpTransport => RequireManagedUdpTransport;

    protected override bool TryUnwrapManagedDatagram(ReadOnlyMemory<byte> datagram, IPEndPoint sender, out ReadOnlyMemory<byte> innerDatagram)
    {
        innerDatagram = default;
        if (!ManagedUdpEnvelope.TryRead(datagram.Span, out ManagedUdpEnvelopeHeader header, out ReadOnlySpan<byte> payload, out _))
            return RejectManagedEnvelope();

        return header.Kind switch
        {
            ManagedUdpMessageKind.Hello => HandleManagedHello(datagram.Span, header, payload, sender),
            ManagedUdpMessageKind.Commit => HandleManagedCommit(datagram.Span, header, payload, sender),
            ManagedUdpMessageKind.Data => TryUnwrapManagedData(datagram.Span, header, payload, sender, out innerDatagram),
            _ => RejectManagedEnvelope(),
        };
    }

    private bool HandleManagedHello(ReadOnlySpan<byte> datagram, ManagedUdpEnvelopeHeader header, ReadOnlySpan<byte> payload, IPEndPoint sender)
    {
        if (header.Direction != ManagedUdpDirection.ClientToServer || header.Counter != 0 || header.AssociationId != Guid.Empty
            || !ManagedUdpHandshakeCodec.TryReadHello(payload, out PlayerJoinRequest request, out byte[] clientNonce)
            || !TryResolveManagedVerifier(request, header, out ManagedAdmissionVerifier verifier))
            return RejectManagedEnvelope();

        byte[] joinKey = ManagedUdpAuthentication.DeriveJoinKey(verifier.RootKey, ManagedUdpDirection.ClientToServer);
        if (!ManagedUdpEnvelope.Verify(datagram, joinKey))
        {
            CryptographicOperations.ZeroMemory(joinKey);
            CryptographicOperations.ZeroMemory(verifier.RootKey);
            RecordBadMacRejection();
            return false;
        }

        byte[] serverNonce = RandomNumberGenerator.GetBytes(32);
        byte[] requestHash = SHA256.HashData(payload);
        byte[] cookie = [];
        byte[] responseKey = [];
        try
        {
            Guid associationId = Guid.NewGuid();
            long expiry = DateTimeOffset.UtcNow.Add(ManagedChallengeLifetime).ToUnixTimeSeconds();
            cookie = CreateEndpointCookie(sender, verifier.Identity, clientNonce, serverNonce, associationId, requestHash, expiry);
            responseKey = ManagedUdpAuthentication.DeriveJoinKey(verifier.RootKey, ManagedUdpDirection.ServerToClient);
            byte[] challenge = ManagedUdpHandshakeCodec.WriteChallenge(clientNonce, serverNonce, associationId, requestHash, expiry, cookie);
            SendManagedEnvelope(sender, new ManagedUdpEnvelopeHeader(ManagedUdpMessageKind.Challenge, ManagedUdpDirection.ServerToClient,
                verifier.Identity.SessionId, verifier.Identity.Generation, associationId, verifier.Identity.CredentialEpoch, 0), challenge, responseKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(joinKey);
            CryptographicOperations.ZeroMemory(responseKey);
            CryptographicOperations.ZeroMemory(verifier.RootKey);
            CryptographicOperations.ZeroMemory(clientNonce);
            CryptographicOperations.ZeroMemory(serverNonce);
            CryptographicOperations.ZeroMemory(requestHash);
            CryptographicOperations.ZeroMemory(cookie);
        }
        return false;
    }

    private bool HandleManagedCommit(ReadOnlySpan<byte> datagram, ManagedUdpEnvelopeHeader header, ReadOnlySpan<byte> payload, IPEndPoint sender)
    {
        if (header.Direction != ManagedUdpDirection.ClientToServer || header.Counter != 0 || header.AssociationId == Guid.Empty
            || !ManagedUdpHandshakeCodec.TryReadCommit(payload, out byte[] hello, out byte[] serverNonce, out Guid associationId, out long expiry, out byte[] cookie)
            || associationId != header.AssociationId
            || !ManagedUdpHandshakeCodec.TryReadHello(hello, out PlayerJoinRequest request, out byte[] clientNonce)
            || !TryResolveManagedVerifier(request, header, out ManagedAdmissionVerifier verifier))
            return RejectManagedEnvelope();

        byte[] joinKey = ManagedUdpAuthentication.DeriveJoinKey(verifier.RootKey, ManagedUdpDirection.ClientToServer);
        byte[] requestHash = SHA256.HashData(hello);
        try
        {
            byte[] expectedCookie = CreateEndpointCookie(sender, verifier.Identity, clientNonce, serverNonce, associationId, requestHash, expiry);
            try
            {
                if (!ManagedUdpEnvelope.Verify(datagram, joinKey)
                    || expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    || !CryptographicOperations.FixedTimeEquals(cookie, expectedCookie))
                {
                    RecordBadMacRejection();
                    return false;
                }

                byte[] transcriptHash = ManagedUdpAuthentication.DeriveTranscriptHash(verifier.Identity, clientNonce, serverNonce, associationId, requestHash, expiry, cookie);
                try { return QueueManagedAdmission(request, verifier, sender, associationId, transcriptHash, datagram); }
                finally { CryptographicOperations.ZeroMemory(transcriptHash); }
            }
            finally { CryptographicOperations.ZeroMemory(expectedCookie); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(joinKey);
            CryptographicOperations.ZeroMemory(serverNonce);
            CryptographicOperations.ZeroMemory(cookie);
            CryptographicOperations.ZeroMemory(clientNonce);
            CryptographicOperations.ZeroMemory(requestHash);
        }
    }

    private bool QueueManagedAdmission(PlayerJoinRequest request, ManagedAdmissionVerifier verifier, IPEndPoint sender, Guid associationId, byte[] transcriptHash, ReadOnlySpan<byte> datagram)
    {
        byte[] commitment = SHA256.HashData(datagram);
        PendingManagedAdmission? pending;
        lock (_managedAssociationLock)
        {
            PruneAcceptedIdentityBindings_NoLock(DateTimeOffset.UtcNow);
            if (_acceptedManagedIdentities.TryGetValue(verifier.Identity, out AcceptedAssociationBinding binding))
            {
                bool exactActiveRetry = binding.AssociationId == associationId
                    && binding.Endpoint.Equals(sender)
                    && _managedAssociations.TryGetValue(associationId, out ManagedUdpAssociation? active)
                    && !active.Closed;
                if (exactActiveRetry)
                    ResendManagedAccept_NoLock(associationId, sender);
                else
                    RecordBadSourceRejection();
                CryptographicOperations.ZeroMemory(commitment);
                CryptographicOperations.ZeroMemory(verifier.RootKey);
                return false;
            }
            if (_acceptedManagedIdentities.Count + _pendingManagedAdmissions.Count >= ManagedIdentityBindingCapacity)
            {
                RecordUnauthorizedRejection();
                CryptographicOperations.ZeroMemory(commitment);
                CryptographicOperations.ZeroMemory(verifier.RootKey);
                return false;
            }
            if (_managedAssociations.TryGetValue(associationId, out ManagedUdpAssociation? established))
            {
                bool same = !established.Closed && established.Endpoint.Equals(sender) && established.Identity == verifier.Identity;
                if (same)
                    ResendManagedAccept_NoLock(associationId, sender);
                else
                    RecordBadSourceRejection();
                CryptographicOperations.ZeroMemory(commitment);
                CryptographicOperations.ZeroMemory(verifier.RootKey);
                return false;
            }
            if (_pendingManagedAdmissions.TryGetValue(associationId, out pending))
            {
                bool same = pending.Endpoint.Equals(sender) && pending.Verifier.Identity == verifier.Identity
                    && CryptographicOperations.FixedTimeEquals(pending.Commitment, commitment);
                if (!same)
                    RecordBadSourceRejection();
                CryptographicOperations.ZeroMemory(commitment);
                CryptographicOperations.ZeroMemory(verifier.RootKey);
                return false;
            }
            pending = new PendingManagedAdmission(request, verifier, sender, associationId, transcriptHash.ToArray(), commitment);
            _pendingManagedAdmissions.Add(associationId, pending);
        }

        RuntimeNetworkingHostServices.Current.EnqueueSimulation(() => CompleteManagedAdmission(pending));
        return false;
    }

    private void CompleteManagedAdmission(PendingManagedAdmission pending)
    {
        byte[] receiveKey = ManagedUdpAuthentication.DeriveTrafficKey(pending.Verifier.RootKey, pending.TranscriptHash, ManagedUdpDirection.ClientToServer);
        byte[] sendKey = ManagedUdpAuthentication.DeriveTrafficKey(pending.Verifier.RootKey, pending.TranscriptHash, ManagedUdpDirection.ServerToClient);
        var provisional = new ManagedUdpAssociation(pending.AssociationId, pending.Verifier.Identity, pending.Endpoint, sendKey, receiveKey);
        lock (_managedAssociationLock)
        {
            if (!_pendingManagedAdmissions.TryGetValue(pending.AssociationId, out PendingManagedAdmission? current) || !ReferenceEquals(current, pending))
            {
                provisional.Dispose();
                return;
            }
            _provisionalManagedAssociations.Add(pending.AssociationId, provisional);
        }

        bool committed = RuntimeNetworkingHostServices.Current.CommitManagedAdmission(pending.Request, pending.Verifier, admission =>
        {
            HandlePlayerJoin(pending.Request, pending.Endpoint, admission);
            return IsManagedConnectionPublished(pending.Request, pending.Endpoint, admission);
        });

        lock (_managedAssociationLock)
        {
            if (!_pendingManagedAdmissions.Remove(pending.AssociationId, out PendingManagedAdmission? current) || !ReferenceEquals(current, pending))
                return;
            _provisionalManagedAssociations.Remove(pending.AssociationId);
            if (!committed)
            {
                provisional.Dispose();
                pending.Dispose();
                return;
            }

            _managedAssociations.Add(pending.AssociationId, provisional);
            _acceptedManagedIdentities[pending.Verifier.Identity] = new AcceptedAssociationBinding(
                pending.AssociationId, pending.Endpoint, pending.Verifier.ExpiresUtc);
            _managedAcceptPending.Add(pending.AssociationId);
            pending.Dispose();
        }
    }

    private bool TryUnwrapManagedData(ReadOnlySpan<byte> datagram, ManagedUdpEnvelopeHeader header, ReadOnlySpan<byte> payload, IPEndPoint sender, out ReadOnlyMemory<byte> innerDatagram)
    {
        innerDatagram = default;
        if (header.Direction != ManagedUdpDirection.ClientToServer || header.Counter == 0)
            return RejectManagedEnvelope();

        ManagedUdpAssociation association;
        lock (_managedAssociationLock)
        {
            if (!_managedAssociations.TryGetValue(header.AssociationId, out association!)
                || association.Closed || !association.Endpoint.Equals(sender)
                || association.Identity.SessionId != header.SessionId || association.Identity.Generation != header.Generation
                || association.Identity.CredentialEpoch != header.CredentialEpoch)
            {
                RecordBadSourceRejection();
                return false;
            }
            if (!ManagedUdpEnvelope.Verify(datagram, association.ReceiveKey))
            {
                RecordBadMacRejection();
                return false;
            }
            if (!association.ReceiveReplay.CanAccept(header.Counter))
            {
                RecordReplayRejection();
                return false;
            }
            if (!IsStructurallyValidClientFrame(payload))
                return RejectManagedEnvelope();
        }

        if (!TryAuthorizeManagedStateChange(association, payload, sender))
            return RejectManagedEnvelope();

        lock (_managedAssociationLock)
        {
            if (!_managedAssociations.TryGetValue(header.AssociationId, out ManagedUdpAssociation? current)
                || !ReferenceEquals(current, association) || association.Closed || !association.ReceiveReplay.Commit(header.Counter))
            {
                RecordReplayRejection();
                return false;
            }
            innerDatagram = payload.ToArray();
            return true;
        }
    }

    protected override byte[]? ProtectOutboundDatagram(byte[] innerDatagram, IPEndPoint target)
    {
        lock (_managedAssociationLock)
        {
            ManagedUdpAssociation? association = _managedAssociations.Values.FirstOrDefault(value => !value.Closed && value.Endpoint.Equals(target));
            if (association is null || !association.TryNextSendCounter(out ulong counter))
                return null;

            ManagedUdpMessageKind kind = _managedAcceptPending.Remove(association.AssociationId) ? ManagedUdpMessageKind.Accept : ManagedUdpMessageKind.Data;
            byte[] envelope = ManagedUdpEnvelope.Create(new ManagedUdpEnvelopeHeader(kind, ManagedUdpDirection.ServerToClient,
                association.Identity.SessionId, association.Identity.Generation, association.AssociationId,
                association.Identity.CredentialEpoch, counter), innerDatagram, association.SendKey);
            if (kind == ManagedUdpMessageKind.Accept)
            {
                if (_managedAcceptEnvelopes.Remove(association.AssociationId, out byte[]? previous))
                    CryptographicOperations.ZeroMemory(previous);
                _managedAcceptEnvelopes.Add(association.AssociationId, envelope.ToArray());
            }
            return envelope;
        }
    }

    private bool TryResolveManagedVerifier(PlayerJoinRequest request, ManagedUdpEnvelopeHeader header, out ManagedAdmissionVerifier verifier)
    {
        verifier = null!;
        if (!RuntimeNetworkingHostServices.Current.TryGetManagedAdmissionVerifier(request, out verifier)
            || verifier.Identity.SessionId != header.SessionId || verifier.Identity.Generation != header.Generation
            || verifier.Identity.CredentialEpoch != header.CredentialEpoch || verifier.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            verifier?.RootKey.AsSpan().Clear();
            return false;
        }
        return true;
    }

    private byte[] CreateEndpointCookie(IPEndPoint endpoint, ManagedAdmissionIdentity identity, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce, Guid associationId, ReadOnlySpan<byte> requestHash, long expiry)
    {
        byte[] endpointBytes = endpoint.Address.GetAddressBytes();
        byte[] identityBytes = ManagedUdpAuthentication.BuildIdentityContext(identity);
        byte[] label = "endpoint\0"u8.ToArray();
        byte[] input = new byte[label.Length + endpointBytes.Length + sizeof(int) + identityBytes.Length + clientNonce.Length + serverNonce.Length + 16 + requestHash.Length + sizeof(long)];
        int offset = 0;
        label.CopyTo(input, offset); offset += label.Length;
        endpointBytes.CopyTo(input, offset); offset += endpointBytes.Length;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(offset, sizeof(int)), endpoint.Port); offset += sizeof(int);
        identityBytes.CopyTo(input, offset); offset += identityBytes.Length;
        clientNonce.CopyTo(input.AsSpan(offset)); offset += clientNonce.Length;
        serverNonce.CopyTo(input.AsSpan(offset)); offset += serverNonce.Length;
        associationId.TryWriteBytes(input.AsSpan(offset, 16)); offset += 16;
        requestHash.CopyTo(input.AsSpan(offset)); offset += requestHash.Length;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(input.AsSpan(offset, sizeof(long)), expiry);
        try { return HMACSHA256.HashData(_managedEndpointCookieKey, input); }
        finally { CryptographicOperations.ZeroMemory(input); CryptographicOperations.ZeroMemory(identityBytes); }
    }

    private void SendManagedEnvelope(IPEndPoint target, ManagedUdpEnvelopeHeader header, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> key)
    {
        byte[] bytes = ManagedUdpEnvelope.Create(header, payload, key);
        _ = UdpMulticastSender?.SendAsync(bytes, target);
    }

    private static bool IsStructurallyValidClientFrame(ReadOnlySpan<byte> payload)
    {
        const int headerLength = 16;
        const int guidLength = 16;
        if (payload.Length < headerLength || !payload[..3].SequenceEqual("FRK"u8))
            return false;
        byte flags = payload[3];
        if ((flags & 0b1111_0000) != 0)
            return false;
        int type = (flags >> 1) & 0b111;
        if (type != (int)EBroadcastType.StateChange)
            return false;
        int dataLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[12..16]);
        if (dataLength < 0 || dataLength > MaxInboundFrameBytes)
            return false;
        return payload.Length == headerLength + dataLength + ((flags & 1) != 0 ? 0 : guidLength);
    }

    private bool TryAuthorizeManagedStateChange(ManagedUdpAssociation association, ReadOnlySpan<byte> payload, IPEndPoint sender)
    {
        if (!TryDecodeStateChangeFrame(payload, out StateChangeInfo change)
            || !TryGetManagedConnection(association, sender, out NetworkPlayerConnection connection))
        {
            return false;
        }

        return change.Type switch
        {
            EStateChangeType.PlayerInputSnapshot => StateChangePayloadSerializer.TryDeserialize<PlayerInputSnapshot>(change.Data, out PlayerInputSnapshot? input)
                && input is not null && input.ServerPlayerIndex == connection.ServerPlayerIndex && input.SessionId == connection.SessionId
                && input.EntityId == connection.NetworkEntityId
                && double.IsFinite(input.TimestampUtc) && double.IsFinite(input.ClientSendTimestampUtc)
                && input.Input is CharacterPawnInputSnapshot characterInput
                && float.IsFinite(characterInput.Movement.X) && float.IsFinite(characterInput.Movement.Y)
                && float.IsFinite(characterInput.ViewAngles.X) && float.IsFinite(characterInput.ViewAngles.Y)
                || input is not null && input.ServerPlayerIndex == connection.ServerPlayerIndex && input.SessionId == connection.SessionId
                && input.EntityId == connection.NetworkEntityId && input.Input is GameInputSnapshot gameInput
                && GameInputSchemaRegistry.TryValidate(gameInput, out _),
            // Character movement is reconstructed from buffered input on the fixed
            // server simulation. Transform proposals never carry authority here.
            EStateChangeType.PlayerTransformUpdate => false,
            EStateChangeType.Heartbeat => StateChangePayloadSerializer.TryDeserialize<PlayerHeartbeat>(change.Data, out PlayerHeartbeat? heartbeat)
                && heartbeat is not null && heartbeat.ServerPlayerIndex == connection.ServerPlayerIndex && heartbeat.SessionId == connection.SessionId
                && string.Equals(heartbeat.ClientId, connection.ClientId, StringComparison.Ordinal),
            EStateChangeType.PlayerLeave => StateChangePayloadSerializer.TryDeserialize<PlayerLeaveNotice>(change.Data, out PlayerLeaveNotice? leave)
                && leave is not null && leave.ServerPlayerIndex == connection.ServerPlayerIndex && leave.SessionId == connection.SessionId
                && string.Equals(leave.ClientId, connection.ClientId, StringComparison.Ordinal),
            EStateChangeType.HumanoidPoseFrame => StateChangePayloadSerializer.TryDeserialize<HumanoidPoseFrame>(change.Data, out HumanoidPoseFrame? pose)
                && pose is not null && TryPreflightManagedHumanoidPoseFrame(connection, pose),
            EStateChangeType.ReplicationTransferAck => StateChangePayloadSerializer.TryDeserialize<ReplicationTransferAck>(change.Data, out ReplicationTransferAck? ack)
                && ack is not null && IsCurrentReplicationControl(connection, ack.SessionId, ack.ConnectionGeneration, ack.CredentialEpoch),
            EStateChangeType.ReplicationResyncRequest => StateChangePayloadSerializer.TryDeserialize<ReplicationResyncRequest>(change.Data, out ReplicationResyncRequest? resync)
                && resync is not null && IsCurrentReplicationControl(connection, resync.SessionId, resync.ConnectionGeneration, resync.CredentialEpoch),
            EStateChangeType.ReplicationSyncComplete => StateChangePayloadSerializer.TryDeserialize<ReplicationSyncComplete>(change.Data, out ReplicationSyncComplete? complete)
                && complete is not null && IsCurrentReplicationControl(connection, complete.SessionId, complete.ConnectionGeneration, complete.CredentialEpoch),
            _ => false,
        };
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W)
            && value.LengthSquared() > 0.0f;

    private bool IsManagedAssociationForConnection(NetworkPlayerConnection connection, IPEndPoint sender)
    {
        lock (_managedAssociationLock)
            return _managedAssociations.Values.Any(association => !association.Closed && association.Endpoint.Equals(sender)
                && association.Identity.SessionId == connection.SessionId
                && association.Identity.ClientId == connection.ClientId
                && association.Identity.CredentialEpoch == connection.CredentialEpoch);
    }

    private bool TryGetManagedConnection(ManagedUdpAssociation association, IPEndPoint sender, out NetworkPlayerConnection connection)
    {
        lock (_playerLock)
        {
            if (_playersByClientId.TryGetValue(association.Identity.ClientId, out connection!)
                && connection.LastEndpoint?.Equals(sender) == true
                && connection.SessionId == association.Identity.SessionId
                && connection.CredentialEpoch == association.Identity.CredentialEpoch
                && string.Equals(connection.ReservationId, association.Identity.ReservationId, StringComparison.Ordinal))
            {
                return true;
            }
            connection = null!;
            return false;
        }
    }

    private static bool IsCurrentReplicationControl(NetworkPlayerConnection connection, Guid sessionId, Guid connectionGeneration, long credentialEpoch)
        => sessionId == connection.SessionId && connectionGeneration == connection.ReplicationConnectionGeneration
            && credentialEpoch == connection.CredentialEpoch;

    private bool IsManagedConnectionPublished(PlayerJoinRequest request, IPEndPoint endpoint, ServerJoinAdmissionResult admission)
    {
        lock (_playerLock)
            return _playersByClientId.TryGetValue(request.ClientId, out NetworkPlayerConnection? connection)
                && connection.LastEndpoint?.Equals(endpoint) == true
                && connection.SessionId == admission.SessionContext?.SessionId
                && string.Equals(connection.ReservationId, admission.ReservationId, StringComparison.Ordinal)
                && connection.CredentialEpoch == admission.CredentialEpoch;
    }

    private bool CloseManagedAssociation(IPEndPoint? endpoint)
    {
        if (endpoint is null)
            return false;

        bool closed = false;
        lock (_managedAssociationLock)
        {
            foreach ((Guid associationId, ManagedUdpAssociation association) in _managedAssociations.Where(pair => pair.Value.Endpoint.Equals(endpoint)).ToArray())
            {
                SendManagedClose_NoLock(association);
                closed = true;
                _managedAssociations.Remove(associationId);
                _managedAcceptPending.Remove(associationId);
                if (_managedAcceptEnvelopes.Remove(associationId, out byte[]? accept))
                    CryptographicOperations.ZeroMemory(accept);
                association.Dispose();
            }
        }
        UnregisterUdpPeer(endpoint);
        return closed;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_simulationTiming is { } timing)
                timing.FixedUpdate -= AdvanceSimulationTick;
            _simulationTiming = null;
            DisposeServerReplication();
            ClearCanonicalPoses();
            lock (_managedAssociationLock)
            {
                foreach (ManagedUdpAssociation association in _managedAssociations.Values)
                    association.Dispose();
                foreach (ManagedUdpAssociation association in _provisionalManagedAssociations.Values)
                    association.Dispose();
                foreach (PendingManagedAdmission pending in _pendingManagedAdmissions.Values)
                    pending.Dispose();
                foreach (byte[] accept in _managedAcceptEnvelopes.Values)
                    CryptographicOperations.ZeroMemory(accept);
                _managedAssociations.Clear();
                _provisionalManagedAssociations.Clear();
                _pendingManagedAdmissions.Clear();
                _acceptedManagedIdentities.Clear();
                _managedAcceptPending.Clear();
                _managedAcceptEnvelopes.Clear();
                CryptographicOperations.ZeroMemory(_managedEndpointCookieKey);
            }
        }
        base.Dispose(disposing);
    }

    private bool RejectManagedEnvelope()
    {
        RecordUnauthorizedRejection();
        return false;
    }

    private void ResendManagedAccept_NoLock(Guid associationId, IPEndPoint target)
    {
        if (_managedAcceptEnvelopes.TryGetValue(associationId, out byte[]? accept))
            _ = UdpMulticastSender?.SendAsync(accept, target);
    }

    private void SendManagedClose_NoLock(ManagedUdpAssociation association)
    {
        if (!association.TryNextSendCounter(out ulong counter))
            return;

        byte[] payload = "Managed connection closed."u8.ToArray();
        byte[] close = ManagedUdpEnvelope.Create(new ManagedUdpEnvelopeHeader(
            ManagedUdpMessageKind.Close, ManagedUdpDirection.ServerToClient,
            association.Identity.SessionId, association.Identity.Generation, association.AssociationId,
            association.Identity.CredentialEpoch, counter), payload, association.SendKey);
        _ = UdpMulticastSender?.SendAsync(close, association.Endpoint);
    }

    private void PruneAcceptedIdentityBindings_NoLock(DateTimeOffset now)
    {
        foreach (ManagedAdmissionIdentity identity in _acceptedManagedIdentities
            .Where(pair => pair.Value.ExpiresUtc <= now)
            .Select(static pair => pair.Key)
            .ToArray())
        {
            _acceptedManagedIdentities.Remove(identity);
        }
    }

    private int ManagedIdentityBindingCapacity => Math.Clamp(MaxPlayers, 1, 512) * 8;

    private sealed class PendingManagedAdmission(PlayerJoinRequest request, ManagedAdmissionVerifier verifier, IPEndPoint endpoint, Guid associationId, byte[] transcriptHash, byte[] commitment) : IDisposable
    {
        public PlayerJoinRequest Request { get; } = request;
        public ManagedAdmissionVerifier Verifier { get; } = verifier;
        public IPEndPoint Endpoint { get; } = endpoint;
        public Guid AssociationId { get; } = associationId;
        public byte[] TranscriptHash { get; } = transcriptHash;
        public byte[] Commitment { get; } = commitment;
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Verifier.RootKey);
            CryptographicOperations.ZeroMemory(TranscriptHash);
            CryptographicOperations.ZeroMemory(Commitment);
        }
    }

    private readonly record struct AcceptedAssociationBinding(Guid AssociationId, IPEndPoint Endpoint, DateTimeOffset ExpiresUtc);
}
