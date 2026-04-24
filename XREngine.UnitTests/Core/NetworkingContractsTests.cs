using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using MemoryPack;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Core.Files;
using XREngine.Networking;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class NetworkingContractsTests
{
    private static readonly Type[] RealtimeDtoTypes =
    [
        typeof(StateChangeInfo),
        typeof(PlayerJoinRequest),
        typeof(PlayerAssignment),
        typeof(PlayerInputSnapshot),
        typeof(WorldSyncDescriptor),
        typeof(PlayerTransformUpdate),
        typeof(PlayerLeaveNotice),
        typeof(PlayerHeartbeat),
        typeof(ServerErrorMessage),
        typeof(HumanoidPoseFrame),
        typeof(NetworkEntityId),
        typeof(NetworkAuthorityLease),
        typeof(NetworkSnapshotEnvelope),
        typeof(NetworkDeltaEnvelope),
        typeof(ClockSyncMessage),
        typeof(NetworkRelevanceHint),
        typeof(NetworkReplicationBudgetState),
        typeof(WorldAssetIdentity),
        typeof(RealtimeEndpointDescriptor),
    ];

    [TearDown]
    public void TearDown()
    {
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.Development);
        XRRuntimeEnvironment.ConfigurePublishedPaths(null);
    }

    [Test]
    public void StateChangePayloadSerializer_RoundTripsMemoryPackPayload()
    {
        var sessionId = Guid.NewGuid();
        var payload = new PlayerHeartbeat
        {
            ServerPlayerIndex = 7,
            ClientId = "client-a",
            TimestampUtc = 123.5,
            SessionId = sessionId
        };

        string serialized = InvokeSerializerSerialize(payload);
        bool success = InvokeSerializerTryDeserialize(serialized, out PlayerHeartbeat? deserialized);

        success.ShouldBeTrue();
        deserialized.ShouldNotBeNull();
        deserialized!.ServerPlayerIndex.ShouldBe(payload.ServerPlayerIndex);
        deserialized.ClientId.ShouldBe(payload.ClientId);
        deserialized.TimestampUtc.ShouldBe(payload.TimestampUtc);
        deserialized.SessionId.ShouldBe(sessionId);
    }

    [TestCaseSource(nameof(RealtimeDtoSamples))]
    public void RealtimeDtos_RoundTripThroughStateChangeSerializer(object payload)
    {
        MethodInfo method = typeof(NetworkingContractsTests)
            .GetMethod(nameof(RoundTripThroughStateChangeSerializer), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(payload.GetType());

        object? clone = method.Invoke(null, [payload]);

        clone.ShouldNotBeNull();
        clone.GetType().ShouldBe(payload.GetType());
    }

    [Test]
    public void PlayerJoinRequest_RoundTripsDirectSessionAndWorldAsset()
    {
        Guid sessionId = Guid.NewGuid();
        PlayerJoinRequest request = new()
        {
            ClientId = "client-a",
            DisplayName = "Client A",
            BuildVersion = "1.2.3",
            WorldName = "Unit Test World",
            PreferredScene = "Arena",
            ClientWorldAsset = CreateWorldAsset(),
            SessionId = sessionId,
            SessionToken = "opaque-control-plane-token"
        };

        PlayerJoinRequest clone = RoundTripThroughStateChangeSerializer(request);

        clone.ClientId.ShouldBe(request.ClientId);
        clone.SessionId.ShouldBe(sessionId);
        clone.SessionToken.ShouldBe(request.SessionToken);
        clone.ClientWorldAsset.ShouldNotBeNull();
        clone.ClientWorldAsset!.IsSameAssetAs(request.ClientWorldAsset).ShouldBeTrue();
    }

    [Test]
    public void WorldAssetIdentity_RequiresExactLocalAssetIdentity()
    {
        WorldAssetIdentity expected = CreateWorldAsset();
        WorldAssetIdentity sameAssetDifferentMetadata = CreateWorldAsset();
        sameAssetDifferentMetadata.Metadata["source"] = "copied-local-file";
        WorldAssetIdentity differentHash = CreateWorldAsset(contentHash: "sha256:changed");
        WorldAssetIdentity differentRevision = CreateWorldAsset(revisionId: "rev-2");
        WorldAssetIdentity differentSchema = CreateWorldAsset(assetSchemaVersion: 2);

        expected.IsSameAssetAs(sameAssetDifferentMetadata).ShouldBeTrue();
        expected.IsSameAssetAs(differentHash).ShouldBeFalse();
        expected.IsSameAssetAs(differentRevision).ShouldBeFalse();
        expected.IsSameAssetAs(differentSchema).ShouldBeFalse();
        expected.IsBuildCompatible("1.2.3").ShouldBeTrue();
        expected.IsBuildCompatible("1.2.4").ShouldBeFalse();
        CreateWorldAsset(requiredBuildVersion: "dev").IsBuildCompatible("1.2.4").ShouldBeTrue();
    }

    [Test]
    public void RealtimeJoinHandoff_MapsPayloadIntoClientSettings()
    {
        Guid sessionId = Guid.NewGuid();
        RealtimeJoinHandoffPayload payload = new()
        {
            SessionId = sessionId,
            SessionToken = "opaque-token",
            Endpoint = new RealtimeEndpointDescriptor
            {
                Host = "10.20.30.40",
                Port = 47777,
                Transport = RealtimeTransportKind.NativeUdp,
                ProtocolVersion = "1.2.3"
            },
            WorldAsset = CreateWorldAsset()
        };
        GameStartupSettings settings = new() { NetworkingType = ENetworkingType.Local };

        RealtimeJoinHandoff.ApplyToSettings(settings, payload);

        settings.NetworkingType.ShouldBe(ENetworkingType.Client);
        settings.MultiplayerTransport.ShouldBe(RealtimeTransportKind.NativeUdp);
        settings.ServerIP.ShouldBe("10.20.30.40");
        settings.UdpServerSendPort.ShouldBe(47777);
        settings.ExpectedMultiplayerProtocolVersion.ShouldBe("1.2.3");
        settings.MultiplayerSessionId.ShouldBe(sessionId);
        settings.MultiplayerSessionToken.ShouldBe("opaque-token");
        settings.ExpectedMultiplayerWorldAsset.ShouldBe(payload.WorldAsset);
    }

    [Test]
    public void RealtimeJoinHandoff_RejectsMismatchedExpectedWorldBeforeConnect()
    {
        GameStartupSettings settings = new()
        {
            NetworkingType = ENetworkingType.Client,
            ExpectedMultiplayerProtocolVersion = "1.2.3",
            ExpectedMultiplayerWorldAsset = CreateWorldAsset(contentHash: "sha256:expected")
        };
        WorldAssetIdentity local = CreateWorldAsset(contentHash: "sha256:local");

        Should.Throw<InvalidOperationException>(() =>
            RealtimeJoinHandoff.ValidateClientStartup(settings, local, "1.2.3"));
    }

    [Test]
    public void RealtimeJoinHandoff_ReadsLocalJsonPayloadFromEnvironment()
    {
        string tempRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "RealtimeJoinHandoff", Guid.NewGuid().ToString("N"));
        string payloadPath = Path.Combine(tempRoot, "join.json");
        string? previousFile = Environment.GetEnvironmentVariable(RealtimeJoinHandoff.PayloadFileEnvironmentVariable);
        string? previousInline = Environment.GetEnvironmentVariable(RealtimeJoinHandoff.PayloadEnvironmentVariable);

        try
        {
            Directory.CreateDirectory(tempRoot);
            RealtimeJoinHandoffPayload payload = new()
            {
                SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                SessionToken = "from-file",
                Endpoint = CreateEndpoint(),
                WorldAsset = CreateWorldAsset()
            };
            File.WriteAllText(payloadPath, System.Text.Json.JsonSerializer.Serialize(payload, XREngineRuntimeJsonContext.Default.RealtimeJoinHandoffPayload));

            Environment.SetEnvironmentVariable(RealtimeJoinHandoff.PayloadFileEnvironmentVariable, payloadPath);
            Environment.SetEnvironmentVariable(RealtimeJoinHandoff.PayloadEnvironmentVariable, null);
            GameStartupSettings settings = new();

            bool applied = RealtimeJoinHandoff.TryApplyFromEnvironment(settings, out RealtimeJoinHandoffPayload? imported, out string? source);

            applied.ShouldBeTrue();
            imported.ShouldNotBeNull();
            source.ShouldNotBeNull();
            source!.ShouldContain(RealtimeJoinHandoff.PayloadFileEnvironmentVariable);
            settings.ServerIP.ShouldBe("127.0.0.1");
            settings.UdpServerSendPort.ShouldBe(5000);
            settings.MultiplayerSessionId.ShouldBe(payload.SessionId);
            settings.ExpectedMultiplayerWorldAsset.ShouldNotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(RealtimeJoinHandoff.PayloadFileEnvironmentVariable, previousFile);
            Environment.SetEnvironmentVariable(RealtimeJoinHandoff.PayloadEnvironmentVariable, previousInline);

            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }

    [Test]
    public void RealtimeAdmissionValidator_RejectsMissingClientWorldIdentity()
    {
        PlayerJoinRequest request = CreateJoinRequest(clientWorldAsset: null);

        AdmissionFailureReason reason = RealtimeAdmissionValidator.ValidateBuildAndWorld(
            request,
            CreateWorldAsset(),
            "1.2.3",
            out string message);

        reason.ShouldBe(AdmissionFailureReason.InvalidRequest);
        message.ShouldContain("Client did not provide");
    }

    [TestCase("contentHash")]
    [TestCase("revisionId")]
    [TestCase("assetSchemaVersion")]
    public void RealtimeAdmissionValidator_RejectsWorldAssetMismatch(string mismatch)
    {
        WorldAssetIdentity clientAsset = mismatch switch
        {
            "contentHash" => CreateWorldAsset(contentHash: "sha256:changed"),
            "revisionId" => CreateWorldAsset(revisionId: "rev-2"),
            "assetSchemaVersion" => CreateWorldAsset(assetSchemaVersion: 2),
            _ => CreateWorldAsset()
        };
        PlayerJoinRequest request = CreateJoinRequest(clientAsset);

        AdmissionFailureReason reason = RealtimeAdmissionValidator.ValidateBuildAndWorld(
            request,
            CreateWorldAsset(),
            "1.2.3",
            out string message);

        reason.ShouldBe(AdmissionFailureReason.WorldAssetMismatch);
        message.ShouldContain("does not exactly match");
    }

    [Test]
    public void RealtimeAdmissionValidator_RejectsRequestedSessionNotHostedByServer()
    {
        AdmissionFailureReason reason = RealtimeAdmissionValidator.ValidateSession(
            CreateJoinRequest(CreateWorldAsset(), sessionId: Guid.NewGuid()),
            Guid.NewGuid(),
            requiredSessionToken: null,
            out string message);

        reason.ShouldBe(AdmissionFailureReason.SessionNotFound);
        message.ShouldContain("not hosted");
    }

    [Test]
    public void RealtimeAdmissionValidator_RejectsInvalidOpaqueToken()
    {
        Guid sessionId = Guid.NewGuid();
        PlayerJoinRequest request = CreateJoinRequest(CreateWorldAsset(), sessionId: sessionId, sessionToken: "wrong");

        AdmissionFailureReason reason = RealtimeAdmissionValidator.ValidateSession(
            request,
            sessionId,
            requiredSessionToken: "expected",
            out string message);

        reason.ShouldBe(AdmissionFailureReason.Unauthorized);
        message.ShouldContain("token");
    }

    [Test]
    public void RealtimeReplicationCoordinator_GrantsAndValidatesAuthorityLease()
    {
        RealtimeReplicationCoordinator coordinator = new();
        Guid sessionId = Guid.NewGuid();
        NetworkEntityId entityId = NetworkEntityId.FromGuid(Guid.NewGuid());
        NetworkAuthorityLease lease = coordinator.GrantLease(entityId, sessionId, "client-a", 2, nowUtc: 100.0d);

        bool valid = coordinator.TryValidateOwner(
            entityId,
            "client-a",
            2,
            sessionId,
            nowUtc: 101.0d,
            out NetworkAuthorityLease? resolved,
            out NetworkAuthorityRevocationReason failureReason);

        valid.ShouldBeTrue();
        failureReason.ShouldBe(NetworkAuthorityRevocationReason.None);
        resolved.ShouldNotBeNull();
        resolved!.EntityId.ShouldBe(lease.EntityId);
        resolved.OwnerClientId.ShouldBe("client-a");
    }

    [Test]
    public void RealtimeReplicationCoordinator_RejectsInvalidAuthorityOwner()
    {
        RealtimeReplicationCoordinator coordinator = new();
        Guid sessionId = Guid.NewGuid();
        NetworkEntityId entityId = NetworkEntityId.FromGuid(Guid.NewGuid());
        coordinator.GrantLease(entityId, sessionId, "client-a", 2, nowUtc: 100.0d);

        bool valid = coordinator.TryValidateOwner(
            entityId,
            "client-b",
            2,
            sessionId,
            nowUtc: 101.0d,
            out _,
            out NetworkAuthorityRevocationReason failureReason);

        valid.ShouldBeFalse();
        failureReason.ShouldBe(NetworkAuthorityRevocationReason.InvalidOwner);
    }

    [Test]
    public void RealtimeReplicationCoordinator_BuffersInputsWithinBoundedWindow()
    {
        RealtimeReplicationCoordinator coordinator = new(TimeSpan.FromSeconds(0.25d));

        coordinator.BufferInput(new PlayerInputSnapshot
        {
            ServerPlayerIndex = 2,
            InputSequence = 1,
            ClientSendTimestampUtc = 10.0d
        }, serverTimeUtc: 10.0d).ShouldBe(1);

        coordinator.BufferInput(new PlayerInputSnapshot
        {
            ServerPlayerIndex = 2,
            InputSequence = 2,
            ClientSendTimestampUtc = 10.3d
        }, serverTimeUtc: 10.3d).ShouldBe(1);

        coordinator.LastBufferedInputSequence(2).ShouldBe((uint)2);
    }

    [Test]
    public void NetworkBandwidthBudget_ConsumesAndRefillsTokens()
    {
        NetworkBandwidthBudget budget = new(bytesPerSecond: 100, nowUtc: 10.0d);

        budget.TryConsume(80, nowUtc: 10.0d).ShouldBeTrue();
        budget.TryConsume(30, nowUtc: 10.0d).ShouldBeFalse();
        budget.TryConsume(30, nowUtc: 10.2d).ShouldBeTrue();
    }

    [Test]
    public void NetworkingAotContractRegistry_IncludesEveryRealtimeDto()
    {
        Type[] registered = NetworkingAotContractRegistry.ContractTypes;

        foreach (Type dtoType in RealtimeDtoTypes)
            registered.ShouldContain(dtoType);
    }

    [Test]
    public void NetworkingAotContractRegistry_ResolvesContractsFromPublishedAotMetadata()
    {
        string tempRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "NetworkingAotContracts", Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(tempRoot, "config");
        string archivePath = Path.Combine(tempRoot, "config.archive");

        try
        {
            Directory.CreateDirectory(staging);
            AotRuntimeMetadata metadata = new()
            {
                KnownTypeAssemblyQualifiedNames = [.. NetworkingAotContractRegistry.ContractTypes.Select(t => t.AssemblyQualifiedName!)]
            };

            File.WriteAllBytes(
                Path.Combine(staging, AotRuntimeMetadataStore.MetadataFileName),
                MemoryPackSerializer.Serialize(metadata));
            AssetPacker.Pack(staging, archivePath);

            XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
            XRRuntimeEnvironment.ConfigurePublishedPaths(archivePath);

            foreach (Type dtoType in RealtimeDtoTypes)
                AotRuntimeMetadataStore.ResolveType(dtoType.FullName).ShouldBe(dtoType);
        }
        finally
        {
            XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.Development);
            XRRuntimeEnvironment.ConfigurePublishedPaths(null);

            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }

    [Test]
    public void HumanoidPoseCodec_WriteBaselineAvatar_ProducesExpectedPacketLength()
    {
        var buffer = new List<byte>();
        QuantizedHumanoidPose pose = HumanoidPoseCodec.Quantize(new HumanoidPoseSample(
            Vector3.Zero,
            0f,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero));

        int written = HumanoidPoseCodec.WriteBaselineAvatar(buffer, entityId: 3, pose, lod: 1, baselineSequence: 9);

        written.ShouldBe(HumanoidPoseCodec.BaselineAvatarBytes);
        buffer.Count.ShouldBe(HumanoidPoseCodec.BaselineAvatarBytes);
        HumanoidPoseCodec.TryReadBaselineAvatar(buffer.ToArray(), out var header, out _, out int bytesConsumed).ShouldBeTrue();
        header.EntityId.ShouldBe((ushort)3);
        header.Sequence.ShouldBe((ushort)9);
        bytesConsumed.ShouldBe(HumanoidPoseCodec.BaselineAvatarBytes);
    }

    private static string InvokeSerializerSerialize<T>(T payload)
    {
        var serializerType = typeof(PlayerJoinRequest).Assembly.GetType("XREngine.Networking.StateChangePayloadSerializer")!;
        var method = serializerType.GetMethod("Serialize")!.MakeGenericMethod(typeof(T));
        return (string)method.Invoke(null, [payload])!;
    }

    private static bool InvokeSerializerTryDeserialize<T>(string data, out T? payload)
    {
        object?[] args = [data, null];
        var serializerType = typeof(PlayerJoinRequest).Assembly.GetType("XREngine.Networking.StateChangePayloadSerializer")!;
        var method = serializerType.GetMethod("TryDeserialize")!.MakeGenericMethod(typeof(T));
        bool result = (bool)method.Invoke(null, args)!;
        payload = (T?)args[1];
        return result;
    }

    private static IEnumerable<TestCaseData> RealtimeDtoSamples()
    {
        Guid sessionId = Guid.NewGuid();
        Guid transformId = Guid.NewGuid();
        NetworkEntityId entityId = NetworkEntityId.FromGuid(Guid.NewGuid());

        yield return new TestCaseData(new StateChangeInfo(EStateChangeType.Heartbeat, "payload")).SetName("StateChangeInfo_RoundTrip");
        yield return new TestCaseData(CreateWorldAsset()).SetName("WorldAssetIdentity_RoundTrip");
        yield return new TestCaseData(CreateEndpoint()).SetName("RealtimeEndpointDescriptor_RoundTrip");
        yield return new TestCaseData(entityId).SetName("NetworkEntityId_RoundTrip");
        yield return new TestCaseData(new NetworkAuthorityLease
        {
            EntityId = entityId,
            SessionId = sessionId,
            OwnerClientId = "client-a",
            OwnerServerPlayerIndex = 2,
            AuthorityMode = NetworkAuthorityMode.ClientPredicted,
            AuthorityLeaseExpiryUtc = 999.0d
        }).SetName("NetworkAuthorityLease_RoundTrip");
        yield return new TestCaseData(new NetworkSnapshotEnvelope
        {
            SessionId = sessionId,
            Channel = NetworkReplicationChannel.Transform,
            ServerTickId = 10,
            SnapshotSequence = 3,
            ServerTimestampUtc = 456.0d,
            EntityIds = [entityId],
            Payload = [1, 2, 3]
        }).SetName("NetworkSnapshotEnvelope_RoundTrip");
        yield return new TestCaseData(new NetworkDeltaEnvelope
        {
            SessionId = sessionId,
            Channel = NetworkReplicationChannel.Transform,
            ServerTickId = 11,
            BaselineTickId = 10,
            DeltaSequence = 4,
            ServerTimestampUtc = 457.0d,
            EntityIds = [entityId],
            Payload = [4, 5, 6]
        }).SetName("NetworkDeltaEnvelope_RoundTrip");
        yield return new TestCaseData(new ClockSyncMessage
        {
            SessionId = sessionId,
            ClientId = "client-a",
            ServerPlayerIndex = 2,
            ClientSendTimestampUtc = 100.0d,
            ServerReceiveTimestampUtc = 100.1d,
            ServerSendTimestampUtc = 100.2d,
            ServerTickId = 12
        }).SetName("ClockSyncMessage_RoundTrip");
        yield return new TestCaseData(new NetworkRelevanceHint
        {
            EntityId = entityId,
            Center = Vector3.One,
            Radius = 10.0f,
            Tags = ["player"]
        }).SetName("NetworkRelevanceHint_RoundTrip");
        yield return new TestCaseData(new NetworkReplicationBudgetState
        {
            ClientId = "client-a",
            ServerPlayerIndex = 2,
            BytesPerSecond = 4096,
            BytesAvailable = 1024,
            UpdatedUtc = 123.0d
        }).SetName("NetworkReplicationBudgetState_RoundTrip");
        yield return new TestCaseData(new PlayerJoinRequest
        {
            ClientId = "client-a",
            DisplayName = "Client A",
            BuildVersion = "1.2.3",
            ClientWorldAsset = CreateWorldAsset(),
            SessionId = sessionId,
            SessionToken = "token"
        }).SetName("PlayerJoinRequest_RoundTrip");
        yield return new TestCaseData(new PlayerAssignment
        {
            ServerPlayerIndex = 2,
            PlayerEntityId = entityId,
            PawnId = Guid.NewGuid(),
            TransformId = transformId,
            World = CreateWorldDescriptor(),
            ClientId = "client-a",
            DisplayName = "Client A",
            SessionId = sessionId,
            IsAuthoritative = true,
            AuthorityLease = new NetworkAuthorityLease
            {
                EntityId = entityId,
                SessionId = sessionId,
                OwnerClientId = "client-a",
                OwnerServerPlayerIndex = 2,
                AuthorityMode = NetworkAuthorityMode.ClientPredicted,
                AuthorityLeaseExpiryUtc = 999.0d
            },
            ServerTickId = 22,
            ServerTimeUtc = 333.0d
        }).SetName("PlayerAssignment_RoundTrip");
        yield return new TestCaseData(new PlayerInputSnapshot
        {
            ServerPlayerIndex = 2,
            EntityId = entityId,
            TimestampUtc = 123.5,
            ClientSendTimestampUtc = 123.6,
            InputSequence = 4,
            ClientTickId = 44,
            SessionId = sessionId
        }).SetName("PlayerInputSnapshot_RoundTrip");
        yield return new TestCaseData(CreateWorldDescriptor()).SetName("WorldSyncDescriptor_RoundTrip");
        yield return new TestCaseData(new PlayerTransformUpdate
        {
            ServerPlayerIndex = 2,
            EntityId = entityId,
            TransformId = transformId,
            Translation = Vector3.One,
            Rotation = Quaternion.Identity,
            Velocity = new Vector3(1.0f, 2.0f, 3.0f),
            SessionId = sessionId,
            ServerTickId = 55,
            BaselineTickId = 50,
            ClientInputSequence = 6,
            LastProcessedInputSequence = 5,
            AuthorityMode = NetworkAuthorityMode.ServerAuthoritative,
            IsServerCorrection = true,
            ServerTimestampUtc = 456.0d
        }).SetName("PlayerTransformUpdate_RoundTrip");
        yield return new TestCaseData(new PlayerLeaveNotice
        {
            ServerPlayerIndex = 2,
            ClientId = "client-a",
            Reason = "done",
            SessionId = sessionId
        }).SetName("PlayerLeaveNotice_RoundTrip");
        yield return new TestCaseData(new PlayerHeartbeat
        {
            ServerPlayerIndex = 2,
            ClientId = "client-a",
            TimestampUtc = 456.5,
            ClientSendTimestampUtc = 456.6,
            LastReceivedServerTickId = 77,
            LastProcessedInputSequence = 8,
            SessionId = sessionId
        }).SetName("PlayerHeartbeat_RoundTrip");
        yield return new TestCaseData(new ServerErrorMessage
        {
            StatusCode = 412,
            Title = "World Asset Mismatch",
            Detail = "Client and server worlds differ.",
            ClientId = "client-a",
            ServerPlayerIndex = 2,
            Fatal = false
        }).SetName("ServerErrorMessage_RoundTrip");
        yield return new TestCaseData(new HumanoidPoseFrame
        {
            SessionId = sessionId,
            SourceClientId = "client-a",
            Channel = NetworkReplicationChannel.HumanoidPose,
            Kind = HumanoidPosePacketKind.Baseline,
            BaselineSequence = 9,
            ServerTickId = 88,
            BaselineTickId = 80,
            FrameSequence = 2,
            ServerTimestampUtc = 789.0d,
            AuthorityMode = NetworkAuthorityMode.ServerAuthoritative,
            EntityIds = [entityId],
            AvatarCount = 1,
            Payload = [1, 2, 3]
        }).SetName("HumanoidPoseFrame_RoundTrip");
    }

    private static T RoundTripThroughStateChangeSerializer<T>(T payload)
        where T : class
    {
        string serialized = InvokeSerializerSerialize(payload);
        InvokeSerializerTryDeserialize(serialized, out T? clone).ShouldBeTrue();
        clone.ShouldNotBeNull();
        return clone!;
    }

    private static PlayerJoinRequest CreateJoinRequest(
        WorldAssetIdentity? clientWorldAsset,
        Guid? sessionId = null,
        string? sessionToken = null)
        => new()
        {
            ClientId = "client-a",
            DisplayName = "Client A",
            BuildVersion = "1.2.3",
            ClientWorldAsset = clientWorldAsset,
            SessionId = sessionId,
            SessionToken = sessionToken
        };

    private static WorldAssetIdentity CreateWorldAsset(
        string revisionId = "rev-1",
        string contentHash = "sha256:abcdef",
        int assetSchemaVersion = 1,
        string requiredBuildVersion = "1.2.3")
        => new()
        {
            WorldId = "unit-test-world",
            RevisionId = revisionId,
            ContentHash = contentHash,
            AssetSchemaVersion = assetSchemaVersion,
            RequiredBuildVersion = requiredBuildVersion,
            Metadata = { ["source"] = "unit-test" }
        };

    private static RealtimeEndpointDescriptor CreateEndpoint()
        => new()
        {
            Transport = RealtimeTransportKind.NativeUdp,
            Host = "127.0.0.1",
            Port = 5000,
            ProtocolVersion = "1.2.3",
            Metadata = { ["role"] = "server" }
        };

    private static WorldSyncDescriptor CreateWorldDescriptor()
        => new()
        {
            WorldName = "Unit Test World",
            GameModeType = "UnitTestGameMode",
            SceneNames = ["Arena"],
            Asset = CreateWorldAsset()
        };
}
