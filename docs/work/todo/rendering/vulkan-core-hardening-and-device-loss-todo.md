# Vulkan Core Hardening And Recording Code Changes TODO

Last Updated: 2026-08-10
Owner: Rendering
Status: Active

This is the single implementation tracker for Vulkan core hardening, frame-plan
recording, primary recording fast paths, Forward+ render-graph cost, render tail
latency, and advanced-render-pipeline architectural phases 06 through 10. Its
companion, [Vulkan Core Hardening And Recording Testing
TODO](../../testing/rendering/vulkan-core-hardening-and-recording-testing-todo.md), owns every build,
test, capture, stress, visual, and performance validation task.

The required end state is defined by the
[Vulkan Render Loop Target Architecture](../../design/rendering/vulkan-render-loop-target-architecture.md).
The implementation must combine production-grade fault containment and
lifecycle correctness with a small readable ownership surface, zero-allocation
steady-state hot paths, and complete CPU critical-path attribution. Stability,
speed, observability, and source simplification are joint completion gates; none
may be traded away to claim progress on another.

Completed implementation history remains in the
[completed-work record](vulkan-core-hardening-and-device-loss-completed.md).

The context-isolation and generation-publication work below incorporates the
remaining architectural findings from the
[directional-light and final-presentation investigation](../../investigations/rendering/directional-light-inspector-shadow-2026-08-03.md).
The renderer must not mix main-view, shadow, UI-preview, capture, or swapchain
resource generations even when those outputs share a window or frame.

## Code Changes

### Cross-Cutting Vulkan Buffer Invariant

- [x] Keep canonical frame-data arena lanes capability-based rather than
  payload-name-based. `TransferUpload`, `TransferStaging`, `Readback`,
  `Uniform`, `Storage`, and `Indirect` describe the Vulkan operations supported
  by an allocation; callers select the required lane explicitly. Do not infer
  camera, transform, material, skinning, debug, indirect, upload, or readback
  behavior from `XRDataBuffer.AttributeName` or another diagnostic/resource
  name. Content semantics may be carried separately for diagnostics, but must
  not determine native usage flags or allocation behavior.

### 1. Contain Device Loss And Make Resource Lifetime Explicit

- [x] Make device-loss transition first-writer-wins and stop all new recording,
  submission, waits, allocation, mapping, descriptor updates, and planner
  publication after it is confirmed.
- [x] Preserve the original failing Vulkan/OpenXR call, result, frame operation,
  submission context, and in-flight resource generations for diagnostics.
- [x] Make resource retirement timeline/fence-safe for images, views,
  framebuffers, descriptors, buffers, command pools, and plans.
- [x] Replace physical resources through one generation transaction: prepare
  the new allocation, image views, framebuffer attachments, descriptor payloads,
  layout requirements, and command dependencies; publish them atomically; then
  retire the previous generation only after every owning frame slot and timeline
  dependency completes.
- [x] Give every native image, image view, framebuffer/dynamic-rendering
  attachment, sampler, descriptor payload, and recorded command artifact an
  explicit allocation/publication generation. Do not use managed-object hashes,
  resource names, or stable logical wrappers as physical-lifetime identity.
- [x] Ensure retirement cannot wait on a frame that was rejected for a planner,
  viewport, collect, or publication-generation mismatch; cancellation and
  supersession must release or transfer all completion dependencies explicitly.
- [x] Add descriptor fingerprints that include every binding identity, resource
  allocation generation, expected layout, image view, and sampler.
- [x] Replace placeholder descriptor behavior with explicit required-binding
  failures and bounded diagnostic counters.

### 2. Build Immutable Context-Local Frame, View, And Output Plans

- [x] Introduce immutable `FramePlan`, `ViewSetPlan`, `OutputRequest`,
  `RenderPacket`, `RecordedPacketKey`, and `FramePlanBuilder` types; lower the
  existing `FrameOp` stream into these plans while preserving frame-slot
  ownership.
- [x] Build one immutable logical view set after OpenXR locates views; key
  temporal state by logical-view identity rather than batch position or
  swapchain image.
- [x] Route desktop, OpenXR, mirror, capture, probe, shadow, UI-preview, and
  diagnostic outputs through the same output and view-family plan.
- [x] Make resource-planner state context-local. Its key must include pipeline,
  logical view, output target, resource registry/generation, display extent, and
  internal extent; a swapchain-targeting operation must never copy internal
  dimensions or allocator state from whichever unrelated pipeline is currently
  live on the render thread.
- [x] Replace the multi-context "merged physical plan" fallback with either a
  genuinely merged immutable allocation plan that preserves each context's
  registry and extents, or independently activated per-context planner states.
  If neither is available, reject or defer the frame before recording rather
  than warning and continuing with the first context's plan.
- [x] Treat a planner-context change during command recording as a failed plan
  precondition. Abort and re-plan against one immutable snapshot; never continue
  recording shadow, main-view, UI-preview, capture, or presentation operations
  against a pre-recorded plan owned by another context.
- [x] Compile explicit resource dependencies into a deterministic render-pass
  DAG; centralize primary-owned operations and reject invalid graph dataflow.
- [x] Version plans, resources, attachments, and publications so resize,
  topology, descriptor, and allocation changes invalidate only affected work.
- [x] Publish the final presentation source as one immutable tuple containing
  logical resource epoch, native allocation/view generation, image, image view,
  sampler, expected layout, descriptor set/slot generation, output extent, and
  owning command artifact. Re-resolve nothing between validation, descriptor
  publication, command selection, and submission; reject or defer if the tuple
  changes before submit.

### 3. Make Command Recording Snapshot-Driven And Reusable

- [x] Add `FreshSerial` recording mode and retain comparable recording scopes,
  counters, and miss reasons without hot-path allocation.
- [x] Record from immutable `RenderPacket` snapshots with frame-local tracking,
  persistent per-thread command pools, scratch storage, and capacity-backed
  collections.
- [x] Replace the primary-reuse hard-off gate with generation-complete
  `RecordedPacketKey` dependency validation.
- [x] Make `RecordedPacketKey` and secondary-chain dependencies include exact
  native image-allocation generation, image-view generation,
  framebuffer/dynamic-rendering attachment identity, render area and extent,
  descriptor payload/publication generation, sampler generation, and pipeline
  layout generation. Remove placeholder `RenderArea=0` and logical target/name
  hashes from fields that claim to represent physical resources.
- [x] Couple in-place descriptor-set updates to recorded-artifact ownership. A
  descriptor payload that changes image/view/layout under a stable set handle
  must also republish the secondary's descriptor-image requirements or
  invalidate and re-record every dependent artifact before submission.
- [x] Retain live descriptor-image transition/preflight for mutable frame-source
  bindings until a scheduled secondary proves that its frozen requirements were
  produced from the exact descriptor payload generation being submitted. Never
  skip the live scan solely because a reusable chain exists.
- [x] Cache only stable secondary command ranges; keep UI, text, debug,
  dynamic-resource, and output-sensitive ranges dynamic.
- [x] Record stable GPU-driven dispatch, barrier, indirect-draw, and count
  topology once; update only bounded data ranges for changing visibility and
  counts.
- [x] Remove obsolete primary-command variant caches and cache-only scheduling
  branches once packet reuse owns their responsibility.

#### Phase 1-3 Closeout Status (2026-08-06)

Implementation is complete for the first three phases. Independent strict
source audits passed Phase 1 at 8/8 items and Phases 2-3 at 18/18 items. The
Vulkan renderer project builds with warnings treated as errors (0 warnings,
0 errors), and a named isolated Vulkan editor session started successfully and
answered MCP `ping` after the oversized prepared-command value-type startup
failure was fixed.

The remaining closeout work is complete:

1. The final named `vulkan-core-phase4` editor session ran with Vulkan and
   `StandardValidation`. Its profiler snapshot reported zero validation errors
   and zero pending retired resources. `log_vulkan.log` and
   `log_rendering.log` contained no VUID, device-loss, submission-rejection,
   lifetime-rejection, fatal, or unhandled-exception match, including shutdown.
2. Two materially different camera views exercised the viewport capture path.
   Both returned the intended typed Vulkan failure because no live
   transfer-readable color image was available, and explicitly confirmed that
   no CPU or OS-window fallback was used.
3. The stale Vulkan unit-test call sites were migrated for the removed
   schedule-cache authority and required `CommandChainKey.ChainOrdinal`.
   `Test-VulkanPhase3-Regression` passed all 110 tests.
4. `dotnet build .\XRENGINE.slnx --no-restore -warnaserror` completed with zero
   warnings and zero errors.

### 4. Collapse The Runtime Surface And Remove Hot-Path Work

Phase 4 uses hierarchical completion. A checked child records one independently
validated vertical slice. Its parent remains open until every child and the
parent's final integration gate pass. This makes progress visible without
treating partial authority extraction as completion of the seven-authority end
state.

#### 4.0 Compact Planning And Stable-Frame Work

- [x] Pre-resolve pass, resource, material, pipeline, and descriptor decisions
  into compact plan data; defer diagnostic string construction until emission.
  - [x] Pre-resolve resource, material, pipeline, and descriptor choices into
    immutable plan records consumed directly by recording.
- [x] Move required pipeline/shader creation and planner warnings out of primary
  recording.
  - [x] Prewarm every required engine pipeline and shader variant before its
    first production recording use.
  - [x] Publish typed planner outcomes and defer warning formatting until cold
    diagnostics or export.
- [x] Remove per-frame attachment-layout arrays, UI split arrays, graph sorting,
  and other repeated or superlinear plan construction.
- [x] Make stable-frame work proportional to changed or visible content rather
  than registered resources, cache size, or historical frame operations.
  - [x] Remove steady-state full scans, string parsing/hashing, reflection,
    repeated graph sorting, and cache-wide dirty propagation.
  - [x] Demonstrate proportional scaling with stable-frame counters and matched
    representative workloads.
- [x] Keep the normal hot path free of LINQ, closures, boxing, strings,
  expected-status exceptions, `Task` creation, collection growth, and
  contention-heavy global atomics.
  - [x] Use fixed frame-slot arenas and persistent worker-owned storage for all
    remaining normal-path transient work.
  - [x] Pass the warmed zero-managed-allocation and observer-overhead gates.

#### 4.1 Extract The Seven Runtime Authorities

> **Status on 2026-08-10:** Phase 4.1 is complete. The implementation contains
> one 442-line, non-partial `VulkanRenderer` facade with exactly the seven
> authority-root fields and no renderer partial declarations. The final exact
> inventory gates report no unapproved authority edge, renderer backlink,
> facade callback, ambient renderer lookup, or thread-static escape hatch.
>
> **Execution rule:** restore the Vulkan runtime baseline in blocker 4.1.0
> before performing more authority moves. Then close 4.1.1 through 4.1.6 in
> dependency order; 4.1.7 is the final proof. The parent checkbox may be checked
> only when every blocker below meets its stated exit condition.

- [x] **4.1.0 Restore a working Vulkan runtime baseline before continuing the
  extraction.** The original failure was a resource-generation identity and
  publication regression. After that was corrected, live tracing exposed a
  second blocker: `XRQuadFrameBuffer.Render` rejected the final Vulkan present
  quad before its typed publisher could capture `SourceTexture`, so the frame
  omitted `RenderToWindow`, the final-presentation tuple stayed incomplete, and
  the window presented recovery content indefinitely. The present path now
  enqueues publisher-owned draws without descriptor preflight and treats a
  successful first descriptor write as authoritative publication.
  - [x] Fix the resource-generation identity/lifetime instability which lets the
    `BloomBlurTexture` image-view or descriptor payload change between prepare
    and commit. Do not suppress or retry past the invariant failure.
  - [x] Validate the fix through a Vulkan Unit Testing World run: the initial
    generation commits, a non-white scene is visible in screenshots from two
    camera positions, and the Vulkan/rendering logs contain no repeating
    generation exception, retry-backoff loop, or validation error.
    Named session `vulkan-core-410-final23-20260806` completed frames with 34
    frame operations, including `RenderToWindow_TsrOutputTexture`, and two
    swapchain writes. The final-presentation ledger matched the source image
    view/sampler, descriptor set/slot, and scene primary with no invariant
    failure. Two inspected camera-dependent screenshots showed Sponza geometry
    instead of white/purple recovery content. The warning-as-error Vulkan build
    passed, and clean shutdown logs contained no repeated generation-commit
    exception, null reference, retry backoff, incomplete presentation epoch,
    stale secondary, VUID, device loss, or validation error.
- [x] **4.1.1 Cut the frame loop's facade callback spine.**
  - [x] Delete `IVulkanDesktopFramePhaseService` and the stateful desktop frame
    coordinator; `VulkanFrameLoop` owns admission, settlement, and ordered phase
    sequencing.
  - [x] Replace `VulkanFrameLoop.Render(VulkanRenderer renderer, double delta)`
    and every phase call it makes back into the facade with typed authority
    inputs and renderer-free phase owners.
  - Exit condition: `Frame/Loop/Authority` contains no `VulkanRenderer`
    reference, and the facade performs one one-way call into `VulkanFrameLoop`
    without being passed as an argument or callback target.
- [x] **4.1.2 Cut the output authority's retained facade and finish output
  behavior ownership.**
  - [x] Move output mutable state, target identities, and surface authority into
    `VulkanOutputRuntime` and renderer-free surface contracts.
  - [x] Remove `VulkanTargetOutputContext._renderer`, its
    `VulkanTargetOutputContext(VulkanRenderer)` constructor, and its 32 direct
    `_renderer.*` forwarding calls; replace the catch-all context with the
    narrow device, resource, command, and telemetry capabilities each target
    operation actually needs.
  - [x] Remove `VulkanRenderer` from
    `VulkanOutputRuntime.InitializeTargetFinalOutput(...)` and from
    `VulkanDesktopWsiTargetDriver.RecreateFinalOutput(...)`,
    `ShouldKeepPresentScalingSwapchain(...)`, `AcquireFrameTarget(...)`, and
    `PresentFrameTarget(...)`.
  - [x] Rehome remaining desktop, presentationless, OpenXR, mirror, capture,
    probe, shadow, preview, readback, and ImGui lifecycle methods from
    `VulkanRenderer.*` partials into `VulkanOutputRuntime` or focused typed
    adapters.
  - Exit condition: output authorities, target drivers, and output adapters
    neither accept nor retain `VulkanRenderer`, and no output lifecycle method is
    implemented on a renderer partial.
- [x] **4.1.3 Remove renderer-backed resource wrappers and finish resource
  behavior ownership.**
  - [x] Move resource mutable state, registries, descriptor state, pipeline
    caches, lifetime tracking, and allocator state to resource/device
    authorities.
  - [x] Remove the transitional `VkObject(VulkanRenderer renderer, T data)`
    constructor and `VkObject<T>.Renderer` accessor. Convert wrapper operations
    to `VulkanBackendObjectContext` plus narrow resource/command contracts.
  - [x] Rehome wrapper, registry, allocation, upload, descriptor, pipeline,
    pinning, retirement, and readback implementation still present in
    `VulkanRenderer.*` partials.
  - [x] Move `VulkanGraphicsPipelineCompileJob`,
    `VulkanPipelineManifestCacheKey`, and `VulkanPipelineVariantManifest` out of
    the renderer so `VulkanPipelineManager` no longer names renderer-nested
    contracts.
  - Exit condition: no `VkObject`-derived type reaches operations through a
    renderer backlink, `VulkanPipelineManager` contains no `VulkanRenderer`
    reference, and resource implementation lives under the resource authority.
- [x] **4.1.4 Move command execution, recording, and workers behind frozen
  command-runtime inputs.**
  - [x] Move command mutable state, worker synchronization, workspaces, and
    caches into `VulkanCommandRuntime`.
  - [x] Remove the `VulkanRenderer` parameters from
    `VulkanCommandRecorder.Begin(...)` and `Record(...)`.
  - [x] Replace `CommandChainRecordingBatch.WorkerProcedure` and the bound
    renderer worker method with a command-runtime worker procedure over an
    immutable prepared recording context. Move tracked reset/end, binding
    state, descriptor/local-read inheritance, and device admission into that
    context or its owning command services.
  - [x] Rehome scheduling, frame operations, primary/secondary recording,
    barriers, queue submission, receipts, and OpenXR recording workers that are
    still implemented by `VulkanRenderer.*` partials.
  - Exit condition: command authorities and workers neither accept nor retain
    `VulkanRenderer`; workers consume only frozen prepared inputs; command
    recording, synchronization, submission, and settlement behavior is owned by
    `VulkanCommandRuntime` and focused command services.
  - Completion evidence (2026-08-08): exact authority/worker scans contain no
    retained or accepted `VulkanRenderer` references outside the intentional
    compatibility facade. Warning-as-error Vulkan and editor builds pass with
    zero warnings and errors. Named Vulkan-only session
    `phase4-core-hardening-final` reached MCP readiness after a clean rebuild;
    two visually inspected, camera-dependent captures rendered scene geometry
    and debug overlays, readback alternated between slots 0 and 1, and the live
    Vulkan/general logs contained none of the prior null-context, frame-slot,
    unsealed-secondary, VUID, validation, or device-loss failures. Evidence is
    under `Build/_AgentValidation/mcp-sessions/phase4-core-hardening-final/`.
- [x] **4.1.5 Replace opaque authority state and renderer-nested planner
  contracts with concrete types.**
  - [x] Replace the planner's former type-keyed state bag with a concrete
    `MutableState` owner and remove duplicate planner/cache state.
  - [x] Replace `VulkanCommandRuntime._threadWorkspaces` and
    `GetThreadWorkspace<...>()`, which currently use
    `Dictionary<Type, object>`, with explicitly owned typed workspaces.
  - [x] Replace `VulkanFramePlanner.PublishResourcePlannerGeneration(object)`
    with a concrete generation contract.
  - [x] Move `PooledExternalResourcePlannerReadbackScope`,
    `FrameOpResourcePlannerSwitchingState`, `QueueOwnershipConfigCacheEntry`,
    `MergedFrameOpRegistryCacheEntry`, `FrameOpRegistryCacheSource`, and
    `ActivePassMetadataFilterCacheEntry` out of `VulkanRenderer`; update
    `VulkanFramePlanner.MutableState` to use the independent types.
  - Exit condition: authorities contain no type-keyed `object` service/state bag,
    opaque generation publication, all-authorities context, or
    `VulkanRenderer.*` contract type.
- [x] **4.1.6 Rehome the remaining renderer implementation and create the final
  facade.** This is a semantic migration, not a file rename.
  - [x] Establish exactly seven authority-root instance fields on
    `VulkanRenderer`: device, output, frame loop, planner, resources, commands,
    and telemetry.
  - [x] Complete `VulkanDeviceContext` as the sole native device, capability,
    validation, debug-messenger, presentation-probe, and typed device-fault
    authority with no renderer backlink.
  - [x] Complete `VulkanFrameTelemetry` as the typed lifecycle outcome and
    bounded publication owner used by profiler, MCP, trace, counter, and export
    consumers.
  - [x] After blockers 4.1.1 through 4.1.5 are closed, move every remaining
    implementation member out of all 207 `VulkanRenderer` partial declarations
    and delete those declarations.
  - [x] Create one non-partial `VulkanRenderer` facade of at most 500 physical
    lines which owns only public API translation, authority construction, and
    one-way composition. Generated code may not hide renderer state or excluded
    implementation behavior.
  - Exit condition: the inventory reports exactly one non-partial
    `VulkanRenderer` declaration, zero partial declarations, seven authority-root
    fields, and no subsystem implementation member on the facade.
- [x] **4.1.7 Prove dependency direction and close Phase 4.1.**
  - [x] Maintain the declaring-type-aware inventory in
    `Tools/Reports/Get-VulkanCoreArchitectureInventory.ps1`.
  - [x] Current inventory reports zero unapproved authority-to-authority edges,
    zero ambient renderer lookups, zero ordinary hot-path thread-static files,
    and no device/validation backlink.
  - [x] Remove the six previously reported authority renderer-backlink files:
    `VulkanDesktopWsiTargetDriver.cs`, `VulkanCommandRecorder.cs`,
    `VulkanFrameLoop.cs`, `VulkanOutputRuntime.cs`,
    `VulkanPipelineManager.cs`, and `VulkanFramePlanner.cs`.
  - [x] Classify and eliminate the 99 previous facade-callback candidate files.
    If a candidate is a lexical false positive, tighten the inventory rule and
    document the allowed edge; do not waive a real retained reference, facade
    parameter, callback interface, or call back into the facade.
  - [x] Run the final inventory and archive its summary with the Phase 4
    validation evidence.
  - Exit condition: zero unapproved authority dependency edges, zero authority
    renderer backlinks, zero unresolved facade-callback candidates, zero
    ambient lookup/thread-static escape hatches, and a passing Vulkan runtime
    baseline from blocker 4.1.0.
  - Completion evidence (2026-08-10): the archived inventory at
    `Build/_AgentValidation/20260809-phase41-facade-close/reports/architecture-inventory-final.json`
    reports one non-partial 442-line facade, zero partial declarations, seven
    authority-root fields, zero authority dependency violations, zero renderer
    backlinks, zero facade callbacks, zero ambient renderer lookups, and zero
    thread-static/ambient-thread-state files. The conservative retained-type
    graph still lists 22 multi-authority types and 28 broad advisory flags; that
    is follow-on hardening evidence, not an exact Phase 4.1 exit gate.
    Warning-as-error builds passed for the Vulkan project and editor. Named
    Vulkan session `phase41-final-20260810` committed resource generation 1
    with 51 textures and 59 framebuffers; two inspected camera-dependent Sponza
    captures read back from alternating slots, and steady-state logs contained
    no VUID, validation error, exception, or frame failure. One bounded startup
    rejection occurred while the render pipeline warmed, then recovered.

#### 4.2 Make Frame Settlement Explicit

- [x] Add allocation and timing scopes for plan build, recording, queue-lock
  wait, native submission, and worker wait.
- [x] Make the acquire-to-settlement frame loop one readable orchestration spine
  with typed phase outcomes and exactly-once ownership settlement on every
  return, exception, resize, output-unavailable, and device-loss path.
  - [x] Publish typed frame/output identity, stage outcomes, wait reasons, and
    unreached-stage state through `VulkanFrameTelemetry`.
  - [x] Return `VulkanSubmissionReceipt` immediately after accepted native queue
    work and before fallible telemetry or publication work.
  - [x] Settle presentationless accepted command-buffer lifetime before command
    pool reset and defer accepted incomplete OpenXR fences.
  - [x] Express acquire, plan, prepare, schedule, record, submit, output
    completion, and settlement in one readable orchestration spine.
  - [x] Prove exactly-once settlement and one terminal typed outcome for every
    early return, exception, resize, unavailable output, and injected device
    loss.

#### 4.3 Finish Frame-Slot And Native-Memory Arenas

- [x] Use frame-indexed upload/storage arenas, stable bindings and offsets, and
  capacity growth with subrange updates for camera, transform, material,
  skinning, debug-line, and indirect data.
  - [x] Introduce the persistently mapped mesh `VulkanMappedFrameArena` with
    typed aligned slices, generation checks, and explicit writable, prepared,
    submitted, and reusable states.
  - [x] Validate mapped mesh slices against device ownership and
    `nonCoherentAtomSize`, including recorded flush expansion.
  - [x] Migrate camera, transform, material, skinning, debug-line, indirect,
    upload, staging, and readback consumers to the canonical frame-slot arenas.
  - [x] Add bounded capacity growth and dirty-subrange publication for every
    migrated stream.

Implementation evidence (2026-08-10): the desktop loop now retains typed stage
results across the acquire, plan, prepare, schedule, record, submit, output, and
terminal-settlement spine. Native acquire, submit, and present ownership changes
are committed immediately after the native boundary, accepted submissions keep
their receipt and reuse/timeline retirement debt, and the settlement pass uses a
one-way claim to publish one `VulkanDesktopFrameTerminalResult` on every unwind.

`VulkanFrameDataArena` now provides capability-based upload, staging, readback,
uniform, storage, and indirect lanes with stable typed slices, bounded geometric
chunk growth, fixed dirty-range coalescing, non-coherent atom expansion, and
explicit writable/prepared/submitted/reusable states. Generic `VkDataBuffer`
subrange uploads cover camera, transform, material, skinning, debug-line, and
indirect destinations without payload-name classification. Ordinary texture
uploads use exclusive synchronous arena scratch; accepted-incomplete fence,
command-buffer, and arena ownership is retained in preallocated completion debt.
Independently fenced imported texture
streaming remains in the bounded staging pool because its lifetime is not owned
by a desktop frame slot. Screenshot, asynchronous depth, and GPU-stat readbacks
use independent fence-owned arena rings. The final named Vulkan session produced two successful
16,588,800-byte readbacks on alternating slots with no arena rejection, ImGui
assertion, VUID, validation error, render exception, frame failure, or device
loss in its logs.
Native-boundary transient arrays, mapped-memory safety, and final unsafe-code
containment are tracked once in section 4.5 to avoid duplicate completion boxes.

#### 4.4 Meet Structural And Unsafe-Code Gates

- [x] Inventory hand-written/generated source separately, dependency direction,
  file/line counts, partials, fields, largest files/methods, directory depth, and
  duplicated authorities before each consolidation phase. Evidence includes the
  reproducible 2026-08-06 pre-extraction baseline and post-cut inventories after
  the initial, device-capability, and native-lifetime vertical slices.
- [ ] Reduce the hand-written Vulkan core from the reproducible 2026-08-06
  Phase-4 baseline of 890 files / 178,506 physical lines to at most 550 files /
  125,000 lines, and reduce the acquire-to-settlement lifecycle spine to at most
  40 files / 20,000 lines.
  - [x] Reduce renderer partial declarations from 320 after the device-capability
    cut to 308 after the native-lifetime cut.
  - [x] Reduce type-wide unsafe files from 378 to 372 over the same cut.
  - [ ] Meet the final file, line, partial, lifecycle-spine, and unsafe budgets.
- [ ] Keep the main frame orchestration method at most 100 logical lines. Split
  any hand-written file above 1,500 physical lines or method above 150 logical
  lines unless a documented ownership exception is approved before cutover.
- [ ] Consolidate or delete duplicate planners, schedulers, profilers, caches,
  descriptor/lifetime models, forwarding shims, compatibility branches, and
  one-method partials as their consumers migrate.
  - [x] Remove the nested queue-family selector/type authority and lift the KHR
    device-fault records, fault-injection stage, and submission diagnostic
    context out of renderer partials.
  - [ ] Delete every remaining duplicate authority and forwarding shim with its
    final consumer. Never preserve two production authorities or meet counts by
    combining unrelated top-level types.

#### Phase 4 Implementation Status (2026-08-06)

The hierarchy above distinguishes validated slices from final gates. Checked
children are complete and evidence-backed; an unchecked parent is not a claim
that its checked children are unfinished. The final seven-authority, frame-loop,
arena, hot-path, structural, and unsafe-code parents remain open until all of
their children and promotion evidence pass.

The pre-extraction structural baseline is generated by
`Tools/Reports/Get-VulkanCoreArchitectureInventory.ps1`; its ignored evidence is
under `Build/_AgentValidation/mcp-sessions/vulkan-core-phase4/reports/`. The
inventory separates generated source, identifies partial and unsafe facade
surface, ambient facade callbacks, thread-static state, authority matches,
directory depth, and largest files. It supersedes the older unreproducible
858-file baseline that predated the current Phase 1-3 source.

The durable per-frame/per-draw stream baseline is
[`vulkan-hot-data-layout-inventory.md`](../../inventory/rendering/vulkan-hot-data-layout-inventory.md).
It records current element sizes/layout, managed state, producer/consumer field
access, copies and compatibility conversions, mutation frequency, and generation
ownership for GPUScene, indirect, planning, packet, prepared-draw, descriptor,
graph/barrier, worker, upload, mapped, and native-scratch streams.

Phase 4 is being migrated in vertical ownership cuts. The first validated cut
is now implemented: `VulkanDeviceContext` owns the monotonic device-state
machine, configuration/probe data, and first typed native device fault;
`VulkanFrameTelemetry` owns typed lifecycle-stage publication; desktop planning
and packetization consume a numeric operation stream; `RenderPacket` stores
numeric ranges into a frame-owned payload arena; mapped mesh frame data uses an
atom-aware `VulkanMappedFrameArena`; native barrier arrays use reusable typed
scratch; and accepted queue submissions return a `VulkanSubmissionReceipt`
before fallible publication work. Presentationless fence waits now settle the
accepted command-buffer lifetime before pool reset.

The next validated device-capability cut is also complete. `VulkanDeviceContext`
is now the sole mutable authority for selected physical-device identity and its
immutable capability snapshot, queue-family selection and queue handles,
available/enabled extension publication, alignment limits, logical-device
identity, OpenXR creation identity, and final device capabilities. Native device
success is committed before enabled extensions become authoritative; queues and
capabilities publish through explicit exactly-once gates. Renderer facade
members are read-only behavioral projections, and the old nested queue-family
type/selector authority was removed. Focused tests passed 50/50, the real
presentationless clear/readback/hash smoke passed without skipping, the full
warnings-as-errors build passed, and a final validation-enabled Vulkan session
and clean shutdown produced no validation or device-loss signature.

The third validated device-context cut now owns the Vulkan instance and enabled
instance-extension authority, API/OpenXR bootstrap identity, debug-utils loader
and messenger lifetime, native callback registration, bounded validation
aggregation, output-supplied presentation probing, required/optional device
extension policy, submission-diagnostic snapshot, and KHR/EXT device-fault
capability plus KHR function-table state. Presentation queries propagate native
failure results instead of treating query failure as unsupported presentation.
The callback sink retains no renderer reference; device-address binding payloads
cross into renderer-level resource correlation only through a bounded drained
record stream on the cold device-loss path. Surface teardown now occurs while
the debug messenger remains active, followed by messenger and instance teardown.
The Phase 3 regression passed 110/110, focused ownership and diagnostics coverage
passed 25/25, the solution warning-as-error build passed, and three clean
validation-enabled Vulkan runs reported zero validation messages/errors and no
shutdown fault signature.

This is a substantial Phase 4 vertical slice, not completion of the complete
seven-authority target. Bounded native device-fault report retrieval and
persistence, dense typed per-kind operation payloads, prepared-draw flattening, resource,
planning, command, output, and frame-loop authority extraction, OpenXR runtime
validation, facade collapse, and the final file/line/unsafe budgets remain open.
The latest inventory is 926 hand-written files / 181,668 physical lines, 308
renderer partial declarations, 372 type-wide unsafe files, 101
ambient facade-callback files, and two thread-static files. This records the
effect of splitting native contracts into one-type-per-file focused owners while
removing twelve renderer partials and six type-wide unsafe files from the prior
cut; it does not satisfy the final structural reduction gate.

#### 4.5 Make Hot Data Layout And Unsafe Boundaries Explicit

The [target architecture's data-layout contract](../../design/rendering/vulkan-render-loop-target-architecture.md#data-layout-and-native-memory-boundary)
is mandatory. SoA is used for field-wise bulk stages, compact AoS for records
consumed as a unit, and hot/cold or AoSoA only where measured. Unsafe code is a
contained native-memory mechanism, not a general performance mode.
The [Vulkan CPU SIMD Refactor Pass Design](../../design/rendering/vulkan-cpu-simd-refactor-pass-design.md)
defines the shared scalar oracle, width-selection policy, candidate order, and
promotion gates; it does not authorize SIMD in branch-heavy lifecycle, graph,
descriptor, barrier, or native command code.

- [x] Add a reproducible hot-data inventory containing every per-frame/per-draw
  stream, current element size/alignment, managed-reference fields, arrays or
  pooled buffers per element, producer/consumer stages, fields touched by each
  consumer, bytes copied/converted, mutation frequency, and owning generation.
- [ ] Establish a layout decision record for every changed stream. Compare the
  existing AoS with candidate SoA, compact AoS/hot-cold, and—only when useful—
  AoSoA layouts at representative counts before selecting the canonical form.
- [x] Preserve exact ABI/layout checks for `DrawMetadata`, `BoundsGpu`,
  `GPUIndirectRenderCommandHot`, shader structs, and native Vulkan records. Treat
  the current 64-byte metadata, 64-byte bounds, and 80-byte hot command as a
  measured baseline, not an immutable contract.
- [ ] Make GPUScene publish stage-native cull-control, cull-bounds,
  classification/sort-key, material/state, transform, previous-transform,
  visibility, and optional AABB streams directly. Keep compact vector AoS inside
  a stream when one shader invocation consumes the complete vector group.
- [ ] Define those logical streams in one scene-layout schema and publish them
  through one storage owner and generation transaction. Use typed aligned ranges
  in shared backing allocations where appropriate; do not add a wrapper, source
  file, Vulkan allocation, or descriptor binding for every individual column.
- [x] Remove `GPURenderExtractSoA.comp`, its scratch buffers, and the uncalled
  `SoACull` compatibility path unless a real consumer and a whole-stage win are
  demonstrated first. Do not pay a conversion merely to label a path SoA.
- [ ] Replace unconditional broad hot-command conversion with direct
  stage-native GPUScene consumption. Retain a compatibility envelope only for a
  named temporary consumer, meter its bytes/time, and give it a deletion gate.
- [ ] Generate the final contiguous `VkDrawIndirectCommand` or
  `VkDrawIndexedIndirectCommand` AoS stream after culling; do not split the
  driver-required indirect command structure into submission-time SoA buffers.
- [ ] Lower polymorphic `FrameOp` objects exactly once into an opcode/payload
  index stream plus dense per-kind draw, dispatch, copy, clear, barrier, and
  output payload arrays before sorting, planning, or worker scheduling.
  - [x] Convert desktop operation ingress, ordering, planning keys, and
    packetization to numeric operation headers and stable numeric identities.
  - [ ] Replace the remaining per-kind `FrameOp` reference sidecars with dense
    typed payload arrays and remove the compatibility object path.
- [x] Replace `RenderPacket`-owned draw/dispatch arrays and hot diagnostic target
  strings with compact numeric headers and `start/count` ranges into frame-owned
  arenas. Resolve names only in diagnostics/exporters.
- [ ] Replace `VulkanPreparedMeshDrawState` and `VkPreparedMeshDraw` per-draw
  descriptor, dynamic-offset, vertex, frame-payload, viewport, scissor, and
  other pooled arrays with flattened frame-slot streams and typed ranges.
  Separate the compact encoder hot header from managed owners, generation audit
  data, and diagnostic names in cold indexed sidecars.
- [ ] Keep prepared-draw headers compact AoS by default because encoding consumes
  most hot fields together. Introduce AoSoA tiles only if CPU counter and
  full-frame measurements beat compact AoS after including transpose, tail, and
  publication cost.
- [ ] Replace render-graph and barrier execution data based on strings,
  dictionaries, and lists-of-lists with typed numeric resource IDs and flat
  offset/count adjacency and barrier ranges. Materialize contiguous
  `VkMemoryBarrier2`, `VkBufferMemoryBarrier2`, and `VkImageMemoryBarrier2` AoS
  arrays only at the native call boundary.
- [ ] Store descriptor dirty state, resource/allocation generations, layouts,
  samplers, slots, and update frequency in scan-friendly publication streams.
  Build native descriptor-info/write arrays or aligned descriptor-buffer bytes
  only for dirty ranges in preallocated frame-slot scratch storage.
- [ ] Keep worker queue entries as compact AoS records. Move mutable worker
  counters and trace rings into independently write-owned blocks whose allocation
  base and stride are both aligned; merge them after completion instead of using
  contended per-item global atomics.
- [ ] Keep frame-attempt/lifecycle state, typed outcomes, queue receipts, and
  whole-record dependency keys as safe AoS. Split dependency identity into
  structural, binding, and data keys only if comparison profiles show a benefit;
  never replace resource ownership with naked pointers.
- [ ] Introduce small focused `VulkanNativeScratchArena` and
  `VulkanMappedFrameArena`-style owners for Vulkan ABI arrays, `pNext` chains,
  mapped upload/uniform/staging/readback memory, descriptor bytes, and validated
  binary decoding. Expose typed offset/length/alignment/generation slices and
  acquire raw pointers only at the final boundary.
  - [x] Replace native barrier pooled arrays with reusable typed
    `VulkanNativeScratchArena` reservations.
  - [ ] Add equivalent focused owners for every remaining ABI array, `pNext`
    chain, mapped upload/staging/readback path, descriptor byte stream, and
    validated binary decoder.
- [ ] Remove type-wide `unsafe` from the renderer facade and ordinary planning,
  graph, scheduling, and lifecycle owners. Prefer safe `Span<T>`/
  `ReadOnlySpan<T>` and measured vector APIs; every retained pointer loop must
  name the benchmarked gap that safe code could not close.
- [ ] Forbid raw managed pointers escaping `fixed`, pooled buffers escaping
  return ownership, per-frame `GCHandle` pinning that survives a native call,
  unchecked bitwise copies of padded/non-blittable structs, and `stackalloc`
  inside loops or with unbounded lengths.
- [ ] Validate mapped-memory slices against host/device ownership,
  `minMemoryMapAlignment`, and `nonCoherentAtomSize`; record flush/invalidate
  expansion rather than confusing CPU cache-line alignment with Vulkan memory
  visibility.
  The completed mapped mesh-frame slice is credited once in section 4.3.
  - [ ] Apply the same ownership, minimum-alignment, atom-size, flush, and
    invalidate contract to every remaining mapped-memory owner.
- [ ] Add allocation-free telemetry for elements and bytes read/written per hot
  stream, compatibility/conversion bytes, native scratch reservations/high-water
  marks, dirty descriptor ranges, graph edges, prepared-draw side-stream bytes,
  worker queue depth, and worker-local merge cost.
- [ ] Delete superseded object pools, per-packet arrays, conversion shaders,
  scratch buffers, descriptor builders, and unsafe helpers immediately after
  their final consumer moves to the canonical layout.

### 5. Schedule Outputs And Submission Without Cross-Output Blocking

- [ ] Build one deadline-aware submission DAG for uploads, shadows, desktop,
  OpenXR eyes, mirror, probes, captures, and publication.
- [ ] Prioritize acquired OpenXR eyes and reserve their critical path before
  optional output work; make desktop/secondary acquisition nonblocking for
  XR-owned frames.
- [ ] Add bounded, observable deferral, cadence, and stale-reuse policy for
  mirrors, probes, optional effects, and captures.
- [ ] Narrow native queue-lock ownership and never hold it across a blocking
  fence wait; use timeline/frame-slot completion for queue and OpenXR image
  ownership.
- [ ] During Win32 modal interactive resize, keep the already-published scene,
  shadow, UI, and presentation generations frozen independently and use WSI
  presentation scaling for the changing surface. Do not rebuild or retire the
  main physical resource plan inside the drag callback; publish one catch-up
  generation after the modal loop exits.
- [ ] Make modal resize dispatch bounded and nonblocking with respect to
  visibility publication, GPU completion, and retirement drains. A missing or
  incompatible frame package must produce an explicit defer/stale-reuse result,
  not leave the interactive-render guard latched indefinitely.
- [ ] Add persistent worker recording for independent safe packet classes and
  preserve serial recording for packets that cannot yet be isolated.

### 6. Simplify The Forward+ Render Graph

- [ ] Co-produce or reuse depth, normals, and velocity where possible; skip the
  depth prepass when no consumer requires it.
- [ ] Remove redundant opaque/masked geometry replay, full-resolution
  color/depth copies, paired blits, transitions, and barriers.
- [ ] Model attachment lifetime, aliasing, input attachments, and explicit
  transitions in backend-neutral graph intent with Vulkan realization.
- [ ] Conditionally allocate and execute AO, bloom, probe, shadow, temporal,
  and post-process producers only when their consumers are enabled.

### 7. Bound Shadow, Streaming, And Render-Thread Tail Work

- [ ] Define directional cascade invalidation from camera, light, caster,
  receiver, atlas, and quality state; stabilize projections and reuse unaffected
  cascade recording/data.
- [ ] Add a bounded per-frame directional-cascade update budget and explicit
  temporal policy.
- [ ] Move texture decode, transcode, mip preparation, and upload planning off
  the render thread; batch transfer recording, sparse transitions,
  finalization, and descriptor publication.
- [ ] Publish immutable texture generations with narrow descriptor/command
  invalidation and bounded per-frame upload work.
- [ ] Move pure generic jobs, BVH work, physics preparation, and capture
  preparation to their owning workers; split render-thread-affine work into
  budgeted increments with admission control.

### 8. Add End-To-End CPU Observability And Runtime Diagnostics

- [ ] Publish explicit counters and state for device loss, frame/output status,
  reuse decisions and misses, queue/fence wait, worker wait, allocations,
  jobs, cascade invalidation, uploads, descriptor publication, GPU work, and
  deferred work.
- [ ] Add device-fault, TDR-risk, memory-budget, and submission-breadcrumb
  diagnostics when supported by the active Vulkan device.
- [ ] Add concise Vulkan/OpenXR submit and descriptor-state dumps that preserve
  the last successful submission context without adding steady-state work.
- [ ] Record per-context planner ownership, display/internal extents, registry
  and resource generations, active physical allocation, and every attempted
  cross-context substitution. Promote incompatible context/extent reuse from a
  throttled warning to a structured frame-rejection reason.
- [ ] Extend final-presentation diagnostics with the complete immutable source
  tuple, bound descriptor payload, selected primary/secondary artifacts, layout
  transitions, swapchain image, and submit generation so a stale view cannot be
  mistaken for a valid logical `SourceTexture` binding.
- [ ] Add an interactive-resize liveness watchdog with breadcrumbs for modal
  callback entry/exit, visibility publication, package selection, plan
  replacement, retirement backlog, queue/timeline waits, submission, and
  present. Report renderer hangs separately from validation errors, device loss,
  managed exceptions, and native process crashes.
- [ ] Replace the disconnected desktop lifecycle counters, flat
  `EVulkanCpuStage` interpretation, and targeted Vulkan CPU spans with one
  `VulkanFrameTelemetry` schema; retain compatibility adapters only until every
  dashboard, profiler, MCP tool, and benchmark consumes the shared schema.
- [ ] Define one stable coarse stage taxonomy from frame pacing and snapshot
  handoff through acquire, plan, resource preparation, scheduling, recording,
  submit, output completion, and settlement; keep detailed operation IDs nested
  below those stages rather than expanding the top-level budget vocabulary.
- [ ] Correlate every aggregate or retained span with engine/render frame IDs,
  output and view-set IDs, frame slot, relevant generation, stage/detail ID,
  span/parent/cross-thread link IDs, thread/worker ID, start/end timestamp,
  allocation, operation count/bytes, typed outcome, and wait/reuse/invalidation
  reason.
- [ ] Classify time as engine work, wait, native-driver call, external-runtime
  work, or intrusive diagnostics; prohibit unlabeled blocking calls and ensure
  queue-lock, fence/timeline, acquire, present, worker, collect, and retirement
  waits remain individually visible.
- [ ] Compute inclusive and exclusive time, aggregate worker CPU, wall active
  span, overlap, imbalance, render-thread wait, causal critical path, and
  attributed/unattributed root time after capture without double-counting nested
  or parallel scopes.
- [ ] Keep aggregate mode allocation-free and low-contention with fixed
  per-thread/per-frame storage. Keep targeted traces in pre-warmed bounded rings,
  freeze bounded before/after windows for slow frames, and serialize or aggregate
  only outside measured threads.
- [ ] In detailed captures, attribute at least 99% of each frame-root wall
  interval and emit an explicit `Unattributed` failure record for every gap of
  50 microseconds or more.
- [ ] Publish the same stable IDs and results to the editor frame tree/timeline,
  runtime counters, MCP/component-profiler results, and machine-readable
  JSON/CSV/trace exports; defer all string formatting until consumption.
- [ ] Measure aggregate and targeted observer overhead against the accepted
  clean-profile contract. Diagnostic instrumentation may not masquerade as a
  clean promotion capture or invalidate unrelated reusable commands.

### 9. Make Occlusion Modes Bounded And Effective

- [ ] Separate occlusion candidates, occluders, tested bounds, rasterized
  triangles, queries, Hi-Z invocations, indirect commands, and actual culls in
  runtime telemetry.
- [ ] Add representative open, moderate, occluder-heavy, masked, static, and
  deterministic moving-camera occlusion scenarios.
- [ ] Bound CPU-software candidate selection, sorting, and rasterization; bypass
  cheaply when candidates, occluders, or prior benefit do not justify the work.
- [ ] Define CPU-query latency, refresh, stale-result, and camera-motion policy
  without CPU waits or current-frame result dependencies.
- [ ] Use persistent minimal-format GPU Hi-Z resources; bound pyramid, barriers,
  refinement, and count-copy work, consume visibility on GPU, and cheaply bypass
  ineffective cases.
- [ ] Define selection thresholds and hysteresis, retain a forced diagnostic
  mode, and explicitly mark each CPU-software, CPU-query, and GPU Hi-Z mode as
  production, opt-in, diagnostic-only, or retired.

## Advanced Render Pipeline Phases 06 Through 10

These phases continue the ordered
[Advanced Render Pipeline Architectural Refactor](architectural-refactor/00-advanced-render-pipeline-refactor-todo.md)
after [05 - Attribute Reconstruction](architectural-refactor/05-attribute-reconstruction-todo.md).
They consume the immutable frame, resource, descriptor, scheduling, and
diagnostic contracts in sections 1 through 9 above rather than creating a
parallel renderer architecture.

Sections 10 through 14 are backend-neutral rendering architecture. OpenGL and
Vulkan may use different native encodings, but both must implement the same
logical visibility, material, view, resource-generation, and output contracts;
the Vulkan path additionally inherits every hardening invariant above.

| Former phase | Consolidated section | Dependency outcome |
| --- | --- | --- |
| 06 | 10. Classify Visible Material Work | Attribute reconstruction supplies stable `AdvancedSurface` identity and derivatives. |
| 07 | 11. Shade Native Opaque Materials | Classification supplies bounded visible kernel work. |
| 08 | 12. Integrate Transparency, Special Passes, And Post | Native opaque HDR and depth become the only ordinary scene-color foundation. |
| 09 | 13. Integrate Stereo, XR, Capture, And Editor Views | Every output consumes the same scene/feature contracts through independent context-local plans. |
| 10 | 14. Cut Over And Retire Legacy Rendering | The companion testing tracker supplies the required promotion evidence. |

### 10. Classify Visible Material Work On The GPU

The canonical classification key is shading kernel, material layout,
material-state/coverage class, required attribute/derivative mode, and view
mode. Material-row ID remains data within a compatible kernel, and descriptor
set object identity is never part of the logical key.

#### 10.1 Work Domain And Tile Policy

- [ ] Select initial tile dimensions from measured occupancy and subgroup
  behavior.
- [ ] Define mono and per-eye/layer addressing.
- [ ] Define active-tile, kernel-tile, and optional compact pixel-list records.
- [ ] Reserve capacities from screen size and documented worst-case material
  diversity.
- [ ] Define empty-pixel and background exclusion.

#### 10.2 Classification Kernels

- [ ] Read final visibility and resolve the material/kernel key from immutable
  GPU tables.
- [ ] Build active tiles and per-kernel tile membership.
- [ ] Add a compact pixel-list path for sparse or highly mixed tiles only where
  it wins measured workloads.
- [ ] Use subgroup ballot/scan where available.
- [ ] Provide a deterministic bounded shared-memory fallback when subgroup
  operations are unavailable.
- [ ] Avoid atomics proportional to total registered material count.
- [ ] Skip empty tiles and kernels without CPU involvement.

#### 10.3 GPU Dispatch Construction

- [ ] Prefix-sum or otherwise compact kernel/tile/pixel ranges.
- [ ] Build indirect dispatch arguments entirely on the GPU.
- [ ] Keep bounded fixed command topology over kernel families or use a
  backend-supported indirect execution mechanism.
- [ ] Treat count and range changes as data-only publication that does not
  rerecord otherwise reusable primary or secondary packets.
- [ ] Publish the minimum resource-specific barriers required before native
  shading through the immutable frame plan.
- [ ] Keep delayed statistics readback outside the frame dependency chain.

#### 10.4 Capacity And Overflow

- [ ] Define independent overflow contracts for active tiles, kernel
  memberships, pixel lists, and indirect-argument ranges.
- [ ] Never drop pixels silently.
- [ ] In automatic mode, use a bounded conservative full-tile kernel recovery
  only when it preserves correctness.
- [ ] In required mode, expose an error surface and structured failure when
  correctness cannot be preserved.
- [ ] Record the first overflow cause, required capacity, selected recovery,
  and affected pixels through delayed diagnostics.
- [ ] Grow persistent capacity only at a safe frame boundary through the
  generation transaction in section 1.

#### 10.5 Material Diversity And Kernel Scheduling

- [ ] Keep many material rows sharing one kernel within common dispatch work;
  never create one dispatch per material instance.
- [ ] Order kernel work to reduce pipeline changes without changing visibility
  correctness.
- [ ] Prewarm engine-owned kernel families and backend variants.
- [ ] Define explicit behavior for rare kernels, pending shader compilation,
  and nonresident textures.
- [ ] Expose material eligibility, kernel ID, and selected recovery in editor
  diagnostics.

#### 10.6 Classification Diagnostics

- [ ] Add views for active tiles, kernel IDs, material IDs, mixed-tile density,
  pixel-list density, dispatch ranges, and overflow.
- [ ] Add counters for visible pixels, active tiles, kernel-tile pairs,
  compacted pixels, active kernels, dispatches, overflows, and GPU time.
- [ ] Report classification work independently for every stereo eye/layer.
- [ ] Give every classification buffer a stable capture name.

### 11. Shade Native Opaque Materials, Lighting, Decals, And GI

Compatible opaque and masked surfaces shade directly from reconstructed
visibility into advanced opaque HDR. This section must not recreate the classic
GBuffer, deferred-light accumulation, ordinary opaque Forward+, or full-frame
light-combine graph.

#### 11.1 Native Kernel Interface

- [ ] Define a generated/authored kernel interface receiving `AdvancedSurface`,
  material row, view record, light/decal ranges, shadow tables,
  environment/probe data, and GI resources.
- [ ] Define outputs for opaque HDR, dense velocity, temporal/reactive masks,
  exposure/luminance inputs, and only the minimal optional sidecars required by
  later effects.
- [ ] Load textures through material-row references and the active global
  texture-indirection rung.
- [ ] Bind global scene, material, light, and texture tables once per compatible
  command scope.
- [ ] Compile one kernel per material family/layout/feature contract, not per
  material instance.
- [ ] Define explicit missing-kernel, pending-compile, invalid-layout, and
  nonresident-texture behavior.

#### 11.2 Standard Material Families

- [ ] Implement standard opaque PBR first.
- [ ] Add masked PBR using the coverage decision already established by the
  visibility pass.
- [ ] Add unlit/emissive shading.
- [ ] Add subsequent engine-owned families in measured priority order, such as
  skin, cloth, terrain, toon, and hair cards.
- [ ] Define custom-material opt-in metadata and reject undeclared arbitrary
  shader state.
- [ ] Add kernel prewarm and permutation-budget telemetry.

#### 11.3 Clustered Lighting

- [ ] Define one backend-neutral froxel grid per view using screen-tile X/Y and
  depth-slice Z.
- [ ] Build local point- and spot-light lists on the GPU.
- [ ] Keep directional lights in a bounded global list.
- [ ] Share the same light records and froxel indexing across every native
  material kernel.
- [ ] Define overflow and conservative recovery without silently dropping light
  contribution.
- [ ] Add froxel occupancy, light-count, overflow, and selected-light debug
  views.

#### 11.4 Shared Shadow Records

- [ ] Publish directional, point, spot, cascade, atlas, filter, and fallback
  metadata through GPU shadow records instead of large per-program uniform
  sets.
- [ ] Preserve the relevance, dirty-tile, stale-tile, contact-shadow, and
  bounded cascade-update policies established in section 7.
- [ ] Make every material kernel use shared shadow-sampling helpers.
- [ ] Consume reconstructed screen position and depth consistently under normal
  and reversed depth.
- [ ] Publish machine-readable missing, stale, and unavailable shadow fallback
  state.
- [ ] Keep cascade transitions, atlas edges, cubemap seams, filter modes, and
  stereo addressing explicit in the shadow contract.

#### 11.5 Ambient Occlusion

- [ ] Select the advanced AO contract: depth plus reconstructed normal, a
  compact normal sidecar, or provider-specific visibility sampling.
- [ ] Schedule AO before the lighting contribution that consumes it.
- [ ] Do not recreate a multi-channel GBuffer solely for AO compatibility.
- [ ] Adapt supported AO providers to declared advanced inputs.
- [ ] Mark unsupported providers unavailable for the advanced pipeline instead
  of silently invoking legacy resources.
- [ ] Define coordinates, depth convention, half/full resolution, stereo,
  temporal-history, and camera-cut behavior for every supported provider.

#### 11.6 Decals And Surface Modifiers

- [ ] Build per-tile/froxel decal lists.
- [ ] Apply compatible decals as material/surface modifiers before lighting
  using reconstructed position and normal basis.
- [ ] Define decal ordering, blend semantics, normal blending, material
  filters, and overflow.
- [ ] Route geometry-changing or unsupported decals to an explicit special path
  or error state.
- [ ] Do not require classic deferred decal GBuffer writes.

#### 11.7 Environment, Probes, And GI

- [ ] Publish IBL and light-probe lookup through shared GPU records.
- [ ] Define a narrow `IAdvancedGlobalIlluminationProvider` contract for
  radiance/irradiance queries and optional screen-space outputs.
- [ ] Adapt supported probe, surfel, radiance-cascade, voxel, ReSTIR, and future
  providers without full-frame light-combine compositing.
- [ ] Ensure only one selected GI mode contributes unless an explicitly
  documented blend is requested.
- [ ] Expose unavailable providers and required resources before rendering.
- [ ] Define invalid-history, missing-probe, provider-switch, and stereo
  behavior.

#### 11.8 Background And Uncovered Pixels

- [ ] Shade visibility-sentinel pixels through the selected sky/background
  contract.
- [ ] Preserve atmospheric sky inputs without drawing an ordinary opaque
  forward background mesh where a compute/background kernel suffices.
- [ ] Define clear color, alpha, HDR encoding, and external-capture behavior.
- [ ] Keep procedural/custom background geometry as an explicit compatible
  visibility producer or special pass.

#### 11.9 Native Shading Diagnostics

- [ ] Add views for reconstructed albedo, normal, roughness, metalness,
  emission, AO, direct light, indirect light, shadow factor, decal contribution,
  kernel ID, and final opaque HDR.
- [ ] Add a diagnostic difference view against the original pipeline without
  using the original pipeline in production execution.
- [ ] Record GPU time per classification, kernel family, lighting, shadow, AO,
  decal, and GI stage.

### 12. Integrate Transparency, Special Passes, And Post-Processing

Every late draw must declare whether it is temporally participating
transparency, scene-color-dependent refraction, exact transparency/OIT,
volumetric/atmospheric work, a post-temporal overlay, editor/debug/on-top work,
or UI/presentation. Opaque and masked materials may not use these categories
merely because their native kernel is unavailable.

#### 12.1 Late-Pass Eligibility

- [ ] Add explicit material/pass metadata for blend, refraction, order
  dependence, temporal participation, depth-write behavior, and scene-color
  dependency.
- [ ] Remove advanced-pipeline use of `OpaqueForward` and `MaskedForward`.
- [ ] Reject compatible opaque work that attempts to enter a late path.
- [ ] Render unsupported required-mode opaque work with an observable error
  material or fail pipeline selection.
- [ ] Report late-pass counts and reasons per category.

#### 12.2 Scene Color And Depth Contract

- [ ] Publish native opaque HDR, final visibility depth, optional normal/AO
  sidecars, and exposure state under advanced resource names.
- [ ] Create a dedicated scene-color snapshot only when a visible refractive or
  scene-color-dependent pass requires it.
- [ ] Never sample an attachment while writing the same image without a
  supported feedback-loop contract.
- [ ] Preserve depth testing against final visibility depth.
- [ ] Define internal/output resolution and stereo-layer policy for every
  scene-color consumer.

#### 12.3 Transparency And OIT

- [ ] Port weighted blended OIT to native opaque HDR and advanced depth.
- [ ] Port PPLL and depth peeling through declared resources and typed commands.
- [ ] Define which transparent materials use sorted alpha, weighted OIT, PPLL,
  or depth peeling.
- [ ] Preserve shadow, froxel-light, probe, fog, and texture-table access through
  shared GPU records.
- [ ] Define current/previous transform and reactive-mask behavior for
  transparent motion.
- [ ] Add capacity and overflow diagnostics for OIT buffers without same-frame
  readback.

#### 12.4 Special Material Families

- [ ] Classify water, hair, particles, trails, beams, portals, mirrors, and
  custom effects as native visibility, transparent, refractive, volumetric, or
  unsupported.
- [ ] Give required geometry-displacing opaque effects a specialized visibility
  writer plus native material kernel.
- [ ] Keep simulation and update work outside the pipeline command-chain
  builder.
- [ ] Share global tables and avoid per-object descriptor reconstruction.
- [ ] Expose an editor-visible reason for every unsupported special effect.

#### 12.5 Atmosphere And Volumetric Fog

- [ ] Define sky, aerial-perspective, volumetric-fog, transparency, and
  refraction ordering.
- [ ] Adapt atmosphere and fog providers to final visibility depth and native
  HDR.
- [ ] Preserve half-resolution resources and temporal histories through
  declared generation-owned resources.
- [ ] Fog transparent objects consistently without relying on a legacy
  light-combine output.
- [ ] Define camera-cut, underwater/interior, stereo, and disabled-provider
  behavior.

#### 12.6 Dense Motion And Temporal Inputs

- [ ] Consume visibility-reconstructed opaque velocity directly.
- [ ] Merge transparent/special velocity only for participating pixels.
- [ ] Generate disocclusion, reactive, transparency, and invalid-history masks.
- [ ] Preserve exact jitter and motion-vector conventions required by TAA, TSR,
  DLSS, FSR, XeSS, and other active upscalers.
- [ ] Reset history explicitly for resize, pipeline switch, camera cut,
  view-count change, render-scale change, HDR change, and shader/resource
  generation replacement.

#### 12.7 Temporal And Post Chain

- [ ] Place temporal accumulation correctly relative to participating
  transparency and fog.
- [ ] Reconnect motion blur, depth of field, bloom, exposure, tone mapping,
  color grading, vignette, FXAA/SMAA, TSR, and vendor upscalers to advanced
  resource names.
- [ ] Skip disabled passes before resolving their resources or shaders.
- [ ] Preserve HDR/SDR output encoding and alpha behavior.
- [ ] Keep post-temporal overlays and UI outside temporal history.
- [ ] Remove legacy post-process bindings that assume GBuffer or light-combine
  attachment names.

#### 12.8 Late-Pass Diagnostics

- [ ] Add a pass-category overlay and per-category counts.
- [ ] Add views for scene-color snapshot, transparency accumulation/revealage,
  PPLL/depth-peel occupancy, reactive mask, velocity, history validity, fog,
  bloom, exposure, and final output.

### 13. Integrate Stereo, XR, Capture, And Editor Views

Desktop Advanced, RVC-owned OpenXR eyes, and offscreen consumers share logical
scene, mesh, material, GI, temporal, froxel, and post contracts while retaining
independent output-local pipeline instances, resource generations, histories,
and submission topology.

#### 13.1 View-Set Contract

- [ ] Specialize the immutable section-2 `ViewSetPlan` with view count, layer
  mapping, current/previous matrices, jitter, render region, foveation region,
  and output target.
- [ ] Give every view independent visibility, depth pyramid, history validity,
  material work, velocity, and temporal state.
- [ ] Share only view-independent scene, material, animation, deformation,
  light, and immutable-geometry preparation.
- [ ] Define conservative union rules only for work that is genuinely shared
  across views.
- [ ] Never reuse one eye's occlusion or depth verdict as another eye's
  authoritative result.

#### 13.2 Stereo And Multiview

- [ ] Declare layered visibility, depth, optional barycentric, HDR, velocity,
  reactive, and post-process histories.
- [ ] Add required RVC two-pass, OpenGL single-pass-stereo, and Vulkan
  parallel-recording/multiview variants.
- [ ] Add layered classification and native shading with explicit eye/layer
  addressing.
- [ ] Preserve per-eye derivatives, depth conventions, motion, and temporal
  reprojection.
- [ ] Select transparent, fog, atmosphere, shadow, probe, and post resources by
  explicit view/layer identity.
- [ ] Report the selected stereo mode and every structured fallback reason.

#### 13.3 XR Timing And Foveation

- [ ] Preserve runtime wait, begin, acquire, render, release, and end ordering.
- [ ] Fit RVC compute/graphics work into the section-5 deadline scheduler without
  hidden queue or device waits.
- [ ] Represent runtime-provided swapchains and image-array layers as imported
  generation-owned resources.
- [ ] Define foveated and variable-rate visibility/shading behavior without
  invalidating identity reconstruction.
- [ ] Keep periphery derivative and texture-LOD behavior conservative.
- [ ] Preserve late-latching, predicted-pose, motion-vector, and camera-cut
  contracts.
- [ ] Record CPU/GPU timing against the canonical XR budget while identifying
  capture overhead separately.

#### 13.4 Offscreen And Secondary Views

- [ ] Select the advanced pipeline for scene capture, mirror, portal,
  reflection, light probe, impostor, thumbnail, and test viewports through
  capabilities rather than concrete V2 type checks.
- [ ] Define minimal capture profiles that omit unrequested temporal, post, and
  late stages.
- [ ] Define depth-only and visibility-only capture profiles where useful.
- [ ] Preserve external-target ownership, synchronization, and output format.
- [ ] Avoid executing the main-view post chain for probe or shadow captures.
- [ ] Isolate nested and repeated capture resource names and generations.

#### 13.5 Editor Identity And Selection

- [ ] Resolve transform, component, mesh section, material, primitive, meshlet,
  and instance identity from visibility records.
- [ ] Route picking through asynchronous readback or GPU selection queries,
  never a frame-blocking full visibility readback.
- [ ] Preserve outlines, hover, gizmos, bounds, icons, physics debug, and on-top
  overlays.
- [ ] Add an inspector panel for decoded visibility payload and material-kernel
  eligibility.
- [ ] Replace editor checks for `DefaultRenderPipeline` and
  `DefaultRenderPipeline2` with focused provider interfaces.
- [ ] Prevent editor platform windows and previews from reusing stale or
  cross-context pipeline generations.

#### 13.6 Debug And Capture Tooling

- [ ] Register stable capture names for every advanced resource.
- [ ] Add command annotations for every early/late visibility, classification,
  shading, transparency, temporal, post, and output phase.
- [ ] Add MCP-visible settings and state for selected advanced mode, capability
  result, fallback/error reason, and debug view.
- [ ] Capture final advanced output in viewport screenshots without relying on
  legacy diagnostic FBO names.
- [ ] Make visibility payloads, draw records, material work lists, and indirect
  arguments RenderDoc-friendly.
- [ ] Keep delayed profiler readback bounded and explicitly removable from
  benchmark captures.

### 14. Cut Over Production Rendering And Retire Legacy Architecture

Code cutover begins only after the companion testing tracker records passing
correctness, stability, performance, allocation, readback, desktop, offscreen,
and XR evidence for the affected profile.

#### 14.1 Production Cutover

- [ ] Make `AdvancedRenderPipeline` the desktop and applicable offscreen default
  only after its gates pass; promote the RVC-owned OpenXR eye path only after the
  matching XR gates pass.
- [ ] Replace development selectors with the final pipeline-kind setting and
  documented launch/config behavior.
- [ ] Update generated settings, schemas, editor defaults, launch profiles, and
  unit-testing-world setup.
- [ ] Remove every remaining `DefaultRenderPipeline2`, `Default2`, pipeline-V2
  environment variable, diagnostic label, source-path assertion, and
  documentation instruction.
- [ ] Update `README.md`, `docs/README.md`, runtime overview, rendering
  architecture, material authoring, pipeline authoring, MCP, benchmark, and
  launch documentation.
- [ ] Regenerate MCP documentation if tool names or settings change.

#### 14.2 Legacy Retirement

- [ ] Delete deferred/forward resources, shaders, commands, settings, and tests
  that are unreachable after advanced cutover.
- [ ] Delete the original `DefaultRenderPipeline` after every required desktop,
  offscreen, capture, and XR consumer has migrated.
- [ ] If immediate deletion is blocked by a named required consumer, rename it
  to `LegacyDefaultRenderPipeline`, keep it opt-in, record its owner and exact
  blocker in the closeout, and set a dated deletion gate.
- [ ] Do not preserve both architectures through continued symmetric feature
  development.
- [ ] Move completed and superseded TODO material to the repository's historical
  convention and update every canonical link.
- [ ] Update dependency-free legal/product language only where renderer naming
  changes; do not alter licensing policy.

#### 14.3 Closeout

- [ ] Create a progress closeout under `docs/work/progress/rendering/` with the
  architecture summary, feature matrix, validation commands, images/captures,
  performance tables, remaining risks, and legacy-deletion status.
- [ ] Keep `Build/_AgentValidation/` within its ten-run-root limit and remove
  unneeded disposable evidence.
- [ ] Ensure tracked documentation does not depend on ignored evidence for
  required behavior.
- [ ] Mark the consolidated program complete only after no required work
  remains.

#### 14.4 Program Completion

- [ ] Make `AdvancedRenderPipeline` the desktop production default and keep
  production OpenXR eye output owned by `RvcRenderPipeline`.
- [ ] Route compatible opaque and masked rendering through visibility plus
  native material/lighting shading.
- [ ] Remove the classic GBuffer, deferred light accumulation, ordinary opaque
  Forward+, and light-combine stages from the advanced production graph.
- [ ] Remove `DefaultRenderPipeline2` completely.
- [ ] Delete the original default pipeline or retain exactly one explicit,
  bounded legacy blocker with an owner and dated removal gate.
- [ ] Meet the target architecture's facade, lifecycle-spine, complete Vulkan
  source/line, directory-depth, file-size, method-size, dependency-direction,
  and single-authority budgets from a reproducible final inventory.
- [ ] Demonstrate zero warmed managed hot-path allocation and approved desktop,
  presentationless, and XR p50/p95/p99/worst CPU budgets without moving cost to
  waits, retirement, descriptors, another output, or tail latency.
- [ ] Demonstrate that every hot stream has one canonical measured layout, no
  unconsumed compatibility extraction/conversion pass remains, and bytes touched
  scale with active stage work rather than broad record size or registry size.
- [ ] Demonstrate that data-oriented layouts reduced or preserved source files,
  runtime owners, Vulkan allocations, descriptor bindings, and lifetime
  transitions instead of moving complexity into per-column infrastructure.
- [ ] Demonstrate that unsafe code is confined to audited native/mapped-memory
  owners, safe span-based implementations remain the default when equivalent,
  and all retained unsafe paths pass lifetime, bounds, alignment, concurrency,
  and end-to-end performance gates.
- [ ] Demonstrate one correlated CPU lifecycle tree whose detailed captures
  attribute at least 99% of frame-root time, identify every gap of 50
  microseconds or more, and distinguish exclusive work, waits, driver/external
  time, worker overlap, and the required-output critical path.
- [ ] Demonstrate that a developer can locate every lifecycle owner and explain
  a retained slow frame from the frame spine, editor profiler, and exported
  trace without reconstructing state across `VulkanRenderer` partial files.

## Superseded Trackers

This tracker replaces the following implementation TODOs; their validation and
test work is consolidated in the companion testing tracker.

- Vulkan frame-plan recording refactor
- Vulkan primary command-recording fast path
- Forward+ prepass and render-graph cost
- Occlusion systems performance
- Render tail latency: shadows, streaming, and jobs
- Vulkan runtime code organization: remaining small-facade, source-surface, and
  ownership debt after the 2026-07-30 extraction milestone
- Architectural refactor 06: visible material work classification
- Architectural refactor 07: native material, lighting, decals, and GI
- Architectural refactor 08: transparency, special passes, and post-processing
- Architectural refactor 09: stereo, XR, capture, and editor integration
- Architectural refactor 10: validation, performance, cutover, and retirement
