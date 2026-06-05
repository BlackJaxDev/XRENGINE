# XRENGINE Control Plane

`XREngine.ControlPlane` is a small local-dev control-plane DLL for tests, editor tooling, launchers, and future service wrappers. It keeps room/instance orchestration outside the realtime engine while producing the same realtime handoff payload that `Engine.InitializeNetworking` already consumes.

## Responsibilities

- Register local hosts and track basic capacity.
- Create/list/stop multiplayer instances.
- Issue opaque session tokens for realtime admission.
- Join multiple clients to the same instance.
- Generate server launch environment variables.
- Generate client `XRE_REALTIME_JOIN_PAYLOAD` environment variables.
- Build and verify local world package manifests for test delivery flows.

The DLL is intentionally not an HTTP service. A future directory service can wrap these contracts, but the engine-side realtime worker should still receive only endpoint, session, token, protocol, and world identity data.

## Basic Usage

```csharp
using XREngine.ControlPlane;
using XREngine.Networking;

var controlPlane = new InMemoryControlPlane();
controlPlane.RegisterHost(new ControlPlaneHostRegistration
{
    HostId = "local-host",
    Endpoint = new RealtimeEndpointDescriptor
    {
        Host = "127.0.0.1",
        Port = 5000,
        ProtocolVersion = "dev",
    },
    MaxInstances = 1,
    MaxPlayers = 8,
});

var created = controlPlane.CreateInstance(new CreateMultiplayerInstanceRequest
{
    DisplayName = "Editor Collaboration",
    WorldAsset = worldAsset,
    MaxPlayers = 4,
});

ServerLaunchPlan server = controlPlane.CreateServerLaunchPlan(created.Value!.InstanceId);
var joined = controlPlane.JoinInstance(new JoinMultiplayerInstanceRequest
{
    InstanceId = created.Value.InstanceId,
    ClientId = "editor-1",
    LocalWorldAsset = worldAsset,
    ClientReceivePort = 5001,
});
```

Use `server.Environment` when launching `XREngine.Server.exe`; it includes `XRE_SESSION_ID`, `XRE_SESSION_TOKEN`, `XRE_WORLD_*`, and UDP port values. Use `joined.Value.ClientEnvironment` when launching an editor/client; it includes `XRE_REALTIME_JOIN_PAYLOAD`, `XRE_NET_MODE=Client`, matching `XRE_WORLD_*`, and the optional client receive port.

## Editor Workflow

The ImGui networking panel embeds an in-memory `InMemoryControlPlane` for local editor-to-editor smoke tests:

1. In the host editor, open the networking panel and click `Create / Start Editor Server Instance`.
2. Click `Issue Client Handoff` if you need to refresh the payload, then `Copy Handoff`.
3. In another editor process, paste the payload into `Handoff JSON` and click `Join From Handoff`.

The host editor installs `Engine.ServerJoinAdmissionResolver` while the editor-hosted instance is active. Incoming joins must present the control-plane session id/token and a matching world identity. The join path also records admitted clients in the in-memory control plane so max-player checks still apply.

For generated/local worlds, the panel temporarily applies the handoff `WorldAssetIdentity` through `XRE_WORLD_*` process environment variables before restarting networking. `Disconnect` restores the previous process environment values.

## World Packages

`WorldPackageManifestBuilder` supports local package verification and mirroring:

```csharp
WorldPackageManifest manifest = WorldPackageManifestBuilder.CreateFromDirectory(root, worldAsset);
WorldPackageManifestBuilder.Verify(manifest);
WorldPackageManifestBuilder.Mirror(manifest, clientCacheRoot);
```

This is a test/local-dev delivery helper. Realtime networking still does not download large worlds; callers should verify or mirror the package before starting the client join loop.

## Current Limits

- No HTTP API or persistent database.
- No cloud host placement.
- No signed URLs, remote chunk transport, or eviction policy.
- No process launcher abstraction yet; callers receive environment dictionaries and launch however they prefer.
- No full scene/component snapshot replication; it only prepares matching worlds and realtime admission data.
