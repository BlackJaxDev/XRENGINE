# Advanced Render Pipeline

This document describes the frame flow, invariants, capability floor, and output ownership of `AdvancedRenderPipeline`. Selection settings, shared GPU scene tables, and command-authoring rules are in [Default Render Pipeline Notes](default-render-pipeline-notes.md#selecting-advancedrenderpipeline).

`AdvancedRenderPipeline` uses a visibility-buffer opaque renderer. `DefaultRenderPipeline` stays the visual reference and the explicit legacy source. New renderer work goes into `AdvancedRenderPipeline` only. No `DefaultRenderPipeline2` type, alias, or `XRE_USE_PIPELINE_V2` selector exists.

## Code Map
| Area | Location |
|---|---|
| Pipeline partials | `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/` |
| Capability, selection, output binding, frame contract | `Advanced/Contracts/`: `AdvancedRenderPipelineCapabilities`, `AdvancedRenderPipelineCapabilityResolver`, `AdvancedRenderPipelineSelectionResolver`, `AdvancedRenderPipelineOutputBinding`, `AdvancedRenderPipelineFrameContract` |
| Stage identity | `EAdvancedRenderStage`, `AdvancedRenderStageDescriptor` |
| Resource ownership and synchronization | `EAdvancedRenderResourceOwnership`, `AdvancedRenderResourceOwnershipContract`, `EAdvancedSynchronizationBoundary`, `AdvancedSynchronizationContract` |
| Shared scene and material tables | `AdvancedSharedGpuSceneDatabase`, `AdvancedFrameSlotUploadArena` |
| Native opaque shading (Vulkan) | `Build/CommonAssets/Shaders/Advanced/Shading/ShadeNativeOpaque.comp` |

## Frame Flow

`EAdvancedRenderStage` defines the stage order:

```text
FrameBegin
  -> Deformation
  -> VisibilityPreparation
  -> VisibilityRaster
  -> DepthPyramidAndLateVisibility
  -> DirectionalShadowRaster
  -> AmbientOcclusion
  -> WorkClassification
  -> NativeOpaqueShading
  -> LatePasses
  -> TemporalAndPostProcessing
  -> Output
  -> UserInterface
```

- Deformation produces current and previous deformed vertex data one time. Visibility, shadow, velocity, and other geometry consumers reuse it.
- Visibility preparation runs early GPU culling and indirect preparation. Visibility raster writes depth and one visibility payload per pixel. The depth pyramid stage recovers late-visible geometry.
- Work classification builds GPU material and light work proportional to visible coverage.
- Native opaque shading reconstructs surface attributes from visibility identity, evaluates material lighting, and writes HDR scene color.
- Late passes hold transparent, refractive, volumetric, particle, editor-overlay, gizmo, and other special work.

### Scene registration identity reuse

`AdvancedGpuScenePublisher` retains the registration lookup and an ordered
array of source identities. Each row records the source reference, primitive
index and count, support state, source group, registration slot, and complete
draw handle. Reuse requires the same
command count and registration membership generation. Each present row must
still match an active registration and its draw generation. An absent source
must match an absent cached row.

Registration addition, slot reuse, removal, lookup growth, and publication
failure invalidate this cache. A new ordered array becomes valid only after
commit and successful source identity delivery. Successful publication reuse
follows the same delivery rule. Shrink and disposal clear retained source
references.

The publisher also retains source groups and flat primitive-to-draw handle
slices. Reuse requires exact ordered rows and primitive counts, unchanged
registration membership, and no pending identity recipients. A mismatch
rebuilds the whole grouping from active registrations and the current captured
plans. It does not repeat source capture. Registration or lookup storage growth
invalidates the relevant plan before commit. Scene-table journal growth alone
can preserve these identities and groups. Current unsupported sources receive
invalid handles.

Cold delivery preserves registration order, then current command order.
Removed sources receive cleared handles. Primitive shrink first clears the
removed tail; the next stable group uses the smaller handle count. A failed committed delivery retains
all recipients and their required clear lengths for a later caller retry.
Preflight reserves this storage before commit. A rejection cannot discard it.
Full successful delivery clears the pending list. Membership-change and retry
groups cannot become the reusable plan; the next stable publication builds it.
Callback exceptions still propagate to the caller. In the normal editor loop,
they cause a terminal timer fault, so automatic retry is not guaranteed.

These caches replace identity searches and grouping work. When the unchanged-row
gate below does not pass, the publication captures current source state, command
metadata, effective materials, poses, and GPU bounds sources. Existing geometry,
material, temporal, resource, and transaction checks remain active. Structural
transaction plans are rebuilt. Every identity callback still receives the
current accepted publication. No frame resource or output-page lease is stored
in these caches.

### Unchanged-row gate

Before the whole-scene plan, the publisher compares each command row with the
retained plan of the last accepted publication. For each row, the gate reads
the scene command row and source lookup, the command render snapshot, the
primitive mesh and effective material, the raster state, the cull bound version
of a CPU-owned bound, and the registration. It checks the material header one
time for each run of rows with one material, because the material versions do
not cover every header input. For an unsupported row, the gate repeats the
geometry check and the layout selection, and it requires the same compatibility
reason. A covered row requires the same renderer bone set. One read of the
GPU physics-chain dispatcher spatial epoch covers all covered rows, and the read
fails while the published output page is not usable.

The gate passes only when all of these conditions are true:

- The retained plan describes the current publication.
- The ordered identities and source groups are valid, and no identity recipient is pending.
- The frame identifier follows the last published frame.
- The boundary storage did not grow.
- Each supported row reused its committed material plan. Its material is untextured
  or uses the fixed `NativeUberV1` layout with current 2D or CPU-backed cube bindings.
- Each active registration belongs to one row.

Any difference runs the full plan with no change in behavior. Thus a rejected
bounds route rejects in the full plan with its usual reason.

Native Uber reuse keeps the five material revision checks and the committed
layout, coverage, render-state, and header checks. It encodes the current texture
roles through the same role-only encoder as the full plan and compares each
source with its committed logical binding. This includes the imported metadata
epoch, source-content generation, dimensions, format, and sampler state. The
check repeats when consuming a prepared verdict. Scalar encoding is skipped
only while these checks pass. GPU-authored cubes without a content witness, arrays, projective mirrors, and other
textured material families keep the full plan. Native readiness and descriptor
ownership checks remain at their existing render boundaries.

CPU-backed cubes retain an exact witness for the mip array, mip and face
identities, CPU data identity, dimensions, and formats. `Mipmap2D` records active
field writers and a content epoch. `Invalidate` advances this epoch after direct
CPU writes. Callers that write through a data pointer must call `Invalidate`.
A changed witness produces a new cube source generation. Stable reads do not
allocate. Storage images, framebuffer attachments, and cubes without complete
CPU face data cannot use this reuse path. This witness does not replace native
texture preparation or descriptor lifetime checks.

When the gate passes and no global resource changed, the publisher reuses the
current publication and delivers the identities again, as the full reuse path
does. The reuse applies the same lease rule as the full reuse path, so the
publisher does not reuse a publication that the ring drains. When a global
resource changed, or when the ring drained the publication, the publisher
commits the retained submission rows with the global transition. This commit
skips the per-command apply, tombstone, and committed-plan capture passes. Every
source still receives the new publication identity. The commit needs the rows
of a full commit, so after a full-path reuse the next commit runs the full plan.
The retained rows include the managed deformation-source rows. The commit reuses
their browser companion data and renderer rejection text without a new
computation, as the full reuse path does.

The existing `get_render_state` response exposes
`canonicalResidentScene.registrationIdentityReuseCount` and
`registrationLookupRebuildCount`, plus `sourceGroupReuseCount`,
`sourceGroupRebuildCount`, and `pendingIdentityRecipientCount`, for the latest
publication attempt. `unchangedRowGateHitCount` gives the rows that the latest
publication reused without the full plan; zero means that the full plan ran.
`retainedRowCommit` is true when the latest publication committed the retained
rows. The `GpuIndirect.AdvancedPublication.UnchangedRowGate` profiler scope
times the gate. These live
counters can be read while the publisher is updating them. Use repeated samples
for diagnostics; do not treat one response as an atomic publication snapshot.

`AdvancedGpuSceneIdentityCacheTests` and
`AdvancedGpuSceneIdentityCacheGrowthTests` check real scene publication without
a graphics device. They cover exact source-to-handle mapping, warm-cache
invalidation, capacity growth, missing and unsupported sources, failed delivery,
and cache reference cleanup. `AdvancedGpuSceneIdentityDeliveryBoundaryTests`
checks failures after commit and during reuse of a pinned publication. A
successful caller retry must deliver the accepted publication and clear every
pending recipient before the image becomes consumable.
`AdvancedGpuSceneUnchangedRowGateTests` checks that an unchanged scene skips the
full plan, that a pinned publication is reused, that a global change commits the
retained rows unchanged, and that material, raster, geometry, transform, mesh,
header, frame-gap, and membership changes run the full plan.

### Prepared unchanged-row verdict

The gate walk can run on the collect thread before the swap. After
`PostCollectVisible`, `EngineTimer` raises `PrepareSwapPublications`.
`RuntimeWorldRenderer.GlobalPrepareSwapPublications` calls
`GPUScene.PrepareAdvancedResidentPublication`, which runs the same row evaluator
as the swap-time gate (`EvaluateUnchangedRow`) and stores a single-use verdict.
At the swap, `PublishCore` first tries to consume the verdict and runs
`TryPassUnchangedRowGate` only when the verdict did not decide. The commit is
unchanged and stays in the swap: boundary capacity, tombstone reclaim, the
global preflight, the transaction, the stamp, the seal, the captures,
`_currentPublication`, and identity delivery. The global capture is final only
after the light swap, and render frame N still reads publication N.

Every input that the gate reads has a witness that the prepare samples before
the first row read. Each cross-thread writer advances its witness after the
write:

- `GPUScene` row-input version: every draw row, bound, lookup, count, render
  snapshot, and capacity write. The swap records it right after `CopyStreams`,
  under the scene lock, and the prepare requires the same version with empty
  dirty ranges. The prepare reads render-side rows only through
  `TryGetAdvancedRowInputs`, under the scene lock.
- Static epochs: late render-thread world writes, GPU-driven bone state and
  coverage, and mesh geometry revisions.
- Per row: the command `MutationVersion`, and a mesh witness for each run of rows
  with one mesh (vertex count, index count, geometry revision, primitive type,
  triangle count).
- Per material and per raster option set: the values that the evaluator compared.
- The publisher epoch (advanced only when a publication ends), frame continuity,
  the settings bits, and the dispatcher bounds epoch.

A row is evaluated again at the consume, instead of trusted, when its source is
not a `RenderCommandMesh3D`, when its renderer has no current mutation binding,
when its material override differs from its snapshot, when its command is dirty,
when its command version changed, or when it is an unsupported row. Any other
witness change is a miss, and the swap runs today's gate, which falls back to
the full plan. The prepare takes the collect publication lock first and the
scene lock only for one row at a time. It never calls the committed spatial
query, so the recorded lock order is unchanged. An exception in the prepare
discards the verdict and is recorded; it never stops the collect thread.

`XRE_ADVANCED_PREPARED_GATE=0` disables the prepare; the swap then runs exactly
today's gate. `XRE_ADVANCED_PREPARED_GATE_VERIFY=1` also runs the swap-time gate
after each hit, publishes its result, and counts each disagreement with the
witness that should have caught it. `get_render_profiler_stats` reports the
counters in `frame_lifecycle.collect_stages.swap.advanced_publication.prepared_gate`:
prepares, refusals and misses by reason, hits, re-evaluated rows, verify
disagreements, and the last exception.

### Extraction lookups

`AdvancedPreparationExtractor.Build` reads each skinned draw's mesh preparation
once. The pre-pass `TryPrepareCanonicalDeformationMeshes` keeps a ready mesh
slice for each command (`AdvancedPreparedDeformationMesh`), and consecutive
rows that draw one shared mesh use one preparation. `TryAddDeformation` reuses
the stored slice while `AdvancedGpuDeformationResources.MeshSliceRevision` is
unchanged and the mesh keeps its geometry revision, vertex count, and topology
generation. Otherwise it calls `TryPrepareMesh` again. The revision advances
when a frame opens, when the static generation changes or resets, and when a
mesh slice is added or removed. Slice offsets do not move in other cases:
copy-on-write keeps them, and a commit only appends rows.

A covered draw captures its retained renderer output state once. The bounds
route material check, the pose, and the history check use the same capture.
`PhysicsChainMaterialBoundsContractMemo` keeps up to four material bounds
contracts for one `Build`, keyed by the published material snapshot. A hit still
requires the material to be current. The memo is cleared for each `Build`, so a
shader change that does not advance a material version is seen in the next
frame. `PhysicsChainMaterialBoundsContractMemoTests` cover it.

A covered renderer without active blendshapes takes a fast path in
`AdvancedGpuDeformationResources.TryGetOrAddPoseCore`: it skips
`XRMeshRenderer.TryPrepareDeformationInputs`, because the retained palette
replaces the renderer palette and the retained envelope proves the mesh,
geometry revision, bind root, and bone layout. The full path runs until the
renderer records GPU-owned bones (`HasGpuOwnedDeformationPose`), so a later loss
of coverage still resets the CPU palette seed state. Covered rows also skip the
CPU animation scheduler and use bone tier 0.

### Visibility content generation

After the indirect planner, `Build` compares its visibility columns with the
columns of the last compared build (`AdvancedVisibilityContentWitness`): the
payloads, the geometry producers, the indirect ranges, the range payload order,
and the deformed-arena slices. The frame slots of a slice change in each frame,
so the comparison ignores them; it compares the slice offsets and the other
fields. `StableVisibilityContentGeneration` keeps its value while every column
is equal, and it takes a new value from one process-wide counter when any column
changes. `VisibilityContentGeneration` is different: it advances on each build
and guards the mutable extractor columns. A deferred build does not compare, so
the next successful build compares with the last successful content.
`AdvancedSharedPreparationService.GetCurrentDiagnostics()` reports the
cumulative counts in `VisibilityContent`: comparisons, unchanged comparisons,
and the builds in which each column changed.

### Vulkan visibility stream reuse

Each Vulkan frame plan owns the stable-bin stream of its visibility family
(`VulkanPreparedStableBinStream`). Frame plans are pooled for each frame slot,
so one stream serves every second frame on desktop. The stream keeps its sealed
records, bin headers, manifests, and submission plans across frames when every
seal input is equal.

Records and bin keys contain no frame binding:

- A deformed record does not hold the deformation output buffer. The deformed
  arena rotates three frame slots, so that buffer changes in each frame. The
  record keeps only its vertex base inside the buffer
  (`VulkanVisibilityGeometryRecordClosure.UsesDeformedVertexSource`), and its
  manifest has no native use for the buffer. The bin key uses a fixed vertex
  binding signature for deformed bins.
- Records and keys do not contain the scene native generation, which advances
  with each scene publication preparation. Each frame binds the generation to
  the stream (`BoundSceneNativeGeneration`).
- The static vertex and index slices come from the resident geometry cache.
  They keep their identity while the cache entry stays resident.

When the frame plan resets, `RetainForNextPlan` releases the template leases and
clears the sealed state of the plan, but it keeps a valid seal. The next seal
calls `TryReuseVisibilitySeal`. It keeps the stream only when these inputs are
equal:

- `VulkanVisibilitySealSignature`: the database epoch, the draw and geometry
  table generations, the resident static vertex and index slices, the pass
  index, the view mask, the frame-operation context of the bin keys, the
  submission strategy, the lane capabilities, the output policy (with the output
  reservation), the diagnostic plan, and the deformation history validity.
- An exact row comparison of the visibility columns: payloads, producers,
  indirect ranges, range payload order, and deformed-arena slices without their
  frame slots. A deformed slice must also name the frame slots of this frame's
  deformation output.
- This frame's deformation output must cover the largest vertex extent of the
  kept records.

A kept stream binds the frame: the deformation output buffers, the scene native
generation, and the geometry sources. It clears the raster pipelines, header
native state, and shadow pipelines, which the raster pipeline step prepares
again for the frame's target closure. That step binds the frame's deformation
output into each deformed bin header. It validates range members once for each
sealed content, because a kept stream has equal records and payloads. Recording
checks each bin once: the stream must be bound to the scene publication of the
frame, the bin's sources must be live, and the header must bind the vertex
source of its records. The records of a bin share one index slice and vertex
source through the bin key, so one check covers every member.

Any other difference thaws the stream and seals it again with no change in
behavior. `AdvancedVisibilityStreamReuseTelemetry.Capture()` reports the reuse
count and the reseal count for each first reason
(`EAdvancedVisibilityStreamReuseMiss`).

### Material payload slots

`AdvancedMaterialDatabase` gives each material a fixed constant-word slot and
a fixed texture-binding slot. Adding or replacing a payload writes its active
entries and clears each unused tail. The dirty range covers the whole slot,
including the cleared entries. Adjacent writes merge into one range.

`AdvancedMaterialDatabaseContractTests` checks complete slots for addition,
replacement, and payload shrink. Replacement uses no managed allocation when
capacity is sufficient.

### General debug shapes

`ModelComponent` copies its `RenderBounds` setting into each new runtime mesh.
A model rebuild preserves the requested bounds display.

Initial native visibility raster clears both depth and stencil. Late raster
loads both attachments to preserve values written in the current frame. MSAA
resolve clears the canonical stencil target because the depth resolve does not
write stencil. Gizmo bit `0x80` must not survive into the next frame: postprocess
uses it to bypass tone mapping for current debug shapes.

`AppendAdvancedDebugShapeCommands` draws queued shapes after late scene work
and physics debug work. It binds the forward color and depth target at internal
resolution. Depth-tested shapes run first, followed by overlay shapes. Neither
pass writes depth. The commands then restore depth testing and depth writes.

This step supplies the draw path for `ModelComponent.RenderBounds`. Bounds
collection alone does not draw a box. `VPRC_RenderDebugShapes` declares each pass
to the render graph. The `DebugOverlays` capture policy excludes this work from
shadow and light-probe outputs.

### Selected GPU chain debug

`AppendAdvancedPhysicsDebugCommands` runs after late scene work and before
temporal and post-processing work. It is independent of the transparent mesh
count. The hook binds `ForwardPassFBO` and loads its existing HDR color and
native visibility depth without clearing either attachment. It uses the
internal viewport, depth testing, depth writes, and `LessEqual`.

`VPRC_RenderDebugPhysics` declares the dynamic `Advanced.PhysicsWorldDebug`
pass. The Advanced hook sets `RenderWorldPhysics` to `false`, so selecting a
GPU chain does not also draw the world's general physics debug frame. Other
callers keep the option's default value of `true`.

No selected GPU chain means no framebuffer setup, debug generation, or draw.
The hook also respects `DebugInstanceRenderingAvailable` and the capture
policy's `DebugOverlays` permission. Shadow and light-probe passes skip it.
Minimal depth and visibility output profiles omit the late stage entirely.

The dispatcher generates one bounded batch per world and reuses successful
content across views in the same frame. Deferred or native uses can block
batch reuse, so a later frame can skip debug output. The hook adds no CPU
readback. Buffer ownership, barriers, and indirect draw rules are defined in
the [physics-chain compute contract](../physics/physics-chain-compute-backends.md#global-gpu-chain-debug).

### Synchronization boundaries

OpenGL and Vulkan lower the same four logical boundaries (`EAdvancedSynchronizationBoundary`) through their own encodings:

1. `ComputePreparationToVisibilityRaster`
2. `VisibilityRasterToComputeShading`
3. `ComputeShadingToLateGraphics`
4. `LateGraphicsToPresentation`

### Resource ownership

Each declared resource has one ownership class (`EAdvancedRenderResourceOwnership`): `PipelinePersistent`, `FrameSlotTransient`, `TemporalHistory`, `Imported`, or `External`. Current and previous frame-slot reuse waits on a fence or timeline completion.

## Invariants

- Compatible opaque and masked surfaces write one visibility payload and shade through the visibility path. They do not choose between deferred and ordinary opaque-forward color paths.
- The production Advanced path does not allocate or populate a classic full GBuffer. Diagnostic reconstruction targets exist only behind an explicit capture or debug mode.
- Late and special passes are explicit. They do not make the opaque renderer a deferred and forward composite.
- Geometry identity is backend-neutral across CPU-direct, zero-readback indirect, meshlet, skinned, and future virtual-geometry producers.
- Material work groups by shading-kernel compatibility and visible coverage, not by material object or descriptor-set identity.
- Scene, geometry, material, light, shadow, animation, and texture data are GPU-addressable through stable table records. Warmed production frames have no per-object or per-material binding loops.
- Production GPU-driven modes do no same-frame count, visibility, material-range, or overflow readback.
- Frame execution never creates or resizes declared pipeline resources.
- GPU-written frame data does not invalidate reusable command topology. Recorded command packets change only for topology, capacity, binding, shader, or resource generations.
- A required accelerated mode that is not available fails visibly with a machine-readable reason. It does not route through CPU emulation.
- Per-frame hot paths allocate no managed heap memory in steady state.
- OpenGL 4.6 and Vulkan use the same logical contracts. Backend encodings can differ.
- An unsupported opaque material in a required Advanced mode renders an observable error material or fails selection. It does not enter the classic opaque renderer.

## Capability Floor

`AdvancedRenderPipelineCapabilityResolver` rejects an output with an `EAdvancedRenderPipelineRejectionReason` when a required capability is missing:
| Requirement | Rejection reason |
|---|---|
| A live renderer on a supported backend | `RendererUnavailable`, `UnsupportedBackend` |
| Integer visibility render targets | `MissingIntegerRenderTargets` |
| Compute shaders | `MissingComputeShaders` |
| Storage buffers for geometry and scene tables | `MissingStorageBuffers` |
| Indirect dispatch and draw | `MissingIndirectSubmission` |
| A production texture-indirection mode | `MissingTextureIndirection` |
| Explicit image and buffer synchronization | `MissingSynchronization` |
| Current and previous frame-slot storage | `MissingFrameSlotStorage` |
| Stereo-array resources, when stereo is requested | `MissingStereoArrayResources` |
| OpenGL multiview raster, for OpenGL stereo | `MissingOpenGlMultiviewRaster` |
| The `VisibilityBuffer` shader family | `MissingShaderFamily` |

Optional acceleration flags (`SupportsBufferDeviceAddress`, `SupportsDescriptorIndexing`, `SupportsDescriptorHeap`, `SupportsSubgroupOperations`, `SupportsMeshShaders`, `SupportsAsyncCompute`, `SupportsTimelineSemaphores`) can improve an implementation. They never change the logical visibility, scene, or material contracts.

## Output Ownership

Every pipeline request carries an explicit output purpose. Outputs can share scene, mesh, and material data and compatible GI, temporal, froxel, and post-process feature contracts. They never share output-local pipeline instances, command recordings, frame-slot ownership, or temporal histories.
| Output | Owner |
|---|---|
| Desktop scene | The standard selection policy. `AdvancedRenderPipeline` is the configured source for new desktop cameras under the `Available` or `Required` mode (`XRE_ADVANCED_RENDER_PIPELINE_MODE`, `AdvancedRenderPipelineMode`). The camera pipeline asset is authoritative; capability results and visibility-family reservations live on each `XRRenderPipelineInstance`. |
| OpenXR eyes | An independently owned pipeline of the family that `VrRenderPipeline` selects: `DefaultRenderPipeline`, `AdvancedRenderPipeline` with the OpenXR eye profile, or `RvcRenderPipeline`. The desktop Advanced policy does not affect eyes. An unsupported view mode and family pair submits no projection layer. |
| Offscreen capture | The standard capability policy. Desktop debug and RVC settings do not redirect it. |

Under `Available`, an output that cannot reserve the required family stays explicitly unbound and reports the rejection. Under `Required`, selection throws `AdvancedRenderPipelineNotSupportedException`.

## Known Limits

- The Advanced pipeline still builds the classic GBuffer and `DeferredLightCombine` resources for its opaque path. Their removal is open work.
- `AdvancedRenderPipeline` is the source default for new desktop cameras, but it is not yet certified production-ready for every backend, material, and output profile.

## Related Documentation

- [Default Render Pipeline Notes](default-render-pipeline-notes.md)
- [Render Pipeline Resource Lifecycle](render-pipeline-resource-lifecycle.md)
- [Mesh Submission Strategies](mesh-submission-strategies.md)
- [GPU Record Layouts](gpu-record-layouts.md)
