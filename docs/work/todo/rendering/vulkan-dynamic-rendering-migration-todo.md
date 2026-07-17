# Vulkan Dynamic Rendering And Modern Backend Completion TODO

Last Updated: 2026-07-16
Owner: Rendering
Status: Active

## Goal

Finish every feature originally grouped with the Vulkan dynamic-rendering
migration. Dynamic rendering itself is promoted, but this tracker remains open
until dynamic local read, descriptor heap, shader objects, Vulkan XR foveation,
transient attachments, GPU-driven rendering, device-generated commands, and ray
tracing have production implementations and the required validation coverage.

These features may remain runtime capability-gated. Capability gating controls
which backend a device can execute; it does not remove implementation work from
this tracker. `Auto` may select a supported lower rung, but an explicitly
requested unsupported or failed backend must report the exact reason and must
not silently substitute a CPU path or a different GPU architecture.

## Ownership Boundaries

- This tracker owns modern render-target, descriptor, program-binding,
  foveation, transient-memory, GPU-driven, DGC, and ray-tracing implementation.
- The [Vulkan core-hardening tracker](vulkan-core-hardening-and-device-loss-todo.md)
  owns general synchronization/layout correctness, acquire/present recovery,
  device loss, resource retirement, OpenXR submit lifecycle, memory-pressure/TDR
  policy, and the shared performance/stress harness.
- When work here needs a core primitive, add or repair the shared primitive in
  the core subsystem and consume it here; do not create a second lifetime,
  barrier, or submission model.
- Unsupported local hardware blocks runtime acceptance for that hardware lane,
  not source implementation. Keep deterministic source/unit coverage and record
  the exact device/runtime still required for live validation.

## Completed Baseline

- [x] Promote dynamic rendering as the default Vulkan render-target mode while
  retaining explicit legacy render passes.
- [x] Cover swapchain, generic FBO, resolve, multiview/stereo, secondary command
  buffers, capture, shadow, bloom, ImGui, compute/blit re-entry, and VR mirror
  targets with shared rendering plans.
- [x] Make dynamic rendering format signatures allocation-free value identities.
- [x] Complete same-layout exit visibility and sampled-final-layout handling.
- [x] Close the core-hardening Phase 5.1 five-lane synchronization matrix.
- [x] Demonstrate dynamic/legacy visual and CPU/GPU pacing parity on the retained
  promotion workload.
- [x] Query and report dynamic local read, descriptor heap, shader object,
  fragment shading rate, fragment density map, lazy memory, mesh shader,
  indirect count, DGC, acceleration structure, ray query, and ray-tracing
  capability state where the bindings expose it.
- [x] Implement descriptor-heap host-visible and staged device-local storage,
  copy synchronization/counters, legacy set/binding mapping, heap inheritance,
  and material/mesh/compute/ImGui compatibility binding.
- [x] Query fragment-shading-rate attachment texel-size and combiner properties.
- [x] Keep explicit unsupported backend requests failure-visible.
- [x] Update the focused migration source contracts; the current focused cohort
  passes 30/30.

## Phase 0 — Work Isolation And Baseline

- [ ] Create a dedicated implementation branch before changing production code.
- [ ] Capture the current commit, dirty-worktree state, GPU/driver/Vulkan SDK,
  validation-layer version, OpenXR/OpenVR runtime, headset, and enabled feature
  snapshot.
- [ ] Preserve the current explicit dynamic and explicit legacy screenshots,
  frame metrics, pipeline-cache metrics, and VUID counts as the before baseline.
- [ ] Split validation into capability-independent unit/source tests and
  hardware-dependent runtime lanes so unavailable extensions do not hide missing
  implementation.
- [ ] Repair or retire stale Vulkan source-shape contracts. The 2026-07-16 broad
  filter ran 667 tests: 576 passed, 1 skipped, and 90 failed across 22 fixtures.
  Replace brittle file/token assertions with behavioral or narrowly scoped
  structural contracts where practical.

## Phase 1 — Dynamic Rendering Promotion Completion

- [ ] Run default editor and Unit Testing World startup with explicit
  `DynamicRendering` and `LegacyRenderPass` under current standard and
  synchronization validation.
- [ ] Exercise both default render pipelines through deferred GBuffer, forward
  depth reuse, transparent/OIT, compute and blit interruption/re-entry, bloom,
  shadows, forced diagnostics, pipeline rebuild, and shader invalidation.
- [ ] Exercise multiple color, depth-only, stencil-only, combined depth/stencil,
  read-only depth/stencil, mip, array layer, cubemap face, texture array,
  multisample resolve, and transient attachment targets.
- [ ] Validate resize, minimize/restore, swapchain recreation, dynamic UI/ImGui
  secondary buffers, OpenVR mirror, OpenXR sequential/parallel views, and true
  multiview without stale command or resource reuse.
- [ ] Run at least three warmed dynamic/legacy repetitions and require dynamic
  p50/p95/p99 CPU frame time to remain within 5% unless a documented GPU win
  explains the cost.
- [ ] Confirm warmed pipeline compatibility keys remain bounded by actual format,
  sample, and view-mask permutations and produce zero steady compile-required
  misses.

## Phase 2 — Dynamic Rendering Local Read

### 2.1 Render-Graph And Scope Model

- [ ] Add an explicit render-graph local-read resource contract that identifies
  producer attachment location, consumer input-attachment index, aspects,
  sample count, and required feature subset.
- [ ] Teach render-graph compilation to fuse compatible producer and consumer
  passes into one dynamic-rendering instance without widening unrelated scopes.
- [ ] Reject fusion when extent, layers, samples, view mask, aliasing, feedback
  loops, or intervening compute/blit work make attachment-local access invalid.
- [ ] Carry non-empty `DynamicRenderingLocalReadPlan` values through primary
  begin/resume, secondary inheritance, command-chain signatures, and cache keys.
- [ ] Emit exact `RenderingAttachmentLocationInfo` and
  `RenderingInputAttachmentIndexInfo` chains and local-read barriers for color,
  depth, and stencil aspects.

### 2.2 First Production Consumer

- [ ] Implement a fused deferred GBuffer-to-lighting/local-resolve lane as the
  first production consumer; keep the sampled-attachment lane for devices
  without local-read support.
- [ ] Add Vulkan shader variants with explicit input-attachment indices and
  reflection metadata that agree with the render-graph mapping.
- [ ] Support mono and multiview, single-sample and supported multisample input,
  and the queried depth/stencil subset.
- [ ] Ensure pipeline/prewarm identity includes the local-read interface without
  multiplying keys for inactive mappings.
- [ ] Add diagnostics for requested, selected, rejected, and fallback local-read
  plans, including the first incompatible resource or pass boundary.

### 2.3 Validation

- [ ] Add deterministic tests for attachment-location/input-index mapping,
  barriers, pass fusion rejection, secondary inheritance, and cache identity.
- [ ] Compare rendered GBuffer/depth/light output with the sampled path across
  opaque, alpha-tested, stereo, and MSAA scenes.
- [ ] Record bandwidth, pass count, GPU p50/p95, and CPU recording cost for local
  read versus sampled attachments on at least one tile-based and one desktop GPU.

## Phase 3 — Descriptor Heap Production Backend

### 3.1 Stable Resource Model

- [ ] Introduce a backend-neutral stable resource reference containing resource
  heap index, sampler heap index, generation, descriptor type, and flags.
- [ ] Replace monotonic-only heap publication with generation-safe allocation,
  free/reuse, null/default records, capacity growth policy, and completion-safe
  retirement.
- [ ] Publish stable resource references from textures, buffers, buffer views,
  samplers, render-graph resources, and imported/streamed assets.
- [ ] Make material rows, pass tables, draw metadata, light/probe tables, and
  post-process resource tables store stable references rather than Vulkan set
  handles or CPU-only mapping objects.

### 3.2 Native Shader Interface

- [ ] Add shader compiler, preprocessing, reflection, cache, and hot-reload
  support for native `SPV_EXT_descriptor_heap` variants.
- [ ] Define shader metadata for heap resource type, array stride, immutable or
  mutable sampler policy, residency requirements, and fallback entry.
- [ ] Add native heap variants for standard material, depth/shadow, deferred
  lighting, post-process, compute, mesh/task, ImGui, and editor UI shaders.
- [ ] Migrate engine-owned per-draw descriptor push indices into GPU material,
  draw, or pass rows; retain only small frame/pass/view push data and unrelated
  user push constants.
- [ ] Keep legacy set/binding mapping as a compatibility mode, not the final
  native heap shader model.

### 3.3 Descriptor Coverage And Lifetime

- [ ] Implement acceleration-structure descriptor writes and generation/lifetime
  validation for heap-backed ray query and ray tracing.
- [ ] Complete native writes for sampled/storage images, input attachments,
  UBO/SSBO, uniform/storage texel buffers, mutable and immutable samplers, arrays,
  and null descriptors.
- [ ] Batch staged device-local copies per frame/command scope and integrate copy
  completion with descriptor publication so consumers cannot observe unwritten
  heap records.
- [ ] Make hot reload, material invalidation, streaming replacement, command
  reuse, secondary inheritance, and frame-slot retirement generation-correct.
- [ ] Remove per-draw payload allocation and mapping lookup from the steady
  recording path; precompute or pool compatibility payloads.

### 3.4 Diagnostics And Validation

- [ ] Report heap residency, bytes/capacity/high-water, free-list pressure,
  allocation failures, writes/copies by type, bind count, push bytes, missing
  mappings, generation mismatches, null fallback use, and denied fallback.
- [ ] Add descriptor-indexing versus native-heap parity tests for material and
  ImGui textures, every image/buffer/sampler type, graphics, compute, secondary
  buffers, streaming, hot reload, and acceleration structures.
- [ ] Validate descriptor heap on supporting hardware under standard/sync
  validation and inspect the heap/material rows in a GPU capture.
- [ ] Keep descriptor indexing as the supported fallback, while making descriptor
  heap the preferred Vulkan backend when its complete feature contract is met.

## Phase 4 — Shader Object Program Binding

### 4.1 Backend And Artifact Model

- [ ] Preserve `PipelineObjects`, `ShaderObjectsNative`, and any deliberately
  packaged `ShaderObjectsLayer` as distinct modes; never report layer coverage
  as native hardware coverage.
- [ ] Enable `VK_EXT_shader_object` features and load create/destroy/bind commands
  only when the selected device/backend supports them.
- [ ] Implement a `VkShaderEXT` artifact cache keyed by source/SPIR-V identity,
  entry point, stage, specialization constants, descriptor interface, push
  layout, linkage, required subgroup/features, and dynamic-state requirements.
- [ ] Support linked and unlinked vertex, tessellation, geometry, fragment,
  task, mesh, and compute shader objects with completion-safe lifetime tracking.
- [ ] Route hot reload through artifact generations so only affected shader
  objects and compatible command packets are invalidated.

### 4.2 Dynamic Fixed-Function State

- [ ] Implement explicit state emission/tracking for vertex input, topology and
  primitive restart, viewport/scissor, rasterization, depth/stencil, blend/write
  masks, multisample/sample mask, logic operation, attachment feedback/local
  read state, and active foveation state.
- [ ] Define one canonical backend-neutral graphics-state snapshot consumed by
  both pipeline creation and shader-object command emission.
- [ ] Invalidate all affected state when switching between pipeline and shader
  object binding; count and minimize mixed-mode transitions.
- [ ] Fail recording with a precise missing-state diagnostic rather than issuing
  an incompletely specified shader-object draw.

### 4.3 Engine Integration

- [ ] Implement shader-object program binding for opaque/forward/deferred,
  transparency/OIT, depth/shadow, post-process, dynamic UI/ImGui, compute, and
  mesh/task paths under dynamic rendering.
- [ ] Integrate descriptor indexing and native descriptor heap interfaces without
  rebuilding shader artifacts for irrelevant fixed-function state.
- [ ] Update command packet, secondary inheritance, prewarm, and cache databases
  to store shader-object identities and state snapshots.
- [ ] Keep ray-tracing pipelines on their required pipeline-object model while
  sharing descriptor/resource tables and lifetime tracking.

### 4.4 Validation

- [ ] Add source/unit tests for artifact identity, linkage, state completeness,
  mixed-mode invalidation, hot reload, and lifetime retirement.
- [ ] Run pipeline-object versus native shader-object visual parity across both
  default pipelines, ImGui, compute, mesh/task, stereo/multiview, descriptor
  indexing, and descriptor heap.
- [ ] Measure cold/warm creation, prewarm size, cache misses, permutation count,
  hot-reload latency, CPU recording cost, and mixed-mode transition count on at
  least one native shader-object GPU and one pipeline/GPL-only GPU.

## Phase 5 — Vulkan XR Foveation

### 5.1 Runtime Policy And Per-View Data

- [ ] Connect OpenXR and OpenVR runtime/headset capability data to the existing
  foveation resolution policy; do not infer runtime support from Vulkan alone.
- [ ] Carry fixed, runtime-preferred, and eye-tracked centers plus per-view inner
  radius, outer radius, shading rates, visibility margin, near-field rule, and
  UI override into immutable frame/view data.
- [ ] Define update frequency, filtering, clamping, and stale-gaze behavior
  without forcing command-buffer rebuilds for every gaze sample.

### 5.2 Fragment Shading Rate Backend

- [ ] Create mono, stereo-layered, and multiview shading-rate images using device
  texel-size/alignment limits and supported fragment-size combinations.
- [ ] Generate rate maps on GPU from the per-view foveation profile and publish
  them with exact transfer/compute-to-attachment synchronization.
- [ ] Add `RenderingFragmentShadingRateAttachmentInfoKHR` to dynamic-rendering
  plans and command scopes; include attachment identity in command compatibility
  and secondary execution contracts.
- [ ] Emit pipeline and dynamic fragment-shading-rate state with supported
  pipeline, primitive, and attachment combiners.
- [ ] Support UI/text and configured near-field full-rate masks without breaking
  stereo layer consistency.

### 5.3 Fragment Density Map Backend

- [ ] Query and retain complete density-map properties and image requirements,
  including dynamic-map and non-subsampled-image support.
- [ ] Create and update fixed/dynamic density maps for each view family with the
  required image flags, layout, usage, and lifetime.
- [ ] Add `RenderingFragmentDensityMapAttachmentInfoEXT` to dynamic-rendering
  plans where supported and implement the required legacy render-pass path where
  dynamic density-map attachment is unavailable.
- [ ] Integrate subsampled render targets and resolve/upscale/composition without
  sampling, copy, or framebuffer-size mismatches.

### 5.4 Validation

- [ ] Add unit tests for backend selection, per-view map geometry, texel limits,
  pNext construction, layouts, inheritance/compatibility, and explicit failure.
- [ ] Validate center clarity, peripheral stability, stereo consistency, UI/text
  readability, near-field behavior, gaze motion, head motion, and map edges.
- [ ] Measure fragment invocations/work, GPU p50/p95, missed XR deadlines, map
  generation cost, and image-memory cost for both foveation backends.

## Phase 6 — Transient Attachment Memory

- [ ] Extend resource planning with explicit `TransientPreserve`,
  `TransientDiscard`, and ordinary persistent attachment lifetime semantics.
- [ ] Select lazily allocated memory for eligible transient images and report the
  chosen heap/type; retain diagnosed device-local allocation on devices without
  lazy memory.
- [ ] Reject lazy allocation when an attachment is sampled, copied, read back,
  exported, preserved after the render scope, or uses incompatible load/store
  operations.
- [ ] Propagate transient identity through resize, aliasing, render-target plans,
  dynamic/legacy paths, and retirement without stale image/view reuse.
- [ ] Validate bloom, shadows, capture, depth, MSAA, resize, and memory-pressure
  behavior on devices with and without lazily allocated memory.
- [ ] Record committed/resident bytes and bandwidth changes rather than assuming
  transient intent produced a physical-memory win.

## Phase 7 — GPU-Driven Rendering And Device-Generated Commands

### 7.1 Indirect Count And Mesh Shaders

- [ ] Complete production `VK_KHR_draw_indirect_count` recording, count-buffer
  synchronization, bounds validation, and zero-readback fallback reporting.
- [ ] Complete `VK_EXT_mesh_shader` task/mesh pipeline and shader-object paths,
  indirect-count dispatch, payload/workgroup limit enforcement, and statistics.
- [ ] Make GPU draw/mesh records consume stable material/resource-table IDs under
  descriptor indexing and descriptor heap without per-draw CPU descriptor work.
- [ ] Carry dynamic-rendering formats, samples, view mask, local-read, foveation,
  and shader/pipeline binding identity into GPU-driven compatibility keys.

### 7.2 Device-Generated Commands

- [ ] Query and enable the complete `VK_EXT_device_generated_commands` feature
  and property subset and load all required commands.
- [ ] Implement execution-set ownership for pipeline and shader-object modes,
  indirect command layouts/tokens, preprocess buffers, generated-command memory
  requirements, and completion-safe retirement.
- [ ] Define GPU command records for program/state selection, vertex/index or
  mesh-task work, push constants/addresses, draw IDs, and material-table IDs.
- [ ] Add compute-to-preprocess/generated-command and generated-command-to-draw
  synchronization to the shared barrier planner.
- [ ] Bound preprocessing and generated work per frame/output so DGC cannot
  create TDR-sized submissions or starve XR deadlines.
- [ ] Report optional, profile-required, and explicitly requested fallback state
  for every GPU-driven lane.

### 7.3 Validation

- [ ] Add deterministic tests for indirect bounds/counts, mesh/task limits, DGC
  token/layout identity, execution-set generations, barriers, and retirement.
- [ ] Validate CPU-direct, indirect-count, mesh-shader, and DGC output parity in
  dynamic/legacy, mono/stereo, descriptor-indexing/heap, and pipeline/shader-
  object configurations supported by each device.
- [ ] Prove production GPU-driven modes execute with zero same-frame CPU readback
  and no hidden per-draw CPU loop or descriptor fallback.
- [ ] Measure CPU submission, GPU execution, generated command count, preprocess
  cost, memory, and frame pacing with increasing draw/material diversity.

## Phase 8 — Ray Query And Ray-Tracing Production Paths

- [ ] Implement shared BLAS/TLAS build, compaction, refit, update, scratch-memory,
  queue synchronization, generation, and completion-safe retirement services.
- [ ] Publish TLAS, geometry addresses, instance/mesh/submesh/transform tables,
  material rows, textures, lights, probes, blue noise, reservoirs, and GI buffers
  through the shared descriptor-indexing/descriptor-heap resource model.
- [ ] Implement acceleration-structure descriptors for both binding backends and
  reject stale TLAS/material generations before submit.
- [ ] Implement ray-query compute/graphics shader variants using the shared scene
  tables and explicit capability/fallback diagnostics.
- [ ] Implement ray-tracing pipelines, shader groups, shader binding tables,
  stack-size/state handling, trace dispatch, cache/prewarm identity, and hot
  reload.
- [ ] Integrate ray-query/ray-traced outputs with dynamic-rendered consumers using
  explicit render-graph dependencies and no implicit global waits.
- [ ] Add deterministic geometry/build/SBT/descriptor tests and live validation
  for transforms, alpha-tested geometry, material diversity, rebuild/refit,
  streaming, resize, device loss, and mixed raster/ray frames.
- [ ] Measure build/update/trace cost, memory pressure, submission size, and TDR/XR
  budget behavior before enabling expensive effects by default.

## Phase 9 — Cross-Feature Acceptance

- [ ] Build a capability matrix covering at least NVIDIA, AMD, and Intel desktop
  GPUs plus the supported OpenXR/OpenVR runtime/headset set. Record unsupported
  lanes explicitly; do not count them as passed runtime validation.
- [ ] Run every supported combination of dynamic rendering/local read,
  descriptor indexing/heap, pipeline/shader objects, foveation backend,
  CPU-direct/GPU-driven/DGC, and raster/ray-query/ray-tracing consumers.
- [ ] Run standard and synchronization validation with zero engine-owned VUID,
  sync hazard, stale generation, rejected submission, or device-loss event.
- [ ] Capture and visually inspect desktop, each XR view/layer, mirrors, shadows,
  bloom, UI/text, local-read output, foveation maps, transient targets,
  GPU-driven output, acceleration structures, and ray-traced targets.
- [ ] Verify stable workloads have zero per-frame/per-draw managed allocation in
  recording/binding hot paths, bounded cache/heap growth, and completion-safe
  retirement.
- [ ] Run `dotnet restore`, `dotnet build XRENGINE.slnx`, the focused migration
  suite, the broader Vulkan filter, and the full unit-test project from freshly
  built assemblies.
- [ ] Update user/developer docs, environment variables, settings schemas,
  capability diagnostics, launch profiles, and the MCP/runtime validation guide.
- [ ] Move this tracker to `docs/work/todo/COMPLETED/` only after every source
  implementation task is checked and every available hardware lane has durable
  evidence; list unavailable hardware lanes as explicit validation gaps.
- [ ] Merge the dedicated implementation branch back into `main` after review and
  validation.

## Current Evidence

- [Vulkan Dynamic Rendering Migration Design](../../design/rendering/vulkan-dynamic-rendering-migration-design.md)
- [Vulkan Descriptor Heap Optimization Design](../../design/rendering/vulkan-descriptor-heap-optimization-design.md)
- [Vulkan Shader Object Pipeline Replacement Design](../../design/rendering/vulkan-shader-object-pipeline-replacement-design.md)
- [Vulkan Dynamic Rendering Promotion](../../investigations/rendering/vulkan-dynamic-rendering-promotion-2026-07-10.md)
- [Vulkan CPU Framerate Regression](../../investigations/rendering/vulkan-cpu-framerate-regression-2026-07-09.md)
- [Vulkan Pipeline Cache And Prewarm](../../investigations/rendering/vulkan-pipeline-cache-prewarm-2026-07-16.md)
- [Vulkan Core Hardening Completed Work](vulkan-core-hardening-and-device-loss-completed.md)
