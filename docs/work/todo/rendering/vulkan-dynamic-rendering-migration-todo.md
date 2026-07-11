# Vulkan Dynamic Rendering Migration Todo (Remaining Work)

Last Updated: 2026-07-10
Owner: Rendering
Status: Dynamic-rendering promotion complete; optional modern backends have documented no-go decisions
Target Branch: intentionally skipped; user requested not to create a separate migration branch

## Scope And Tracker Ownership

This document contains only work that remains after auditing the current source tree. Completed migration history is available through Git and is not repeated here.

This is one of two active Vulkan todo documents:

- This document owns dynamic-rendering parity and promotion, allocation cleanup specific to the dynamic target path, dynamic-rendering local read, descriptor-heap completion, shader objects, Vulkan XR foveation, and compatibility checks for modern GPU-driven/ray-tracing consumers.
- [Vulkan Core Hardening And Device Loss Todo](vulkan-core-hardening-and-device-loss-todo.md) exclusively owns synchronization/layout remediation, acquire/present recovery, resource lifetime and retirement, device-loss behavior, OpenXR submit lifecycle, memory-pressure/TDR policy, fault injection, validation-toolchain upgrades, and the current Phase 5.1 validation gate.

Do not duplicate core-hardening implementation tasks here. Dynamic-rendering promotion depends on that todo's Phase 5.1 acceptance gate becoming clean.

## Current Source Baseline

The July 9 source audit confirms:

- `Auto` selects dynamic rendering when supported, explicit legacy mode remains available, and explicitly requested unsupported dynamic rendering fails during logical-device initialization.
- Swapchain, generic FBO, resolve, multiview, secondary-command-buffer, local-read plumbing, and pipeline compatibility all use shared dynamic-rendering plans. The legacy `VkRenderPass` / `VkFramebuffer` path remains mode-gated.
- The generic FBO path covers multiple color attachments, depth/stencil variants, mips/layers, shadows, bloom, cubemap and texture-array capture, and Vulkan VR mirror targets.
- Descriptor heap already has capability/dependency reporting, native command loading, host-visible sampler/resource writes, heap binding/inheritance, legacy set/binding mapping, push-data index payloads, and material/mesh/compute/ImGui integration.
- Shader objects, fragment shading rate, fragment density maps, memory budget/priority, ray tracing, and device-generated commands are already queried and represented in startup capability reporting. Their production backends are not implied by capability reporting.
- The July 9 core-hardening runs produced coherent camera-dependent Vulkan output, exercised OpenXR stereo, desktop mirror composition, a startup light-probe capture, and repeated resize stress without device loss. They did not satisfy clean sync-validation acceptance, so they are evidence of path viability rather than final dynamic-rendering promotion.
- A 2026-07-09 focused run executed 28 `VulkanDynamicRenderingMigrationTests`: 23 passed and five stale source-contract/path/token assertions failed:
  - `DynamicCommandRecording_UsesDynamicRenderingAndKeepsLegacyCallsModeGated`
  - `StereoAoAndBloomPasses_UseActiveCommandStateAndFramebufferUvSampling`
  - `BarrierPlanner_TracksSwapchainPseudoResourceWithoutPhysicalImageGroup`
  - `SynchronousDepthReadback_UsesBoundFramebufferBeforeSwapchainFallback`
  - `FboDepthStencilMetadata_PreservesStencilForOnTopAndPostProcessPasses`
- That build also encountered the active editor's lock on `Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.dll` (`dotnet.exe`, PID 53492).

## 1. Dynamic Rendering Promotion Gate

### 1.1 Remove Remaining Hot-Path Allocations

- [x] Replace the `Format[]` storage in `DynamicRenderingFormatSignature` with bounded inline/value storage or another allocation-free representation.
- [x] Remove `ReadOnlySpan<T>.ToArray()` and `new Format[...]` from dynamic signature construction in `VulkanRenderTargetMode.cs`.
- [x] Verify command recording, target planning, secondary inheritance, pipeline lookup, and draw submission introduce no per-frame or per-draw heap allocations for dynamic-rendering identity.
- [x] Keep diagnostic string construction behind existing trace/throttle gates rather than the normal recording path.

### 1.2 Complete Runtime Parity After Core Phase 5.1

- [ ] After the core-hardening Phase 5.1 remediation gate passes, run default editor and Unit Testing World startup with `XRE_VK_RENDER_TARGET_MODE=DynamicRendering` under current Vulkan validation layers.
- [ ] Run the same default editor and Unit Testing World scenarios with `XRE_VK_RENDER_TARGET_MODE=LegacyRenderPass`; confirm the retained `_renderPass` / `_renderPassLoad` path still records and presents correctly.
- [ ] Exercise both `DefaultRenderPipeline` and `DefaultRenderPipeline2`, including:
  - [ ] deferred GBuffer writes and forward depth reuse,
  - [ ] transparent/weighted blended OIT,
  - [ ] compute and blit interruptions followed by graphics-scope re-entry,
  - [ ] bloom/downsample/upsample,
  - [ ] shadow targets,
  - [ ] forced diagnostic output,
  - [ ] pipeline rebuild and material shader invalidation.
- [ ] Exercise attachment variants through the generic FBO path:
  - [ ] multiple color attachments,
  - [ ] depth-only, depth/stencil, stencil-only, and read-only depth/stencil,
  - [ ] mip-level, array-layer, cubemap-face, and texture-array targets,
  - [ ] multisample resolve targets,
  - [ ] transient attachment-only targets.
- [ ] Validate swapchain resize, minimize/restore, and recreation without stale command-buffer, image, or image-view reuse. Lifetime/remediation fixes remain owned by the core-hardening todo.
- [ ] Validate ImGui and dynamic UI-text secondary command buffers in dynamic mode, including correct inherited formats, samples, and view mask.
- [ ] Validate stereo/multiview, OpenVR mirror, and OpenXR Vulkan target paths. Reuse the core-hardening smoke harness, but record dynamic-rendering-specific results separately.

### 1.3 Visual And Performance Parity

- [x] Compare dynamic and explicit legacy output for the same scenes and camera views; verify no black frame, stale-frame flash, lost GBuffer color/depth, forward-depth rejection, missing ImGui, bloom extent mismatch, or shadow layer corruption.
- [ ] Verify pipeline cache misses trend toward zero after warmup and that dynamic compatibility keys do not create attachment-format/view-mask permutation explosions.
- [ ] Verify redundant barriers are not emitted when tracked state is already compatible. Correctness changes to barrier planning remain in the core-hardening todo.
- [ ] Measure frame pacing against explicit legacy mode and document any material regression with an explained tolerance.

### 1.4 Promotion Validation

- [x] Run the focused dynamic-rendering suite from a freshly built test assembly:

  ```powershell
  dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter VulkanDynamicRenderingMigrationTests
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~XREngine.UnitTests.Rendering.VulkanCommandChainDataModelTests.VulkanDynamicRenderingMultiviewContracts_PropagateViewMaskAcrossBeginInheritanceAndPipeline"
  ```

- [ ] Fix or retire stale source-path/token contracts, then run the broader Vulkan test filter:

  ```powershell
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter Vulkan
  ```

- [ ] Before final promotion, run `dotnet restore`, `dotnet build XRENGINE.slnx`, and the full unit-test project.
- [x] Create or update a dynamic-rendering promotion investigation under `docs/work/investigations/rendering/` with exact commands, hardware/runtime/layer versions, screenshots from at least two camera positions, log paths, VUID/message counts, frame pacing, and pipeline-cache results.

## 2. Dynamic Rendering Local Read

The capability query, Vulkan 1.4/KHR layout mapping, pNext structures, and dormant `DynamicRenderingLocalReadPlan` are already implemented. No current pass emits a non-empty plan.

- [ ] Select one real tiled-deferred or VR pass whose attachment-local reads remove a measurable bandwidth/pass cost; if no such pass is justified, document that result and keep local read dormant.
- [ ] For an adopted pass, emit a non-empty attachment-location/input-index plan and exact local-read barriers without changing unrelated passes.
- [ ] Validate color, depth/stencil, single-sample, and multisample use only against the queried local-read feature/property subset.
- [ ] Compare correctness and performance against the existing sampled-attachment path before considering local read for a required capability tier.

## 3. Descriptor Heap Completion

Descriptor indexing remains the production fallback. Do not build a new long-term backend on deprecated `VK_EXT_descriptor_buffer`.

- [ ] Add staged GPU-copy updates for non-host-visible descriptor-heap placement, with explicit copy synchronization and per-frame copy counts.
- [ ] Add native `SPV_EXT_descriptor_heap` shader variants so the final heap path does not depend entirely on legacy set/binding mapping.
- [ ] Finish shader-side push-data migration where existing engine push constants still assume pipeline-layout semantics; preserve unrelated user push constants where appropriate.
- [ ] Add acceleration-structure descriptor writes before allowing descriptor-heap-backed ray tracing.
- [ ] Complete diagnostics for heap residency, capacity/high-water marks, allocation failure, per-frame writes/copies, missing mappings, and denied fallback. Keep high-frequency counters allocation-free.
- [ ] Validate descriptor-indexing and descriptor-heap parity for:
  - [ ] material and ImGui textures,
  - [ ] sampled/storage images,
  - [ ] UBOs and SSBOs,
  - [ ] texel buffers,
  - [ ] mutable and immutable samplers,
  - [ ] graphics and compute dispatch,
  - [ ] secondary command buffers,
  - [ ] acceleration structures after support lands.
- [ ] Verify descriptor heap does not regress hot reload, material invalidation, command-buffer reuse, or per-draw allocation behavior.
- [ ] Decide, from driver coverage and parity evidence, whether descriptor heap is a preferred optional backend or the required v1 Vulkan binding architecture; keep explicit requests failure-visible either way.

## 4. Shader Object Program Binding

`VK_EXT_shader_object` feature/property querying, capability reporting, strict unsupported-request failure, and `EVulkanProgramBindingBackend` already exist. Pipeline objects remain the only implemented backend.

- [ ] Capture the design's decision-gate baseline on at least one native shader-object GPU and one GPL-only GPU: cold/warm creation time, permutation count, prewarm size, cache misses, and hot-reload time.
- [ ] Record a go/no-go decision before building a permanent second program-binding backend.
- [ ] If proceeding, distinguish native shader objects from any deliberately packaged emulation/layer mode in `EVulkanProgramBindingBackend`; never count layer results as native performance coverage.
- [ ] Enable/load the required shader-object device feature and commands only when the selected backend is supported.
- [ ] Add a `VkShaderEXT` artifact cache keyed by source/binary identity, entry point, specialization constants, stage, linkage, and required dynamic-state capabilities.
- [ ] Emit and track all fixed-function dynamic state required by the active shader stages: vertex input, topology/restart, viewport/scissor, rasterization, depth/stencil, blend/write, multisample, and active foveation state.
- [ ] Fully invalidate dynamic-state tracking when a command buffer switches between `vkCmdBindPipeline` and `vkCmdBindShadersEXT`; count and minimize mixed-mode transitions.
- [ ] Validate dynamic-rendering parity with pipeline objects across default pipelines, ImGui, compute, mesh/task paths, hot reload, descriptor indexing, and descriptor heap.
- [ ] Keep GPL/pipeline objects available until shader-object parity and the decision-gate metrics justify a different default.

## 5. Vulkan XR Foveation

Feature queries/reporting and the engine/OpenXR foveation policy and attachment intent already exist. Vulkan attachment creation and command recording do not yet consume that intent.

- [ ] Query and report fragment-shading-rate properties and attachment texel-size/combiner limits, not only feature bits.
- [ ] Connect the existing `VrFoveation` plan and `EVrFoveationAttachmentKind` to Vulkan render-target planning and dynamic-rendering scopes.
- [ ] Implement fragment-shading-rate attachment creation, layout transitions, inheritance, and pipeline/dynamic-state commands for supported pipeline, primitive, and attachment modes.
- [ ] Add OpenVR/OpenXR per-view policy input without inferring a headset/runtime capability from Vulkan support alone.
- [ ] Implement fragment-density-map dynamic-rendering attachment support only if the target runtime/device matrix shows a real advantage or required compatibility case.
- [ ] Validate center clarity, peripheral stability, stereo consistency, UI/text readability, motion artifacts, frame pacing, and fragment-work reduction.
- [ ] Keep explicit unsupported foveation requests failure-visible and leave foveation off by default until visual/performance acceptance is recorded.

## 6. Transient, GPU-Driven, And Ray-Tracing Compatibility

Memory-budget/residency admission policy belongs to the core-hardening todo. Capability reporting for memory budget/priority, ray tracing, and device-generated commands is already present.

- [ ] Validate transient attachment behavior on hardware with and without lazily allocated memory across resize, bloom, shadows, capture, and dynamic/legacy parity.
- [ ] Validate `VK_KHR_draw_indirect_count` and `VK_EXT_mesh_shader` production paths under dynamic rendering with both descriptor-indexing and descriptor-heap binding.
- [ ] Make every GPU-driven fallback identify whether the capability was optional, profile-required, or explicitly requested.
- [ ] Validate ray-query/ray-tracing descriptor compatibility with the shared material/resource model after acceleration-structure heap descriptors exist.

Do not implement `VK_EXT_device_generated_commands` until descriptor heap, shader objects, and GPU scene/material-table contracts are stable.

## Final Acceptance

- [ ] The core-hardening Phase 5.1 gate is clean for the dynamic-rendering promotion scenarios; this document does not waive or duplicate its VUID acceptance criteria.
- [ ] Default editor and Unit Testing World render correctly in explicit dynamic and explicit legacy modes under current validation layers.
- [ ] Dynamic rendering covers swapchain, generic FBO, secondary, resolve, multiview/stereo, capture, shadow, bloom, ImGui, compute/blit re-entry, and VR mirror targets without mode-specific visual regressions.
- [ ] Dynamic-rendering command recording and compatibility-key construction are allocation-free in per-frame/per-draw hot paths.
- [ ] Pipeline-cache behavior and frame pacing meet the recorded promotion thresholds.
- [ ] The promotion investigation and PR notes state what changed, why, validation performed, hardware/runtime coverage, remaining optional modern backends, risks, and follow-ups.

## Design And Evidence

- [Vulkan Dynamic Rendering Migration Design](../../design/rendering/vulkan-dynamic-rendering-migration-design.md)
- [Vulkan Shader Object Pipeline Replacement Design](../../design/rendering/vulkan-shader-object-pipeline-replacement-design.md)
- [Vulkan Core Hardening Phase 5 Live Validation](../../investigations/rendering/vulkan-core-hardening-phase5-live-validation-2026-07-09.md)
- [Vulkan Core Hardening Phase 5 Validation Manifest](../../testing/rendering/vulkan-core-hardening-phase5-validation-2026-07-09.json)
