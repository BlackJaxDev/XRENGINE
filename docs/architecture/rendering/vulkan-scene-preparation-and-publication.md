# Vulkan Scene Preparation And Publication

This document describes the retained preparation and publication contracts.
It covers shared GPUScene work and the Vulkan consumers of that work.
Performance measurements and validation limits remain in the
[result record](../../work/progress/rendering/vulkan-stall-remediation-results.md).
These contracts do not establish a pass for every workload or hardware path.

## Ownership And Data Flow

The canonical scene database owns mesh and material identity. Dirty queues
carry real scene changes to the accepted publication boundary. A frame package
then carries an immutable publication to its rendering consumers. Resource
planners and leases retain the resources for those consumers.

The main boundaries are:

1. Collect commands and capture their mutation state.
2. Update mesh registration and auxiliary rows for changed inputs.
3. Accept a coherent canonical publication and render-buffer identity.
4. Extract shared Advanced scene data for the accepted generation.
5. Prepare Vulkan scene resources once for each compatible output family.
6. Validate each stage, record commands, and retain dependencies through use.

Reuse the existing database, queues, packages, planners and leases. A second
cache or publication service needs a measured cause and an ownership contract.
See the [frame lifecycle](frame-lifecycle-and-dispatch-paths.md),
[mesh submission contracts](mesh-submission-strategies.md), and
[command recording architecture](vulkan-command-recording.md).
The [frame-loop design](frame-loop-design.md) also covers OpenGL admission,
shadow recording and reconstruction after Play transitions. The
[Play architecture](../editor/play-mode-architecture.md) owns snapshot and
runtime-world restoration contracts.

## Publication Identity And Scene Mutation

`RenderCommandMesh3D.PublishCanonicalDrawIdentities` publishes the accepted
canonical and render-buffer identity. An advancing publication identity does
not, by itself, mean that scene content changed.

The dirty-property filter distinguishes the caller-member notification
`nameof(PublishCanonicalDrawIdentities)` from real draw-state mutations.
`SetField` and its required notifications remain in use. The filter must not
suppress all mesh-property changes, freeze publication sequences, or reuse an
old snapshot merely because its handles are stable.

A swap acknowledges only the mutation state captured by that swap. A newer
change during a callback or after capture stays pending for its next owning
boundary. Collection membership and command acknowledgement must agree on
that boundary. An aborted or superseded publication cannot expose provisional
identity as an accepted snapshot.

Previous/current transforms, canonical identities and command fields must
belong to one admitted snapshot. Motion must advance velocity and history;
stopping motion must settle them. Shared views cannot acknowledge each other's
required mutations.

`render<-collect` measures elapsed waiting for a fresh collect generation.
Command-swap callbacks can delay that generation after the narrow frame-package
publication scope has finished. `collect<-render` is previous-render
backpressure. The intervals can overlap and must not be added.

## Retained Mesh And LOD Registration

`LogicalMeshState` retains the registration signature used by
`ResolveLogicalMeshRegistration`. The signature includes the LOD list version,
per-level mesh identity, geometry revision and threshold, streaming policy,
required resident mesh, and per-level atlas residency.

A transform-only change does not invalidate that signature. An exact hit uses
the accepted registration without temporary LOD lists, arrays or hash sets.
It performs no redundant atlas ensure, logical-table write or residency delta.
Misses use bounded scratch. Already packed meshes do not resynchronize the
legacy atlas mirror on each update.

The complete dependency contract also includes submesh mapping, LOD order,
residency changes and atlas relocation. Each mutation producer must advance
the relevant identity before reuse. Failure or supersession must preserve
the prior accepted state and release partial residency references.

`RenderableComponent` unregisters a removed mesh from the render info's
recorded world instance. Component-world identity cannot substitute for the
actual registration target during shape replacement.

## Auxiliary Rows And Dirty Ranges

Each destination stream has its own change test and accepted revision:

| State | Update rule |
| --- | --- |
| `BoundsGpu` and `DrawMetadata` comparisons | Use typed equality. Preserve all consumed fields without boxing. |
| Draw metadata and bounds | Write only changed rows. Extend dirty ranges only for rewritten rows. |
| Transform | Compare the matrix while preserving previous-frame semantics. |
| Transparency | Track its own dirty range. A content-dirty swap does not require a full-stream copy. |
| Material state class | Rewrite the class row only when its content changes. |

An unchanged CPU object does not prove that a newly allocated or rotating GPU
destination is current. Initial population, buffer growth and retries must
still satisfy the destination's publication contract.

GPUScene auxiliary rows do not contain descriptor, texture or sampler state.
Those epochs belong to Advanced material-table publication. A draw-metadata
comparison cannot validate those separate dependencies.

Several materials can share one state class under the current last-writer
row definition. This can still cause state-class writes during merged-node
motion. The result record preserves that limit; it is not evidence that every
remaining write is redundant.

## Shared Advanced Extraction

`AdvancedSharedPreparationService` retains immutable generations and consumer
leases. The range planner uses preallocated hash lookup storage and remembered
payload-range indices. It compares the complete key on collision and preserves
first-encounter grouping.

Scene/publication identity, temporal and feedback epochs, view capacity,
geometry compaction and bounded static-deformation generations remain part of
the planner contract. Both Vulkan copy boundaries retain coherent owned data.
Mutable extractor spans cannot replace those copies while deferred consumers
can still read them.

An oversized view request must reject without remembering success. An identical
retry must also reject, and a later valid request must still succeed.
Same-frame world publication is first-wins. That rule does not prove safe reuse
between different runtime owners across frames with older work in flight.

## Compatible Family Preparation

`TryPrepareAdvancedVisibilityScenePublication` prepares one immutable scene
state for a compatible family. Later stages reuse it only while they resolve
the same current backend package.

The compatibility key contains:

- Runtime identity and the current backend package object.
- Database and publication references.
- Frame-plan generation and logical slot.
- Resource frame slot and family reservation.
- Authoring views.

Stage, phase, target, native view and planner scope vary by stage. They are not
inputs to the shared scene publication. Each eye output remains a separate
family. Stage reuse must preserve target closures, bin sealing, pipeline
readiness, associations and per-stage planner validation.

One lease and one native use belong to each family. Transfer them once into
the frame slot. Associate shared state only after complete success. A failed
attempt leaves no ready family state; retry starts clean. Supersession requires
fresh preparation and a compatible family check.

Required upload dependencies, attachment validation, barriers and ordering
remain live. Resource retention continues until all recorded and in-flight
consumers finish. See [resource lifetime and retirement](vulkan-resource-lifetime-and-retirement.md).

## Pipeline Readiness And Invalidation

Advanced family preparation has explicit Missing, Pending, Ready and Failed
outcomes. A generation owns its preparation task. Polling must not synchronously
link, compile or wait, duplicate requests, or reset progress. Only a complete
compatible family can become ready. Unsupported capability and failed work
remain visible; required draws cannot silently disappear or use a CPU fallback.

Additive program links and first shader-module creation retain existing
dependency leases. Program/layout and shader-module replacement use exact
dependency scopes. Deliberate device-wide mutation owns global invalidation.
Reject stale work at enqueue and worker entry. Retire compiler dependencies
only after compiler, cache-publication and GPU users finish. Abandoning a
managed task does not cancel native compilation.

Persisted Vulkan cache headers are checked for header version, vendor/device
and pipeline-cache UUID before driver use. Rejected data recreates empty
foreground/background caches. It does not disable caching for the process.
Native creation, host waits, cache merge, capture and persistence have separate
telemetry. Their measurements did not justify new queue or worker-count policy.

Warmed shader identity traversal uses a fixed-count indexed loop to avoid
boxed `EventList` enumeration. Every revision and currentness check remains.
Raster preparation resolves programs and prepared pipelines once for each
coverage, meshlet and cull combination within its preparation work. This does
not authorize memoizing general readiness across dependency generations.
Pending work must still progress to Ready or an observable failure when the
plan is unchanged. [Pipeline compilation](vulkan-pipeline-compilation.md)
describes required-family and optional directional-shadow readiness separately.

## Resource And Mesh Preparation

Initial and replacement resources use bounded owner-thread materialization.
Active, pending and retired generations remain separate. A failed or stale
pending generation cannot replace the active one. An indivisible factory needs
its own cost boundary; an inter-spec budget cannot bound its internal work.
See the [resource lifecycle contract](render-pipeline-resource-lifecycle.md)
and [pipeline resource notes](default-render-pipeline-notes.md#resource-generation-lifecycle).

Thread-affine nested publication transactions keep CPU mesh data, render
objects, backend wrappers and compound renderer resources hidden until root
commit. Rollback restores references, releases leases and destroys unpublished
resources in reverse order. CPU preparation does not create backend wrappers
off their owner thread.

Index preparation uses immutable topology inputs and exact-revision tickets.
Normal Vulkan draw admission requests and polls preparation rather than joining
the worker. Topology changes invalidate obsolete cached indices. Pending and
failed geometry remain explicit admission outcomes.

`XRQuadFrameBuffer` leases one immutable CPU mesh per fullscreen topology.
Each consumer retains its own renderer, material, shader callbacks, versions
and multiview state. The last lease controls mesh retirement. This contract
does not extend to light volumes, debug primitives or procedural fullscreen
drawing without separate evidence.

## Recording Allocations And Borrowed State

Prepared-cohort comparisons use scalar extent, viewport, scissor and
fixed-function fields. They preserve float semantics and indexed-array
reference identity. Nested Vulkan handle equality must not cause boxing in
warmed validation or lowering.

Subscription refresh reuses sets, dictionaries and submesh snapshot buffers.
Program activation invalidates pipeline, descriptor and vertex-input state
only when the linked interface changes. Cold activation, program switches,
relinks and geometry/buffer changes retain their required invalidation paths.

`ProgramUniformValue` stores its managed reference outside the numeric union.
Its compact storage preserves constructor-kind getter semantics and does not
change snapshot lifetime.

Immediately drained desktop materialization can use the materializing worker's
operation workspace. Receipt-owned, captured, ordered-batch and OpenXR work
remain excluded. Pooled objects can retain their last resource/context
references up to historical demand; scene-unload retention still needs proof.

`ApplyBindingSnapshot` can retain snapshots beyond recording. Frame-data
signatures also use snapshot identity. Frame-slot retirement alone therefore
cannot authorize snapshot storage reuse. Any reuse design needs explicit
program-borrow retirement and content generations. Ordinary snapshots cannot
be labelled immutable binding artifacts merely to avoid copies.

The GPUScene mutation lock and the Vulkan Advanced storage gate are separate
owners. The storage gate protects shared arena lanes, transactional rollback
and preparation scratch. Long held work does not establish lock contention.
Changing synchronization needs wait/hold attribution and a complete lifetime
contract for concurrent consumers.
