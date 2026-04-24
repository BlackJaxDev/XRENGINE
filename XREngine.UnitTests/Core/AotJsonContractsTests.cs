using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using Valve.VR;
using XREngine.Components;
using XREngine.Networking;

namespace XREngine.UnitTests.Core;

public sealed class AotJsonContractsTests
{
    [SetUp]
    public void SetUp()
    {
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.Development);
        XRRuntimeEnvironment.ConfigurePublishedPaths(null);
    }

    [TearDown]
    public void TearDown()
    {
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.Development);
        XRRuntimeEnvironment.ConfigurePublishedPaths(null);
    }

    [Test]
    public void DiscoveryAnnouncement_SourceGeneratedContextRoundTrips()
    {
        DiscoveryAnnouncement announcement = new()
        {
            BeaconId = "beacon-1",
            Host = "192.168.0.42",
            MulticastGroup = "239.1.2.3",
            MulticastPort = 47777,
            UdpServerSendPort = 5010,
            UdpClientReceivePort = 5011,
            AdvertisedRole = ENetworkingType.Server,
            TimestampUtc = 123456789,
        };

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(announcement, XREngineRuntimeJsonContext.Default.DiscoveryAnnouncement);
        DiscoveryAnnouncement? roundTrip = JsonSerializer.Deserialize(payload, XREngineRuntimeJsonContext.Default.DiscoveryAnnouncement);

        roundTrip.ShouldNotBeNull();
        roundTrip.BeaconId.ShouldBe(announcement.BeaconId);
        roundTrip.Host.ShouldBe(announcement.Host);
        roundTrip.MulticastGroup.ShouldBe(announcement.MulticastGroup);
        roundTrip.AdvertisedRole.ShouldBe(announcement.AdvertisedRole);
        roundTrip.TimestampUtc.ShouldBe(announcement.TimestampUtc);
    }

    [Test]
    public void RealtimeJoinHandoffPayload_SourceGeneratedContextRoundTrips()
    {
        RealtimeJoinHandoffPayload payload = new()
        {
            SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SessionToken = "opaque-token",
            Endpoint = new RealtimeEndpointDescriptor
            {
                Transport = RealtimeTransportKind.NativeUdp,
                Host = "127.0.0.1",
                Port = 5000,
                ProtocolVersion = "dev"
            },
            WorldAsset = new WorldAssetIdentity
            {
                WorldId = "world-a",
                RevisionId = "rev-1",
                ContentHash = "sha256:abcdef",
                AssetSchemaVersion = 1,
                RequiredBuildVersion = "dev"
            }
        };

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, XREngineRuntimeJsonContext.Default.RealtimeJoinHandoffPayload);
        RealtimeJoinHandoffPayload? roundTrip = JsonSerializer.Deserialize(json, XREngineRuntimeJsonContext.Default.RealtimeJoinHandoffPayload);

        roundTrip.ShouldNotBeNull();
        roundTrip.SessionId.ShouldBe(payload.SessionId);
        roundTrip.SessionToken.ShouldBe(payload.SessionToken);
        roundTrip.Endpoint.ShouldNotBeNull();
        roundTrip.Endpoint!.Host.ShouldBe("127.0.0.1");
        roundTrip.WorldAsset.ShouldNotBeNull();
        roundTrip.WorldAsset!.IsSameAssetAs(payload.WorldAsset).ShouldBeTrue();
    }

    [Test]
    public void VrInputData_SourceGeneratedContextRoundTrips()
    {
        Engine.VRState.VRInputData input = new()
        {
            DeviceClass = ETrackedDeviceClass.Controller,
            TrackingResult = ETrackingResult.Running_OK,
            Connected = true,
            PoseValid = true,
            Position = new Vector3(1.0f, 2.0f, 3.0f),
            Velocity = new Vector3(4.0f, 5.0f, 6.0f),
            RenderPosition = new Vector3(7.0f, 8.0f, 9.0f),
            ulButtonPressed = 10,
            ulButtonTouched = 11,
        };

        string json = JsonSerializer.Serialize(input, XREngineRuntimeJsonContext.Default.VRInputData);
        Engine.VRState.VRInputData? roundTrip = JsonSerializer.Deserialize(json, XREngineRuntimeJsonContext.Default.VRInputData);

        roundTrip.ShouldNotBeNull();
        roundTrip.Value.DeviceClass.ShouldBe(input.DeviceClass);
        roundTrip.Value.TrackingResult.ShouldBe(input.TrackingResult);
        roundTrip.Value.Position.ShouldBe(input.Position);
        roundTrip.Value.Velocity.ShouldBe(input.Velocity);
        roundTrip.Value.RenderPosition.ShouldBe(input.RenderPosition);
        roundTrip.Value.ulButtonPressed.ShouldBe(input.ulButtonPressed);
        roundTrip.Value.ulButtonTouched.ShouldBe(input.ulButtonTouched);
    }

    [Test]
    public void WebhookEvent_GenericDeserializeJson_IsBlockedInPublishedRuntime()
    {
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);

        WebhookEvent webhook = CreateWebhook(JsonSerializer.Serialize(
            new DiscoveryAnnouncement { BeaconId = "generic-blocked" },
            XREngineRuntimeJsonContext.Default.DiscoveryAnnouncement));

        Should.Throw<NotSupportedException>(() => webhook.DeserializeJson<DiscoveryAnnouncement>());
    }

    [Test]
    public void WebhookEvent_TypedDeserializeJson_RemainsAvailableInPublishedRuntime()
    {
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);

        DiscoveryAnnouncement expected = new()
        {
            BeaconId = "typed-ok",
            Host = "127.0.0.1",
            AdvertisedRole = ENetworkingType.Server,
        };

        WebhookEvent webhook = CreateWebhook(JsonSerializer.Serialize(expected, XREngineRuntimeJsonContext.Default.DiscoveryAnnouncement));
        DiscoveryAnnouncement? result = webhook.DeserializeJson(XREngineRuntimeJsonContext.Default.DiscoveryAnnouncement);

        result.ShouldNotBeNull();
        result.BeaconId.ShouldBe(expected.BeaconId);
        result.Host.ShouldBe(expected.Host);
        result.AdvertisedRole.ShouldBe(expected.AdvertisedRole);
    }

    private static WebhookEvent CreateWebhook(string body)
        => new(
            "POST",
            new Uri("http://localhost/test"),
            new Dictionary<string, string>(),
            body,
            "127.0.0.1:12345",
            DateTimeOffset.UtcNow);
}
