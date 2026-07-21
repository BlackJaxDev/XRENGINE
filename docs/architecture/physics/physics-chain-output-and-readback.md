# Physics-chain output and readback contract

The mass-production physics-chain path treats simulation state, skin palettes,
and conservative bounds as world-owned outputs. Rendering consumes the current
and previous palette slices and the bounds slot directly. Scene `Transform`
mutation and CPU copies are compatibility consumers, not prerequisites for
simulation, skinning, culling, or dispatch sizing.

## CPU consumer inventory

The July 2026 source audit found no external caller reading the private
`Particle` or `ParticleTree` runtime types. Existing non-test call sites author
components, toggle debug rendering, invalidate GPU-driven renderer bindings,
or locate a chain for the benchmark scene. The remaining output needs are
classified as follows.

| Consumer | Requested fields | Contract |
| --- | --- | --- |
| Normal skinning and motion vectors | Bone palettes | Direct current/previous atlas slices; no readback |
| GPUScene visibility | Bounds | Direct conservative bounds slot; no readback |
| Gameplay attachment or socket | Specific bones or sockets | Bounded asynchronous selection |
| Gameplay collision notification | Collision events | Delayed bounded event selection; use strict CPU simulation when same-frame authority is required |
| Editor selection and debug inspection | Selected particles, bones, sockets, bounds, or events | Diagnostic asynchronous selection; bounded and absent from production profiles |
| Legacy transform-dependent code | Full transform mirror | Explicit opt-in rate after delayed data arrives; never enabled implicitly |
| Authoring, serialization, and root input | None | These write structural or dynamic inputs and do not consume solver output |

Adding a new CPU output consumer requires choosing one of the typed fields in
`PhysicsChainReadbackFields`, documenting its maximum element and byte demand,
and deciding whether delayed data is semantically valid. A consumer requiring
authoritative current-frame GPU state must be redesigned or select strict CPU
simulation; it must not add a blocking readback.

## Request lifetime and freshness

Requests carry the generational instance handle, exact field mask, an immutable
copy of selected element indices, expected byte count, submission frame,
earliest completion frame, and expiry frame. The default contract caps a world
to 4,096 selected elements and 4 MiB per submission frame, with an eight-frame
lifetime.

For a request submitted in frame `N`:

- The earliest legal completion is `N + 1`; completion in frame `N` is never
  allowed.
- The result is a snapshot produced no earlier than its submission point. Its
  `CompletionFrame` and `SubmissionFrame` define age; it is never authoritative
  current-frame state merely because it is available.
- A pending or in-flight request becomes `Expired` at its expiry frame. Timeout,
  cancellation, stale-generation discard, backend failure, and capacity
  rejection are explicit states or rejection reasons.
- Destroy, reuse, resize, retemplate, or backend-switch delivery must validate
  the instance generation again before publishing. A stale result is
  `DiscardedStale`, never attached to the new occupant of a slot.
- Terminal requests remain queryable until explicitly released. Release frees
  the request slot and advances its generation, so the old request handle can
  no longer resolve.

Exact duplicate pending requests in the same submission frame coalesce to one
handle. Coalescing never expands a selection, crosses frames, or weakens byte
and element budgets. Submission, polling, cancellation, and release perform no
render-thread or simulation-thread wait.

## Strict zero-readback behavior

With no explicit request, the readback gather list is empty and the transfer
path copies zero bytes. Simulation, activity classification, palette and bounds
generation, culling, indirect dispatch sizing, and rendering remain entirely
GPU-resident in strict GPU profiles. Aggregate diagnostics are delayed or
sampled and cannot change current-frame work decisions.

Full transform mirroring is disabled by default. When enabled for a selected
chain, it runs only after asynchronous data becomes available, at an explicit
caller-selected rate, and reports both update cost and data age.
