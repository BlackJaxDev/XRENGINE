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
