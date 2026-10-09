# Transform Architecture

`TransformBase` remains the authoring, serialization, animation and editor API.
A `RuntimeWorld` owns the runtime-only `TransformHierarchyStore` that holds its
attached transforms' local, world and published render matrices.

## Storage and lifetime

The store uses growable structure-of-arrays storage, initially 256 slots and
geometrically enlarged at attachment boundaries. This keeps the expected
hundreds-to-tens-of-thousands population contiguous and avoids a pointer lookup
for every matrix read. It does not assume a fixed maximum avatar population.
Growth and hierarchy edits can allocate; warmed propagation and publication
reuse their bitsets, ranges, changed lists and worker synchronization objects.
The population/latency tradeoff still requires measured qualification.

A `TransformHandle` contains a world-local slot and generation. Attachment
acquires a slot; detach, destruction and world transfer invalidate it by
incrementing its generation. Stale access throws a diagnostic. A handle must
always be used with its owning store. Detached transforms retain three matrices
in one small state object; attached objects release that state. Bind matrices
remain authored object state. Inverses and directional snapshots are computed
on demand, rather than reserving inverse matrices and six locks per transform.
Legacy replication baselines are created lazily by `TransformReplicationState`,
whose weak ownership cannot retain a destroyed transform.

Hierarchy insertion, removal and reparenting invalidate a parent-first depth-first
order. Each subtree has a contiguous half-open range. Mutation batches flush the
order at disposal; ordinary edits rebuild before the next pass. Parenting cycles
are rejected. Reparenting into a different world's hierarchy transfers the
transform and its descendants. Immediate hierarchy edits inside a matrix
evaluator are rejected; schedule them through deferred `SetParent` instead.

## Simulation and notifications

Scene nodes subscribe to transform property events through cached
`XRPropertyNotificationHandlers` filters. They observe `Parent` before a change
and `Parent` or `World` after a change. Pose setters still use `SetField` and run
their matrix invalidation hooks. When only these internal listeners are present,
a pose change creates no property event arguments. `XRBase` uses an immutable
delegate snapshot and keeps listener order. It creates one fresh typed argument
object for the first matching listener and shares it with the remaining matching
listeners. Public listeners can retain the arguments, and changing listeners keep
the same cancellation behavior. Filter names are copied at subscription setup;
dispatch does not use shared mutable argument objects or scratch storage.

`MarkLocalModified` preserves immediate local recalculation by default. Deferred
local changes are evaluated during propagation. Dirty registration sets a slot
bit, so duplicate registrations require neither hash sets nor ancestor walks.
`RuntimeWorld.ProcessDirtyTransforms` merges ancestor/descendant work into
subtree ranges and walks those ranges once. Ordinary world composition reads
`local * parentWorld` from the arrays. Custom world owners retain their explicit
virtual evaluator.

`TransformBase.BeginHierarchyMutationBatch` returns a stack-only, allocation-free
`TransformHierarchyMutationBatch`. Inside the batch, each changed transform still
raises its property notifications and sets its local and world dirty flags, but
it does not register its own world recalculation. Disposing the batch enqueues
the batch root once when any mutation was applied. Callers mutate only
descendants of that root. The CPU physics-chain transform mirror uses one batch
for each particle tree.

For immediate recalculation of an attached transform, the store reads both
dirty flags in one operation. It commits a changed local matrix and clears its
local dirty flag in one write. For an ordinary child with a valid cached parent
order, it composes and commits the world matrix, marks render publication, and
clears the world dirty flag in one write. Root transforms, external parents,
custom world evaluators, and invalid cached order use the existing composition
path. Local callbacks run before world composition; world callbacks and render
publication keep their order. The store checks ownership again after local
callbacks because a callback can detach or transfer the transform. It does not
clear global dirty registration bits in the immediate path.

For an exact `Transform` instance with a clean local matrix and a valid cached
parent order, one store gate covers the dirty check and world composition.
Derived types, dirty local matrices, and invalid parent order keep the general
path. World callbacks still run after the store gate is released.

`Sequential` processes ranges on the caller. `Parallel` and `Asynchronous` both
join persistent world-owned workers at the simulation barrier, rather than
creating per-root tasks. They parallelize disjoint ranges only when all included
transforms use ordinary parent composition. Passes containing custom world
owners remain ordered because those evaluators can depend on other roots. The
asynchronous setting deliberately means joined parallel work for this bulk API;
existing explicit hierarchy methods retain their task-returning API.

Matrix callbacks are collected while the bulk pass runs and dispatched after
all descendants finish. Subscriber counts update only on event subscription
changes. Inverse notifications calculate inverses only when subscribed. Render
events only run for subscribers. Local/world virtual hooks still execute because
UI bounds and rigid-body reset checks own required side effects there. Mutations
from callbacks remain dirty for the next pass. Explicit immediate recalculation
outside a bulk pass continues to notify synchronously. Diagnostic evaluation
suppresses external matrix notifications and publication.

## Snapshot and render publication contract

Cooked restoration marks the local and world matrices dirty after rebuilding parent
and child links. Authored setters run with notifications suppressed, so their usual
matrix invalidation cannot be assumed. Recalculation stays deferred until the graph
is complete; world startup evaluates the hierarchy before gameplay activation.

Matrix accessors return the latest complete matrix; reading does not recalculate
or wait for the next simulation tick. Store writers serialize array edits and
publish an even sequence after the transaction. Readers retry if a concurrent
write crossed their copy, preventing torn 64-byte matrices. Separate accessor
calls are individually coherent, not a combined multi-transform snapshot.

`PublishRenderMatrices` copies pending world entries into the render array in
one transaction. `RuntimeWorldRenderer` invokes it before visibility collection
and at buffer swap. It no longer applies each stored transform through the
object-level render setter. `TransformPublicationRecords` consumes changed slots
directly into `TransformGpu` and `AdvancedTransformRecord` arrays. GPU scene
conversion and canonical scene publication copy those rows when their identity
and exact captured matrix agree. Canonical current/previous handles remain owned
by their existing transaction and retirement machinery. Procedural model
matrices, skinning's identity model matrix, previous-frame history and late
render overrides use their exact captured values when they differ from the
hierarchy row; no canonical temporal state is overwritten by a simulation row.

Physics simulation and interpolation update simulation world space before the
ordinary propagation/publication boundary. `RigidBodyTransform` explicitly owns
world space; children consume that world matrix after it is evaluated. VR device
and action transforms generate tracking-local poses in simulation. Their draw
callbacks remain render-only late overrides, after ordinary publication, and
compose against `ParentRenderMatrix`. `SetRenderMatrix` keeps its descendant
cascade, including the OpenXR headset exception. An immediate render write
clears older pending simulation publication for that slot, preventing a stale
queued pose from replacing a later draw pose. It does not write simulation world
space. New simulation changes can publish at the next frame boundary.

## Subclass audit

The runtime subclasses were classified by their matrix creation methods:

| Family | Authority |
|---|---|
| `Transform`, `TransformNone`, `RectTransform` | Local matrix and ordinary parent composition |
| `OrbitTransform`, `BoomTransform`, `DrivenLocalTransform` | Local matrix and ordinary parent composition |
| `NoiseRotationTransform`, `ScreenShakeTransform` | Local noise; ordinary parent composition |
| `LaggedTranslationTransform`, `SmoothedTransform`, `Spline3DTransform` | Local evaluation; ordinary parent composition |
| `RigidBodyTransform`, `DrivenWorldTransform` | Explicit world owner |
| `CopyTransform`, `MultiCopyTransform`, `MirroredTransform`, `LookatTransform`, `PositionOnlyTransform` | Custom world evaluator, potentially depending on another root |
| `WorldTranslationLaggedTransform`, `SmoothedParentConstraintTransform` | Custom world evaluator with smoothing state |
| `BillboardTransform` | Custom camera-dependent world evaluator |
| `VRDeviceTransformBase` and device subclasses; `VRActionTransformBase` and action subclasses; `VREyeTransform` | Tracking-local simulation pose; render-only draw override where implemented |
| `UITransform` and layout subclasses | Local layout; `UICanvasTransform` additionally supplies a custom world evaluator |
| `SerializedModelSkeletonTransform`, `BrowserBoneTransform` | Inherit their existing local transform behavior |

New subclasses that override `CreateWorldMatrix` must also override
`HasCustomWorldMatrix` to return true. Their dependent local state and ordinary
property setters still use `MarkLocalModified`/`MarkWorldModified`.

## Editor, serialization and diagnostics

Gizmos, transform editors, undo and inspector changes continue through the
existing pose setters, `DeriveLocalMatrix`, immediate recalculation and
`SetParent`. These methods now address the store automatically. The runtime
handle and matrices remain excluded from authored serialization; authored TRS,
parent relationships and bind matrices keep their existing contracts.

`TransformHierarchy.Counters` is a value snapshot. Registered is the current
population; dirty-local and dirty-world are cumulative invalidation counts;
propagated, published records and events are cumulative work counts. Timings and
allocated bytes describe the most recent pass (including callbacks and worker
allocations), so capture them immediately after the pass under measurement.
Cold array growth and worker startup are deliberately visible in those scopes.
The benchmark must warm up before making steady-state claims.

Live avatar, physics, gizmo, OpenGL/Vulkan and VR qualification remains required.
A build is not evidence of visual parity or zero allocation; unavailable VR
hardware remains explicitly unqualified.

## Related documentation

- [Scene architecture](overview.md)
- [Component API](../../developer-guides/components/component-api.md)
- [Rendering runtime overview](../rendering/runtime-overview.md)
