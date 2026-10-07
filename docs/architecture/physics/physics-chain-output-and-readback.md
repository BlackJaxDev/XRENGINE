# Physics-chain output and readback contract

The mass-production physics-chain path treats simulation state, skin palettes,
and conservative bounds as world-owned outputs. Rendering consumes the current
and previous palette slices and the bounds slot directly. Scene `Transform`
mutation and CPU copies are compatibility consumers, not prerequisites for
simulation, skinning, culling, or dispatch sizing.

## Renderer bone ownership

`XRMeshRenderer` assigns a nonzero generation to each bone-buffer set.
Registration checks the captured mesh, bone order, and generation under the
bone-set lock. A release with an old generation has no effect on the new set.
`StaleGpuDrivenBoneReleaseCount` records these releases. Index zero is the
identity bone and never counts as a utilized bone.

`CaptureGpuDrivenBoneCoverage` reports one coherent ownership snapshot.
Complete coverage requires an owner for every utilized bone and one certified
complete external palette. A union of partial palettes does not establish
complete coverage. Coverage does not certify one root anchor or fixed authored
bone scale. The first GPU owner removes the CPU palette listener. The
last owner restores the listener and seeds the current bone matrix.

Replacement and rollback create a new ownership generation. They clear the
external source and seed CPU matrices from the current transforms. Coverage
notifications follow the resource transaction and listener installation.
Compute-skinning settings changes release or restore chain registrations at
the next input-copy boundary. Disabling compute skinning restores CPU bone
listeners. Enabling it registers the current bone set again.

## Canonical GPU bounds

The dispatcher publishes the bounds atlas, slot metadata, and current and
previous palettes as one output page. Four pages separate production from
retained consumers. `PhysicsChainGpuOutputPageToken` contains the page index,
page generation, and producer epoch. Every identity field is nonzero. A page
can be reused only after its CPU retains and native GPU use permit reuse.
Backend replacement retires the old ring. Old tokens remain releasable, but
consumers cannot acquire retired storage. Native destruction requires a
successful GPU-idle boundary in the producing renderer context.

`PhysicsChainBounds.comp` transforms a mesh-local vertex box through every
current and previous bone palette matrix. It reduces those boxes into one
world-space bound. The matrices already include the bind-root and inverse-bind
transforms. The pass supports affine scale and shear. Positive normalized skin
weights keep each skinned vertex inside the union of these boxes.

`PhysicsChainMeshEnvelope` caches the local box and the largest bind-pose vertex
distance from its influencing bones. The local box includes authored
blendshape extents. Geometry, bone layout, bind root, and blendshape weight
changes invalidate the cache. The producer unions the current local box with
the prior published box. It saves only the raw current box for the next frame.
Vertices without a moving bone influence cannot certify this route. Each
48-byte bounds work item holds two local `Vector4` extrema and four unsigned
words for palette base, palette count, bounds slot, and flags. The `w`
component of the local minimum holds the material vertex-effect padding.

The generated and Uber vertex shaders apply material vertex effects after
compute skinning. Skinned draws use an identity model matrix, so these
effects move world-space positions. `PhysicsChainMaterialBoundsEvaluator`
classifies each drawn material:

- Translation-class effects add a bounded displacement to each vertex. These
  are local and world translation, height, wave, equation, depth bulge,
  vertex-color offset, glitch, rounding, the declared
  `_VertexConservativeBounds`, and the Uber outline width. Their sum is a
  valid padding in any raster order. The bounds pass adds the largest
  per-axis padding to the reduced box after the numeric margin.
- Scale, rotation, look-at, and barrel effects multiply positions about the
  world origin. A padding cannot bound them. A non-finite parameter is also
  unsupported. These materials set the rejected-contract flag. The bounds
  pass then writes a NaN slot, and the canonical candidate patch clears the
  view mask. The GPUScene compatibility copy is rejected for every covered
  chain, independent of this material check. The dispatcher counts material
  rejections separately and writes a rate-limited warning. It never
  substitutes a CPU bound or reads GPU memory.

The renderer routes that GPUScene publishes at command-buffer swap include
the material of each published draw. The route material comes from the draw
metadata, so it includes command overrides. A draw-metadata change also
republishes the routes. The dispatcher combines the contracts of all routed
materials for a renderer: the largest padding applies, and any rejection
rejects the slot. A renderer without a route uses its own submesh materials.
The evaluator caches each result and recalculates it when the parameter
layout, a parameter value, or the Uber authored state changes. Material
animation therefore updates the contract on the next publication. A custom
vertex shader that is not an engine Uber shader must declare its displacement
in `_VertexConservativeBounds`.

The dispatcher keeps a renderer material-route snapshot while the scene and
renderer-command publication generation stay unchanged. A warm query still
checks the producer page, source, envelope, bone generation, and current
material contract. It checks the scene generation and route presence again
before it returns a bound. The material evaluator checks shader identity and
source revision on a cache hit. It scans shader declarations only when the
material layout, values, Uber state, or shader witness changes. A shader that
does not meet the trust check uses the conservative declaration path.

Each output page records renderer slice identity, range, and bone generation.
Compatible ranges copy the prior palette even when the atlas layout changes.
New or incompatible ranges seed previous from current and clear deformation
history. Shared destinations must use the same prior source. The palette
completion barrier makes compute writes visible to buffer copies before the
history copy starts.

Physics palette history and retained aggregate deformation history have
separate lifetimes. Aggregate deformation writes only current vertices;
previous vertices remain in the prior output slot. Frame gaps invalidate
velocity history, and same-frame preparation reuse retains its bounds source.
Previous-slot selection rejects missing, disposed, failed, unsubmitted, and
poisoned producer fences. Ordered submitted work can supply history without a
CPU completion wait. Each aggregate output slot records the exact physics
producer token it consumed. History is valid only for consecutive aggregate
frames that consume the same physics page or the direct successor that copied
that page's palette. Other page relationships reset history. A direct successor
also requires a compatible renderer palette slice.

Each retained physics page stores the active blendshape inputs used to build
its local mesh box. Aggregate deformation copies those exact inputs. A mesh,
bone layout, bind root, geometry revision, or authored weight revision mismatch
defers preparation with a diagnostic. The box covers the current and copied
predecessor inputs without accumulating older morph extents.

`PhysicsChainGpuBoundsSource` identifies a renderer's logical slot, slot
generation, and bone-buffer generation. The canonical scene publication stores
that source with its exact draw. The extractor retains a physical page and
creates one 32-byte `AdvancedGpuBoundsPatchRoute` for each covered candidate.
The route contains the draw index and generation, candidate index, bounds slot
and generation, producer epoch, and both words of the bone generation.

`PatchPhysicsChainBounds.comp` runs before early visibility. It validates the
route and metadata before it writes the candidate sphere and AABB. Vulkan uses
storage bindings 60 to 62 in set 1. OpenGL borrows bindings 53 to 55 and restores
only those bindings before visibility. Invalid routes and non-finite or
reversed bounds clear that candidate's view mask and increment delayed
diagnostic counters. An identity mismatch never changes another candidate.

The dispatcher retains the real bounds in the physics output atlas for the
canonical Advanced route. It writes a rejected record to
`GPUScene.CommandAabbBuffer` and `CullBoundsBuffer` for each covered chain
command. The compatibility GPUScene culling route has no retained physics
output page and source identity for its `PendingMeshDraw`. It cannot use the
physics bound safely. The rejected copy uses a zero version and cannot admit
that command through compatibility GPU culling. No CPU bound replaces it.
CpuDirect can still draw the command. The renderer-to-command map is frozen at
command-buffer swap. Update-side compaction cannot change the rejected copy
targets. GPU publication advances the command-bounds revision so consumers
see the rejection.

Complete coverage bypasses stale CPU bone bounds during view and shadow
admission. Layer, material, and `CastShadow` policy still apply. Partial coverage
uses the existing CPU admission path. A missing committed source rejects the
canonical publication with a diagnostic and permits later recovery. It does
not substitute CPU bounds. Runtime checks are in the
[validation plan](../../work/testing/physics/physics-validation.md#gpu-skinned-chain-scale).

Directional shadow work uses the frozen strategy of each complete consumer.
The atlas keeps strict GPU tiles dirty when that consumer cannot accept a
lane; it retries them without a generic or sequential CPU collection. Shadow
authoring has a unique input lease for each operation. A physical operation
copy retains the cascade data and effective layer mask until native work
finishes. A manual caller can use
`XRViewport.GlobalPreRenderWithShadowConsumerAuthority` to publish all
registered desktop consumers before shadow scheduling. The call requires the
consumer set for the same world and rejects duplicate viewports.

## CPU consumer inventory

The July 2026 source audit found no external caller reading the private
`Particle` or `ParticleTree` runtime types. Existing non-test call sites author
components, toggle debug rendering, invalidate GPU-driven renderer bindings,
or locate a chain for the benchmark scene. The remaining output needs are
classified as follows.

| Consumer | Requested fields | Contract |
| --- | --- | --- |
| Normal skinning and motion vectors | Bone palettes | Direct current/previous atlas slices; no readback |
| Canonical Advanced visibility | Bounds | Retained physics output page and direct conservative bounds slot; no readback |
| Compatibility GPUScene culling | Bounds | Rejected copy for a covered chain; no CPU fallback or readback |
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

Requests carry the generational instance handle, the instance source generation,
the exact field mask, an immutable
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
  the instance and source generations again before publishing. Reset,
  teleport, hierarchy transfer, and same-capacity template changes advance
  the source generation. Ordinary pose updates do not advance it. A stale result is
  `DiscardedStale`, never attached to the new occupant of a slot.
- Terminal requests remain queryable until explicitly released. Release frees
  the request slot and advances its generation, so the old request handle can
  no longer resolve.

Exact duplicate pending requests in the same submission frame coalesce to one
handle. Coalescing includes the source generation. It never expands a
selection, crosses frames, or weakens byte and element budgets. The world
captures source identity at a structural boundary. A per-service gate
serializes request, cancellation, polling, and release. No operation waits
for GPU completion.

A request made under another world's tick uses nonblocking source admission.
It returns `SourceBusy` when the source boundary is occupied. Source identity
capture precedes the service gate. Polling checks the published binding and
source generation without taking a world tick or component binding lock.

Each world owns three independent native staging slots. A logical failed or
released request does not prove that GPU storage can be reused. Each native
slot retains its producer marker after the service releases its adapter.
Vulkan reuse requires completed native buffer uses, even if the marker
failed. Missing proof or a device fault rejects reuse. OpenGL reuse requires
a successful completion marker. No failed transfer can publish a result.

Each gather and buffer copy retains the native slot through its deferred
authoring request and physical frame payload. Logical release does not end
these uses. `RenderResourceLeaseOwner` combines the use count and retirement
state in one atomic value. The final release disposes retired storage once.
A retired slot with no counted use cannot return to active use. Renderer
replacement retains the old banks and rejects their logical transfers; the
new renderer gets separate banks. Old storage waits for its original owner.

A held chain can service a pending request without another simulation step.
The dispatcher publishes the retained accepted input page and gathers the
solved output. It seals that page after the output readers finish.

The gather admits only a matching source generation and resident particle,
static, transform, and arena generations. It checks the logical selection
and byte ranges before GPU dispatch. Bone, socket, and full-transform values
use the same solved-bone composition as the skinning palette. Their three
rows contain the linear terms and world translation in `Row0.W`, `Row1.W`,
and `Row2.W`. `PhysicsChainAffineTransformReadbackValue.ToMatrix4x4` restores
the C# row-vector matrix. These values are delayed snapshots.

## Strict zero-readback behavior

With no explicit request, the readback gather list is empty and the transfer
path copies zero bytes. Simulation, activity classification, palette and bounds
generation, culling, indirect dispatch sizing, and rendering remain entirely
GPU-resident in strict GPU profiles. Aggregate diagnostics are delayed or
sampled and cannot change current-frame work decisions.

Full transform mirroring is disabled by default. When enabled for a selected
chain, it runs only after asynchronous data becomes available, at an explicit
caller-selected rate, and reports both update cost and data age.
