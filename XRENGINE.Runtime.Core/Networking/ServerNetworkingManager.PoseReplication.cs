using System.Net;
using XREngine.Networking;

namespace XREngine;

public partial class ServerNetworkingManager
{
    private readonly object _poseReplicationLock = new();
    private readonly Dictionary<int, CanonicalPoseState> _canonicalPoses = [];

    /// <summary>
    /// Validates a managed humanoid pose against its admitted player before it is
    /// allowed into the replication stream. A managed player owns exactly one
    /// pose avatar: its server player index encoded as an unsigned 16-bit id.
    /// </summary>
    public bool TryAcceptHumanoidPoseFrame(HumanoidPoseFrame frame, IPEndPoint sender, out HumanoidPoseFrame accepted)
    {
        accepted = null!;
        NetworkPlayerConnection? connection;
        double nowUtc = GetUtcSeconds();
        lock (_playerLock)
        {
            if (string.IsNullOrWhiteSpace(frame.SourceClientId)
                || !_playersByClientId.TryGetValue(frame.SourceClientId, out connection)
                || !IsAuthenticatedSender(connection, sender, frame.SessionId))
            {
                return false;
            }

            if (!RequiresManagedUdpTransport)
            {
                if (frame.EntityIds.Length == 0)
                    frame.EntityIds = [connection.NetworkEntityId];
                foreach (NetworkEntityId entityId in frame.EntityIds)
                    if (!ValidateAuthority(connection, entityId, nowUtc, out _))
                        return false;

                accepted = frame;
                accepted.SessionId = connection.SessionId;
                accepted.ServerTickId = _replication.CurrentServerTickId == 0 ? _replication.AdvanceServerTick() : _replication.CurrentServerTickId;
                accepted.ServerTimestampUtc = nowUtc;
                accepted.AuthorityMode = NetworkAuthorityMode.ServerAuthoritative;
                return true;
            }

            if (connection.ServerPlayerIndex is <= 0 or > ushort.MaxValue
                || frame.SessionId != connection.SessionId
                || !string.Equals(frame.SourceClientId, connection.ClientId, StringComparison.Ordinal)
                || frame.Channel != NetworkReplicationChannel.HumanoidPose
                || frame.AvatarCount != 1
                || frame.EntityIds.Length != 1
                || frame.EntityIds[0] != connection.NetworkEntityId
                || frame.FrameSequence == 0
                || frame.Payload.Length == 0)
            {
                return false;
            }

        }

        // Parse and commit the immutable avatar payload before advancing the
        // replication clock. Rejected UDP bytes therefore have no server-state
        // side effects.
        if (!TryValidateAndCacheCanonicalPose(connection!, frame))
            return false;

        accepted = frame;
        accepted.ServerTickId = _replication.CurrentServerTickId == 0 ? _replication.AdvanceServerTick() : _replication.CurrentServerTickId;
        accepted.ServerTimestampUtc = nowUtc;
        accepted.AuthorityMode = NetworkAuthorityMode.ServerAuthoritative;
        return true;
    }

    /// <summary>Gets a player's last baseline and its latest independent delta for a late joiner.</summary>
    public bool TryGetLateJoinHumanoidPoseFrames(Guid sessionId, string clientId, out HumanoidPoseFrame[] frames)
    {
        frames = [];
        lock (_poseReplicationLock)
        {
            foreach (CanonicalPoseState state in _canonicalPoses.Values)
            {
                if (state.SessionId != sessionId || !string.Equals(state.ClientId, clientId, StringComparison.Ordinal))
                    continue;

                if (state.Baseline is null)
                    return false;

                frames = state.LatestDelta is null ? [state.Baseline] : [state.Baseline, state.LatestDelta];
                return true;
            }
        }

        return false;
    }

    /// <summary>Forgets a departed player's canonical pose state and releases its retained payloads.</summary>
    public void ForgetCanonicalPose(int serverPlayerIndex)
    {
        lock (_poseReplicationLock)
            _canonicalPoses.Remove(serverPlayerIndex);
    }

    /// <summary>Clears all cached pose state when a server world or session ends.</summary>
    public void ClearCanonicalPoses()
    {
        lock (_poseReplicationLock)
            _canonicalPoses.Clear();
    }

    /// <summary>
    /// Performs the managed-transport pose checks without changing the canonical
    /// cache. This is used before a UDP replay counter is committed so a signed
    /// frame for another avatar cannot consume a valid counter.
    /// </summary>
    private bool TryPreflightManagedHumanoidPoseFrame(NetworkPlayerConnection connection, HumanoidPoseFrame frame)
    {
        if (connection.ServerPlayerIndex is <= 0 or > ushort.MaxValue
            || frame.SessionId != connection.SessionId
            || !string.Equals(frame.SourceClientId, connection.ClientId, StringComparison.Ordinal)
            || frame.Channel != NetworkReplicationChannel.HumanoidPose
            || frame.AuthorityMode != NetworkAuthorityMode.ClientPredicted
            || frame.ServerTickId != 0
            || frame.ServerTimestampUtc != 0
            || frame.AvatarCount != 1
            || frame.EntityIds.Length != 1
            || frame.EntityIds[0] != connection.NetworkEntityId
            || frame.FrameSequence == 0
            || frame.Payload.Length == 0)
        {
            return false;
        }

        ushort avatarId = (ushort)connection.ServerPlayerIndex;
        return frame.Kind switch
        {
            HumanoidPosePacketKind.Baseline => TryAcceptBaseline(frame, avatarId, out _, out _),
            HumanoidPosePacketKind.Delta => TryPreflightDelta(frame, avatarId),
            _ => false,
        };
    }

    private bool TryValidateAndCacheCanonicalPose(NetworkPlayerConnection connection, HumanoidPoseFrame frame)
    {
        ushort entityId = (ushort)connection.ServerPlayerIndex;
        lock (_poseReplicationLock)
        {
            _canonicalPoses.TryGetValue(connection.ServerPlayerIndex, out CanonicalPoseState? state);
            if (state is not null
                && (state.SessionId != connection.SessionId || !string.Equals(state.ClientId, connection.ClientId, StringComparison.Ordinal)))
            {
                state = null;
            }

            if (state is not null && frame.FrameSequence <= state.LastFrameSequence)
                return false;

            FixedQuantizedHumanoidPose baselinePose = default;
            ushort baselineSequence = 0;
            bool parsed = frame.Kind switch
            {
                HumanoidPosePacketKind.Baseline => TryAcceptBaseline(frame, entityId, out baselinePose, out baselineSequence),
                HumanoidPosePacketKind.Delta => state is not null && TryAcceptDelta(frame, state, entityId),
                _ => false,
            };
            if (!parsed)
                return false;

            if (frame.Kind == HumanoidPosePacketKind.Baseline)
            {
                state ??= new CanonicalPoseState(connection.SessionId, connection.ClientId, entityId);
                _canonicalPoses[connection.ServerPlayerIndex] = state;
                state.Baseline = frame;
                state.BaselinePose = baselinePose;
                state.BaselineSequence = baselineSequence;
                state.LatestDelta = null;
            }
            else
            {
                state!.LatestDelta = frame;
            }

            state!.LastFrameSequence = frame.FrameSequence;
            return true;
        }
    }

    private static bool TryAcceptBaseline(HumanoidPoseFrame frame, ushort entityId, out FixedQuantizedHumanoidPose pose, out ushort baselineSequence)
    {
        pose = default;
        baselineSequence = 0;
        if (!HumanoidPoseCodec.TryReadBaselineAvatarFixed(frame.Payload, out HumanoidPoseAvatarHeader header, out pose, out int consumed)
            || consumed != frame.Payload.Length
            || header.EntityId != entityId
            || !header.Flags.HasFlag(HumanoidPoseFlags.Baseline)
            || !HasOnlyKnownPoseFlags(header.Flags)
            || header.Sequence == 0
            || header.Sequence != frame.BaselineSequence)
        {
            return false;
        }

        baselineSequence = header.Sequence;
        return true;
    }

    private static bool TryAcceptDelta(HumanoidPoseFrame frame, CanonicalPoseState state, ushort entityId)
    {
        if (state.Baseline is null || frame.BaselineSequence == 0 || frame.BaselineSequence != state.BaselineSequence)
            return false;

        return HumanoidPoseCodec.TryReadDeltaAvatar(frame.Payload, state.BaselinePose, out HumanoidPoseAvatarHeader header, out _, out int consumed)
            && consumed == frame.Payload.Length
            && header.EntityId == entityId
            && !header.Flags.HasFlag(HumanoidPoseFlags.Baseline)
            && HasOnlyKnownPoseFlags(header.Flags);
    }

    private static bool TryPreflightDelta(HumanoidPoseFrame frame, ushort entityId)
    {
        // A baseline and delta may arrive back-to-back while their accepted
        // processing is queued on the simulation thread. Decode with a default
        // baseline here only to prove bounds and complete consumption; the
        // canonical-baseline and monotonic-frame checks remain in the fenced
        // cache mutation path.
        return frame.BaselineSequence != 0
            && HumanoidPoseCodec.TryReadDeltaAvatar(frame.Payload, default(FixedQuantizedHumanoidPose), out HumanoidPoseAvatarHeader header, out _, out int consumed)
            && consumed == frame.Payload.Length
            && header.EntityId == entityId
            && !header.Flags.HasFlag(HumanoidPoseFlags.Baseline)
            && HasOnlyKnownPoseFlags(header.Flags);
    }

    private static bool HasOnlyKnownPoseFlags(HumanoidPoseFlags flags)
    {
        const HumanoidPoseFlags KnownFlags = HumanoidPoseFlags.Hip
            | HumanoidPoseFlags.Head
            | HumanoidPoseFlags.LeftHand
            | HumanoidPoseFlags.RightHand
            | HumanoidPoseFlags.LeftFoot
            | HumanoidPoseFlags.RightFoot
            | HumanoidPoseFlags.LodMask
            | HumanoidPoseFlags.Baseline
            | HumanoidPoseFlags.RootChanged
            | HumanoidPoseFlags.ForceResend;
        return (flags & ~KnownFlags) == 0;
    }

    private sealed class CanonicalPoseState(Guid sessionId, string clientId, ushort entityId)
    {
        public Guid SessionId { get; } = sessionId;
        public string ClientId { get; } = clientId;
        public ushort EntityId { get; } = entityId;
        public HumanoidPoseFrame? Baseline { get; set; }
        public FixedQuantizedHumanoidPose BaselinePose { get; set; }
        public ushort BaselineSequence { get; set; }
        public HumanoidPoseFrame? LatestDelta { get; set; }
        public uint LastFrameSequence { get; set; }
    }
}
