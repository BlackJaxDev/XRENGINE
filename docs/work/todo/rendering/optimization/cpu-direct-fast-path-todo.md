# CPU Direct Fast Path TODO

Last Updated: 2026-07-20
Owner: Rendering
Status: Active supporting tracker for Vulkan Core Hardening Phase 5.2A
Execution: Current worktree only; do not create or switch branches for this effort.

Design source:

- [Canonical Vulkan Core Hardening And Device-Loss TODO](../vulkan-core-hardening-and-device-loss-todo.md)
- [Engine Rendering Optimization Design](../../../design/rendering/engine-optimization-and-avatar-optimizer-design.md)
- [Engine Rendering Optimization Roadmap](engine-rendering-optimization-roadmap.md)
- [Render Submission Performance Debug Plan](../../../design/rendering/render-submission-perf-debug-plan.md)
- [Frame Lifecycle And Dispatch Paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
- [Mesh Submission Strategies Contract](../../../../architecture/rendering/mesh-submission-strategies.md)

Related todos (overlap guard):

- [Vulkan Primary Command Recording Fast Path](vulkan-primary-command-recording-fast-path-todo.md)
  owns Vulkan submission-side cost (primary recording, descriptor churn); do
  not duplicate its items here.
- [Default Pipeline GPU Hotspots](default-pipeline-gpu-hotspots-todo.md) owns
  GPU-side pass cost; this TODO covers CPU submission cost only.

## Goal

Make CPU direct rendering a fast, allocation-free, easy-to-debug baseline. It
should stay useful for small and medium scenes, editor diagnostics, unsupported
backend cases, and performance comparisons against GPU-driven strategies.

## Scope

- Render command collection handoff.
- Per-object constant data upload.
- Persistent mapped or ring-buffered uploads.
- State caching and state-change counters.
- Pass-local sorting where semantics allow it.
- Shader/material/texture prewarm boundaries.
- Hot-path allocation elimination.

## Backend Scope

- Every phase defines a backend-neutral `CpuDirect` performance contract and is
  validated on matched OpenGL and Vulkan workloads.
- OpenGL implements the contract with persistent-mapped rings and redundant
  program/VAO/buffer/texture-bind elimination.
- Vulkan implements it with frame-indexed upload/storage arenas, stable
  descriptor/dynamic-offset bindings, capacity-backed resources, compatible
  primary/secondary reuse, and redundant pipeline/descriptor/mesh-bind
  elimination. The Vulkan recording details remain owned by
  [Vulkan Primary Command Recording Fast Path](vulkan-primary-command-recording-fast-path-todo.md),
  while the canonical Phase 5.2A gate owns promotion.

## Non-Goals

- Do not replace zero-readback GPU-driven rendering.
- Do not remove diagnostic or editor-only paths.
- Do not reorder transparent draws in a way that changes blending behavior.
- Do not add per-frame shader parsing, material layout synthesis, or asset
  deserialization to render submission.

## Phase 0 - Baseline And Audit

- [ ] Execute and report this supporting work through the canonical Phase 5.2A
  gate; do not create a separate branch or independent completion status.
- [ ] Capture Release CPU direct baseline for the unit-testing avatar scene,
  Sponza/static high-object scene, and a material-diverse scene. Use the
  existing tasks (`Measurement-Baseline-CpuDirect`,
  `Measurement-P3-CpuDirect-Census`, `Measurement-P3-CpuDirect-Census-NoOcclusion`,
  `Measurement-GameLoopRenderPipeline-Release-All`, backed by
  `Tools/Measure-MeshSubmissionBaselines.ps1`) rather than new ad-hoc capture.
- [ ] Capture current counters: draw calls, program switches, VAO binds, buffer
  binds, SSBO/UBO binds, texture binds, uniform calls, buffer upload bytes,
  barriers, and readback bytes.
- [ ] Capture a sampled CPU profile with ETW, Superluminal, or `dotnet-trace`.
- [ ] Inventory render-submission hot paths for `new`, LINQ, captured
  closures, boxing, string concatenation, and `foreach` over class enumerators.
  Start from the `Report-NewAllocations` task output.
- [ ] Inventory places where shader linking, asset deserialization, texture
  upload, or meshlet generation can occur during visible render frames.

Acceptance criteria:

- [ ] Baseline captures include build configuration, backend, GPU, driver,
  scene, camera, lights, stereo mode, shader-cache state, texture-cache state,
  validation/debug layer state (Vulkan validation is opt-in via
  `XRE_VULKAN_VALIDATION=1` and materially skews CPU cost when loaded), and
  profiler attach state.
- [ ] Hot-path allocation and late-work inventory is recorded in this TODO or a
  linked audit note.

## Phase 1 - Command Handoff And Hot-Path Allocation Cleanup

- [ ] Ensure visible collection builds stable command buffers that can be
  handed to rendering without copying full command payloads.
- [ ] Preallocate per-frame command, state-key, and pass scratch storage.
- [ ] Replace LINQ in render submission with explicit loops.
- [ ] Replace captured callbacks or closures with cached delegates or static
  helpers.
- [ ] Replace boxing-prone counters/enums/log payloads with typed structs or
  pooled event records.
- [ ] Replace `foreach` over class enumerators in hot paths with index loops or
  struct enumerators.
- [ ] Ensure profiler labels are stable strings or interned/static identifiers,
  not per-frame concatenations. Partially landed already (XRWindow viewport
  profile scope, Vulkan FBO timing labels); audit the remaining submission
  paths.
- [ ] Add source-contract tests for known no-allocation hot-path methods where
  practical.

Acceptance criteria:

- [ ] CPU direct steady-state render submission allocates zero managed bytes on
  representative static and skinned-avatar scenes, excluding explicitly opted
  diagnostic modes. Verify with `dotnet-counters` GC allocation-rate sampling
  or an equivalent allocation trace, plus a clean `Report-NewAllocations` pass
  over the submission paths.

## Phase 2 - SRP-Batcher-Equivalent Constant Fast Path

- [ ] Define a stable per-object constant block layout for transform ID, material
  ID, previous transform ID, skin ID, flags, layer/pass masks, and editor ID.
- [ ] Separate per-object, per-material, per-camera, and per-pass data so
  per-draw uploads are minimized.
- [ ] Upload object constants through dirty ranges, not full-scene rebuilds.
- [ ] Bind per-pass object constant buffers once where possible.
- [ ] Keep per-material state in material tables or stable material buffers,
  even for CPU direct where backend support allows it.
- [ ] On Vulkan, keep the buffer/descriptor binding topology stable across
  value-only updates and address ordinary frame changes through frame-slot or
  dynamic offsets. Data publication must not dirty compatible command ranges.
- [ ] Add counters for object constant bytes uploaded, dirty ranges merged, and
  constant-buffer bind count.
- [ ] Validate static, skinned, blendshape, instanced, shadow, velocity, editor
  ID, and override pass consumers.

Acceptance criteria:

- [ ] A scene with many unchanged objects does not upload unchanged object
  constants every frame.
- [ ] CPU direct object constant upload bytes scale with dirty objects, not
  visible objects.

## Phase 3 - Persistent-Mapped And Frame-Indexed Uploads

- [ ] Implement or verify an OpenGL persistent-mapped ring buffer path for
  per-frame dynamic uploads.
- [ ] Implement or verify Vulkan frame-indexed, persistently mapped host-visible
  upload arenas with safe device-local copies or direct bindings as appropriate.
  Reuse stable descriptor bindings and advance offsets/slots instead of
  recreating resources.
- [ ] Use fence sync to prevent overwriting GPU-visible ranges.
- [ ] Provide a safe fallback path for drivers without persistent mapping.
- [ ] Route transforms, previous transforms, bone matrices, blendshape weights,
  object constants, and small pass constants through the upload allocator where
  appropriate.
- [ ] Avoid `glBufferSubData` in steady-state production submission except for
  explicitly documented fallback paths.
- [ ] On Vulkan, update dirty subranges of capacity-backed buffers. Exact logical
  element-count changes, including `LinesBuffer` debug geometry, must not
  recreate backing allocations unless capacity is exceeded.
- [ ] Add upload allocator counters: bytes reserved, bytes committed, wraps,
  stalls, fence waits, orphan/fallback events, and high-water mark.
- [ ] Fail loud in diagnostics if the upload ring is undersized and would block
  repeatedly.
- [ ] Grow capacity only at a safe generation boundary, preserve old backing
  until its last timeline use completes, and report growth separately from
  steady uploads.

Acceptance criteria:

- [ ] Upload stalls are visible and rare under representative scenes.
- [ ] Persistent upload ranges are not overwritten before the GPU has consumed
  them.

## Phase 4 - State Cache And Sorting

- [ ] Build a compact state key for pass, state class, shader program, material
  table layout, texture-binding rung, VAO/mesh format, blend/depth/cull state,
  and instancing/skinning flags.
- [ ] Sort opaque CPU-direct commands by expensive state where pass semantics
  allow it.
- [ ] Preserve explicit ordering for transparent, UI, editor overlay, and
  ordered diagnostic passes.
- [ ] Add state caches for shader program, VAO, buffer binding, texture binding,
  SSBO/UBO binding, and render state.
- [ ] Skip redundant `glUseProgram`, `glBindVertexArray`, `glBindBufferBase`,
  texture bind, and uniform calls.
- [ ] Add counters for avoided redundant binds and unavoidable state changes.
- [ ] Add Vulkan equivalents for avoided/repeated pipeline, descriptor-set,
  vertex-buffer, index-buffer, dynamic-offset, and push-constant state changes.
- [ ] Ensure opaque Vulkan sorting/bucketing is compatible with reusable
  secondary command ranges and does not mix volatile overlays into static work.
- [ ] Add tests or source-contract checks for transparent order preservation.

Acceptance criteria:

- [ ] Opaque CPU direct state changes scale with state groups, not raw draw
  count.
- [ ] Transparent and ordered passes preserve visual ordering.

## Phase 5 - Warmup And Late-Work Boundaries

- [ ] Ensure world shader prewarm includes all CPU direct variants needed by
  visible static, skinned, blendshape, instanced, shadow, depth, velocity,
  editor, forward, deferred, and override passes.
- [ ] Ensure material table rows and texture residency are prepared before
  measured interactive frames where possible.
- [ ] Ensure model import/cooked-cache work does not run from render submission.
- [ ] Ensure texture upload budgets cannot consume the entire frame.
- [ ] Surface missing warmup variants in editor diagnostics instead of silently
  compiling/linking during render.
- [ ] Add profiler events that separate startup, warmup, steady-state, and
  streaming phases.

Acceptance criteria:

- [ ] Warm-start frame 0 does not link shader programs for the measured scene.
- [ ] After declared Vulkan warmup, required pipeline pending/compile counts,
  pipeline-caused `RecordDeferred`, and whole-frame rejection are zero.
- [ ] Late asset or texture work is visible as asset-streaming-bound, not
  mistaken for CPU direct submission cost.

## Phase 6 - Validation

- [ ] Run targeted unit/source-contract tests for render command collection,
  material table bindings, shader prewarm, and upload allocator behavior.
- [ ] Run Release CPU direct baseline after each major phase (same
  `Measurement-*` tasks as Phase 0).
- [ ] Run matched Release OpenGL/Vulkan low-, medium-, and high-draw-count plus
  material-diverse cohorts with identical scene/output/occlusion/debug/warmup
  manifests. Separate collection/recording, native API, and GPU execution time.
- [ ] Compare before/after p50, p90, p99 frame time and state counters.
- [ ] Capture at least one CPU sampled profile after optimization.
- [ ] Validate unit-testing avatar scene with lights disabled and enabled.
- [ ] Validate a high-object-count static scene.
- [ ] Validate a material-diverse scene.
- [ ] Validate editor overlays and selection IDs.

Acceptance criteria:

- [ ] CPU direct is stable, allocation-free in steady-state submission, and no
  longer performs render-thread shader linking or asset deserialization during
  measured frames.
- [ ] Vulkan reaches the canonical command-reuse, no-buffer-recreation,
  no-pipeline-deferral, absolute frame-time, and matched-OpenGL Phase 5.2A gates.

## Final Validation And Closeout

- [ ] Update linked design or architecture docs if the CPU direct contract
  changes.
- [ ] Record final before/after results in this TODO.
- [ ] Close this supporting tracker only when the canonical Phase 5.2A gate
  records the same implementation, validation, and documentation evidence.
