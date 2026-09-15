# Managed transport and synchronization — Phases 5–6

Date: 2026-09-14

Implementation and local process validation cover managed native UDP admission, sender authorization, world bootstrap, state deltas, late join, and teardown. Public encrypted transport, authoritative input simulation, website/launcher UX, and durable recovery remain Phases 7–10.

## Implementation

The worker caches derived admission verifiers. Hello/Challenge/Commit proves possession without transmitting the admission secret on UDP. Endpoint cookies, session/generation/epoch binding, directional HMAC keys, counters, replay windows and consumed-credential bindings fence the association. Exact active Commit retries recover a lost Accept; copied credentials cannot create another association.

Managed workers reject bare development framing. Typed client messages must match the admitted owner and permitted direction before reliable peer/ACK mutation. Pose ownership includes its embedded avatar ID. Remote jobs and arbitrary object mutation are outside the managed packet surface. Packet, decompression, traffic, peer and queue limits have credential-free rejection counters.

Opted-in nodes and registered component codecs produce real world state. The adapter preserves verified authored-node identity, creates dynamic entities, resolves parents/references, applies components, and removes entities/components in a defined order. Registration is explicit and frozen before replication; no downloaded code or reflection-selected network factory is activated.

An update/physics fence captures an immutable baseline at a server simulation tick. Its hash covers relevant entities, complete roster, leases, stationary transforms, and canonical humanoid baseline plus latest delta. One coalesced successor snapshot is retained during transfer. Chunks/deltas carry connection generation, epoch, transfer identity and base tick; ACK/retry limits, duplicates, resynchronization and timeouts bound recovery. Oversized deltas use a new chunked baseline.

Clients pause gameplay until validated application and synchronization confirmation. Worker synchronized occupancy follows the actual acknowledgment. Fatal close/silence resets replicas, leases and avatars. Stable world contexts and ownership tokens prevent delayed old-manager cleanup from resetting a later connection's world.

## Runtime evidence

An ignored walkthrough runs the real server entry point under the service supervisor and native clients through `Engine.Run`. Its observer uses the simulation fence; it does not replace networking or world application. All process handles and private handoffs belong to the walkthrough.

Validated scenarios:

1. Verified worker startup and authenticated first admission; two authored nodes appear exactly once.
2. Twenty-four dynamic creates, changed authored state, and complete late-join equality with the existing client.
3. Actual remote VRIK consumption of a stationary player's baseline and latest independent pose delta.
4. Twelve dynamic removals, component destruction/recreation, and relevance leave/enter convergence.
5. Connected/synchronized occupancy reaching two; kick returning it to zero and restoring authored state while removing dynamic replicas and avatars.
6. Convergence with a dropped initial Accept, packet loss, duplicate/reordered datagrams, altered authentication tags, and incorrect source endpoints.
7. Six authenticated forged messages rejected without disconnecting the victim; complete traffic interruption produces visible timeout and cleanup.
8. A separate process reusing the consumed handoff fails authentication while both admitted clients stay ready.
9. The standalone `XREngine.Server.exe` starts under the supervisor, synchronizes two native clients, and closes both connections on stop; clients return to paused state.

Scratch reports, logs and helpers are under `Build/_AgentValidation/20260914-185107-managed-sync/` and remain disposable. Required behavior is in tracked runtime code. No new or modified tests were added under the feature-validation gate.

The full Editor build (all renderer backends), isolated headless Server/Bootstrap/client walkthrough build, and ControlPlane.Service build passed with zero warnings and zero errors. An initial editor build with renderer backends disabled was not a supported editor configuration; the subsequent full build passed.

## Defects found and resolved

- The client restarted authentication while assignment was queued after erasing its secret. Startup now owns the handshake retry state throughout assignment.
- Deferred destruction left replaced components in captured graphs. Simulation fences drain lifecycle work and batch network mutations; stale-player pruning has a separate cadence.
- Clearing keys before sending a queued error made kicks invisible. An authenticated Close is sent before association teardown; authenticated server silence has a bounded timeout.
- Server tick progress followed UDP sends. It now follows runtime updates.
- Delayed cleanup needed stable contexts and monotonic ownership tokens across manager replacement.

The final independent review used exact requested/actual `gpt-5.6-sol`. Its binding-token ABA finding was addressed. Its proposed duplicate byte-array comparison defect was ruled out: `StateChangePayloadSerializer.Serialize` returns `string`, whose equality compares values.

## Scope

HMAC authenticates packets without encrypting their contents. Direct development remains a separate explicit mode. A managed player owns one humanoid avatar. Game validators must be side-effect-free and apply callbacks deterministic and nonblocking. Application failure pauses and restores package state before resynchronization. Public deployment and restoration after worker crashes are not claimed.
