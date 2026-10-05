# Browser Realtime Gateway Local Validation

Date: 2026-10-01

The portable WebSocket leaf and encrypted socket gateway compile. A real local
network smoke reaches the existing managed server's admission callback through
the new gateway. This is transport qualification, not browser gameplay or
production acceptance. Configuration and lifecycle policy are described in the
[browser realtime guide](../../../developer-guides/networking/browser-realtime.md).

## Build Evidence

Using the installed .NET SDK 10.0.401 and isolated artifacts:

- `XREngine.Server` with `XREngineRendererBackends=None`: zero warnings/errors
- `XREngine.ControlPlane.Service`: zero warnings/errors
- `XREngine.Runtime.Net.WebSockets`: zero warnings/errors

The Windows-targeted server/service were cross-built on Linux. Their complete
Windows startup, certificate-store configuration, and deployed hosting were not
executed. The first build attempt failed because PowerShell inherited a
read-only cache directory; directing XDG cache/config/data to the authorized
workspace resolved that environment issue without source changes.

Evidence is under
`Build/_AgentValidation/20261001-163800-webgpu-baseline/`:

- `logs/transport-server-build-3.log`
- `logs/transport-control-plane-build.log`
- `logs/transport-leaf-build.log`
- `logs/transport-live-build-4.log`
- `logs/transport-live-3.log`

Generated outputs and the disposable walkthrough remain beneath this run's
`temp-build/transport-hosts/` and `scratch/transport-live/`. No formal regression
tests were added or modified.

## Real Local Network Walkthrough

TCP and UDP loopback binds succeeded. The walkthrough used the compiled
`RealtimeWebSocketGateway`, the real `ServerNetworkingManager`, production
managed-envelope/handshake/authentication codecs, and a native .NET WebSocket
client. It created a short-lived in-memory certificate and synthetic in-memory
admission. The native client accepted only that exact certificate; no operating
system/browser trust store, production credential, account, or server changed.

Observed results:

1. TLS WebSocket upgrade negotiated the exact realtime subprotocol
2. A corrupted admission MAC was rejected and counted by the production worker
3. A valid Hello produced the worker's authenticated Challenge through the gateway
4. The client verified the Challenge MAC, client nonce, and Hello hash
5. The authenticated Commit passed the worker's transcript and endpoint-cookie
   checks and reached its admission host callback
6. The host deliberately denied player publication because this walkthrough had
   no loaded world; no assignment, baseline, entity synchronization, or gameplay
   success is claimed
7. Unlisted origins, cookie-bearing upgrades, and the wrong subprotocol were
   rejected and recorded
8. Final gateway counters recorded 959 received bytes, 278 sent bytes, and 13
   closed/rejected connections. The native HTTP client retried rejected upgrades;
   these counters are connection attempts, not distinct users

This run exposed a real error-observation defect: `InvalidDataException` derives
from `SystemException`, not `IOException`. Explicit catches now classify invalid
frames and rejected upgrades in both new transport pumps. The repeated live run
confirmed rejection counters and credential-free failure classification.

## Transform Codec Finding

The first live call through `Transform.EncodeToBytes(false)` and
`DecodeFromBytes` confirmed scale and translation but exposed quaternion
corruption. A quaternion `(0, 0.24740396, 0, 0.9689124)` decoded as approximately
`(0.003921628, 1, 0.003921628, 0)`: the old encoder divided retained components
by their magnitude without recording that magnitude. The original failure is
recorded in `Build/_AgentValidation/20261001-163800-webgpu-baseline/logs/transport-transform-live.log`.

The shared codec now quantizes the retained components without discarding their
magnitude. A fresh `XREngine.Server` build passed with zero warnings and errors,
and the existing local network walkthrough passed. Its absolute-transform
roundtrip decoded the source above as approximately
`(0.003921628, 0.24705887, 0.003921628, 0.9689846)` and also restored identity
defaults. The same run rejected legacy managed-envelope version 1 and `FRK`
frames before admission or peer state changed, reported the update-required
wire diagnostic, and rejected the legacy WebSocket subprotocol. A current-wire
Hello/Challenge/Commit still reached the production admission callback.

The compatibility boundary is realtime wire revision 2: `FR2` frame magic,
managed-envelope version 2, version-2 admission key context, TLS ALPN
`xrengine-realtime/2`, and WebSocket subprotocol `xrengine-realtime.v2`. The
transform payload itself has no version marker, so client, server, and gateway
must update together. There is no old-payload decoder or downgrade. Evidence is
under `Build/_AgentValidation/20261001-183210-quaternion-wire/`:

- `logs/data-build.log`
- `logs/server-build-4.log`
- `logs/transport-live-build.log`
- `logs/transport-live.log`

This local walkthrough qualifies the corrected absolute-transform bytes and
wire rejection, but does not establish queue-pressure coalescing behavior,
loaded-world replication, or browser gameplay.

## Remaining Acceptance

- Real browser leaf connection with browser certificate/origin/cookie behavior
- Trusted handoff acquisition and asynchronous browser session composition
- Player/world admission, full baseline, entity/asset references, and gameplay
- Queue-pressure, stale-state coalescing, throttling, reconnect/resume, expiry,
  scene transitions, page suspension, and head-of-line latency measurements
- Optional microphone/voice services and physical mobile-device checks
- Full server deployment, native desktop preservation, and operating-system
  certificate-store startup

## Managed local correction preparation (2026-10-05)

Source inspection found that managed local corrections invoked the controller's
network-transform hook and then replayed unacknowledged inputs without first
restoring the authoritative pose. `LocalPlayerController` inherits a no-op hook;
its plain `Transform` therefore accumulated replay on the predicted position.
The replay path now restores authoritative local translation/rotation even when
no history remains, clears stale translation/rotation smoothing targets, and
then applies only unacknowledged locomotion commands for that exact session and
entity. Commands retained from a prior assignment cannot move its replacement.
Scale is unchanged.

Direct transform messages and replication batches can carry the same stored
pose in different transport packets. The new local correction watermark is
scoped to the assigned player, session, entity and connection generation. It
rejects an older tick, a regressed input acknowledgment, an exact repeated
version, or a mismatched assignment before drift metrics and replay. A newer
tick with an unchanged acknowledgment is accepted. Global clock/ack telemetry
is not used to decide admission. A genuinely new connection generation clears
prediction history and correction marks; a baseline resynchronization on the
same connection preserves them. Existing terminal cleanup clears both.

This change is restricted to managed local plain-`Transform` correction. Remote
interpolation, legacy UDP controller behavior, rigid-body character simulation,
wire formats and host scheduling remain unchanged. The existing
`CharacterPawnComponent.CaptureNetworkInputState` already captures shared input,
but that pawn uses `RigidBodyTransform` and does not exercise the existing
plain-transform server locomotion/replay fallback. A real browser gameplay
fixture must select a compatible, explicit pawn rather than claim that the
free-camera or physics-character paths already qualify this behavior.

The source and assignment/ordering paths were reviewed. The current development
environment has no .NET SDK, so compile and real-network execution remain
pending; this record does not close browser networking acceptance. No browser
certificate was installed or trusted. Production gateway TLS/origin/authentication
checks are retained for the eventual real-server run.

The correction source at `b0af163766b5f50e3ffbf318750cddae51e17c3a`
subsequently passed the portable build, browser WebAssembly publish, and Windows
Editor build/publication stages in
[run 37297750717](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37297750717).
Those compile/publication results do not exercise a network connection.

An opt-in `NetworkKinematicPawnComponent` now captures W/A/S/D through the
existing shared input interface; W maps to world -Z. The existing automatic
snapshot sender and managed server simulator own movement. Unregister,
deactivation, controller replacement, and local/global UI capture clear held
movement. Each captured snapshot is distinct because prediction retains it.
The server package generator's `--network-kinematic` option selects that pawn
and includes a plain replicated landmark. The ordinary generator is unchanged.
A browser query exposes only current ready-client assignment/ack/pose status.

The Windows publisher lane creates this real native package, stages the exact
world bytes into a browser project, invokes the normal Editor CLI, and checks
retained native identity and world bytes. Its successful execution is recorded
below. It does not start a server, trust a certificate, relax TLS, or qualify
browser networking. The native package inspector's existing mesh/read-converter
and shader-path restrictions remain intact; the landmark has no visual mesh.

On `97f75841beb84b5d2dadf358da4d93fb509b61a9`,
[Windows job 111735025939](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37301461684/job/111735025939)
builds the Server, creates the 2,025-byte native world, stages it unchanged, and
completes the real Editor browser publish. The added verification then fails
because it incorrectly compares the native-only input content hash with the
expanded package's hash. The shared-package contract deliberately recomputes
that hash over the browser catalog and all added payloads. This is a validation
error, not evidence that the publisher changed the retained world.

The preserved published descriptor's World.asset SHA-256
`7bd0d87571ba6f26485eed6d967f1967da659aa1dc9983e76f541dc96cd88836`
and length match the native staging result. The shipping browser
`validateSharedWorldPackage` function accepts the actual 310-file manifest and
its canonical hash
`02e189868fbbc8fbd9a3f6b103765ae04267fa5ac7bd3aba247c3db6c441966f`
in a local Node invocation. This validates the manifest calculation, not the
unavailable complete payload or a browser session. Evidence artifact
`11343327945` has independently verified ZIP SHA-256
`abe074bbaf38999b7e5a801d1745f76df2a4e7eae4e115cfc20b2b71353284b7`.

`Tools/BrowserSmoke/verify-network-kinematic-publication.mjs` replaces the
incorrect comparison. It reuses that shipping canonical validator, retains the
stable native world/package/schema/build/bootstrap identifiers and metadata,
compares the actual native world bytes, and checks every declared published
file's length/hash and the complete file inventory. The new admission identity
must bind the expanded package. Generated output is now preserved for inspection even if a
subsequent verification fails; consuming it still requires a successful
producer job and verification.

The verifier also accepts `--published-only <site>` when consuming a qualified
artifact without its original native input directory. That mode retains the
shipping canonical hash, world-binding, complete file-byte and inventory
checks and explicitly reports `nativeInputCompared: false`. The producer lane
continues to pass both native input and published site, so its retained-native
identity and exact-byte comparisons are not optional there.

On `b64f65a7fd7d619e47ec3defdfaa6733a35bbb4b`,
[Windows job 111754237186](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37307383257/job/111754237186)
passed the genuine native-world generation, Editor browser publication and full
verification. The result records `nativeInputCompared: true`, 310 declared
content files totaling 11,359,301 bytes, and the unchanged 2,025-byte world with
SHA-256 `3f1853781ba028be21670fea376080e2b210e8aaebc0284ba4f4f15cf42c040c`.
The expanded package identity is
`sha256:7594d13009b22812d5cc575c2add80114bc2afc5d146821fe8a761ac21006f9a`.
World identity is generated per fixture run, so a new run is not expected to
have the same world hash as the earlier `97f75841` fixture.

The complete `windows-editor-network-kinematic-bundle` artifact is
[`11346111752`](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37307383257/artifacts/11346111752),
55,824,360 ZIP bytes, with GitHub-reported archive SHA-256
`9278a05d81f94c48d3c2c224f184ec5b41db9d2fa0b229580bbccb1be6884562`.
The producer checked every content file before upload; this archive digest has
not yet been independently checked after download. The Linux portable/runtime
job also passed on this commit. These results qualify the native/browser
publication boundary, not browser connection, locomotion, reconnection or TLS
certificate-store behavior. The owner subsequently approved one isolated
Windows real-server walkthrough and its validation helper/workflow. It uses the
exact qualified bundle, normal TLS and one temporary current-user localhost
certificate, with an eight-minute live-operation limit and one-minute cleanup
limit. Windows parsing, embedded-helper compilation, independent workflow
review and exact-run activation remain prerequisites. No certificate/trust or
live server-connection action has run yet.
