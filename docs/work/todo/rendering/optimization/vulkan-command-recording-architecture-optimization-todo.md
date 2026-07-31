# Vulkan Command Recording Architecture Optimization TODO

Last Updated: 2026-07-30
Owner: Rendering / Vulkan Command Buffers
Status: In Progress; First Binding/Data-Publication Slice Implemented; Full Acceptance Open

Current architecture:

- [Vulkan Primary And Secondary Command Recording](../../../../architecture/rendering/vulkan-command-recording.md)
- [Vulkan Primary Command-Buffer Reuse](../../../../architecture/rendering/vulkan-primary-command-buffer-reuse.md)

Predecessor:

- [05 - Vulkan Command Recording Worker Architecture](05-vulkan-command-recording-worker-architecture-todo.md)
  implemented persistent workers, per-worker/per-frame-slot command pools,
  immutable planner snapshots, deterministic merge, bounded waits, and worker
  quarantine. This TODO must extend that implementation rather than recreate it.

Related acceptance tracker:

- [01-08 Optimization Acceptance Closeout](../../../testing/rendering/01-08-optimization-acceptance-closeout.md)

Measured blocker:

- [Vulkan Editor Steady-Frame CPU Cost Investigation](../../../investigations/rendering/vulkan-editor-frame-time-spikes-2026-07-30.md)

Related data-path plan:

- [CPU Direct Fast Path](cpu-direct-fast-path-todo.md)

## Goal

Make stable Vulkan submission nearly free on the CPU by carrying immutable,
frequency-separated binding data and backend-ready draw artifacts across the
workstream-04 handoff; simplify the command-recording correctness model; reduce
all consumers and workers to immutable inputs; improve cache and lifetime
ownership; remove the OpenXR eye-recording serialization point; and widen
secondary eligibility only where measured performance and Vulkan correctness
justify it.

This is an optimization backlog, not a description of current behavior. No item
is complete merely because the design is valid.

## 2026-07-30 Steady-Frame Blocker

A stable Debug diagnostic frame at 647 render commands took 399.580 ms:

- frontend scene/package consumption: 172.468 ms;
- mesh draw preparation: 162.502 ms;
- material/program binding emission: 60.265 ms;
- binding snapshot copy: 55.187 ms;
- outer swap/present output: 226.904 ms;
- backend frame-data refresh: 170.201 ms;
- reflected auto-uniform upload: 120.188 ms;
- descriptor validation: 30.187 ms;
- actual native present call: 0.113 ms.

All 22 scheduled command chains and the primary command buffer were reused.
There were zero chain recordings, zero worker dispatches, zero pipeline misses,
and no validation messages. Managed allocation in the measured mesh hot stages
was effectively zero.

The 226.904 ms outer Present output is not a native-present wait. It contains
the backend scene-processing, frame-data-refresh, and submission lifecycle.
The measured native present call was only 0.113 ms.

The architecture must therefore optimize data construction and publication,
not command-recording worker utilization. Command-buffer reuse currently avoids
native encoding but still performs a full visible-draw refresh.

## 2026-07-30 Implementation Progress

The first Phase-1 migration slice is now implemented, but the phase is not yet
accepted:

- Normal non-shadow, no-callback material numeric parameters are captured into
  a persistent payload keyed by material layout/value/shader revisions and
  linked-program generation. Frame-local snapshots reference that immutable
  payload instead of copying its uniform dictionary every render frame.
- Auto-uniform writes compile a material revision plus reflected block into a
  cached byte template and a dynamic-member patch list. Qualifying draws copy
  the template and patch only render-scope members; they no longer perform a
  reflected member scan, material parameter lookup, and generic conversion for
  every material-owned member.
- Each qualifying auto-uniform block now remembers which compiled material plan
  was published into each stable buffer slot. An unchanged plan does not copy
  its static material bytes into that slot again. Dynamic member ranges are
  cleared and patched on each current draw so a missing or failed dynamic write
  cannot expose a stale value.
- A runtime uniform-name signature invalidates the compiled plan if scoped
  bindings override a material name. Callback, shadow, and other unclassified
  paths remain on the conservative capture/write path.
- Payload and plan caches are invalidated on material revision, program relink,
  block replacement, UBO destruction, and snapshot-content reuse.
- Allocation-free, frame-reset telemetry now reports material payload and
  frame-snapshot cache activity, payload/snapshot entry counts, parameter
  emissions and dictionary writes, auto-uniform plan hits/misses and byte/member
  traffic, fast/fallback draws, reusable frame-data draw visits, and descriptor
  records validated/written. The counters are available in the profiler stats
  packet, profile-capture NDJSON, and the MCP profiler `binding_data` group.

This deliberately does **not** claim the full frequency-domain architecture is
complete. The physical frame/view/pass/material/object storage split, dirty
owner queues, descriptor-owner migration, prepared-draw consumption, image
journals, and remaining acceptance cohorts below are still required.

### Wrap-up validation checkpoint

- `XREngine.Runtime.Rendering.Vulkan.csproj` and
  `XREngine.Editor.csproj` build with zero compiler errors.
- The focused command-recording/lifecycle/material-cache test selection passes
  53/53 tests.
- The corrected pre-telemetry Release CPU Direct run retained one stable
  workload identity across 96 samples. All 3,582 scheduled chains were reused;
  none were recorded.
- Its final six clean primary-reuse frames rendered in 7.242-9.746 ms overall,
  with 3.046-3.732 ms in the Vulkan frame and 1.655-1.887 ms in frame-data
  refresh. Each reused 43 chains, recorded none, and allocated zero bytes in
  command recording.
- That run is evidence of a much faster clean tail, not acceptance. Fifteen of
  96 captured frames re-recorded the primary, producing render p50/p95/p99 of
  17.726/152.954/195.214 ms and 27,370,648 total command-recording allocation
  bytes. Validation layers were not active, and the GPU timing dump failed.
- The final per-slot static-publication and telemetry additions are
  build/test-validated but have not yet been remeasured in a canonical runtime
  cohort.

### Checklist reconciliation

The checklist was reconciled against the 2026-07-30 source tree and targeted
tests after the first binding-cache slice. Checked items below mean the exact
task is implemented or the requested evidence exists; they do not promote a
parent phase whose acceptance criteria remain open.

Current command-recording ownership is:

| Responsibility | Current owner |
| --- | --- |
| Upcoming-frame selection and ordering | `BackendReadyFramePackage`, `BackendReadyMeshSelection`, and `XRRenderPipelineInstance` |
| Vulkan planner state and context publication | `VulkanRenderer.ResourcePlannerContext.cs` and `VulkanRenderer.ResourcePlannerSwitching.cs` |
| Frame operation carrying a live mesh draw | `MeshDrawOp` and `PendingMeshDraw` |
| Identity-oriented lowered mesh packet | `DrawPacket` and `VulkanRenderer.CommandChainLowering.cs` |
| Mutable renderer draw/refresh entry point | `VkMeshRenderer.RecordDraw` in `VkMeshRenderer.Drawing.cs` |
| Command-chain schedule/cache state | `CommandChain`, `CommandChainSchedule`, `VulkanRenderer.CommandChainLowering.cs`, and `VulkanRenderer.CommandBufferRecording.cs` |
| Primary cache variants | `VulkanRenderer.CommandBufferCacheVariant.cs` |
| Native buffer/resource lifetime tracking | `VulkanRenderer.CommandBufferTrackingBatch.cs`, `VulkanRenderer.CommandBufferState.cs`, and `VulkanRenderer.ResourceLifetimeTracking.cs` |
| Persistent chain worker/pool ownership | `VulkanRenderer.CommandChainWorkers.cs`, `VulkanRenderer.OwnedCommandChainSecondaryPool.cs`, and the owned-pool state in `VulkanRenderer.CommandBufferState.cs` |
| OpenXR eye workers and shared-state lock | `VulkanRenderer.OpenXR.EyeRecordWorkers.cs`, `VulkanRenderer.OpenXrEyeRecordWorkerScheduler.cs`, and `VulkanOpenXrBackend.ParallelEyePrimaryRecordSharedStateLock` |

Targeted reconciliation validation passed 53/53 tests on 2026-07-30,
including `VulkanArchitectureLifecycleGuardTests`,
`SwapchainContextCoalescingTests`, and the material payload/runtime topology
tests in `VulkanStablePacketAndDescriptorTests`. The Vulkan renderer project
and editor project also built successfully with zero compiler errors.

## Ownership Reconciliation

This plan now closes a gap between three workstreams:

- Workstream 04 owns the immutable upcoming-frame handoff. Its current package
  carries ordering, selection, revisions, and live references, but not packed
  binding data, descriptor identities, dirty ranges, or backend-ready draw
  artifacts. Its binding/data acceptance is reopened.
- The CPU Direct fast-path plan already calls for per-frame, per-view,
  per-pass, per-material, and per-object data separation, stable material
  tables, persistent mapped arenas, and dirty-range publication. This plan
  adopts that contract rather than creating a competing Vulkan-only model.
- Workstream 05 owns dirty command-chain recording. Its zero-worker behavior on
  a stable frame is correct. Workers become a consumer of immutable prepared
  draws after the data contract is fixed; they are not a fallback for repeated
  binding reconstruction.

No phase may claim success merely by moving the measured work from the render
thread to collect-visible or a worker. Acceptance requires that unchanged work
is not executed.

## External Validity Review

The proposals were checked against current authoritative Vulkan documentation:

- The [Vulkan threading guide](https://docs.vulkan.org/guide/latest/threading.html)
  confirms that a command pool must be externally synchronized and recommends a
  separate pool per recording thread.
- The Khronos
  [command-buffer usage and multithreaded recording sample](https://docs.vulkan.org/samples/latest/samples/performance/command_buffer_usage/README.html)
  recommends per-frame/per-thread resource pools, warns that many small
  secondaries can cost more than they save, and finds pool reset generally
  cheaper than frequent allocation/free or individual command-buffer reset.
- The specification's
  [command-buffer chapter](https://docs.vulkan.org/spec/latest/chapters/cmdbuffers.html)
  defines primary/secondary lifecycle coupling, pending-state restrictions,
  the listed order of `vkCmdExecuteCommands` secondaries, state inheritance
  rules, and dynamic-rendering compatibility requirements. Command-buffer
  boundaries do not create memory dependencies by themselves.
- The specification's
  [synchronization chapter](https://docs.vulkan.org/spec/latest/chapters/synchronization.html)
  requires layout transitions to be ordered around all accesses and states that
  command-buffer boundaries do not create synchronization by themselves.
- The specification's
  [device and queue chapter](https://docs.vulkan.org/spec/latest/chapters/devsandqueues.html)
  ties command pools and their command buffers to a queue family and defines
  queue-family ownership transfers.
- The specification's
  [query chapter](https://docs.vulkan.org/spec/latest/chapters/queries.html)
  requires a query to begin and end in the same command buffer and adds
  inheritance rules when a primary executes secondaries inside an active query.
- The [Vulkan validation overview](https://docs.vulkan.org/guide/latest/validation_overview.html)
  recommends validation layers during development; synchronization validation
  is an additional required gate for the image-state work.

These sources validate the general direction, but they do not prove that an
engine-internal abstraction will improve XRENGINE. Allocation and performance
claims remain profile-gated.

## Proposal Review Summary

| Proposal | Verdict | Required qualification |
| --- | --- | --- |
| Frequency-separated binding payloads | Required before further worker expansion | Frame, view, pass, material, object, and instance data must have explicit owners, generations, and publication frequency. |
| Link-time compiled binding schema | Required | Reflection remains authoritative, but the frame loop must not interpret string names and generic values per member/per draw. |
| Change-driven packed-data publication | Required | Stable frames must process dirty owner lists, not scan every visible or recorded draw. |
| Stable descriptor tiers and offsets | Required with backend profiling | Descriptor topology and data offsets must survive stable frames; choose dynamic UBO, SSBO/table, descriptor-buffer, or other mechanisms by capability and measurement. |
| Legacy/new payload equivalence mode | Required migration guard | Compare bytes, resource identities, fallback decisions, and visual results before deleting the legacy snapshot path. |
| Immutable prepared-frame phases | Valid engine refactor | Must preserve render order and avoid allocating a large transient object graph every frame. |
| Immutable prepared mesh draws | Strongly justified | Extend the current identity-only selection/`DrawPacket`; do not duplicate it while consumers still read `MeshDrawOp`, live material state, or `ComputeDispatchSnapshot`. |
| Stable worker secondary arenas | Valid refinement | Per-worker/per-slot pools already exist. Optimize ownership metadata and recycling only after measuring reset/allocation cost. |
| Per-chain dependency versions | Conditionally valid | Keep full signatures as the correctness backstop; add a reverse index only if invalidation scans are measured bottlenecks. |
| Shared primary/secondary cache identity | Valid with narrower scope | Share identity primitives and nested-artifact references, not one monolithic key; primary and secondary dependencies differ. |
| Command-buffer-local image state | Valid but high risk | Merge subresource transitions in actual submission order and model queue-family ownership and external/OpenXR image contracts. |
| Immutable primary plan nodes | Valid engine refactor | The interpreter must emit the same barriers, rendering scopes, and execution order as the direct recorder. |
| Typed eligibility/quarantine results | Valid | Keep the result allocation-free and use one value for policy, telemetry, and diagnostics. |
| Remove or redesign inert packet flag | Valid local cleanup | Remove it unless a measured deterministic packet-build experiment is implemented. |
| Expand secondary operation families | Vulkan permits it conditionally | Respect render-pass scope, queue-family capability, synchronization, and query inheritance; add families independently. |
| First-class recorded artifacts | Valid with allocation constraints | Prefer pooled structs/owned slots; do not create a managed object per draw or per frame. |
| Layered acceptance gates | Strongly justified | Include core, synchronization, lifetime, deterministic-order, allocation, and hardware performance cohorts. |

## Architectural Invariants

The following are requirements, not implementation suggestions:

- Stable-frame cost must be proportional to changed owners, not
  `visible draws x reflected members`.
- A material used by many draws is packed once per dirty material and required
  frame slot, not once per draw.
- Frame, view, and pass data are published once per corresponding scope.
- Object and instance updates touch only dirty slots/ranges.
- Descriptor work is proportional to layout/resource topology changes, not
  stable draw count.
- Command encoding is proportional to dirty command artifacts; stable artifacts
  are reused.
- Binding data and recording state have distinct generations. A data-content
  change must not rerecord a command buffer when stable offsets and descriptors
  make rerecording unnecessary.
- Full dependency signatures remain a correctness backstop, but a successful
  stable fast path must not rebuild or rescan all dependency content.
- Every fallback is explicit, counted, and excluded from canonical fast-path
  acceptance.
- All new per-frame hot paths remain allocation-free after warmup.

The plan does not prescribe one Vulkan storage mechanism. Dynamic UBO offsets,
SSBO/material tables, push constants, descriptor buffers, or capability-gated
variants may be selected by measurement. They must all satisfy the same
frequency, lifetime, invalidation, and telemetry contracts.

## Phase 0 - Freeze Baselines And Contracts

- [x] Record current source ownership for prepared planner state, `DrawPacket`,
  `MeshDrawOp`, `VkMeshRenderer.RecordDraw`, command-chain caches, primary
  variants, tracked resources, worker pools, and OpenXR image-state locking.
- [x] Record the current workstream-04 selection/package, live render-command,
  material/program binding, snapshot-copy, reflected auto-uniform,
  reusable-frame refresh, and descriptor-validation dataflow.
- [ ] Capture serial and parallel dirty-chain baselines for small, medium, and
  large workloads.
- [x] Capture stable-frame primary/secondary cache-hit behavior.
- [x] Capture one exact stable diagnostic frame with separate frontend material
  emission, snapshot copy, backend auto-uniform processing, descriptor
  validation, queue submit, acquire, fence-wait, and native-present timings.
- [ ] Capture managed allocations for preparation, worker recording, merge,
  primary assembly, and submission independently.
- [x] Add frame-reset counters for material payload cache hits/misses,
  payload/uniform packing, material parameter emissions and dictionary writes,
  frame material snapshot cache hits/misses, binding snapshot captures/entries,
  fast/legacy snapshot counts, auto-uniform plan hits/misses, static/dynamic
  byte traffic, dynamic member patches, reflected member scans, fast/fallback
  draws, stable frame-data draw visits, and descriptor records
  validated/written.
- [ ] Complete count coverage for visible/prepared draws, unique visible
  materials, frame/view/pass/object/instance payloads, dirty/reused slots,
  reflected-name lookups, generic conversions, descriptor schemas, command
  artifact retirement, and typed fallback reasons.
- [x] Report outer engine output scopes separately from native Vulkan calls so
  the swap/present wrapper cannot be mistaken for `vkQueuePresentKHR`.
- [ ] Capture `vkResetCommandBuffer`, command-pool reset, command-buffer
  allocation, and secondary invocation counts.
- [x] Add or confirm deterministic schedule/merge tests before changing the
  intermediate representation.
- [ ] Document the current OpenXR left/right command-buffer submission order and
  every shared image subresource it can touch.

Acceptance criteria:

- [ ] Baselines distinguish scheduled concurrency from overlapping native
  command recording.
- [ ] Baselines distinguish frontend binding construction, backend data
  publication, command encoding, submission, OS/GPU waits, and native present.
- [ ] Every time counter that can scale with draws has a corresponding count
  and byte counter.
- [x] The cost being optimized is visible in profiler data.
- [ ] Current correctness tests fail if render order, inheritance, or lifetime
  coupling is deliberately broken.

## Phase 1 - Frequency-Separated Binding And Data Publication

This phase is the missing workstream-04 handoff. It precedes worker-facing draw
records because an immutable copy of the current dictionary snapshot would
preserve the dominant cost instead of removing it.

### 1.1 Compile the binding schema

- [ ] Compile shader reflection into an immutable, versioned binding schema at
  shader link/artifact materialization time.
- [ ] Give every non-opaque value a typed source identity, frequency domain,
  destination set/binding/offset, size/stride, conversion operation, and
  default-value policy.
- [ ] Give every opaque resource a typed resource identity, descriptor tier,
  array/indexing policy, and topology/content dependency.
- [ ] Replace per-draw member-name source resolution with compact typed copy
  operations or direct typed writes.
- [ ] Preserve reflection metadata for diagnostics and validation without
  interpreting it on every draw.
- [ ] Reject or explicitly fall back when a shader declaration cannot be
  classified safely.
- [ ] Cache schemas by shader/layout identity and generation; do not rebuild
  them on frame or draw boundaries.

Acceptance criteria:

- [ ] A qualifying draw performs zero reflected-name lookups and zero generic
  type-dispatch operations in steady state.
- [ ] Stable draws perform zero full mixed-frequency block copies and zero
  reflected-member scans; object updates use precompiled direct writes to dirty
  object slots.
- [ ] Schema compilation is deterministic and produces actionable diagnostics
  for unclassified inputs.
- [ ] Shader reload invalidates only affected schemas, pipelines, descriptor
  layouts, and prepared records.

### 1.2 Establish frequency-owned payloads

- [ ] Define explicit frame, view, pass, material, object/draw, and
  instance/batch data domains.
- [ ] Assign each binding schema entry to exactly one declared owner/frequency,
  with documented exceptions for aliases or backend transforms.
- [ ] Split the current auto-uniform storage so a changing object value cannot
  force serialization of unchanged material, view, pass, or frame values.
- [ ] Pack frame data once per frame slot, view data once per active view, and
  pass data once per pass generation.
- [ ] Pack material data once per dirty material and required in-flight frame
  slot, regardless of draw/reference count.
- [ ] Pack object and instance data into stable slots/ranges and update only
  dirty slots.
- [ ] Define frame-slot ownership, publication, in-flight retention, and
  retirement for every payload domain.
- [ ] Define how temporal history and previous-frame object data advance without
  forcing unrelated payload rewrites.

Acceptance criteria:

- [ ] One material referenced by many draws is serialized once per dirty
  material/frame-slot publication, not once per draw.
- [ ] Camera-only motion does not rewrite material or static object payloads.
- [ ] Object-only motion does not rewrite material, frame, view, or pass
  payloads.
- [ ] An unchanged frame performs no material/object serialization and reports
  zero dirty bytes for those domains.
- [ ] All payload publication is zero-allocation after warmup.

### 1.3 Publish dirty ranges from change owners

- [ ] Add precise content generations and dirty-range queues to frame, view,
  pass, material, object, and instance owners.
- [ ] Separate data-content generation from layout/topology generation and
  recording-visible generation.
- [ ] Publish immutable payload handles containing storage identity, offset or
  index, length, generation, frame-slot lifetime, and owner identity.
- [ ] Use persistent mapped or equivalently bounded storage; select dynamic UBO,
  SSBO/table, push-constant, descriptor-buffer, or capability-specific layouts
  only after measuring representative hardware.
- [ ] Coalesce dirty byte ranges without scanning all live or visible objects.
- [ ] Make stable publication a bounded generation check that can return
  without visiting every reusable draw.
- [x] In the current auto-UBO migration path, retain the published material-plan
  identity per block and stable buffer slot, skip unchanged static material
  copies, and clear/patch only the dynamic member ranges on each draw visit.
- [ ] Preserve explicit failure for exhausted arenas or invalid owner lifetime;
  do not silently bind stale data.

Acceptance criteria:

- [ ] Publication CPU and bytes scale with dirty owners/ranges.
- [ ] The stable static cohort visits zero draw operations for data refresh.
- [ ] Storage remains bounded across frame slots, resize, scene churn, shader
  reload, and shutdown.
- [ ] Data-content-only changes reuse command artifacts when their stable
  binding location and recorded dynamic state permit it.

### 1.4 Stabilize descriptor topology

- [ ] Define descriptor ownership by frame/view, pass, material, and
  object/instance domain instead of by accidental draw snapshot composition.
- [ ] Make descriptor schema/layout generation distinct from descriptor
  resource-content generation.
- [ ] Resolve descriptor tier handles and stable offsets/indices before command
  recording.
- [ ] Publish descriptor writes only for changed resource content or topology.
- [ ] Replace per-draw descriptor proof with owner-generation checks and
  precise invalidation lists.
- [ ] Retain full binding/resource fingerprints as a validation backstop during
  migration, but remove their broad stable-frame scans from the accepted fast
  path.
- [ ] Measure descriptor variant, set, reservation, pool, mapped-byte, and
  reserved-byte amplification against unique materials/layouts/frame slots.
- [ ] Set explicit bounded-growth expectations for descriptor and frame-data
  arenas.

Acceptance criteria:

- [ ] An unchanged frame performs zero descriptor writes and no per-draw
  descriptor validation.
- [ ] A material texture change updates only the affected material resource
  records and dependent generations.
- [ ] Descriptor set/record counts scale with declared owners and in-flight
  slots, not draw/pass/frame cartesian products.
- [ ] Core and synchronization validation remain clean through resource
  replacement and retirement.

### 1.5 Constrain callbacks and legacy fallback

- [ ] Require material/render callbacks that qualify for the fast path to
  declare a frequency domain and publish typed output with a generation.
- [ ] Prevent qualifying callbacks from mutating a shared program dictionary
  during draw consumption.
- [x] Define an explicit legacy fallback for shaders or callbacks that cannot
  yet satisfy the contract.
- [x] Count fallback draws, material emissions/dictionary writes, snapshot
  captures/entries, and reflected full-block scans/bytes.
- [ ] Count typed fallback reasons.
- [ ] Make canonical acceptance fail if the representative scene silently uses
  the fallback.

Acceptance criteria:

- [ ] Fast-path draws never call `ClearBindings()`,
  `SetMaterialUniforms(..., forceUpdate: true)`, unrestricted binding callbacks,
  or `ComputeDispatchSnapshot` capture during consumption.
- [ ] Unsupported cases are visible and correct rather than stale or silently
  CPU-bound.

### 1.6 Dual-path equivalence and cutover

- [ ] In validation builds, produce new packed payloads beside the legacy
  snapshot/serializer output.
- [ ] Compare uniform bytes, descriptor resource identities, offsets, dynamic
  state, fallback decisions, and draw order.
- [ ] Add mismatch diagnostics at schema entry and payload-domain granularity.
- [ ] Capture representative render targets and viewport images for static,
  moving, camera-only, material-mutation, resize, and shader-reload cohorts.
- [ ] Remove the legacy path from qualifying draws only after byte/resource,
  visual, lifetime, and synchronization parity passes.

Acceptance criteria:

- [ ] The new path is equivalent where the legacy path is authoritative.
- [ ] Intentional frequency-layout differences are covered by explicit expected
  mappings rather than ignored byte mismatches.
- [ ] No canonical scene draw uses the legacy fallback.

## Phase 2 - Explicit Prepared Frame And Immutable Draw Encoding

### 2.1 Prepared-frame phase boundary

- [ ] Introduce an allocation-bounded `VulkanPreparedFrameRecording` or
  equivalent frame-slot-owned structure.
- [ ] Store ordered primary-plan nodes, resolved render scopes, inheritance,
  stable resource handles/generations, dependency signatures, referenced
  resources, and eligibility results.
- [ ] Build pure selection and binding inputs on the workstream-04 producer
  side; materialize only thread-affine Vulkan handles on their legal owner
  before worker dispatch.
- [ ] Consume frequency-domain payload handles and dirty publications from
  Phase 1 rather than rebuilding draw bindings.
- [ ] Give workers only indexed slices or handles into frozen frame-slot
  storage.
- [ ] Assert that workers cannot publish planner or global renderer mutations.

### 2.2 Prepared mesh draw

- [ ] Extend or replace the current identity-oriented `DrawPacket` with a
  compact `VkPreparedMeshDraw`.
- [ ] Resolve all pipeline, descriptor, vertex/index/indirect binding, viewport,
  scissor, dynamic state, pass metadata, frame-data slot, and lifetime inputs
  before dispatch.
- [ ] Reference stable frame/view/pass/material/object payload handles,
  generations, and dynamic offsets/indices from the prepared draw.
- [ ] Stop worker code from rereading the original `MeshDrawOp`.
- [ ] Stop worker code from calling mutable `VkMeshRenderer.RecordDraw`; route
  it through an encoder that consumes only prepared data.
- [ ] Stop every qualifying consumer from rereading `XRMaterial`, clearing or
  mutating program bindings, or capturing/reading `ComputeDispatchSnapshot`.
- [ ] Use frame-slot arrays, spans, or pools; do not allocate one managed object
  per draw.
- [ ] Remove renderer-to-worker ownership pinning only after tests prove the
  prepared record is complete and independent.

Acceptance criteria:

- [ ] Worker inputs are immutable by construction.
- [ ] Consumer inputs are backend-ready by construction; steady consumption
  performs no live material traversal or reflected serialization.
- [ ] Two chains derived from one renderer can record concurrently without
  accessing shared mutable renderer state.
- [ ] Prepared-frame construction and worker encoding add zero steady-state
  managed allocations.
- [ ] Serial and parallel recordings produce equivalent ordered command plans.

## Phase 3 - Typed Primary Plan And Shared Identity Primitives

### 3.1 Primary plan nodes

- [ ] Represent primary orchestration with compact typed nodes such as
  `BarrierBatch`, `BeginRendering`, `ExecuteSecondaryRange`,
  `RecordInlineOperation`, `EndRendering`, `QueueOwnershipTransfer`, and
  `PreparePresent`.
- [ ] Keep render-scope begin/end and barrier placement in the plan; do not move
  synchronization responsibility into arbitrary worker draws.
- [ ] Implement a deterministic primary recorder over the plan.
- [ ] Compare emitted command/dependency signatures with the existing direct
  recorder during migration.
- [ ] Use the plan identity as an input to primary reuse only after equivalence
  is proven.

### 3.2 Shared identity vocabulary

- [ ] Define shared identity components for ordered command nodes, resource
  handles/generations, render-scope inheritance, queue assumptions, and nested
  recorded artifacts.
- [ ] Keep primary-only and secondary-only dependency fields separate.
- [ ] Make a primary identity reference the exact secondary artifact
  generations it executes.
- [x] Preserve current full dependency signatures as a backstop during rollout.
- [ ] Add mismatch diagnostics at the component level.

Acceptance criteria:

- [ ] Primary and secondary caches cannot disagree about a shared dependency
  generation.
- [ ] A secondary reset, replacement, or retirement invalidates every primary
  identity that references it.
- [ ] Different valid primary and secondary dependencies are not collapsed into
  a misleading universal key.

## Phase 4 - Recorded Artifact And Worker Arena Ownership

### 4.1 First-class recorded artifact

- [ ] Introduce a pooled `VulkanRecordedCommandArtifact` or equivalent owned
  slot containing the native buffer, command level, pool/arena owner,
  dependency identity, referenced-resource set, frame slot, generation,
  in-flight state, retirement state, and failure/invalidation reason.
- [ ] Make primary-to-secondary lifecycle linkage explicit in artifact
  references.
- [ ] Route deferred retirement through the artifact owner.
- [ ] Ensure an artifact cannot be reset or freed while pending.
- [ ] Avoid a managed allocation per artifact transition.

### 4.2 Worker secondary arena

- [ ] Consolidate the existing per-worker/per-frame-slot command pool,
  reusable-buffer slots, signatures, referenced resources, and retirement
  metadata behind one arena owner.
- [ ] Measure pool reset, individual reset, and reuse strategies on XRENGINE's
  actual cached-secondary workload.
- [ ] Prefer pool reset only where it does not invalidate still-reusable or
  primary-referenced secondaries.
- [ ] Audit `VK_COMMAND_BUFFER_USAGE_SIMULTANEOUS_USE_BIT`; remove it only where
  lifecycle tracking proves the same secondary cannot be pending through more
  than one execution and measurements show benefit.
- [ ] Keep chain count and draw count large enough to amortize
  `vkCmdExecuteCommands` and per-secondary state setup.

Acceptance criteria:

- [ ] Pool ownership remains one recording thread at a time.
- [ ] No cached primary references an arena slot that can be recycled.
- [ ] The selected recycling strategy wins on measured CPU cost without
  increasing memory unboundedly.
- [ ] Small workloads remain serial when secondary overhead is not amortized.

## Phase 5 - Profile-Gated Dependency Versioning

- [ ] Measure the cost of full signature comparison, dirty propagation, and
  cache scanning independently.
- [ ] Add explicit generation fields for pipeline/layout, descriptor
  layout/content, geometry bindings, inheritance/target, indirect/count stream,
  and dynamic state only where ownership is unambiguous.
- [x] Keep the complete dependency signature as the correctness authority.
- [ ] Add a reverse dependency index only if measurements show broad scans are
  material.
- [ ] Preallocate index storage or update it outside per-frame hot paths.
- [ ] Validate removal and retirement so the index cannot retain dead resource
  or chain references.

Acceptance criteria:

- [ ] A changed resource dirties all and only the chains that depend on its
  changed recording-visible state.
- [ ] Version wrap, resource recreation, and handle reuse cannot produce a false
  cache hit.
- [ ] The index saves more time than it costs in updates and memory.

## Phase 6 - Command-Buffer-Local Image State And OpenXR Unlock

This phase is correctness-first. Recording completion order must never become
image-state or submission order.

- [ ] Define an immutable starting state per image subresource, including
  layout, access/stage history needed by the planner, queue-family ownership,
  and external/OpenXR ownership state.
- [ ] Record a local transition/access journal for each independently recorded
  primary.
- [ ] Validate and merge journals in the exact order the corresponding command
  buffers will be submitted, not worker completion order.
- [ ] Emit explicit semaphore and queue-family ownership requirements when
  journals cross queues.
- [ ] Reject conflicting journals or serialize their planning rather than
  guessing an `oldLayout`.
- [ ] Commit predicted state to the renderer's submission-state model only when
  the ordered submission is accepted; retain rollback/rebuild behavior for
  failed recording or submission.
- [ ] Cover `VK_IMAGE_LAYOUT_UNDEFINED`, discard transitions, split depth/stencil
  aspects, mip/layer ranges, swapchain acquire/present, and OpenXR acquire/release.
- [ ] Remove `ParallelEyePrimaryRecordSharedStateLock` only after left and right
  eye recording overlaps in native timing and all journal tests pass.

Acceptance criteria:

- [ ] Synchronization validation reports no layout, access, or ownership hazard.
- [ ] Camera-independent and camera-dependent eye targets preserve correct
  subresource state across frames.
- [ ] OpenXR eye primary recording overlaps without a shared recording lock.
- [ ] Submission failure, resize, session restart, and swapchain recreation do
  not publish unexecuted predicted layouts as completed state.

## Phase 7 - Typed Eligibility, Quarantine, And Configuration Truth

- [ ] Replace boolean worker eligibility with an allocation-free enum/result
  covering at least `Eligible`, `TooLittleIndependentWork`,
  `MutableRendererConflict`, `UnsupportedOperation`,
  `UnsupportedInheritance`, `PrimaryOwnedIndirectStream`,
  `WorkerQuarantined`, and `ResourcePreparationFailed`.
- [ ] Use the same result for fallback policy, telemetry, and diagnostics.
- [ ] Separate permanent unsupported cases from transient not-ready and faulted
  worker-domain cases.
- [ ] Remove `XRE_VULKAN_PARALLEL_PACKET_BUILD` while packet lowering remains
  sequential.
- [ ] If parallel packet lowering is reconsidered, require immutable
  partitions, deterministic output slots, zero steady-state allocation,
  sequential-equivalence validation, and a measured win.
- [ ] Remove obsolete logs, settings, and tests that imply inactive
  concurrency.

Acceptance criteria:

- [ ] Telemetry names the path that executed and its exact rejection reason.
- [ ] No configuration flag claims parallel work that is serial.
- [ ] Every rejection has an explicit safe fallback or visible frame failure.

## Phase 8 - Expand Secondary Eligibility Incrementally

Vulkan permits draw, dispatch, copy, and many query commands in secondary
command buffers, but every command retains its own render-pass-scope,
queue-capability, inheritance, and synchronization valid usage. Do not treat
"secondary supported" as "safe in the current command chain."

### 8.1 Additional graphics draws

- [ ] Add direct mesh-draw variants whose complete state fits
  `VkPreparedMeshDraw`.
- [ ] Validate dynamic-rendering formats, samples, view mask, mapping state, and
  render flags against the executing primary.

### 8.2 Immutable indirect work

- [ ] Admit indirect/count commands only when their producer is complete before
  recording/execution and buffer identity/ranges remain stable.
- [ ] Keep mutable zero-readback indirect/count streams primary-owned until a
  separate cross-vendor cohort proves the secondary contract.

### 8.3 Compute and transfer chains

- [ ] Record them outside render-pass instances.
- [ ] Allocate their command buffers from pools for a queue family that supports
  the commands and matches the primary/queue that will execute them.
- [ ] Model resource reads/writes, barriers, and ownership transfers explicitly.
- [ ] Do not label queue-schedule metadata as asynchronous multi-queue execution.

### 8.4 Queries

- [ ] Add query work last.
- [ ] Keep each begin/end pair in the same command buffer.
- [ ] Model primary-active query inheritance, `inheritedQueries`,
  `occlusionQueryEnable`, query flags, pipeline statistics, reset placement, and
  result ordering.
- [ ] Retain the primary path for unsupported query scopes.

Acceptance criteria:

- [ ] Each family has independent enablement, tests, telemetry, and fallback.
- [ ] Core and synchronization validation pass for every family.
- [ ] Each family demonstrates a measured benefit on representative hardware.

## Phase 9 - Acceptance And Cutover

- [ ] Binding-schema classification, std140/layout, array/struct, default-value,
  and shader-reload tests pass.
- [ ] Legacy/new payload byte and resource-identity equivalence passes for every
  qualifying shader family.
- [ ] Frame/view/pass/material/object frequency-isolation tests pass.
- [ ] A stable static frame reports zero material dictionary emissions,
  snapshot copies, auto-uniform template construction/full-block copies/member
  scans, material/object payload serializations, per-draw descriptor
  validations, and descriptor writes.
- [ ] A single shared-material mutation serializes one material payload per
  required frame slot, independent of its draw count.
- [ ] Camera-only and object-only cohorts touch only their declared dirty
  domains.
- [ ] Prepared-frame determinism tests pass.
- [ ] Dirty propagation and cache-identity tests pass.
- [ ] Worker thread-safety, timeout, exception, and quarantine tests pass.
- [ ] Deterministic merge and primary-plan equivalence tests pass.
- [ ] Primary/secondary pending-state and deferred-retirement stress passes.
- [ ] Dynamic-rendering and legacy inheritance matrices pass.
- [ ] Core, synchronization, and best-practices validation are clean.
- [ ] Desktop, OpenXR, resize, shader reload, scene churn, and device shutdown
  stress pass.
- [ ] Release small/medium/large dirty workloads show no regression below the
  declared threshold and a material win above it.
- [ ] Stable workloads continue to reuse command buffers instead of invoking
  workers.
- [ ] All new hot paths report zero steady-state managed allocations.
- [ ] The representative approximately-647-draw Release stable-static cohort
  meets all of these p95 workstream-local budgets:
  - frontend binding/package consumption <= 0.15 ms;
  - frame/view/pass data publication <= 0.15 ms;
  - unchanged material/object publication <= 0.05 ms;
  - descriptor reuse validation/publication <= 0.10 ms;
  - command-artifact reuse validation <= 0.15 ms;
  - total Vulkan preparation/record/submit CPU, excluding separately measured
    OS/GPU waits, <= 1.00 ms.
- [ ] The declared Release moving-object cohort updates only dirty object ranges
  and keeps total Vulkan preparation/record/submit CPU, excluding separately
  measured waits, <= 1.50 ms p95.
- [ ] The canonical CPU Direct desktop render path remains at or below the
  workstream-01 5.00 ms p95 product gate.
- [ ] Every performance result reports build, hardware, scene, resolution,
  strategy, validation state, command/unique-material/dirty-owner counts, bytes
  copied, descriptor writes, fallback counts, and native wait/present time.
- [ ] Documentation and environment-variable references match the shipped path.

The local budgets are intentionally aggressive because the accepted stable path
must be dominated by bounded generation checks and a few scoped writes, not
draw traversal. If representative hardware proves a sub-budget unrealistic,
record the evidence and reallocate within the 5.00 ms product gate. Do not
relax the frequency/scaling invariants.

## Recommended Execution Order

1. Freeze baselines and correctness contracts.
2. Compile binding schemas and implement frequency-separated frame, view, pass,
   material, object, and instance payload ownership.
3. Add dirty-range publication and stable descriptor topology, then prove
   legacy/new payload equivalence.
4. Implement immutable prepared draws and the explicit prepared-frame boundary
   over those stable payload handles.
5. Compile primary plan nodes and introduce shared identity primitives.
6. Consolidate recorded artifacts and worker arenas.
7. Add typed eligibility and remove misleading configuration.
8. Implement command-buffer-local image journals and then unlock OpenXR eye
   recording.
9. Add dependency indexes only if profiling justifies them.
10. Expand secondary operation families one at a time, then complete the full
    acceptance and hardware performance matrix.

The binding/data phase has the largest proven CPU payoff and must precede
further worker expansion. The image-journal phase has the highest correctness
risk. The dependency-index and broader-operation phases have the weakest
guaranteed payoff and therefore remain explicitly measurement-gated.
