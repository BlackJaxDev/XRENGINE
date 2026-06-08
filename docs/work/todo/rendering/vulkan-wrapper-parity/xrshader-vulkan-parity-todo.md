# XRShader Vulkan Parity TODO

Last Updated: 2026-06-08
Status: Active; Vulkan shader lifecycle/status parity pass implemented, with
async compile and hardware prewarm validation still open.

## Goal

Make `VkShader` provide the same engine-facing shader lifecycle and source
semantics as `GLShader`: source invalidation, shader type changes, include
resolution, compile status, diagnostics, source variants, and program
invalidation.

## Source Inventory

OpenGL:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLShader.cs`

Vulkan:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkShader.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VulkanShaderTools.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs`

## Current Parity Already Present

- Vulkan compiles GLSL to SPIR-V and creates a shader module.
- Vulkan extracts descriptor bindings from SPIR-V/reflected source.
- Vulkan parses vertex input locations from rewritten vertex shader source.
- Vulkan tracks text-source content changes and invalidates the shader module.
- `XRShader` now emits source/type invalidation through a backend-neutral
  `SourceChanged` event.
- `VkShader` exposes compile pending/ready/failure status, last artifact, last
  failure, rewritten-source diagnostics, and shader invalidation events.
- `VkRenderProgram` invalidates descriptor layouts/stage lookup when a dependent
  Vulkan shader changes and records structured backend link status.
- Vulkan shader artifact identity includes backend define, shader type, source
  path, optimizer/rewrite identity, generated uber variant metadata, shader
  config version, clip-depth remap choice, source identity, dependencies, and
  rewritten source hash.

## Generation Contract

For shaders, `IsGenerated` should mean the backend shader object exists:
OpenGL has a shader object ID, Vulkan has a non-null `VkShaderModule`. It should
not imply the linked program or graphics pipeline is ready; program and pipeline
readiness must remain separate.

## Vulkan Compile Scheduling Decision

For the current v1 Vulkan wrapper pass, shader compilation remains synchronous
on the render thread/device-ready path. `VkShader` now exposes pending, ready,
and failed status so a future nonblocking compile queue can reuse the same
status contract, but startup and pass prewarm should remain the primary way to
avoid late draw-time stalls until that queue is explicitly implemented.

## Missing Parity TODO

1. React to `XRShader.Type` changes.
   - [x] Destroy/invalidate the Vulkan shader module when `XRShader.Type`
         changes.
   - [x] Recompute `StageFlags`, descriptor reflection, and pipeline-stage
         create info after the type change.
   - [x] Add a source/unit test that changes `Type` and proves the Vulkan
         wrapper no longer reuses the old stage.

2. Expose compile state parity.
   - [x] Add Vulkan equivalents for `IsCompiled` and `IsCompilePending`, or a
         backend-neutral compile-status abstraction.
   - [x] Preserve failure state after compile errors so callers can inspect
         status without catching exceptions as the only signal.
   - [x] Ensure program/pipeline generation can skip failed shaders with clear
         diagnostics.

3. Add source-change notification parity.
   - [x] Emit a `SourceChanged`-equivalent event or route invalidation through a
         backend-neutral shader source event.
   - [x] Ensure active Vulkan programs/pipelines depending on the shader are
         invalidated when source changes.
   - [x] Track active programs or equivalent dependency edges.

4. Align source resolution and variants.
   - [x] Compare `GLShader.ResolveFullSource` and Vulkan shader preprocessing
         for include resolution, optimized source usage, generated source,
         backend-specific compatibility transforms, and identity hashing.
   - [ ] Add a Vulkan equivalent for prepared source variants when prewarming or
         pipeline creation needs resolved source without immediate compile.
   - [x] Ensure shader identity includes optimizer identity, backend rewrite
         identity, macros, source file path, and generated source text.

5. Improve diagnostics.
   - [x] Dump rewritten Vulkan source on compile failure in a stable,
         user-findable location when diagnostics are enabled.
   - [x] Include shader name, stage, file path, entry point, include chain, and
         SPIR-V compiler message.
   - [x] Keep exception throwing for fatal paths, but also record structured
         failure state for renderer diagnostics.

6. Decide on async compile parity.
   - [ ] Determine whether Vulkan needs a nonblocking compile queue equivalent
         to OpenGL's driver parallel shader compile path.
   - [ ] If implemented, add polling/completion state and pipeline readiness
         integration.
   - [x] If not implemented for v1, document that Vulkan shader compile remains
         synchronous and make pipeline prewarm cover startup stalls.

## Vulkan-Native Acceptance Additions

- [ ] Promote Vulkan shader generation into a complete shader artifact chain:
      resolved source, backend-rewritten source, SPIR-V, reflection data,
      descriptor layout signature, push-constant signature, vertex/fragment
      interface signature, and pipeline-stage create info.
- [ ] Cache artifact identity from all source-shaping inputs: include graph,
      macros, optimizer identity, backend rewrite version, generated source
      axes, entry point, target environment, specialization constants, and
      feature-profile gates.
- [ ] Treat reflection output as part of the pipeline/material contract:
      descriptor set layouts, descriptor array indexing rules, uniform/storage
      block layouts, push constants, vertex inputs, fragment outputs, and
      multiview/stereo requirements should be available before pipeline
      creation.
- [ ] Require `nonuniformEXT` or an equivalent validated shader variant for any
      Vulkan path that indexes descriptor arrays with per-draw, per-material, or
      GPU-written values.
- [x] Record structured artifact failures separately for source resolution,
      preprocessing, SPIR-V compilation, reflection, shader-module creation,
      and pipeline-interface mismatch.
- [ ] Feed successful shader artifacts into the Vulkan pipeline prewarm
      manifest so warmup can cover shader variants without waiting for a late
      draw-time miss.

## OpenGL Backfill Additions

- [ ] Give OpenGL shader/program caches the same identity inputs used by the
      Vulkan artifact chain where they affect source shape or interface shape.
- [ ] Emit reflection-like OpenGL diagnostics for active uniforms, samplers,
      storage blocks, uniform blocks, vertex inputs, fragment outputs, and
      program interface mismatches.
- [ ] Report OpenGL program cache hits, misses, compile/link failures, include
      chains, and generated-source axes in a shape comparable to Vulkan pipeline
      miss summaries.
- [ ] Keep OpenGL generated shader variants aligned with Vulkan artifact
      identity so depth-normal, shadow, stereo, skinning, blendshape, meshlet,
      and material-table variants do not diverge silently.
- [ ] Preserve OpenGL driver parallel compile support as an implementation
      detail while exposing backend-neutral compile pending, failed, ready, and
      linked/program-ready states.

## Validation

- [x] Unit/source test: source change invalidates shader and dependent program.
- [x] Unit/source test: type change updates Vulkan stage flags.
- [x] Unit/source test: failed Vulkan compile records shader identity and
      compile status.
- [ ] Unit/source test: OpenGL and Vulkan source identity inputs agree for a
      representative resolved shader.
- [ ] Hardware: run Vulkan shader compilation regression tests and a default
      world prewarm pass.
