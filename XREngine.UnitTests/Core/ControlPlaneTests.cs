using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.ControlPlane;
using XREngine.Networking;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class ControlPlaneTests
{
    [Test]
    public void CreateAndJoinInstance_ReturnsRealtimeHandoffAndLaunchEnvironment()
    {
        InMemoryControlPlane controlPlane = new();
        controlPlane.RegisterHost(new ControlPlaneHostRegistration
        {
            HostId = "local-host",
            Endpoint = new RealtimeEndpointDescriptor
            {
                Host = "127.0.0.1",
                Port = 5010,
                ProtocolVersion = "dev",
            },
            MaxInstances = 2,
            MaxPlayers = 8,
        });

        ControlPlaneResult<MultiplayerInstanceInfo> create = controlPlane.CreateInstance(new CreateMultiplayerInstanceRequest
        {
            DisplayName = "Editor Collaboration",
            WorldAsset = CreateWorldAsset(),
            MaxPlayers = 4,
        });

        create.Success.ShouldBeTrue(create.Message);
        create.Value.ShouldNotBeNull();
        create.Value.SessionToken.ShouldNotBeNullOrWhiteSpace();

        ServerLaunchPlan serverLaunch = controlPlane.CreateServerLaunchPlan(create.Value.InstanceId);
        serverLaunch.Environment[XREngineEnvironmentVariables.SessionId].ShouldBe(create.Value.SessionId.ToString("D"));
        serverLaunch.Environment[XREngineEnvironmentVariables.SessionToken].ShouldBe(create.Value.SessionToken);
        serverLaunch.Environment[XREngineEnvironmentVariables.WorldId].ShouldBe(create.Value.WorldAsset.WorldId);
        serverLaunch.Environment[XREngineEnvironmentVariables.UdpBindPort].ShouldBe("5010");

        ControlPlaneResult<JoinMultiplayerInstanceResult> join = controlPlane.JoinInstance(new JoinMultiplayerInstanceRequest
        {
            InstanceId = create.Value.InstanceId,
            ClientId = "editor-1",
            DisplayName = "Editor 1",
            LocalWorldAsset = CreateWorldAsset(),
            ClientReceivePort = 6010,
        });

        join.Success.ShouldBeTrue(join.Message);
        join.Value.ShouldNotBeNull();
        join.Value.HandoffPayload.SessionId.ShouldBe(create.Value.SessionId);
        join.Value.HandoffPayload.SessionToken.ShouldBe(create.Value.SessionToken);
        join.Value.HandoffPayload.Endpoint!.Host.ShouldBe("127.0.0.1");
        join.Value.HandoffPayload.Endpoint.Port.ShouldBe(5010);
        join.Value.HandoffPayload.WorldAsset!.IsSameAssetAs(CreateWorldAsset()).ShouldBeTrue();
        join.Value.ClientEnvironment[XREngineEnvironmentVariables.NetMode].ShouldBe("Client");
        join.Value.ClientEnvironment[XREngineEnvironmentVariables.UdpClientReceivePort].ShouldBe("6010");

        RealtimeJoinHandoffPayload? payload = JsonSerializer.Deserialize(
            join.Value.HandoffJson,
            XreControlPlaneJsonContext.Default.RealtimeJoinHandoffPayload);
        payload.ShouldNotBeNull();
        payload.SessionId.ShouldBe(create.Value.SessionId);
    }

    [Test]
    public void ControlPlaneJsonContext_GeneratesMetadataForPublicContracts()
    {
        Type[] contracts =
        [
            typeof(Dictionary<string, string>),
            typeof(RealtimeJoinHandoffPayload),
            typeof(WorldAssetIdentity),
            typeof(RealtimeEndpointDescriptor),
            typeof(ControlPlaneOptions),
            typeof(ControlPlaneHostRegistration),
            typeof(ControlPlaneHostSnapshot),
            typeof(CreateMultiplayerInstanceRequest),
            typeof(JoinMultiplayerInstanceRequest),
            typeof(LeaveMultiplayerInstanceRequest),
            typeof(MultiplayerInstanceInfo),
            typeof(MultiplayerPlayerInfo),
            typeof(JoinMultiplayerInstanceResult),
            typeof(ServerLaunchPlan),
            typeof(WorldPackageManifest),
            typeof(WorldPackageFile),
            typeof(WorldPackageVerificationResult),
            typeof(List<string>),
            typeof(List<ControlPlaneHostSnapshot>),
            typeof(List<MultiplayerInstanceInfo>),
            typeof(List<MultiplayerPlayerInfo>),
            typeof(List<WorldPackageFile>),
            typeof(ControlPlaneResult<MultiplayerInstanceInfo>),
            typeof(ControlPlaneResult<JoinMultiplayerInstanceResult>),
        ];

        foreach (Type contract in contracts)
            XreControlPlaneJsonContext.Default.GetTypeInfo(contract).ShouldNotBeNull($"Missing JSON metadata for {contract.FullName}.");
    }

    [Test]
    public void JoinInstance_TracksMultipleClientsAndRejectsWhenFull()
    {
        InMemoryControlPlane controlPlane = new(new ControlPlaneOptions { DefaultMaxPlayers = 2 });
        MultiplayerInstanceInfo instance = controlPlane.CreateInstance(new CreateMultiplayerInstanceRequest
        {
            Endpoint = new RealtimeEndpointDescriptor { Host = "127.0.0.1", Port = 5000 },
            WorldAsset = CreateWorldAsset(),
        }).Value!;

        controlPlane.JoinInstance(new JoinMultiplayerInstanceRequest
        {
            InstanceId = instance.InstanceId,
            ClientId = "client-a",
            LocalWorldAsset = CreateWorldAsset(),
        }).Success.ShouldBeTrue();

        controlPlane.JoinInstance(new JoinMultiplayerInstanceRequest
        {
            InstanceId = instance.InstanceId,
            ClientId = "client-b",
            LocalWorldAsset = CreateWorldAsset(),
        }).Success.ShouldBeTrue();

        ControlPlaneResult<JoinMultiplayerInstanceResult> third = controlPlane.JoinInstance(new JoinMultiplayerInstanceRequest
        {
            InstanceId = instance.InstanceId,
            ClientId = "client-c",
            LocalWorldAsset = CreateWorldAsset(),
        });
        third.Success.ShouldBeFalse();
        third.FailureReason.ShouldBe(ControlPlaneFailureReason.InstanceFull);

        controlPlane.GetInstance(instance.InstanceId).Value!.CurrentPlayers.ShouldBe(2);
    }

    [Test]
    public void JoinInstance_RejectsMismatchedWorldAsset()
    {
        InMemoryControlPlane controlPlane = new();
        MultiplayerInstanceInfo instance = controlPlane.CreateInstance(new CreateMultiplayerInstanceRequest
        {
            Endpoint = new RealtimeEndpointDescriptor { Host = "127.0.0.1", Port = 5000 },
            WorldAsset = CreateWorldAsset(),
        }).Value!;

        ControlPlaneResult<JoinMultiplayerInstanceResult> join = controlPlane.JoinInstance(new JoinMultiplayerInstanceRequest
        {
            InstanceId = instance.InstanceId,
            ClientId = "client-a",
            LocalWorldAsset = CreateWorldAsset(contentHash: "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        });

        join.Success.ShouldBeFalse();
        join.FailureReason.ShouldBe(ControlPlaneFailureReason.WorldAssetMismatch);
    }

    [Test]
    public void HostCapacity_RejectsOverReservedInstances()
    {
        InMemoryControlPlane controlPlane = new();
        controlPlane.RegisterHost(new ControlPlaneHostRegistration
        {
            HostId = "host-a",
            Endpoint = new RealtimeEndpointDescriptor { Host = "127.0.0.1", Port = 5000 },
            MaxInstances = 1,
            MaxPlayers = 4,
        });

        controlPlane.CreateInstance(new CreateMultiplayerInstanceRequest
        {
            HostId = "host-a",
            WorldAsset = CreateWorldAsset(),
            MaxPlayers = 4,
        }).Success.ShouldBeTrue();

        ControlPlaneResult<MultiplayerInstanceInfo> second = controlPlane.CreateInstance(new CreateMultiplayerInstanceRequest
        {
            HostId = "host-a",
            WorldAsset = CreateWorldAsset(revisionId: "rev-2"),
            MaxPlayers = 1,
        });

        second.Success.ShouldBeFalse();
        second.FailureReason.ShouldBe(ControlPlaneFailureReason.NoHostCapacity);
    }

    [Test]
    public void WorldPackageManifest_VerifiesAndMirrorsLocalPackage()
    {
        string sourceRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"), "source");
        string targetRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"), "target");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(Path.Combine(sourceRoot, "Scenes"));
        File.WriteAllText(Path.Combine(sourceRoot, "world.xrworld"), "world");
        File.WriteAllText(Path.Combine(sourceRoot, "Scenes", "main.xrscene"), "scene");

        WorldPackageManifest manifest = WorldPackageManifestBuilder.CreateFromDirectory(sourceRoot, CreateWorldAsset(contentHash: string.Empty));
        manifest.Files.Count.ShouldBe(2);
        manifest.ManifestHash.ShouldStartWith("sha256:");
        manifest.Asset.ContentHash.ShouldStartWith("sha256:");
        WorldPackageManifestBuilder.Verify(manifest).Success.ShouldBeTrue();

        WorldPackageManifestBuilder.Mirror(manifest, targetRoot);
        WorldPackageManifestBuilder.Verify(manifest, targetRoot).Success.ShouldBeTrue();
    }

    private static WorldAssetIdentity CreateWorldAsset(
        string worldId = "xre-editor-collab",
        string revisionId = "rev-1",
        string contentHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        => new()
        {
            WorldId = worldId,
            RevisionId = revisionId,
            ContentHash = contentHash,
            AssetSchemaVersion = 1,
            RequiredBuildVersion = "dev",
        };
}
