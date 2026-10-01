# Browser Realtime Transport

`XREngine.Runtime.Net.WebSockets` is a portable transport leaf for the existing
`ClientNetworkingManager`. It does not create a second browser simulation or
replication protocol. `RealtimeWebSocketGateway` in the socket leaf connects one
WebSocket to one immutable IPv4-loopback managed worker endpoint. Binary
WebSocket messages carry the existing authenticated `ManagedUdpEnvelope`
unchanged; the worker retains admission, MAC/replay checks, entity ownership,
build/world validation, and replication-baseline authority.

## Wire And Trust Policy

- The transport is explicitly `RealtimeTransportKind.WebSocket`; the URL is
  `wss://<advertised-host>:<port>/realtime` and the required subprotocol is
  `xrengine-realtime.v2`. There is no UDP, plaintext WebSocket, native TLS, or
  protocol-version fallback.
- URLs cannot contain credentials, a query, or a fragment. The gateway accepts
  only a bounded HTTP/1.1 upgrade for the exact advertised authority and route.
- Configure exact allowed page origins, for example `https://game.example`.
  Wildcards, missing/opaque origins, paths, and remote HTTP origins are rejected.
  HTTP origins are allowed only for loopback development pages. The connection
  itself always uses `wss` with browser-trusted TLS; no certificate override is
  provided.
- The gateway rejects cookies, Authorization and Proxy-Authorization headers,
  request bodies, and repeated headers. Browsers can attach cookies automatically
  and cannot reliably suppress them for WebSocket upgrades: deploy on a
  dedicated cookieless host outside any credential-cookie domain scope. A
  rejected cookie-bearing upgrade is intentional, not an authentication fallback.
- Player admission remains the managed challenge/commit protocol. No admission
  grant, token, or secret belongs in URLs, HTTP headers, WebSocket subprotocol
  strings, browser storage, diagnostics, or browser publish assets.
- The gateway checks the configured session and worker generation before
  forwarding, but does not declare a player authorized. Only the production
  worker can do that. Origin checking does not replace player authentication.

## Coordinated Wire Compatibility

Realtime wire revision 2 is intentionally incompatible with older peers. It uses
raw-frame magic `FR2`, managed envelope version 2, a version-2 admission key
context, TLS ALPN `xrengine-realtime/2`, and WebSocket subprotocol
`xrengine-realtime.v2`. Client, server, and managed-host binaries must be updated
together. A matching assembly/build version or a `dev` world setting does not
relax this wire check. There is no legacy decoder or automatic downgrade.

The revision corrects quaternion compression that previously discarded rotation
angle information. Old payloads cannot be recovered by a decoder-only change.
Recognized old datagrams are rejected before peer/admission state is changed;
`WireProtocolFailure` supplies a named update-required diagnostic.

## Server Configuration

The existing operator-provisioned certificate remains in the Windows
certificate store. Add the following fields to the service's existing
`realtimeTls` configuration:

```json
{
  "useWebSocket": true,
  "allowedWebSocketOrigins": ["https://game.example"]
}
```

Retain the existing certificate thumbprint/store, literal listen address, and
connection-limit settings. Remote ingress still requires explicit admission
signing trust. The worker supervisor advertises `WebSocket`, copies the origin
policy to the worker launch contract, and starts the WebSocket gateway instead
of the native TLS gateway. The public handoff gateway accepts either encrypted
transport only when protected admission is available. Setting `useWebSocket`
to false preserves the existing native TLS framing.

## Client Composition And Lifecycle

Install `WebSocketNetworkTransportBackend` through `NetworkTransportServices`.
Set a new `ClientNetworkingManager` to `Transport = WebSocket`, supply the
verified managed handoff fields and loaded-world identity through the existing
host services, then await `StartWebSocketAsync(endpoint, protocolPeer, token)`.
Do not call synchronous `Start` or the desktop networking bootstrap for this
transport. Connection completion starts the managed handshake; it does not
establish gameplay readiness. Gate gameplay on `IsGameplayReady` after the
existing replication baseline has been validated and applied.

The Core contract still identifies peers using `IPEndPoint`. `protocolPeer` is
the stable routing key agreed by the host for this one connection; the transport
never binds it, sends UDP to it, performs DNS on it, or presents it as a local
socket. Incoming datagrams are associated only with this immutable peer.
`LocalEndPoint` remains null and `IsBound` is false. Native socket options,
multicast, broadcast, local interface discovery, and raw TCP/TLS fail explicitly.

Call `SuspendWebSocket` on page suspension or hiding, before world teardown.
It aborts the connection, cancels a pending connection attempt, clears managed
keys and obsolete assignments, and resets replication synchronization. The
manager is single-use: resume/disconnect recovery requires a newly authorized
control-plane admission and a new manager, followed by a full baseline. The leaf
never retries a used or expired grant. Dispose the old manager before replacing
it so its timer subscriptions cannot continue. Inspect
`WebSocketTransportFailure`, `ManagedTransportFailure`, and `ReplicationFailure`
for credential-free diagnostics.

## Bounds And Delivery Semantics

- One message is one managed datagram, at most 65,507 bytes and 256 fragments
- Each client transport direction has at most 128 queued datagrams and 2 MiB of
  queued payload, plus its single in-flight message and receive buffer
- Client pre-framing queues have the same packet/byte bounds; reliable retry
  tracking is limited to 32 outstanding packets
- Only unsent, unreliable transform snapshots may be replaced by newer snapshots
  for the same object. The browser path emits absolute transforms, so coalescing
  cannot discard a required delta. Reliable state, admission and baseline
  messages are never silently evicted
- A bounded queue overflow terminates the connection with a named failure;
  fresh admission and baseline resynchronization are required
- One asynchronous sender serializes browser writes. The pinned .NET browser
  runtime waits for `bufferedAmount` to drain once its 65,536-byte threshold is
  reached; a send has a ten-second deadline
- Gateway connection and per-source limits apply before TLS negotiation. TLS
  plus HTTP upgrade has a ten-second deadline. Ingress is limited to 240
  datagrams and 2 MiB per second per connection, with a 30-second silence
  deadline and eight-hour connection lifetime
- Gateway requests 2 MiB UDP buffers (effective sizes depend on the OS) and
  response writes have ten-second deadlines. No unbounded gateway application queue is introduced. UDP between
  gateway and worker can still lose packets; production reliability and
  baseline recovery remain active

WebSocket is reliable and ordered on the browser leg. Head-of-line latency,
browser event-queue behavior under a blocked main thread, server-side backlog
behavior, and reconnect/app-switch timing require measurement against a real
managed worker. This transport does not provide WebRTC/WebTransport, microphone
capture, audio encoding, or voice mixing; projects requiring voice must reject
that unsupported capability explicitly until a separately validated service is
installed.

## Validation Scope

The [local gateway validation record](../../work/progress/networking/browser-realtime-validation.md)
documents successful server/service/leaf builds, a real managed
Hello/Challenge/Commit walkthrough, negative upgrade checks, corrected
absolute-transform quaternion roundtrip, and rejection of recognized older
wire traffic before admission or peer state changes.

This is an implementation path, not production acceptance. Browser session
composition must still obtain a trusted control-plane handoff, route it through
the async start API, connect suspension/resume to the page lifecycle, and prove
real-server join, baseline, gameplay, expiry and recovery. Local compilation or
a transport loopback smoke cannot qualify those behaviors or physical mobile
devices. The frozen reference `browser-network.js` is not this transport and is
not extended by this work.
