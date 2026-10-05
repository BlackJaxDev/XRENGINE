# Frame Loop Design

[← Rendering Architecture index](README.md)

This document explains **why** the rendering frame loop is written the way it
is and **how** the rules are implemented. The stages themselves (update,
`CollectVisible`, `SwapBuffers`, render, record, submit, present) and their
threads are described in
[Rendering Frame Lifecycle And Dispatch Paths](frame-lifecycle-and-dispatch-paths.md);
Vulkan recording is described in
[Vulkan Primary And Secondary Command Recording](vulkan-command-recording.md).
This page covers the rules that keep work off those stages, the mechanisms that
implement them, and how to measure the loop without misleading yourself.

## Why These Rules Exist

The loop runs three threads in lockstep: the update thread mutates the world,
the collect-visible thread collects and publishes a snapshot, and the render
thread prepares, records, submits and presents it. The render thread waits for
the publish, and the collect thread waits for the previous render. Any
synchronous work on the collect or render thread therefore adds directly to
frame time, and any work that happens only occasionally becomes a visible hitch.

At 150 frames per second the render thread has under 7 ms. Measured stalls that
shaped this design include:

- **Draw admission joining compilation.** Shader compilation and pipeline
  creation ran synchronously inside draw admission. A cold Advanced family took
  1.6 s of source work.
- **Identity-only re-publication.** 393 mesh commands were re-published on every
  swap only because their publication identity advanced. That held the
  collect-to-render wait near 50 ms.
- **Generated equality boxing.** Generated record-struct equality boxed Vulkan
  handle values, costing about 250 KB of allocation per frame in visibility
  recording.
- **Repeated scene preparation.** Every stage of an Advanced family re-prepared
  the same scene publication.
- **Thrashing string cache.** A string signature cache thrashed on restored
  copies' uniform names, taking frames from 0.4 s to 1.9 s after a play exit.

Each section below states a rule, the reason, the implementation, and the
invariants that must stay true.

## Rules At A Glance

| Rule | Reason | Main mechanisms |
| --- | --- | --- |
| [The render thread never waits for compilation, linking or uploads](#the-render-thread-never-waits-for-compilation-linking-or-uploads) | A cold compile or upload is tens to thousands of milliseconds | Readiness states, background preparation, revision tickets, upload service |
| [Publish complete state or keep the previous state](#publish-complete-state-or-keep-the-previous-state) | Partial state renders wrong and is hard to retire | Resource generations, publication transactions, complete-family admission |
| [Publication identity is not a content change](#publication-identity-is-not-a-content-change) | Re-publishing unchanged content every frame dominates the collect wait | Dirty-property filter, versioned swap acknowledgement |
| [Rebuild derived state only when its inputs change](#rebuild-derived-state-only-when-its-inputs-change) | Transform-only motion must not rebuild registrations or rows | Retained registration signatures, per-row write gating, per-family preparation |
| [Hot paths do not allocate](#hot-paths-do-not-allocate) | Allocation per frame becomes GC pauses and hitches | Typed equality, indexed traversal, compact values, owned pools |
| [Invalidation is scoped and retirement waits for the GPU](#invalidation-is-scoped-and-retirement-waits-for-the-gpu) | Global invalidation stalls unrelated work; early destruction crashes | Mutation scopes, retirement tickets, retirement generations |
| [Shadow casters are the dominant motion cost](#shadow-casters-are-the-dominant-motion-cost) | Cascade refreshes record hundreds of draws through the slow path | Shared opaque caster material, packet lowering limits |
| [Play-mode restores must rebuild the same derived state](#play-mode-restores-must-rebuild-the-same-derived-state) | A restored object that differs from an imported one changes rendering | Cooked reads rebuild derived data; setters attach only what state needs |

## The Render Thread Never Waits For Compilation, Linking Or Uploads

Anything that compiles, links, uploads or discovers on demand is requested
ahead of use. The frame polls for completion and either proceeds with ready
work or reports the item as pending. It never joins the worker, and it never
substitutes a different path silently.

**Advanced visibility programs.**
[`VulkanAdvancedVisibilityPipelineRuntime.GetReadiness`](../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Advanced/VulkanAdvancedVisibilityPipelineRuntime.Preparation.cs)
returns one of `Ready`, `Missing`, `Pending` or `Failed`
([`VulkanAdvancedVisibilityPipelineReadiness`](../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Advanced/VulkanAdvancedVisibilityPipelineReadiness.cs)).

- **First request.** It publishes `Pending` and starts one background task that
  prepares the complete required family.
- **Preparation identity.** The task is keyed by a hash of the shader
  configuration version and each shader's source revision. A request with the
  same identity reuses the running task. An identity change mid-build restarts
  it.
- **Polling cost.** Polling refreshes generated sources when revisions change
  and checks that the required programs are current. It does not compile, link
  or wait.
- **Admission.** The capability gate in
  [`VulkanCommandRuntime.AdvancedPipelineCapabilities`](../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.AdvancedPipelineCapabilities.cs)
  maps `Pending` and `Missing` to the `PendingResources` admission state. An
  unadmitted family renders nothing rather than falling back to another path.
- **Testing the unsupported path.** `XRE_VK_ADVANCED_FORCE_UNAVAILABLE=1`
  forces `Failed` for validation.

Only shutdown joins the task.

**OpenGL Advanced programs.** The OpenGL backend builds its twelve Advanced
stage programs asynchronously and polls them on the render thread
([`OpenGLRenderer.AdvancedPipelineStages`](../../../XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Features/AdvancedPipeline/OpenGLRenderer.AdvancedPipelineStages.cs)).

- Every evaluation polls every program. Each poll advances only that program's
  own build, so stopping at the first unlinked program would link the family
  one program per evaluation.
- The pending reason counts failed, not-started and still-building programs
  and names the first failed or not-started program, so a permanent failure
  is not reported as "still compiling".
- The multisample and single-pass stereo program sets follow the same rule.
- Specialized shaders retain their authored root and include dependencies.
  Resolution applies the immutable OpenGL preamble and lowering again after
  invalidation; an eagerly expanded source string must not become the reload
  authority. Transforms run during source preparation and use the revision cache.
- Reload keeps the last linked program available while building a replacement.
  A completed source build whose revision is obsolete must release its exact
  compilation claim and old shader state so the replacement can proceed.
  Render admission alone does not prove that replacement reached Ready.

**Graphics pipelines** compile on background workers with dependency-scoped
invalidation; see [Vulkan Pipeline Compilation](vulkan-pipeline-compilation.md).

**Index buffers.** Draw admission asks for index preparation instead of building
indices inline.

- [`XRMesh.RequestIndexBufferPreparation`](../../../XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.IndexPreparation.cs)
  captures an `IndexBufferBuildTicket` for the mesh's exact geometry revision.
- The worker rejects a ticket whose revision is no longer current, so stale
  indices are never published.
- Vulkan renderers request once per observed revision and poll
  ([`VkMeshRenderer.IndexPreparation`](../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.IndexPreparation.cs)).
- A synchronous build happens only where a caller explicitly requires it.

**Texture uploads.** Prepared pixels reach the GPU through the generation-owned
[`VulkanTextureUploadService`](../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanTextureUploadService.cs).

- Admission is budgeted: one prepared upload per drain.
- A caller that receives no result yet polls again next frame.
- The editor toolbar shows the pattern end to end
  ([`EditorImGuiUI.Icons`](../../../XREngine.Editor/IMGUI/EditorImGuiUI.Icons.cs)).
  A worker parses and rasterizes the SVGs. The render owner then publishes
  textures under a per-frame budget and checks session and revision, and the
  draw only looks up ready handles.
- On OpenGL, Advanced material textures are bound through bindless handles, and
  a handle freezes the texture's sampling range. Advanced preparation therefore
  reports the family as pending ("pending sampling-parameter transition")
  until every referenced texture's progressive upload has finished
  ([`OpenGLRenderer.AdvancedBindlessResidency`](../../../XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Features/AdvancedPipeline/OpenGLRenderer.AdvancedBindlessResidency.cs)).
  Until then only the sky draws.
- OpenGL progressive uploads transfer row chunks until the shared byte or
  measured native-upload time budget declines work. Charge actual transferred
  bytes once; a full-mip fallback is indivisible. Partial mips stay outside the
  sampling range. Two slots retain exact texture/work-item owners, while
  priority comparison considers waiting registrations. Releasing an obsolete
  work item cannot release a successor's slot. Local and runtime-managed
  uploads use the same owner and budget rules.

**Cold mesh materialization.** A frame that cannot publish stops materializing
further requests once its slice is spent
([`VulkanFrameLoop.PrimaryRecordingPreparation`](../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanFrameLoop.PrimaryRecordingPreparation.cs),
4 ms). The remaining non-UI requests defer to later frames, instead of one
frame re-materializing every warm request while it waits.

**Editor discovery.** Creatable-type discovery for asset Create/Replace popups
runs on a worker while the popup is open
([`CreatableTypeDiscovery`](../../../XREngine.Editor/IMGUI/EditorImGuiUI.CreatableTypeDiscovery.cs)).
Passive inspector drawing does not discover types. A few camera-inspector type
lists (projection and render-pipeline types) are still resolved synchronously
and cached per catalog generation.

## Publish Complete State Or Keep The Previous State

Consumers only ever see a complete, validated state. While a replacement is
being built, the previous state keeps rendering. If the replacement fails, it is
discarded without touching the previous state.

**Render-pipeline resource generations.**
[`XRRenderPipelineInstance`](../../../XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs)
materializes a pending generation incrementally on the render thread.

- Ordinary slices stop at 2 ms or four specs. Resize catch-up stops at 8 ms or
  16 specs.
- Factories that cannot fit one slice are staged through
  `IIncrementalFrameBufferFactory`.
- Commit is transactional: the backend transaction prepares, then the pending
  generation becomes active and the old one retires after its fences.
- A failed build keeps the active generation and retries after a 1 s backoff.

The full contract is in
[Render Pipeline Resource Lifecycle](render-pipeline-resource-lifecycle.md).

**Render objects and mesh data.**
[`RenderObjectPublicationScope`](../../../XREngine.Runtime.Rendering/RenderObjects/RenderObjectPublicationScope.cs)
keeps mesh CPU data, render objects, backend wrappers and compound resources
hidden until one root commit.

- A nested scope enlists in its parent.
- A failure or an uncompleted dispose rolls back in reverse order.
- Scopes are thread-affine and stack-ordered.
- [`GenericRenderObject.BeginDeferredPublication`](../../../XREngine.Runtime.Rendering/RenderObjects/GenericRenderObject.cs)
  opens one. The cooked mesh reader uses it, for example.

**Frame packages and command chains.** The backend-ready frame package is
validated against collect, pipeline, resource, descriptor and render-graph
generations before it is used. A mismatch is a counted rejection, never a
repair from live scene state. When too many command chains are dirty to publish
a frame, publication is deferred and the frame replays the last complete scene;
see [command recording](vulkan-command-recording.md). Count those replays
separately from fresh frames ([measuring](#measuring-the-frame-loop)).

## Publication Identity Is Not A Content Change

A successful publication gives each mesh command a new canonical draw identity.
If that identity change marked the command dirty, every swap would re-publish
every command. The collect thread would then run hundreds of GPU-scene update
callbacks per frame for an unchanged scene.

- **The filter.**
  [`RenderCommandMesh3D.PublishCanonicalDrawIdentities`](../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandMesh3D.cs)
  still uses `SetField`, so notifications and both snapshots stay correct. But
  `IsRenderStateDirtyProperty` excludes that one caller-member name from
  render-state dirtiness. Every other property change still dirties the
  command.
- **The swap acknowledgement.**
  [`RenderCommand.SwapBuffers`](../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommand.cs)
  captures the mutation version at the start of a swap and acknowledges only
  that version, with a monotonic compare-exchange. A mutation that arrives
  during the swap callback, or after capture, stays pending for the next swap
  instead of being cleared with the captured one.

Invariant: never suppress notifications, write backing fields directly, or freeze
publication sequences to avoid dirtiness. Separate identity from content at the
dirty test.

## Rebuild Derived State Only When Its Inputs Change

Derived state is keyed by its real inputs. A swap that changes only a transform
must not rebuild anything that does not depend on the transform.

**Logical mesh registration.** `LogicalMeshState`
([`GPUScene.MeshAtlas`](../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.MeshAtlas.cs))
retains a registration signature: LOD list version, deferral policy, required
resident mesh, mesh identity and per-level geometry revisions.
`ResolveLogicalMeshRegistration`
([`GPUScene.AtlasManagement`](../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.AtlasManagement.cs))
reuses the retained registration on an exact hit without allocating, ensuring
atlas residency or writing logical tables. A miss rebuilds with bounded scratch.

**GPU scene rows.** Each destination row is written only when its content
changes
([`GPUScene.CommandConversion`](../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.CommandConversion.cs)).

- **Bounds and draw metadata** are compared with typed equality on `BoundsGpu`
  and `DrawMetadata`.
- **Transparency rows** mark their own dirty range, so a swap copies only the
  changed rows.
- **Material-state classes** are rewritten only when their content changes
  (`ResolveStateClassId`).
- **Unconditional writes.** Adds and swap-removes still write their rows.
  `WriteDrawMetadata` also rewrites the classification and visibility rows
  whenever it is called.

**Advanced family scene preparation.** All stages of one Advanced visibility
family consume the same scene publication.

- The family prepares it once, on its first stage.
- Later stages reuse that immutable state while they resolve the same backend
  package object, compared by reference
  ([`VulkanRenderer.CommandBufferRecording.Primary.Preparation`](../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs)).
- A mismatch within a family is a terminal renderer error, not silent reuse.
- Per-stage target validation, barriers and readiness checks are unchanged.

**Shared Advanced preparation.**
[`AdvancedSharedPreparationService.Acquire`](../../../XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedSharedPreparationService.cs)
reuses one publication while the frame, GPU scene and scene publication match,
and merges consumer requests.

- Consumers copy visibility inputs under the same lock and validate them
  against the publication.
- The range planner
  ([`AdvancedIndirectRangePlanner`](../../../XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedIndirectRangePlanner.cs))
  uses preallocated arrays, an open-addressing lookup and an epoch instead of
  per-build clears.

## Hot Paths Do Not Allocate

Steady-state update, collect, swap, preparation, recording, submission and
present allocate nothing. Per-frame allocation turns into GC pauses, and the
pauses turn into hitches.

- **Typed and scalar equality.** Generated record-struct equality traverses
  nested Silk.NET handle values and boxes them.
  - Advanced visibility closure validation and stable-bin lowering compare
    handles and scalars explicitly.
  - `BoundsGpu`, `DrawMetadata` and `MaterialStateGpu` implement
    `IEquatable<T>`.
  - Prepared mesh-operation cohort matching
    (`IsPreparedMeshOperationCohortMatch`) compares references, viewport
    floats, identities and generations field by field.
- **No boxed enumerators.** Readiness polling walks event lists by index with
  one count read.
- **Compact values.**
  [`ProgramUniformValue`](../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/ProgramUniformValue.cs)
  keeps numeric values in a 64-byte inline union, with the managed reference
  outside it. That halves uniform snapshot storage.
- **Pools owned by the right thread.** Desktop mesh materialization rents
  operation workspaces from the materializing worker's own pool, never from a
  producer's workspace. Receipt-owned, captured, ordered-batch and OpenXR work
  stay excluded until every borrow is proven to end before reuse.
- **Stable-bin freeze.**
  [`VulkanPreparedStableBinStream.SortRecordsForFreeze`](../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/StableBins/VulkanPreparedStableBinStream.cs)
  sorts compact keys, then applies the permutation in place cycle by cycle. It
  does not insertion-sort full records under the storage gate.
- **Tick dispatch.**
  [`RuntimeWorldLifecycle`](../../../XREngine.Runtime.Core/World/RuntimeWorldLifecycle.cs)
  dispatches each tick group from a published `TickQueue[]` that is republished
  only when a queue is created, so updates allocate nothing.

Remaining measured owners, chiefly sealed binding snapshots at about 1 MB per
present, are tracked in the
[stall remediation TODO](../../work/todo/rendering/vulkan-stall-remediation-todo.md).

## Invalidation Is Scoped And Retirement Waits For The GPU

Invalidation covers only the dependency that changed. A device-wide change is
the only thing that invalidates everything. A native object is destroyed only
after every recorded or submitted use has completed, and every cached reference
to a retirable object checks that it is still current.

- **Pipeline compilation.** Mutations are scoped to a program, shader module or
  pipeline layout. Only a device-wide scope advances the global compilation
  generation. Additive links pin a dependency lease without advancing it.
  Details are in [Vulkan Pipeline Compilation](vulkan-pipeline-compilation.md).
- **Shared graphics pipelines.** Superseded pipelines retire through retirement
  tickets that wait for the last graphics, transfer and other queue sequences.
  - `VulkanPipelineManager.SharedGraphicsPipelineRetirementGeneration` advances
    whenever shared pipelines leave the cache for retirement.
  - Each `VkMeshRenderer` clears its local pipeline lookup when that value
    changes.
  - The local lookup is keyed partly by native layout handle and link
    generation. Without the generation check, a re-created program and a reused
    layout handle could return a retired pipeline.
- **Planner resource sets.** The Vulkan resource planner caches one state per
  `VulkanFrameOpPlannerStateKey`, and each state's allocator owns a complete
  physical image and buffer set for its output. The key includes the pipeline's
  resource generation, and an allocator retires only when no key still owns it.
  Publishing a generation therefore removes the states that older generations of
  the same pipeline, viewport, output and logical view published
  ([`VulkanResourceGenerationTransactionService`](../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanResourceGenerationTransactionService.cs)).
  Their allocators retire after GPU completion, except for image groups the new
  generation reused. Without this, every resize, render-scale change or play
  transition kept another full set alive until the 12-state cap evicted it.
- **Mesh descriptor variants.** A renderer retires descriptor allocation
  variants idle for more than 5 s whenever it publishes a new variant
  ([`VkMeshRenderer.DescriptorVariantRetirement`](../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.DescriptorVariantRetirement.cs)).
  Their sets still go through ticketed retirement. Superseded generated programs
  are evicted, so material edits do not keep old pipelines and layouts alive.

The exactly-once retirement rules are in
[Vulkan Resource Lifetime And Retirement](vulkan-resource-lifetime-and-retirement.md).

## Shadow Casters Are The Dominant Motion Cost

On a scene like Sponza, the main view draws its meshes on the Advanced
canonical visibility lane at about 1 us per draw: one shared pipeline, a global
geometry atlas and per-draw push constants. Directional cascade shadows are
different. They are recorded through the generic per-renderer path, at about
100 us per caster across materialization, preparation and recording.

While the camera moves, about 45% of frames refresh the cascades, and each
refresh draws every caster once into the cascade atlas. That makes refresh
frames, not ordinary frames, the limit on motion frame rate.

**The shared opaque caster material keeps casters cheap.**
`MeshRenderMaterialResolver` resolves an opaque caster to the light's shared
instanced-layered cascade material. A caster falls back to its own material's
geometry-shader cascade variant if any of the following holds:

- it is drawn instanced or with mesh deformation;
- it is not opaque (masked, blended or alpha-to-coverage);
- its material has a `SettingUniforms` handler;
- its vertex shader needs material bindings;
- it has geometry, tessellation or mesh stages.

The fallback carries the full fragment program and its bindings, roughly ten
times the binding work per caster. Do not attach `SettingUniforms` handlers to
scene materials that do not need them.
[`XRMaterial.EnsureSurfaceEmissionPublisher`](../../../XREngine.Runtime.Rendering/Objects/Materials/XRMaterial.Surface.cs)
attaches its publisher only when the material carries surface-emission state,
because without that state the publisher would write the shader's own defaults.

**Command-chain packets do not form for cascade casters.**

- Packet lowering groups at least `MinMeshDrawsPerRenderPacket` (10) compatible
  draws into a packet, and at most `MaxShadowMeshDrawsPerRenderPacket` (16) for
  shadows
  ([`VulkanRenderer.CommandChains.Policy`](../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Policy.cs)).
- A packet key also holds at most 16 vertex-buffer, 16 auxiliary-buffer and 16
  descriptor-set identities, and per-renderer casters exhaust them before
  reaching the minimum.
- Lowering therefore captures each operation's identity demand once per pass
  (`MeshPacketIdentityDemand`). It rejects start positions that cannot reach
  the minimum within that capacity before running the full compatibility scan
  ([`VulkanRenderer.CommandChains.Packetization`](../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Packetization.cs)).
  The accepted packet set is unchanged.
- Raising the identity capacities makes shadow packets form. But each refresh
  then dirties more command chains than progressive publication admits, so
  frames replay the last scene and casters go missing. The command-chain path
  is not a shadow lane.

**Masked casters hold two descriptor variants.** The 32 masked Sponza casters
use per-material geometry-shader variants. Their descriptor allocation key
includes the identity of the renderer's per-frame auto-uniform arena views,
which differs between load and the first refresh. Each masked caster therefore
holds two variants: about 320 extra descriptor sets, created once at the first
cascade refresh after load, with no growth afterwards.

**The directional shadow lane records cascades from the canonical bins.** On
Vulkan with the Advanced pipeline and CpuDirect submission, the desktop
family's sealed stable bins already hold every canonical draw with its geometry
closure and scene sets. The `DirectionalShadowRaster` stage, which follows the
late visibility raster, records those bins a second time into the directional
atlas page:

- `ShadowAtlasManager.RenderScheduledTiles` still allocates, tracks dirtiness,
  applies budgets and owns completion receipts. A depth-only desktop cascade
  group whose grouped render would run is deferred to the lane when the stage
  reported itself ready in the previous frame
  ([`ShadowAtlasManager.AdvancedDirectionalShadowLane`](../../../XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.AdvancedDirectionalShadowLane.cs)).
  The light publishes the page, one tile rectangle and one world-to-clip matrix
  per cascade. The first successfully built request selects one framebuffer
  for that render frame. Other page framebuffers decline before enqueue and
  render through the generic path immediately; the selection resets on the
  next render frame. Groups sharing the selected framebuffer remain eligible.
- The stage builds the family request as every native stage does, targets the
  atlas page, and enqueues one operation per group. Family preparation
  validates a single-sample depth-only page, prepares one depth-only pipeline
  per bin coverage (`CullMode.None`, `Lequal`) and derives a per-record cascade
  mask from the draw's `CastShadow` flag and the candidate AABB against each
  cascade clip volume, the same test the generic pass applies per caster.
- Recording loads the page, then per cascade sets the tile viewport and
  scissor, clears the tile depth, binds the lane pipeline and the family's
  descriptor sets, and issues the frozen indexed arguments of every masked
  record with the cascade matrix in the push block
  ([`RecordAdvancedDirectionalShadowRasterPayload`](../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.DirectionalShadow.cs)).
- Acceptance commits the cascade slots and receipts like a grouped render. A
  rejected or unconsumed group stays dirty, renders generically next frame and
  holds the lane closed for 60 frames. A lane preparation fault rejects the
  fresh frame with failed receipts, preserves dirty keys for generic retry,
  and reports the stage not ready for 5 s.
- Moment-encoded pages, HMD sources, GPU-driven submission strategies and
  sequential cascade renders keep the generic path with an explicit decline
  reason. `XRE_ADVANCED_DIRECTIONAL_SHADOW_LANE=0` disables the lane;
  `get_advanced_profile_diagnostics.directionalShadowLane` reports the
  counters and the last decline reason.

The measurements and gate record are in the
[S15b record](../../work/investigations/rendering/2026-10-03-s15b-directional-shadow-lane.md).

## Play-Mode Restores Must Rebuild The Same Derived State

Play mode runs on a deserialized copy of the edit world, and exit restores
another copy (see [Play Mode Architecture](../editor/play-mode-architecture.md)).
The frame loop sees every restored mesh and render object as new. Two rules
keep that cheap and correct.

- **A cooked read must reach the same derived state as the original object.**
  Serialized meshes store buffers, not the CPU vertex array.
  [`XRMesh.TryRebuildVerticesFromBuffers`](../../../XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.Core.cs)
  rebuilds the array after buffer assignment and at the end of the cooked read
  ([`XRMesh.CookedBinary`](../../../XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.CookedBinary.cs)).
  The canonical Advanced publisher packs geometry from that array and rejects a
  mesh whose array length differs from its vertex count. Without the rebuild, a
  restored scene publishes no drawable geometry.
- **Property writes during a restore must not attach behaviour the state does
  not need.** A restore assigns properties through their setters. A setter that
  attaches a handler on every write, even an unchanged one, gives every
  restored material that handler. That moves every shadow caster off the shared
  material.

After an exit the scene publishes again within a few seconds. A frame that
cannot publish stops materializing (see above), and a restore releases the
previous restore's copy (`SnapshotRestoredContent`).

## Measuring The Frame Loop

- **Count fresh frames, not frame numbers.**
  `get_render_profiler_stats.vulkan.frame_lifecycle.outcome_counts` reports
  cumulative outcomes of published frames (completed, deferred, skipped,
  rejected, failed) and their command-record stage. Rejected frames replay the
  previous scene, so render-frame-number deltas overstate the rendered rate.
  Use the `completed` delta for frames per second.
- **Report every frame's tail.** `frame_outputs.whole_frame_p50_ms` through
  `whole_frame_p99_ms` and `whole_frame_worst_ms` cover the render thread's
  last 512 frames. Motion is bimodal: ordinary frames sit near the p50 and
  cascade-refresh frames near the p90 and above, so an average hides both.
- **Attribute leaves with engine timers.**
  - `vulkan.cpu_stages.*.process_elapsed_ms` with
    `process_invocation_count` gives exact per-stage cost.
    `XRE_VULKAN_RECORDING_PROFILE_DETAIL=1` adds detail stages.
  - `dump_cpu_frame_profile` gives scope trees for one frame.
  - EventPipe sample traces stop threads at GC safe points. Their leaf frames
    over-represent `Monitor.Enter` slow paths, GC polling and bulk copies even
    when runtime counters show no lock contention and about 1% GC. Use them for
    coarse subtrees only, and confirm contention with `dotnet-counters`
    (`dotnet.monitor.lock_contentions`).
- **Know which fields are gauges.** Most `binding_data.*` fields describe the
  last frame, and many `frame_lifecycle` fields describe the last sample. Diff
  cumulative counters across a window and normalize by completed frames.
- **Profiler scope identity.**
  [`Engine.CodeProfiler`](../../../XREngine.Runtime.Host/Engine/Subclasses/Engine.CodeProfiler.cs)
  gives each completed scope a scope ID, a parent, a session epoch, a logical
  thread and a producer thread. Completions from an earlier profiling session
  are dropped. Frame identity lives on the published `ProfilerFrameSnapshot`
  (update frame, render frame, publication). Root-inclusive and self time are
  different fields; a hottest-path summary pairs a descendant path with its
  root's duration, so it is not the named method's exclusive cost.
- **GPU query ownership.** A window activates and publishes its current renderer
  before frame-start statistics resolve prior-frame timestamp queries. Query
  resolution requires that backend capability; resolving before activation
  leaves results unavailable and accumulates pending queries until retirement.
- **Observer cost.** Compare profiler-off and profiler-on pairs before trusting
  a profiled speedup, and never mix Debug, Release, profiler modes or
  validation-layer settings in one comparison.
- **Retention.** `get_vulkan_live_resource_owners` groups live native resources
  by owner (counts). `list_vulkan_image_allocation_diagnostics` lists image
  sizes with their source (for example `ResourcePlanner`) and the allocator's
  buffer owners with their bytes. Use it when device memory grows without a
  matching change in owner counts.
- **Is the scene actually drawn?** A frame that renders only the sky can still
  report resident commands and accepted Advanced stages.
  `query_advanced_pick` on a view that faces geometry, and
  `get_advanced_profile_diagnostics` admission state, tell published geometry
  from an empty publication. Always view captured images.
- **World ticks.** `XRE_WORLD_TICK_TELEMETRY=1` and `get_world_tick_telemetry`
  report tick-group dispatch and callback costs. The profiler's Component
  Timing panel receives per-component tick timing from the same dispatch
  (`RuntimeComponentTickTiming.Recorder`). See the
  [profiler guide](../../developer-guides/diagnostics/profiler.md#world-tick-counters).

## Known Limits

Open work that affects this design is tracked in the
[stall remediation TODO](../../work/todo/rendering/vulkan-stall-remediation-todo.md):

- the directional caster lane is limited to desktop depth pages and CpuDirect;
  other shadow sources and submission strategies retain generic recording;
- the canonical publisher drops unsupported commands without a diagnostic;
- OpenGL Advanced retains strict texture admission because bindless handles
  freeze sampling state. An explicitly authored placeholder policy would need
  its own material publication and lifetime contract;
- OpenGL observer timing now resolves native query results; full cross-backend
  harness comparisons still require accepted geometry and shadow correctness;
- planner states for short-lived outputs (for example each world copy's shadow
  viewports) accumulate under new keys until the state cap evicts them;
- temporal (TSR) correctness passes cut and render-scale checks, but
  disocclusion and the original ghosting report are not yet verified.
