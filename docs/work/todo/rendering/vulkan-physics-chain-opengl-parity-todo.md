# Vulkan GPU Physics-Chain OpenGL Parity TODO

Last Updated: 2026-07-22
Owner: Rendering and Physics
Status: Planned; OpenGL behavior is validated, Vulkan physics backend has not started
Target Branch: Not created by this document-only task

## Related Work And Ownership

Related local documents:

- [Physics-chain skinned-mesh motion investigation](../../investigations/rendering/physics-chain-skinned-mesh-motion-2026-07-22.md)
- [GPU physics-chain zero-readback skinned-mesh plan](../../design/transforms/gpu-physics-chain-zero-readback-skinned-mesh-plan.md)
- [Skinning GPU efficiency follow-ups](gpu/skinning-gpu-efficiency-followups-todo.md)
- [Physics-chain correctness contract](../../testing/physics-chain-correctness-contract.md)
- [Physics-chain performance validation](../../testing/physics-chain-performance.md)
- [Vulkan renderer architecture](../../../architecture/rendering/vulkan-renderer.md)
- [OpenGL renderer architecture](../../../architecture/rendering/opengl-renderer.md)
- [XRDataBuffer RHI write model](../../../architecture/rendering/xrdatabuffer-rhi-write-model.md)
- [Default render-pipeline notes](../../../architecture/rendering/default-render-pipeline-notes.md)
- [Mesh submission strategies](../../../architecture/rendering/mesh-submission-strategies.md)
- [Vulkan parallel command-chain design](../../design/rendering/vulkan-parallel-command-chain-refactor-design.md)

The motion investigation owns the already-confirmed skinning defects and their
OpenGL validation. This todo does not reopen those findings. It owns the work
needed for Vulkan to execute the same GPU physics-chain features, with the same
observable behavior and failure policy, as OpenGL.

## Goal

Implement the complete GPU physics-chain path on Vulkan and establish engine-
level parity with OpenGL for:

- batched GPU simulation;
- GPU-authored active-work compaction and indirect dispatch;
- zero-readback GPU skin-palette publication;
- partial and complete GPU-driven skin palettes;
- GPU animated bounds publication;
- optional asynchronous particle, bone, socket, and transform readback;
- `GpuSyncToBones` transform application;
- arena growth and in-flight resource retirement;
- standalone, non-batched GPU chains;
- GPU debug visualization; and
- diagnostics, telemetry, lifecycle, and failure semantics.

Parity means matching engine behavior, not copying OpenGL calls into Vulkan.
Vulkan work must be recorded in submission order, use explicit resource usage
and synchronization, retain every referenced resource until completion, and
remain nonblocking during normal frame processing.

## Confirmed Current Baseline

The skinned-mesh matrix, inverse-bind ordering, and GPU-palette ownership bugs
identified in the linked investigation have been corrected in backend-neutral
code. OpenGL now demonstrates both intended scenarios:

- zero-readback: `UseGpuDrivenSkinning=true`, `GpuSyncToBones=false`;
- CPU mirror: `UseGpuDrivenSkinning=false`, `GpuSyncToBones=true`.

Vulkan still cannot run either scenario correctly because the dispatcher stops
before simulation:

1. `GPUPhysicsChainDispatcher.ResolveComputeBackend` creates only
   `OpenGLPhysicsChainComputeBackend`.
2. `IPhysicsChainComputeBackend` has no success-returning direct-dispatch
   operation. Direct passes call `XRRenderProgram.DispatchCompute`, whose
   renderer API returns `void`.
3. Vulkan direct compute compilation and enqueue can be deferred or skipped
   because the program is pending or no render-graph pass can be resolved. The
   dispatcher cannot observe that failure and may advance request state anyway.
4. Vulkan has an ordered `ComputeDispatchOp` and `MemoryBarrierOp`, but no
   equivalent ordered frame operations for compute indirect dispatch, generic
   buffer-range copy, or a submission completion marker.
5. `AbstractRenderer.InsertGpuFence` returns `null` by default and Vulkan does
   not override it.
6. The existing Vulkan readback helpers are synchronous diagnostic mechanisms,
   not the asynchronous staging-slot contract required by physics chains.
7. `PhysicsChainComputeCapabilities.SupportsRequiredPipeline` requires async
   readback even when a request is explicitly zero-readback.
8. `PhysicsChainComponent.GPU.cs` still contains direct OpenGL barriers, buffer
   copies, sync objects, readback, and debug-render assumptions in the
   standalone path.

The current unsupported diagnostic is therefore accurate: Vulkan has no
physics-chain backend adapter, and no CPU fallback is used. Removing that
diagnostic without implementing the ordered operations below would hide lost
GPU work rather than fix it.

## Current Operation Matrix

| Required operation | OpenGL today | Vulkan today | Parity requirement |
|---|---|---|---|
| Direct compute dispatch | Program use plus immediate GL dispatch | Deferred `ComputeDispatchOp`, `void` result | Success-returning ordered enqueue |
| Compute indirect dispatch | `DispatchComputeIndirect` | No physics-chain operation | Ordered `vkCmdDispatchIndirect` frame op |
| Buffer-range copy | `CopyNamedBufferSubData` | Upload-oriented immediate helpers exist | Ordered `vkCmdCopyBuffer` in the same frame stream |
| Compute/transfer barriers | GL memory barriers | Generic Vulkan memory barrier op | Correct stages/access for each producer and consumer |
| Completion fence | GL sync object | No `InsertGpuFence` override | Submission-resolved timeline-backed `XRGpuFence` |
| Async readback | Copy, fence, poll, read | Diagnostic readback only | Pooled host-visible staging, poll, invalidate, copy |
| Arena retirement | Fence or retain until reset | No physics-chain fence | Timeline-safe release after the referencing submission |
| Backend capability | Hardcoded GL adapter | No adapter | Runtime Vulkan capability report with precise reasons |
| Standalone mode | OpenGL-specific | Returns early | Shared backend contract or equivalent Vulkan path |
| GPU debug chain | OpenGL-specific barrier/render path | Not supported | Backend-neutral dispatch and draw handoff |

## Non-Negotiable Contracts

- Explicitly requested GPU physics must never silently run a CPU simulation
  fallback.
- A request is not marked submitted or processed until every required operation
  has been accepted into one ordered Vulkan submission stream.
- No normal path may use `WaitForGpu`, `vkDeviceWaitIdle`, a per-pass one-time
  command buffer, or a synchronous queue submit.
- Buffer copies must not use the upload helper's immediate command scope when
  their source is produced by deferred frame compute; doing so breaks ordering.
- Steady-state dispatch, barrier, copy, fence polling, and readback scheduling
  must allocate zero managed heap memory after warmup.
- Missing program readiness, pass identity, descriptor binding, buffer usage,
  fence support, or staging support must leave work pending or fail it with an
  actionable diagnostic. It must not be reported as successfully dispatched.
- OpenGL behavior must remain functional throughout the refactor.
- Vulkan validation-layer errors, synchronization errors, device loss, and
  resource lifetime leaks are release blockers.
- CPU bone mirroring remains opt-in. Visible zero-readback deformation must not
  acquire a hidden readback through skinning, bounds, or culling.
- Renderer switch, pipeline recreation, arena growth, scene unload, shutdown,
  and device loss must invalidate or retire old work without consuming stale
  results.

## Parity Scenarios

| Scenario | Required component state | Required Vulkan result |
|---|---|---|
| Batched zero-readback skinning | `UseGPU=true`, batched enabled, `UseGpuDrivenSkinning=true`, `GpuSyncToBones=false` | GPU solve publishes the final palette and bounds; mesh follows the chain with zero CPU readback |
| Batched Sync To Bones | `UseGPU=true`, batched enabled, `UseGpuDrivenSkinning=false`, `GpuSyncToBones=true` | Async readback updates CPU transforms; ordinary renderer palette dirties deform the mesh |
| Complete driven palette | Every renderer bone is chain-owned | Palette pass writes the complete slice without a CPU seed copy |
| Partial driven palette | Only some renderer bones are chain-owned | Existing palette rows are copied GPU-to-GPU, then chain rows overwrite their mapped elements |
| Sleeping/inactive chains | GPU active-work generation omits inactive trees | Indirect bucket counts become zero or reduced without a CPU decision |
| Mixed kernels | Short/linear and branched/long trees coexist | Both indirect solver buckets run with correct offsets and arguments |
| Arena growth | Live data exceeds a current arena capacity | Old contents survive ordered copies and old buffers retire only after completion |
| Selective readback | Bone/socket/transform service requests exist | Gather, transfer, fence, poll, and CPU copy complete without blocking |
| Standalone GPU mode | Batched dispatcher disabled | Same simulation/readback/palette semantics work without OpenGL type checks |
| Debug visualization | GPU debug chain drawing enabled | Debug points/lines reflect the same particle state and do not introduce hazards |

## Target Execution Graph

The Vulkan command stream must preserve this logical sequence for each group:

| Order | Producer operation | Consumer or output |
|---:|---|---|
| 1 | Upload new CPU-authored inputs and perform any arena-preservation copies | Active-work and solver inputs |
| 2 | Reset active-work counters and indirect arguments | Active-work compaction |
| 3 | Compact active tree IDs into kernel buckets | Indirect-argument finalization |
| 4 | Finalize `x/y/z` dispatch arguments for each bucket | Indirect solver dispatches |
| 5 | Dispatch short/linear and branched/long solver buckets indirectly | Updated particles and transforms |
| 6 | Publish GPU bounds and copy them into GPU-scene command AABBs | GPU culling consumers |
| 7 | Seed partial palette slices with GPU buffer copies | Bone-palette compute |
| 8 | Build final GPU skin-palette rows | Compute skinning or vertex skinning |
| 9 | Optionally gather selective readback payloads | Readback transfer source |
| 10 | Copy selected or full particle payloads into staging resources | Host-visible readback slots |
| 11 | Insert a submission completion marker | Arena retirement and nonblocking CPU polling |

The implementation may group operations into fewer internal records, but it
must preserve the same dependencies and diagnostics.

## Required Vulkan Synchronization

Prefer resource-scoped buffer barriers where the affected buffer and range are
known. A global memory barrier may be used as an initial correctness milestone,
but it must map to the same stages/access and be replaced or justified before
performance closeout.

| Producer | Consumer | Source stage/access | Destination stage/access | Affected resources |
|---|---|---|---|---|
| CPU/upload transfer | Compute reads/writes | Transfer / transfer write | Compute / shader read and shader write | Particle static data, tree data, metadata, colliders, mappings |
| Arena preservation copy | Compute use | Transfer / transfer write | Compute / shader read and shader write | Replacement arena buffers |
| Active reset | Active compaction | Compute / shader write | Compute / shader read and shader write | Counters, active IDs, indirect args |
| Active compaction | Argument finalization | Compute / shader write | Compute / shader read and shader write | Counters, active IDs, indirect args |
| Argument finalization | Indirect dispatch | Compute / shader write | Draw-indirect / indirect-command read | Indirect argument buffer |
| Solver dispatch | Later compute | Compute / shader write | Compute / shader read and shader write | Particles, transforms, per-tree state |
| Solver or gather output | Buffer copy | Compute / shader write | Transfer / transfer read | Particles or packed readback output |
| Partial-palette copy | Palette compute | Transfer / transfer write | Compute / shader read and shader write | GPU skin-palette atlas |
| Bounds publication | GPU culling | Compute / shader write | Compute / shader read | Bounds atlas and GPU-scene command AABBs |
| Palette publication | Compute/vertex skinning | Compute / shader write | Compute and vertex shader / shader read | Current and previous skin palettes |
| Readback copy | Host read | Transfer / transfer write | Host / host read | Full and selective staging buffers |

`EMemoryBarrierMask.Command` already expresses compute/transfer writes to
indirect-command reads. Verify its Vulkan mapping with tests. The current
`ClientMappedBuffer` mapping is host-write to GPU-read oriented and is not a
valid transfer-write to host-read dependency. Add an explicit readback barrier
contract rather than overloading that mask ambiguously.

## Resource Usage And Lifetime Requirements

- Particle, transform, metadata, active-list, bounds, mapping, inverse-bind,
  and palette buffers require storage-buffer usage.
- Any buffer copied from requires `TransferSrc`; any buffer copied into requires
  `TransferDst`.
- The dispatch-argument buffer requires `IndirectBuffer` in addition to storage
  and any required transfer usage.
- Readback staging requires `TransferDst` plus host-visible memory. Prefer
  host-cached memory; support coherent fallback and explicit invalidation for
  noncoherent memory.
- A captured compute or copy operation must retain the immutable Vulkan buffer
  handle/generation it references. Reallocation after capture must not rewrite
  the meaning of an already queued operation.
- Descriptor snapshots, cached primaries, command-chain secondaries, and
  retirement tickets must all agree on the same resource generation.
- Replacement arena buffers must be published atomically to later operations;
  superseded buffers remain alive until the submission containing their final
  use has completed.
- Mapped staging slots are reusable only after their fence is signaled and the
  CPU consumer has released the lease.
- Device loss marks pending fences failed, invalidates staging leases, clears
  pending timeline markers, and prevents any stale payload from being applied.

## Architectural Decisions To Record Before Implementation

- [ ] Choose the ordered-operation shape. Preferred: reusable generic Vulkan
  frame ops for buffer copy, compute indirect dispatch, and submission markers,
  plus the existing direct compute and memory-barrier ops. Use a composite
  physics batch only if command-chain ordering cannot safely represent the
  dependencies, and document why.
- [ ] Decide where success-returning compute enqueue belongs. Preferred: add a
  reusable renderer-level `TryDispatchCompute` surface and let the thin physics
  backend adapter consume it; do not expose raw Silk.NET calls to the
  dispatcher.
- [ ] Split core, zero-readback, and readback capability gates. Async readback
  must not be required to run a zero-readback request.
- [ ] Define a submission marker whose timeline value is assigned from the
  actual queue submission. Do not guess `_graphicsTimelineValue + 1`; acquire,
  bridge, or other submission signals can advance the timeline.
- [ ] Decide whether the new copy and indirect operations are safe in reusable
  secondary command buffers. If proof is incomplete, classify them as primary-
  only/volatile first rather than allowing unsafe lowering.
- [ ] Define how a direct dispatch reports `Ready`, `ProgramPending`,
  `NoPassContext`, `DescriptorInvalid`, `DeviceLost`, or `Enqueued` without
  allocating or logging every frame.
- [ ] Define the staging memory contract for persistent mappings, coherent
  memory, noncoherent invalidation, and range alignment to
  `nonCoherentAtomSize`.
- [ ] Decide whether standalone physics chains become clients of the same
  backend adapter or are migrated onto the batched dispatcher. Do not maintain
  a second Vulkan-specific synchronization implementation without a documented
  reason.

## Phase 0 - Implementation Baseline And Inventory

- [ ] Create a bounded run root under
  `Build/_AgentValidation/<timestamp>-vulkan-physics-chain-parity/` with
  `logs/`, `reports/`, `mcp-captures/`, `mcp-output/`, and `renderdoc/`.
- [ ] Preserve unrelated working-tree changes and record the exact source
  baseline before implementation begins.
- [ ] Extend the linked investigation rather than creating a duplicate issue
  ledger.
- [ ] Capture the two corrected Math Intersections scenarios on OpenGL from at
  least two times/camera positions as the visual reference.
- [ ] Record OpenGL dispatch counts, barrier counts, copy bytes, readback bytes,
  readback latency, arena capacities, active bucket counts, and palette/bounds
  publication counters.
- [ ] Capture Vulkan's current unsupported-backend diagnostic and prove that no
  hidden CPU fallback occurs.
- [ ] Inventory every direct dispatch, indirect dispatch, buffer copy, pass
  completion, fence insertion, and readback call in the batched and standalone
  paths.
- [ ] Inventory all resource targets, memory policies, storage/range flags, and
  required Vulkan usage flags before changing allocation behavior.
- [ ] Inventory current command-chain sorting, signatures, volatility,
  secondary recording, descriptor snapshots, and retirement behavior for
  `ComputeDispatchOp` and `MemoryBarrierOp`.
- [ ] Run `rdc doctor` before GPU capture work. The CLI was unavailable during
  the 2026-07-22 investigation, so also verify the installed
  `C:\Program Files\RenderDoc\renderdoccmd.exe` fallback and Vulkan layer
  registration.

Acceptance criteria:

- [ ] The OpenGL reference, Vulkan failure state, operation inventory, buffer
  usage inventory, and tooling state are recorded before implementation.
- [ ] Every current physics-chain operation has a named Vulkan implementation
  task below.

## Phase 1 - Refine The Backend-Neutral Contract

- [ ] Add a success-returning direct-dispatch operation to
  `IPhysicsChainComputeBackend`.
- [ ] Route every dispatcher direct compute call through that operation,
  including active reset, compaction, argument generation, bounds, palette,
  and readback gather.
- [ ] Return an explicit result/status when useful; a bare `false` must still
  produce one rate-limited diagnostic identifying the failed operation and
  reason.
- [ ] Keep indirect dispatch, buffer copy, pass completion, fence, and readback
  operations in the same narrow contract or replace them with a cleaner
  renderer compute-work interface used by both adapters.
- [ ] Split capabilities into at least:
  - [ ] direct compute and storage synchronization;
  - [ ] GPU buffer copies;
  - [ ] indirect compute dispatch;
  - [ ] zero-readback palette/bounds publication;
  - [ ] submission fences;
  - [ ] asynchronous host readback; and
  - [ ] optional subgroup arithmetic.
- [ ] Define separate predicates for the core GPU pipeline and readback-
  requiring modes. Remove the unconditional async-readback requirement from
  zero-readback dispatch.
- [ ] Extend backend status with the missing capability or enqueue reason while
  preserving `CpuFallbackUsed=false`.
- [ ] Replace the hardcoded OpenGL resolution expression with a small factory
  or ordered adapter resolver that supports OpenGL and Vulkan without leaking
  backend types throughout the dispatcher.
- [ ] Ensure a renderer change bumps backend/readback epochs, disposes or fails
  incompatible in-flight work, and reevaluates capabilities.
- [ ] Do not advance submission IDs, clear pending work, publish palette
  ownership, or commit a staging lease when any required enqueue fails.

Acceptance criteria:

- [ ] OpenGL still executes every existing mode through the refined contract.
- [ ] A Vulkan program-pending or no-pass condition is observable and cannot be
  mistaken for a completed dispatch.
- [ ] Vulkan can advertise core zero-readback readiness independently of async
  readback readiness.

## Phase 2 - Add Ordered Vulkan Compute Operations

- [ ] Add a success-returning wrapper around direct compute preparation and
  `ComputeDispatchOp` enqueue.
- [ ] Add a `ComputeDispatchIndirectOp` or equivalent that captures:
  - [ ] pass/frame context;
  - [ ] linked compute program and descriptor snapshot;
  - [ ] immutable argument-buffer handle/generation;
  - [ ] validated byte offset and `VkDispatchIndirectCommand` range; and
  - [ ] retained resource ownership.
- [ ] Record the operation with `vkCmdDispatchIndirect` outside an active render
  pass.
- [ ] Add a generic ordered `BufferCopyOp` or equivalent containing immutable
  source/destination handles, offsets, size, resource generations, and context.
- [ ] Validate nonzero handles, bounds, nonnegative offsets, overlap rules,
  required `TransferSrc`/`TransferDst` usage, and device state before enqueue.
- [ ] Record copies with `vkCmdCopyBuffer` in the same command stream as the
  producing and consuming compute operations.
- [ ] Add resource-scoped compute/transfer/indirect/host barriers or extend the
  existing barrier operation to express the table above exactly.
- [ ] Preserve the enqueue order of compute, copy, barrier, indirect, and marker
  operations through render-graph sorting.
- [ ] Add pass resolution behavior for physics work scheduled from
  `GlobalPreRender`. A missing active graph pass must remain a visible failure,
  not cause operations to float into an unrelated pass.
- [ ] Integrate new operation types into:
  - [ ] frame-op diagnostics and labels;
  - [ ] operation counts and failure telemetry;
  - [ ] frame-op signatures and reusable-primary invalidation;
  - [ ] command-chain lowering and eligibility;
  - [ ] volatility classification;
  - [ ] resource planning and immutable snapshot retention;
  - [ ] primary command recording; and
  - [ ] secondary recording only after it is proven safe.
- [ ] Ensure a failed pipeline link or descriptor bind is associated with the
  originating operation/request instead of disappearing only into a renderer
  warning.
- [ ] Keep these operations reusable by other GPU compute systems; do not name
  renderer frame ops after physics chains unless they truly encode a composite
  physics-specific batch.

Acceptance criteria:

- [ ] A captured operation trace proves direct dispatch, barriers, argument
  generation, indirect dispatch, later compute, copy, and marker retain source
  order.
- [ ] No immediate one-shot command scope or queue wait is used by a physics-
  chain copy.
- [ ] Cached primary/secondary decisions cannot replay stale handles,
  descriptors, offsets, or dispatch arguments.

## Phase 3 - Implement The Vulkan Physics Backend Adapter

- [ ] Add `VulkanPhysicsChainComputeBackend` as a thin adapter over reusable
  Vulkan renderer operations.
- [ ] Resolve only an initialized, non-device-lost Vulkan renderer with a
  compute-capable selected queue and all required commands/features.
- [ ] Populate capabilities from the actual enabled device/queue/runtime state;
  do not hardcode support merely because Vulkan types compile.
- [ ] Implement direct dispatch through the success-returning ordered API.
- [ ] Implement indirect dispatch through the new ordered indirect operation.
- [ ] Implement buffer readiness by generating the `VkDataBuffer`, allocating
  storage, and verifying its actual usage flags.
- [ ] Implement ordered byte-range copies without using upload-specific
  immediate helpers.
- [ ] Implement pass completion with operation-specific Vulkan dependencies.
- [ ] Initially report fence/readback unsupported until Phase 5 is complete;
  the zero-readback capability must still be usable.
- [ ] Add rate-limited diagnostics for missing usage, missing pass context,
  pending programs, invalid descriptors, bad offsets/ranges, queue mismatch,
  and device loss.
- [ ] Update dispatcher backend resolution and status text so a ready Vulkan
  adapter is selected and a partially capable adapter explains which mode is
  unavailable.

Acceptance criteria:

- [ ] Vulkan resolves a backend for zero-readback requests and no longer emits
  the generic "not implemented" diagnostic for that supported mode.
- [ ] A readback-requiring request remains pending or explicitly unavailable
  until Phase 5; it never falls back to CPU simulation.
- [ ] OpenGL and Vulkan expose the same backend-neutral operation semantics.

## Phase 4 - Make Batched Zero-Readback Functional

- [ ] Run arena allocation, upload, and growth through Vulkan-valid buffer
  usage and ordered copies.
- [ ] Execute active-work reset, compaction, and indirect-argument finalization.
- [ ] Execute both indirect solver buckets, including zero-work argument values
  and nonzero offsets into the shared argument buffer.
- [ ] Verify particle/transform writes are visible to every subsequent pass.
- [ ] Publish GPU bounds and make bounds-atlas writes visible to GPU-scene
  culling consumers.
- [ ] Preserve and seed partial palettes with ordered GPU buffer copies.
- [ ] Publish complete and partial skin palettes with the corrected row-matrix
  convention and inverse-bind composition.
- [ ] Make palette output visible to both compute skinning and direct vertex
  skinning.
- [ ] Preserve current/previous palette behavior required by temporal rendering
  and motion vectors.
- [ ] Publish renderer GPU-palette ownership only after all required operations
  enqueue successfully.
- [ ] Verify arena replacement does not invalidate descriptor snapshots already
  captured by queued compute operations.
- [ ] Verify sleeping chains and mixed kernel buckets do not require a CPU
  active-tree readback.
- [ ] Functionally validate the Math Intersections zero-readback scenario before
  adding broad regression tests, following the repository testing policy.

Acceptance criteria:

- [ ] The Vulkan zero-readback skinned prism visibly follows and deforms with
  the simulated chain over multiple frames and camera views.
- [ ] No physics-chain CPU readback bytes are recorded for the scenario.
- [ ] GPU bounds follow the animated mesh without a CPU bounds rebuild.
- [ ] No dispatch, copy, descriptor, synchronization, or validation failure is
  logged.

## Phase 5 - Add Timeline Fences And Async Readback

- [ ] Implement a Vulkan `XRGpuFence` backed by the graphics submission
  timeline used by the command stream containing the physics work.
- [ ] Enqueue a lightweight completion marker and bind the fence to the actual
  timeline value assigned during submission.
- [ ] Define fence states for not-yet-submitted, pending, signaled, failed,
  disposed, renderer-reset, and device-lost cases.
- [ ] Make `Poll()` nonblocking and allocation-free by using existing timeline
  counter queries such as `HasTimelineValueCompleted`.
- [ ] Never call the blocking timeline wait from normal physics completion
  polling.
- [ ] Resolve or fail every pending marker during submit failure, frame abort,
  renderer recreation, shutdown, and device loss.
- [ ] Use timeline fences for superseded arena-resource retirement.
- [ ] Implement pooled full-particle readback buffers with `TransferDst` and
  host-visible readback memory.
- [ ] Implement the selective gather path:
  - [ ] upload gather items;
  - [ ] dispatch `PhysicsChainReadbackGather.comp`;
  - [ ] add compute-write to transfer-read synchronization;
  - [ ] copy packed output to the leased staging slot;
  - [ ] add transfer-write to host-read synchronization; and
  - [ ] attach the actual submission fence to the lease.
- [ ] Invalidate noncoherent mapped ranges after the fence signals and before
  copying bytes to managed storage. Normalize ranges to
  `nonCoherentAtomSize`.
- [ ] Verify coherent memory takes the safe no-op invalidation path.
- [ ] Keep staging buffers mapped only under the established memory policy and
  never read their addresses before completion.
- [ ] Preserve readback source epochs and reject stale backend, arena, layout,
  execution-generation, or submission IDs.
- [ ] Recycle staging/readback slots only after GPU completion and CPU lease
  release.
- [ ] Apply `GpuSyncToBones` transforms on the existing completion path and
  ensure renderer palette dirties are honored when GPU ownership is disabled.
- [ ] Record submitted bytes, completed bytes, latency frames, pool pressure,
  stale drops, failures, and fence age without log spam.
- [ ] Functionally validate Sync To Bones before adding broad tests.

Acceptance criteria:

- [ ] The Vulkan Sync To Bones scenario visibly follows the chain without a
  render-thread or device-wide wait.
- [ ] Readback normally completes one or more frames later through polling.
- [ ] Noncoherent and coherent staging paths both return correct payloads.
- [ ] Arena retirement and readback fences cannot signal before the submission
  that contains their preceding operations.

## Phase 6 - Establish Standalone And Debug Parity

- [ ] Remove direct `OpenGLRenderer`, `RawGL`, `GLDataBuffer`, and GL sync-object
  dependencies from `PhysicsChainComponent.GPU.cs`.
- [ ] Route standalone direct dispatch, barriers, copy, fence, polling, and
  readback through the shared backend contract or migrate standalone execution
  onto the batched scheduler.
- [ ] Replace `IntPtr` GL fences in standalone in-flight records with
  `XRGpuFence` ownership.
- [ ] Preserve standalone bandwidth telemetry and execution/submission
  generation checks.
- [ ] Support standalone zero-readback palette publication on Vulkan.
- [ ] Support standalone async readback and `GpuSyncToBones` on Vulkan.
- [ ] Refactor GPU debug generation to use backend-neutral compute barriers and
  render buffers.
- [ ] Verify debug point/line rendering consumes the newly written buffers only
  after a compute-to-vertex/graphics dependency.
- [ ] Ensure cleanup works when the active renderer changes after resources or
  fences were created.

Acceptance criteria:

- [ ] No standalone or debug method returns early merely because the active
  renderer is Vulkan.
- [ ] OpenGL and Vulkan standalone modes produce equivalent particle, palette,
  readback, and debug behavior.
- [ ] One shared synchronization model owns batched and standalone completion.

## Phase 7 - Validate Shaders, Reflection, And Descriptors

Every physics-chain compute shader must compile through the Vulkan GLSL 450 to
SPIR-V path and bind the intended resources:

- `PhysicsChain.comp`
- `PhysicsChainBranched.comp`
- `SkipUpdateParticles.comp`
- `PhysicsChainActiveWork.comp`
- `PhysicsChainBounds.comp`
- `PhysicsChainBoundsToScene.comp`
- `PhysicsChainBonePalette.comp`
- `PhysicsChainReadbackGather.comp`
- `PhysicsChainDebugDraw.comp`

Tasks:

- [ ] Compile every shader with the same include expansion, Vulkan rewrites,
  optimization, cache identity, and reflection path used at runtime.
- [ ] Verify local sizes and direct/indirect group-count assumptions.
- [ ] Verify every SSBO set/binding, descriptor type, writable/read-only intent,
  array stride, alignment, and minimum range.
- [ ] Verify standalone scalar uniforms are represented correctly by Vulkan's
  auto-generated uniform-buffer or push-constant path.
- [ ] Verify descriptor snapshots capture the buffer generation intended for
  each dispatch and are not overwritten by later program rebinding.
- [ ] Preserve the corrected matrix convention in
  `PhysicsChainBonePalette.comp`: reconstruct shader column-form bone world,
  transpose to the engine row-vector convention, then compose with the
  explicitly row-major inverse bind.
- [ ] Verify CPU structs and SPIR-V layouts for particles, trees, colliders,
  mappings, affine palette rows, bounds, gather items, and indirect arguments.
- [ ] Ensure descriptor failure prevents the associated request from being
  reported as dispatched.
- [ ] Add stable debug labels containing the shader/pass and request/group
  identity needed to find every compute operation in RenderDoc.

Acceptance criteria:

- [ ] All nine shaders compile and reflect without fallback or missing bindings.
- [ ] Shader layout tests and a captured Vulkan frame agree on every descriptor
  and buffer range.
- [ ] The OpenGL matrix fix remains unchanged and produces the same pose on
  Vulkan within the agreed numeric tolerance.

## Phase 8 - Lifecycle, Reuse, And Recovery Hardening

- [ ] Validate first allocation, growth, shrink/reset policy, and reuse for all
  dispatcher arenas and staging pools.
- [ ] Validate partial-palette source replacement while prior frames remain in
  flight.
- [ ] Validate descriptor-cache invalidation when an arena handle/generation or
  palette slice changes.
- [ ] Validate reusable primary command buffers refresh frame-dependent
  descriptors and indirect argument resources correctly.
- [ ] Validate command-chain secondary recording or enforce the documented
  primary-only classification.
- [ ] Validate renderer backend switching between OpenGL and Vulkan without
  applying old completions to the new backend.
- [ ] Validate scene/world unload with queued requests, pending staging leases,
  and deferred arena resources.
- [ ] Validate swapchain resize and render-pipeline recreation while physics
  work is pending.
- [ ] Validate desktop and available OpenXR/OpenVR frame-slot/timeline ownership;
  a fence must track the queue submission that actually contains the work.
- [ ] Validate device loss before enqueue, after enqueue/before submit, while in
  flight, and after signal/before CPU consumption.
- [ ] Ensure shutdown releases descriptors, mappings, staging resources,
  timeline markers, and retained buffers exactly once.
- [ ] Add bounded diagnostics for pending-work age, oldest fence age, pool high
  water, deferred bytes, failed markers, and stale result drops.

Acceptance criteria:

- [ ] No stale resource handle or result survives a generation boundary.
- [ ] No pending resource is destroyed while referenced by a command buffer or
  submission.
- [ ] Device loss and shutdown terminate all pending work without hangs,
  double-free, or false success.

## Phase 9 - Automated Regression Coverage

Add these tests only after the corresponding functional path above works.

- [ ] Backend contract tests:
  - [ ] OpenGL and Vulkan adapter selection;
  - [ ] core versus readback capability predicates;
  - [ ] unsupported reason and `CpuFallbackUsed=false`;
  - [ ] direct-dispatch success and program-pending/no-pass failure; and
  - [ ] renderer-switch epoch invalidation.
- [ ] Ordered-operation model tests:
  - [ ] copy range and usage validation;
  - [ ] indirect argument offset/alignment/range validation;
  - [ ] compute/copy/barrier/indirect/marker ordering;
  - [ ] frame-op signatures, volatility, and command-chain eligibility;
  - [ ] primary/secondary recording policy; and
  - [ ] immutable resource generation retention.
- [ ] Synchronization mapping tests:
  - [ ] shader write to shader read/write;
  - [ ] shader write to indirect-command read;
  - [ ] shader write to transfer read;
  - [ ] transfer write to shader read/write;
  - [ ] palette write to vertex/compute shader read; and
  - [ ] transfer write to host read.
- [ ] Timeline-fence tests:
  - [ ] marker remains pending before submit;
  - [ ] marker receives the actual submission value;
  - [ ] nonblocking pending/signaled polling;
  - [ ] submit failure, reset, disposal, and device-loss failure; and
  - [ ] no premature arena or staging reuse.
- [ ] Readback tests:
  - [ ] full and selective payload layout;
  - [ ] coherent and noncoherent invalidation;
  - [ ] epoch/generation/submission stale rejection;
  - [ ] pool exhaustion and recovery;
  - [ ] latency and completion ordering; and
  - [ ] no readback in zero-readback mode.
- [ ] Shader/layout tests for all nine shaders, SPIR-V compilation, SSBO
  reflection, struct stride, indirect command layout, and the palette matrix
  convention.
- [ ] Dispatcher integration tests for active reset/compact/finalize, both
  indirect buckets, arena growth, bounds, complete palette, partial palette,
  selective readback, and no-fallback behavior.
- [ ] Preserve and extend the existing rotated-parent inverse-bind,
  `UseGpuDrivenSkinning`, and shader-source regression tests.
- [ ] Add standalone Vulkan contract tests after the standalone migration is
  functional.

Focused validation commands:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~GPUPhysicsChainDispatcher|FullyQualifiedName~PhysicsChainComponentGpuMode|FullyQualifiedName~PhysicsChainShaderContract|FullyQualifiedName~MathIntersectionsPhysicsChainSkinning|FullyQualifiedName~VulkanCommandChainDataModel" --no-restore /p:UseSharedCompilation=false
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly
```

Broader gates after focused validation is clean:

- [ ] Run the `Test-VulkanPhase3-Regression` task.
- [ ] Run the broader Vulkan unit-test filter.
- [ ] Run the full unit-test project and solution build before closeout.
- [ ] Separate unrelated failures and pre-existing warnings in the evidence
  ledger; do not mask new warnings.

Acceptance criteria:

- [ ] Every new operation and synchronization edge has deterministic coverage.
- [ ] Both user-visible skinning scenarios have automated contract coverage.
- [ ] OpenGL regressions are caught by the same backend-neutral tests.

## Phase 10 - Live Editor And RenderDoc Validation

Use the existing investigation as the durable evidence ledger and follow the
repository editor-iteration loop.

### Editor protocol

- [ ] Build the editor so the running binary matches source.
- [ ] Configure the Unit Testing World for Vulkan and the Math Intersections
  physics-chain skinned scenarios.
- [ ] Launch from the repository root with Unit Testing World and MCP enabled:

  ```powershell
  dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467
  ```

- [ ] Isolate `Physics Chain GPU Dispatcher Skinned Mesh Test` and verify
  `UseGpuDrivenSkinning=true`, `GpuSyncToBones=false`.
- [ ] Capture at least two times and two camera positions; visually verify the
  mesh and debug chain move together.
- [ ] Isolate `Physics Chain GPU Dispatcher Skinned Mesh Sync To Bones Test` and
  verify `UseGpuDrivenSkinning=false`, `GpuSyncToBones=true`.
- [ ] Capture at least two times and two camera positions; visually verify CPU
  transform updates drive ordinary skinning.
- [ ] Repeat complete palette, partial palette, sleeping, mixed-kernel, arena-
  growth, standalone, and debug scenarios.
- [ ] Inspect screenshots rather than relying on MCP success responses.
- [ ] Review `log_vulkan.log`, `log_rendering.log`, and `log_physics.log` for
  skipped dispatches, missing descriptors, invalid usage, stale generations,
  fence/readback failures, validation messages, and device loss.
- [ ] Separate shutdown-only teardown noise from steady-state frame failures.
- [ ] Repeat the validated Vulkan scenarios on OpenGL to prove the refactor did
  not regress the reference backend.

### RenderDoc protocol

- [ ] Run `rdc doctor`. If unavailable, use the installed RenderDoc command
  line fallback and verify its Vulkan implicit layer is registered.
- [ ] Store captures and all exports under the run root's `renderdoc/` folder.
- [ ] Capture from the repository root so assets and settings resolve:

  ```powershell
  rdc capture -o "<RunRoot>\renderdoc\physics-chain-vulkan.rdc" -- dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467
  ```

- [ ] If `rdc` remains unavailable, use:

  ```powershell
  & "C:\Program Files\RenderDoc\renderdoccmd.exe" capture -w -d . -c "<RunRoot>\renderdoc\physics-chain-vulkan.rdc" dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467
  ```

- [ ] Open the capture in an open-work-close session; always close it when done.
- [ ] Start with bounded `info`, `stats`, `passes`, draws, and events output.
- [ ] Locate debug labels for active reset, compaction, argument finalization,
  both indirect solver buckets, bounds, palette, gather, copy, and marker.
- [ ] Inspect compute pipelines, SPIR-V, descriptor bindings, buffer ranges,
  push/auto-uniform values, and dispatch dimensions at each relevant event.
- [ ] Verify the GPU-authored indirect commands contain expected group counts
  and that `vkCmdDispatchIndirect` reads the intended offsets.
- [ ] Inspect particle, transform, bounds, palette, and staging buffer contents
  before and after their producing operations using RenderDoc's buffer viewer
  or available structured export.
- [ ] Debug a representative compute thread when a buffer first diverges.
- [ ] Export the final render target to PNG and visually inspect it. Also export
  any relevant intermediate visual target rather than relying only on numeric
  event data.
- [ ] Capture moving frames before and after a known pose change and use frame
  comparison to prove palette/mesh output changes.
- [ ] Close the `rdc` session to release replay GPU resources.

Acceptance criteria:

- [ ] Captures prove the intended operation order, bindings, indirect values,
  barriers, and resource contents.
- [ ] Screenshots prove visible movement from multiple times and views.
- [ ] Validation layers report zero new VUIDs or synchronization hazards during
  steady-state operation.

## Phase 11 - Performance, Documentation, And Closeout

- [ ] Compare Vulkan against the Phase 0 OpenGL reference for:
  - [ ] direct and indirect dispatch count;
  - [ ] barrier and buffer-copy count;
  - [ ] CPU upload, GPU copy, and CPU readback bytes;
  - [ ] readback latency and pool high water;
  - [ ] arena capacity, growth count, and deferred bytes;
  - [ ] render-thread CPU time and GPU pass time;
  - [ ] descriptor allocation/reuse;
  - [ ] command-buffer reuse; and
  - [ ] steady-state managed allocations.
- [ ] Confirm zero-readback mode records zero physics-chain readback bytes and no
  CPU hierarchy-recalculation time attributable to chain mirroring.
- [ ] Confirm Sync To Bones has bounded multi-frame latency and does not wait on
  the render thread.
- [ ] Confirm no per-frame one-shot command pools, submits, fences, staging
  allocations, arrays, lists, LINQ, closures, or strings were introduced.
- [ ] Profile representative single-chain, mixed-chain, sleeping-heavy, and
  thousands-scale workloads.
- [ ] Validate desktop and every available OpenXR/OpenVR mode; record unavailable
  hardware/runtime cohorts explicitly rather than claiming them tested.
- [ ] Update Vulkan/OpenGL renderer architecture docs with the final ordered
  compute, copy, fence, and readback contracts.
- [ ] Update the physics-chain zero-readback design from proposed to actual
  Vulkan behavior where applicable.
- [ ] Update skinning and `XRDataBuffer` docs for any public capability, usage,
  or staging-memory contract changes.
- [ ] Update settings, launch, or user docs only if user-visible flags or
  workflows change.
- [ ] Record final evidence paths and exact commands in the investigation; do
  not make durable behavior depend on ignored artifacts.

Acceptance criteria:

- [ ] Vulkan achieves the same supported physics-chain feature matrix as
  OpenGL with no hidden CPU fallback or global wait.
- [ ] Performance differences are measured and explained; no unbounded growth
  or hot-path allocation remains.
- [ ] Architecture, testing, and user-facing documentation matches the shipped
  behavior.

## Expected File Change Map

The implementation may split large partial classes further, but should keep
responsibilities cohesive.

| Area | Expected files or location | Intended change |
|---|---|---|
| Backend contract | `XRENGINE/Rendering/Compute/IPhysicsChainComputeBackend.cs` | Success-returning direct dispatch and refined operations |
| Capabilities/status | `PhysicsChainComputeCapabilities.cs`, `GPUPhysicsChainBackendStatus.cs` | Core/readback gates and actionable reasons |
| OpenGL adapter | `OpenGLPhysicsChainComputeBackend.cs` | Implement refined contract without behavior regression |
| Vulkan adapter | New `VulkanPhysicsChainComputeBackend.cs` near the other adapter | Thin bridge to reusable Vulkan renderer operations |
| Batched scheduler | `GPUPhysicsChainDispatcher*.cs` | Route all dispatch/copy/barrier/fence/readback work through the contract |
| Standalone scheduler | `PhysicsChainComponent.GPU.cs` | Remove OpenGL-specific execution and sync assumptions |
| Generic renderer API | `AbstractRenderer.cs` and `XRGpuFence.cs` only if needed | Reusable success/status and fence semantics |
| Vulkan enqueue | `VulkanRenderer.Initialization.cs`, `VulkanRenderer.FrameLoop.cs` or focused new partials | Direct/indirect/copy/barrier/marker enqueue APIs |
| Vulkan frame-op model | `BackendObjects/MeshRendering/` and `Commands/` | New immutable operation records and diagnostics |
| Vulkan recording | `VulkanRenderer.CommandBufferRecording.cs` and focused partials | `vkCmdDispatchIndirect`, `vkCmdCopyBuffer`, precise barriers, marker handling |
| Command chains | lowering, signatures, volatility, render-graph compiler, secondary recording | Ordering, reuse, and safe eligibility |
| Timeline/lifetime | `VulkanRenderer.SyncObjects.cs`, resource-retirement files | Submission-resolved `XRGpuFence` and retained resource release |
| Buffers/readback | `VkDataBuffer.cs`, allocator/readback partials | Usage validation, mapped staging, invalidation, host-read dependency |
| Shaders | `Build/CommonAssets/Shaders/Compute/PhysicsChain/` | Vulkan compilation/layout corrections only where required |
| Tests | `XREngine.UnitTests/Physics/` and `XREngine.UnitTests/Rendering/` | Contract, frame-op, sync, shader, readback, and scenario coverage |
| Durable docs | this todo, linked investigation, renderer/skinning/buffer docs | Design decisions, evidence, final behavior |

If the Vulkan command-buffer recording class must be touched substantially,
prefer adding focused partial-class files for compute indirect dispatch, buffer
copies, and submission markers instead of expanding the existing monolith.

## Principal Risks And Required Mitigations

| Risk | Required mitigation |
|---|---|
| Deferred direct dispatch is skipped but scheduler advances | Success-returning enqueue and transactional request commit |
| Copy runs in a separate immediate submission before compute | Same ordered frame stream; ban upload one-shot scope for this path |
| Indirect args are not visible | Explicit compute-write to indirect-read barrier and capture verification |
| Descriptor captures an old/replaced buffer | Immutable handle/generation snapshots and retirement tracking |
| Cached primary or secondary replays stale work | Signature/resource identity coverage and conservative volatility |
| Fence targets the wrong timeline value | Resolve marker from actual submit signal, never predict the next value |
| CPU reads noncoherent memory without invalidation | Fence first, aligned invalidate, then copy |
| Generic client-mapped barrier uses the wrong direction | Add explicit GPU-to-host readback dependency |
| Partial palette copy races palette compute | Transfer-write to compute shader dependency on the palette range |
| Palette output races skinning | Compute-write to vertex/compute shader-read dependency |
| Arena growth destroys an in-flight resource | Submission ticket retention and deferred destruction |
| No active render-graph pass silently drops work | Explicit enqueue failure and pending request diagnostic |
| Device loss leaves never-signaling fences | Fail all markers/leases and invalidate epochs during loss transition |
| Parallel secondary recording reorders barriers | Prove bucket boundaries or keep new operations primary-only |
| OpenGL regression during abstraction cleanup | Run the same scenario and contract matrix on both backends |

## Evidence Ledger Template

For each implementation iteration, record:

- source revision and working-tree caveats;
- renderer, GPU, driver, desktop/VR mode, and validation preset;
- exact settings and component flags;
- build/test commands and pass/fail counts;
- MCP screenshot paths from multiple times/views;
- Vulkan, rendering, and physics log session paths;
- RenderDoc capture and exported PNG paths;
- backend status and missing-capability diagnostics;
- dispatch/barrier/copy/readback/fence telemetry;
- visual result and whether mesh motion matches the debug chain;
- new VUIDs, warnings, device loss, stalls, or allocations;
- hypothesis, single changed variable, and outcome; and
- remaining work or explicit hardware/runtime validation gap.

## Definition Of Done

- [ ] Vulkan resolves a production physics-chain compute backend with accurate
  core and readback capability reporting.
- [ ] Every direct dispatch reports whether it was actually enqueued.
- [ ] Active-work generation and both solver buckets execute through ordered
  `vkCmdDispatchIndirect` calls.
- [ ] All GPU buffer copies execute in order through Vulkan frame operations.
- [ ] Every compute, transfer, indirect, graphics, and host dependency is
  correct and validation-clean.
- [ ] Timeline-backed `XRGpuFence` polling is nonblocking and tied to the actual
  containing submission.
- [ ] Zero-readback Vulkan skinning publishes correct complete/partial palettes
  and animated bounds with no CPU readback.
- [ ] Vulkan Sync To Bones completes through asynchronous staging and updates
  CPU transforms without a global wait.
- [ ] Standalone and GPU debug paths no longer contain an OpenGL-only behavior
  gap.
- [ ] Arena growth, renderer switch, pipeline recreation, world unload,
  shutdown, and device loss are safe.
- [ ] All physics-chain shaders compile and reflect correctly for Vulkan.
- [ ] Focused, broader Vulkan, full build/test, validation-layer, live editor,
  RenderDoc, performance, and available VR evidence is recorded.
- [ ] Both Math Intersections skinned-mesh scenarios visibly move correctly on
  Vulkan and remain correct on OpenGL.
- [ ] No silent CPU fallback, `WaitForGpu`, `vkDeviceWaitIdle`, per-pass
  one-shot submit, steady-state allocation, new warning, VUID, or device loss
  remains.
- [ ] Architecture and testing docs describe the final implementation, and no
  required proof depends solely on ignored validation artifacts.
