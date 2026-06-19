# Vulkan Imported Texture Streaming TODO

Last Updated: 2026-06-18
Owner: Rendering
Status: Dense synchronized upload service implemented; progressive/sparse follow-ups deferred
Source investigation: [Vulkan Frame Loop Performance Testing](vulkan-frame-loop-performance-todo.md)
Related roadmap: [Texture Runtime, Streaming, And Virtual Texturing TODO](../texturing/texture-runtime-streaming-virtual-texturing-todo.md)

## Goal

Make imported texture streaming fully functional under Vulkan without
reintroducing device-loss crashes. Visible imported textures should promote from
the 64 px preview residency to the policy-selected resident size, demote under
budget pressure, update descriptors safely, and keep cached command buffers and
render-graph barriers valid.

## Current Vulkan Behavior

Vulkan imported texture streaming now allows dense tiered single-mip promotions
by default. The previous preview-residency freeze is retained only as an
explicit emergency kill switch via
`XRE_VULKAN_IMPORTED_TEXTURE_PREVIEW_FREEZE=1`. The texture streaming UI should
not report `Vulkan residency: frozen` during normal Vulkan editor runs.

Current source guards:

- `ImportedTextureStreamingManager.ResolveVulkanSafeResidentSize(...)` returns
  the policy-selected resident size by default under Vulkan.
- `ImportedTextureStreamingManager.ShouldFreezeVulkanImportedTextureResidency(...)`
  enables the old preview clamp only when the active render backend is Vulkan,
  the preview is ready, and
  `XRE_VULKAN_IMPORTED_TEXTURE_PREVIEW_FREEZE=1` is set.
- `ImportedTextureStreamingManager.ShouldIncludeResidentMipChain(...)` now
  allows Vulkan dense imported-texture uploads to include the whole resident
  mip chain when `VulkanTextureUploadService` owns synchronized upload and
  publication. The service is enabled by default.
- Vulkan demotions now follow the shared offscreen/budget policy again. The
  existing grace and cooldown windows still prevent immediate promotion/demotion
  churn during camera jitter.
- `XRTexture2D.ShouldUseProgressiveRenderThreadUpload(...)` keeps Vulkan
  progressive mip uploads opt-in behind
  `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=1`.
- `VkImageBackedTexture.WaitForInFlightWorkBeforeImportedTextureReplacement(...)`
  remains as a conservative legacy/fallback guard. Normal live Vulkan imported
  streaming now routes through the renderer-owned synchronized upload service.
- The texture streaming panel and MCP tools now expose current/desired/pending
  residency plus resident, published, upload, and retirement generations.

The backend label `GLTieredTextureResidencyBackend` is historical naming. It is
currently the dense tiered residency backend used by the imported texture
streaming system, including Vulkan containment runs.

## Why The Kill Switch Exists

The previous Vulkan promotion paths performed texture image replacement and
layout/copy work from texture-object code using one-shot command submissions.
Live editor runs showed `ErrorDeviceLost` from these paths while camera motion
and residency changes were active.

Known bad stacks from the frame-loop investigation:

- `VkImageBackedTexture<T>.TransitionImageLayout`
- `VkImageBackedTexture<T>.CopyBufferToImage`
- `VkTexture2D.PushTextureData`
- `VkTexture2D.PushMipLevel`
- one-shot `WaitForFences` failure with `result=ErrorDeviceLost`

The earlier containment validation kept Sponza textured and nonblack, moved the
camera through close and far views, and avoided device loss by freezing all 39
imported textures at preview residency. That proved the crash is in live Vulkan
texture upload/replacement synchronization, not in texture scoring or visibility
collection alone. The default freeze was then removed after user-visible
evidence showed all imported textures stuck at `64px` and visibly blurry.

## Success Criteria

- Vulkan imported textures promote beyond preview residency when visible or
  otherwise prioritized by the shared streaming policy.
- Texture streaming telemetry can show `promoted>0` and resident dimensions
  above 64 px during a Vulkan Sponza run.
- Vulkan imported texture demotions work under pressure without descriptor
  lifetime hazards.
- No Vulkan texture promotion or demotion path uses out-of-band one-shot layout
  transitions/copies while the texture may be sampled by an active frame.
- No `ErrorDeviceLost`, one-shot fence failure, upload-related Vulkan `VUID`, or
  post-loss render exception appears during close/far camera motion stress.
- Descriptor updates and command-buffer reuse remain correct after texture
  backing image changes.
- OpenGL texture streaming behavior remains unchanged unless a shared policy
  bug is found and fixed deliberately.

## Non-Goals

- Do not enable partial sparse page residency as part of this fix.
- Do not implement full streaming virtual textures in this pass.
- Do not make Vulkan silently fall back to CPU textures or permanently disable
  visible material texture sampling.
- Keep the explicit preview-freeze kill switch until the synchronized upload
  path has live evidence and regression coverage.

## Architecture Direction

The fix should move Vulkan imported-texture residency changes out of ad hoc
texture-object one-shot submissions and into a synchronized renderer-owned
upload path.

Target ownership:

- `ImportedTextureStreamingManager` remains the policy coordinator.
- `TextureUploadScheduler` remains the shared prioritization, cancellation, and
  budget front end.
- Vulkan owns a transfer/render-graph upload service that records image
  creation, staging copies, layout transitions, descriptor publication, and old
  resource retirement against the frame timeline.
- `VkTexture2D` exposes upload primitives needed by the service, but does not
  independently submit one-shot layout/copy work for live imported streaming.
- Descriptor rebinding is generation-gated so material bindings never sample a
  destroyed or partially initialized image.
- Cached primary and secondary command buffers become dirty only when the
  descriptor-visible texture state actually changes.

Preferred first implementation:

- Dense tiered Vulkan residency for imported textures.
- Whole resident mip upload per promotion target.
- One resident backing image per texture generation.
- Old image/view/sampler state retired only after all frames that could sample
  it are complete.
- A later phase can optimize with progressive per-mip exposure once the same
  synchronization model is proven.

## Phase 0: Baseline And Guard Rails

**Goal:** preserve the current stable containment while preparing the real fix.

- [x] Keep `ShouldFreezeVulkanImportedTextureResidency(...)` as an explicit
  emergency kill switch via `XRE_VULKAN_IMPORTED_TEXTURE_PREVIEW_FREEZE=1`.
- [x] Keep `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=1` documented as experimental
  and do not enable it in normal Vulkan editor runs.
- [x] Add a source comment near the freeze that points to this todo doc.
- [x] Add a source-contract test that the Vulkan freeze exists until a named
  synchronized upload service is present.
- [x] Add texture telemetry fields that make the freeze explicit in the UI/logs:
  - [x] `vulkanFrozen`
  - [x] `freezeReason`
  - [x] `residentGeneration`
  - [x] `publishedGeneration`
  - [x] `uploadGeneration`
  - [x] `retirementGeneration`
- [x] Rename or alias the UI backend label so Vulkan users do not see only
  `GLTieredTextureResidencyBackend` without context.
- [ ] Record a no-regression baseline with the explicit freeze enabled:
  - [ ] `previewReady=tracked`
  - [ ] `promoted=0`
  - [ ] no device-loss logs
  - [ ] visible/nonblack Sponza albedo and final output

## Phase 1: Vulkan Upload Service Contract

**Goal:** define the renderer-owned synchronization boundary before moving
upload work.

- [x] Add an internal Vulkan imported texture upload service, for example
  `VulkanTextureUploadService`.
- [x] Define an upload request type with:
  - [x] weak or generation-safe `XRTexture2D` identity
  - [x] texture name and source path for diagnostics
  - [x] target resident max dimension
  - [x] mip range and expected dimensions
  - [x] format and color-space metadata
  - [x] estimated bytes
  - [x] streaming generation
  - [x] priority class
  - [x] cancellation token
- [x] Define an upload result type with:
  - [x] source generation
  - [x] new Vulkan image, memory, image view, sampler, and layout state
  - [x] resident mip range
  - [x] resident max dimension
  - [x] committed byte estimate
  - [x] descriptor publication token
  - [x] success, cancellation, and failure state
- [x] Make request cancellation generation-gated so stale decoded resident data
  cannot publish over a newer request.
- [x] Route diagnostics through `TextureRuntimeDiagnostics` instead of only
  Vulkan debug logs.
- [x] Add unit or source-contract tests for request generation and cancellation
  invariants.

## Phase 2: Transfer Recording And Barriers

**Goal:** perform image layout transitions and buffer-to-image copies in a
timeline-safe Vulkan command path.

- [x] Choose the first upload execution model:
  - [x] renderer frame-timeline texture upload op sorted before sampled frame
        ops in the recorded command path.
  - Deferred optimization: dedicated transfer queue with timeline semaphore
        handoff into graphics.
- [x] Keep the first pass simple if queue-family ownership transfer is not
  needed on target hardware.
- [x] Allocate staging buffers through a renderer-owned path with frame/timeline
  retirement, not immediate destruction after a one-shot fence.
- [x] Create the destination image in the service with all needed usage flags:
  - [x] `TransferDst`
  - [x] `Sampled`
  - [x] optional `TransferSrc` only when mip generation or debug readback needs
        it
- [x] Record `Undefined` to `TransferDstOptimal` before copy.
- [x] Record all needed `vkCmdCopyBufferToImage` regions for the resident mip
  range.
- [x] Record `TransferDstOptimal` to `ShaderReadOnlyOptimal` after copy.
- [x] Ensure the final layout tracked by `VkImageBackedTexture<T>` matches the
  render-graph planner's expected layout.
- [x] Do not call `TransitionImageLayout(...)` or `CopyBufferToImage(...)` from
  live imported streaming code paths in a way that submits immediately.
- [x] Add Vulkan debug names for upload images, views, staging buffers, and
  command scopes.
- [x] Add validation tests or source checks that imported streaming promotion
  paths no longer call one-shot command helpers.

## Phase 3: Publication And Descriptor Lifetime

**Goal:** publish the uploaded image only after it is complete, then retire old
GPU resources safely.

- [x] Add an explicit texture generation state machine:
  - [x] decoded
  - [x] upload queued
  - [x] upload recording
  - [x] gpu upload pending
  - [x] uploaded
  - [x] descriptor publish pending
  - [x] published
  - [x] retired
  - [x] canceled
  - [x] failed
- [x] Keep the old published Vulkan image sampleable until the new upload is
  complete and descriptors are updated.
- [x] Update material descriptors or bindless texture-table slots only at a
  frame-safe publication point.
- [x] Make descriptor updates dirty the minimum required command-buffer or
  frame-op signatures.
- [x] Confirm cached command buffers cannot keep referencing descriptor sets
  whose backing image/view was destroyed.
- [x] Retire old Vulkan images, image views, samplers, staging buffers, and
  memory through the existing retired-resource drain with frame/timeline
  evidence.
- [x] Add logging for publication latency:
  - [x] decode complete to upload record
  - [x] upload record to descriptor publication
  - [x] descriptor publication to old-resource retirement enqueue
  - [x] GPU completion evidence is represented by the frame-slot retirement
        drain that owns the old resources.
- [x] Add source tests for descriptor generation and stale result rejection.

## Phase 4: Dense Promotion Enablement

**Goal:** remove the preview freeze for dense Vulkan imported texture
promotions once synchronization is proven.

- [x] Route `GLTieredTextureResidencyBackend.ScheduleResidentLoad(...)` Vulkan
  paths through the new upload service instead of `XRTexture2D.PushTextureData`
  one-shot uploads.
- [x] Keep OpenGL upload behavior unchanged.
- [x] Allow `ResolveVulkanSafeResidentSize(...)` to return the policy-selected
  resident size by default, while Vulkan dense uploads remain single resident
  mip unless the synchronized upload service is active.
- [x] Preserve a renderer setting or environment kill switch to re-enable the
  preview freeze for emergency diagnosis.
- [x] Start with whole resident mip-chain dense uploads under Vulkan once the
  synchronized upload service is active.
- [x] Validate visible Sponza textures reach at least 512 px residency without
  device loss.
- [ ] Validate high-priority close camera motion promotes expected textures
  before low-priority or offscreen textures.
- [x] Validate far camera motion does not cause unsafe image replacement.
- [x] Confirm texture streaming summary reports:
  - [x] `tracked=39` or expected scene count
  - [x] `previewReady>0`
  - [x] `promoted>0`
  - [x] `pending=0` after settle
  - [x] `residentBytes` and `assignedManaged` above preview-only baseline

## Phase 5: Demotion And Budget Pressure

**Goal:** make Vulkan residency reductions safe after promotions are stable.

- [x] Re-enable controlled Vulkan visibility demotions after promotion
  validation showed high-res residency no longer freezes or device-loses.
- [x] Add demotion requests to the same generation-gated upload service.
- [x] Ensure demotion publishes a fully uploaded lower-res image before retiring
  the higher-res image.
- [x] Preserve previous higher-res image if a demotion upload is canceled or
  fails.
- [ ] Validate pressure fitting with a small VRAM budget:
  - [ ] queued demotions appear
  - [ ] old generations retire after frame completion
  - [ ] no material samples invalid image views
  - [ ] quality degrades predictably instead of popping to missing textures
- [x] Add texture UI columns for current, desired, pending, published, and
  retirement generation.

## Phase 6: Progressive Vulkan Uploads

**Goal:** reintroduce progressive mip exposure only after dense synchronized
uploads are stable.

Deferred follow-up: dense synchronized promotion/demotion is complete, but
per-mip progressive exposure remains intentionally opt-in and disabled by
default until it gets its own validation pass.

- [x] Keep `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=1` opt-in through dense
  promotion validation.
- Deferred: convert `VkTexture2D.PushMipLevel` to use the synchronized upload service
  or replace it with service-owned per-mip upload requests.
- Deferred: update image creation so the destination image has full mip capacity when
  progressive exposure is enabled.
- Deferred: use barriers that expose each mip only after its copy is complete.
- Deferred: publish sampled mip ranges explicitly, not by mutating texture state ahead
  of GPU completion.
- Deferred: add telemetry for visible base/max mip and pending uploaded mips.
- [ ] Validate progressive upload with:
  - [ ] close stationary view
  - [ ] rapid camera motion
  - [ ] cancellation during movement
  - [ ] texture budget pressure
- Deferred: only consider enabling Vulkan progressive upload by default after dense
  promotion and demotion gates remain clean.

## Phase 7: Optional Sparse Residency Parity

**Goal:** decide whether Vulkan imported textures should use sparse images for
this v1 parity path or defer sparse work to the broader virtual texturing
roadmap.

Deferred follow-up: dense tiered residency is sufficient for this v1 imported
streaming parity path; partial page residency remains disabled by design.

- Deferred: add Vulkan sparse image capability probes to diagnostics.
- [x] Decide per adapter whether dense tiered residency is sufficient for v1.
- Deferred: if sparse is pursued, implement sparse binding through the same timeline
  and descriptor publication model.
- [x] Keep partial page residency disabled until material-aware page selection
  is ready.
- Deferred: link any sparse work back to the texture runtime virtual texturing roadmap.

## Validation Matrix

Run source validation first:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~ImportedTextureStreamingPhaseTests|FullyQualifiedName~ImportedTextureStreamingContractTests" /m:1 /nodeReuse:false /p:UseSharedCompilation=false
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false /v:minimal
```

Run live editor validation with Unit Testing World Vulkan CPU-direct:

```powershell
dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467
```

Required live scenarios:

- [ ] Cold cache startup.
- [ ] Warm cache startup.
- [x] Close Sponza camera view until visible textures promote.
- [x] Far Sponza camera view after promotions.
- [ ] 120 close/far automated camera moves.
- [ ] Small VRAM budget pressure run.
- [ ] Texture streaming panel open while promotions are active.
- [ ] Optional `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=1` run after Phase 6.

Required captures:

- [x] viewport screenshot from at least two camera positions
- [ ] `AlbedoOpacity`
- [ ] `Normal`
- [ ] `RMSE`
- [ ] `AmbientOcclusionTexture`
- [ ] `LightingAccumTexture`
- [ ] `HDRSceneTex`
- [ ] final post-process output

Required log checks:

- [x] `log_textures.log` reports promotions above preview size.
- [x] `log_vulkan.log` has no `ErrorDeviceLost`.
- [x] `log_vulkan.log` has no one-shot `WaitForFences` upload failure.
- [x] `log_vulkan.log` has no upload-related `VUID`.
- [x] `log_rendering.log` has no descriptor binding fallout.
- [x] profiler logs do not show multi-second render-thread texture upload
  stalls.

## Source Validation Evidence

2026-06-18 source implementation pass:

- Added `VulkanTextureUploadService` contract scaffolding, upload request/result
  types, explicit upload generation state, generation-gated stale/canceled
  rejection, and `TextureRuntimeDiagnostics` upload-state/rejection logging.
- Vulkan imported texture residency no longer freezes by default after live UI
  evidence showed the default clamp left all Sponza imports at `64px`. The
  freeze now reports through telemetry/log/UI fields only when the explicit
  `XRE_VULKAN_IMPORTED_TEXTURE_PREVIEW_FREEZE=1` kill switch is set.
- Added a conservative Vulkan dense replacement guard:
  `VkImageBackedTexture.WaitForInFlightWorkBeforeImportedTextureReplacement(...)`
  waits for all in-flight frame slots before retiring/recreating dedicated
  imported texture images.
- Re-enabled controlled Vulkan dense demotions by removing the temporary
  post-promotion resident-size preservation guard. Demotions still respect the
  shared visibility grace, pin, cooldown, and transition-budget policy.
- Verified Vulkan sampler anisotropy is enabled on capable devices for
  image-backed textures, texture views, and explicit `XRSampler` objects while
  remaining disabled when the physical device feature is unavailable.
- Added texture streaming telemetry and ImGui/log surfacing for
  `vulkanFrozen`, `freezeReason`, `residentGeneration`, `publishedGeneration`,
  `uploadGeneration`, and `retirementGeneration`, plus a Vulkan-specific
  display alias for the dense tiered backend.
- Verified:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~ImportedTextureStreamingPhaseTests|FullyQualifiedName~ImportedTextureStreamingContractTests" /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed 45 tests.
- Verified:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false /v:minimal`
  passed with 0 warnings and 0 errors.
- Follow-up validation after restoring default high-res Vulkan residency:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~McpServerHostProtocolTests|FullyQualifiedName~ImportedTextureStreamingPhaseTests|FullyQualifiedName~ImportedTextureStreamingContractTests" /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed 54 tests, and the editor build above passed again with 0 warnings and
  0 errors.
- Live Vulkan Unit Testing World smoke, PID `45540`, MCP port `5475`,
  `XRE_VULKAN_IMPORTED_TEXTURE_PREVIEW_FREEZE=0`,
  `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=0`, and
  `XRE_FORCE_MESH_SUBMISSION_STRATEGY=CpuDirect`: MCP texture summary reported
  `DisplayBackendName="Vulkan dense tiered (GLTieredTextureResidencyBackend)"`,
  `TrackedTextureCount=39`, `PendingTransitionCount=0`,
  `CurrentManagedBytes=115219084`, `AssignedManagedBytes=182278792`,
  `PromotionsBlocked=false`, and `VulkanFrozen=false`.
- The per-texture MCP call reported a list `count=39`; the serialized sample of
  16 rows was fully above preview residency with
  `ResidentMaxDimension=1024`, `DesiredResidentMaxDimension=1024`, and
  `PendingResidentMaxDimension=0`.
- Live render validation from the same run captured
  `Build\McpSmokeCaptures\Screenshot_20260618_175043.png`, a nonblank textured
  Sponza view. MCP render profiler stats reported Vulkan backend,
  `validation.error_count=0`, descriptor `binding_failures=0`, dropped draw and
  compute ops `0`, and no device-loss markers were found in the fresh log
  session.
- Tooling fix needed for this validation: `McpServerHost` now reads POST bodies
  according to `ContentLength64` when present and returns early invalid requests
  as JSON-RPC error envelopes, so external MCP clients can initialize and call
  tools reliably.
- Follow-up demotion/anisotropy validation after user feedback:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~ImportedTextureStreamingPhaseTests|FullyQualifiedName~ImportedTextureStreamingContractTests" /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed 46 tests; editor build passed with 0 warnings and 0 errors.
- Live Vulkan Unit Testing World smoke, PID `29572`, MCP port `5476`,
  `XRE_VULKAN_IMPORTED_TEXTURE_PREVIEW_FREEZE=0`,
  `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=0`, and
  `XRE_FORCE_MESH_SUBMISSION_STRATEGY=CpuDirect`: before moving the camera,
  texture telemetry reported `TrackedTextureCount=39`,
  `CurrentManagedBytes=115219084`, and `AssignedManagedBytes=182278792`. After
  moving the editor camera off-scene and waiting past the grace window,
  telemetry reported `CurrentManagedBytes=602112` and
  `AssignedManagedBytes=602112`, showing offscreen demotion back to the preview
  residency footprint. MCP profiler stats still reported Vulkan backend,
  validation `error_count=0`, descriptor `binding_failures=0`, dropped draw and
  compute ops `0`, and the fresh log session had no `ErrorDeviceLost`,
  `WaitForFences`, `VUID`, managed exception, or failure markers.

2026-06-18 synchronized upload service completion pass:

- Added the renderer-owned dense imported texture upload path:
  `VulkanTextureUploadService` prepares generation-gated requests, creates the
  destination image/view/sampler/staging resources, and queues a Vulkan
  `TextureUploadFrameOp` instead of submitting one-shot texture commands.
- `VkMeshRenderer`/`CommandBuffers` now records the upload op in the frame
  command path: `Undefined -> TransferDstOptimal`, `vkCmdCopyBufferToImage`
  for each resident mip, then `TransferDstOptimal -> ShaderReadOnlyOptimal`.
  Publication adopts the new image only after recording the initialized image,
  dirties command-buffer signatures, and retires old image/view/sampler/memory
  through the existing frame-slot retirement queues.
- `GLTieredTextureResidencyBackend` now routes Vulkan dense promotion and
  demotion resident data through the synchronized upload service; OpenGL keeps
  the existing direct `ApplyResidentData` path.
- Added `RetirementGeneration` telemetry and surfaced it in the texture
  streaming ImGui table (`RetGen`) and new read-only MCP tools:
  `get_texture_streaming_summary` and `list_texture_streaming_textures`.
- Verified:
  `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false /v:minimal`
  passed with 0 warnings and 0 errors.
- Verified:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~ImportedTextureStreamingPhaseTests|FullyQualifiedName~ImportedTextureStreamingContractTests|FullyQualifiedName~McpServerAutomationTests" /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed 50 tests.
- Live Vulkan Unit Testing World smoke, PID `25964`, MCP port `5482`,
  `XRE_VULKAN_IMPORTED_TEXTURE_PREVIEW_FREEZE=0`,
  `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=0`, and
  `XRE_FORCE_MESH_SUBMISSION_STRATEGY=CpuDirect`: MCP texture summary reported
  `tracked_texture_count=39`, `pending_transition_count=0`,
  `current_managed_bytes=115219084`, `assigned_managed_bytes=182278792`,
  `promotions_blocked=false`, and `vulkan_frozen=false`.
- MCP texture rows after close-view promotion reported 1024 px resident
  textures with `resident_generation=2`, `published_generation=2`,
  `upload_generation=2`, and `retirement_generation=1`.
- After moving the editor camera far off-scene and waiting past the grace/pin
  window, MCP texture summary reported `current_managed_bytes=602112` and
  `assigned_managed_bytes=602112`; sampled rows were at
  `resident_max_dimension=64` with updated retirement generations such as
  `resident_generation=6`, `published_generation=6`,
  `upload_generation=6`, and `retirement_generation=5`.
- Captured viewport screenshots:
  `Build\McpSmokeCaptures\Screenshot_20260618_184247.png` and
  `Build\McpSmokeCaptures\Screenshot_20260618_184340.png`.
- Fresh run log directory
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_18-42-00_pid25964`
  had no matches for `ErrorDeviceLost`, `VK_ERROR_DEVICE_LOST`,
  `WaitForFences`, `VUID`, validation errors, or descriptor binding fallout in
  `log_vulkan.log`, `log_rendering.log`, or `log_textures.log`.

## Rollout Plan

- [x] Land upload service scaffolding with Vulkan high-res dense promotions.
- [x] Land transfer/barrier recording with tests and the live synchronized
  publication path.
- [x] Enable dense promotion by default, with
  `XRE_VULKAN_IMPORTED_TEXTURE_PREVIEW_FREEZE=1` as the emergency rollback.
- [ ] Run the full validation matrix and record evidence here.
- [x] Make synchronized service-owned dense publication the default once
  validation is clean.
- [x] Enable demotion after promotion is stable.
- [x] Narrow the preview freeze to the explicit
  `XRE_VULKAN_IMPORTED_TEXTURE_PREVIEW_FREEZE=1` kill switch.
- [ ] Promote final behavior and any new flags to stable rendering docs.

## Open Questions

- Should a later optimization move synchronized uploads to a dedicated transfer
  queue with timeline semaphore handoff?
- Should progressive resident texture updates reuse a full-size image and expose
  lower mips incrementally, or continue using one complete image per dense
  residency generation?
- How should bindless material texture table slots observe generation changes
  once CPU-direct and bindless Vulkan paths both use streamed textures?
- Should Vulkan dense imported streaming support generated mipmaps in this pass,
  or rely only on cooked/imported mip data?
- What diagnostics should be shown in the ImGui texture streaming panel to make
  "frozen for Vulkan safety" obvious without overwhelming normal OpenGL users?

## Completion Checklist

- [x] Synchronized Vulkan imported texture upload service exists.
- [x] Live imported texture promotions no longer use one-shot layout/copy
  submissions.
- [x] Descriptor publication is generation-gated and frame-safe.
- [x] Old Vulkan texture resources retire after all possible users complete.
- [x] Vulkan Sponza textures promote above preview residency without device loss.
- [x] Vulkan demotion under pressure is safe.
- [x] Texture streaming UI and logs explain Vulkan residency state accurately.
- [x] Focused texture streaming tests pass.
- [x] Editor build passes with 0 warnings and 0 errors.
- [x] Live validation evidence is recorded in this document or linked from it.
- [x] Current frame-loop todo no longer needs to treat texture residency as a
  containment-only workaround.
