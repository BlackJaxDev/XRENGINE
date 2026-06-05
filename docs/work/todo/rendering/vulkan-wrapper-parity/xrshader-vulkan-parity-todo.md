# XRShader Vulkan Parity TODO

Last Updated: 2026-06-05
Status: Active.

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

## Generation Contract

For shaders, `IsGenerated` should mean the backend shader object exists:
OpenGL has a shader object ID, Vulkan has a non-null `VkShaderModule`. It should
not imply the linked program or graphics pipeline is ready; program and pipeline
readiness must remain separate.

## Missing Parity TODO

1. React to `XRShader.Type` changes.
   - [ ] Destroy/invalidate the Vulkan shader module when `XRShader.Type`
         changes.
   - [ ] Recompute `StageFlags`, descriptor reflection, and pipeline-stage
         create info after the type change.
   - [ ] Add a source/unit test that changes `Type` and proves the Vulkan
         wrapper no longer reuses the old stage.

2. Expose compile state parity.
   - [ ] Add Vulkan equivalents for `IsCompiled` and `IsCompilePending`, or a
         backend-neutral compile-status abstraction.
   - [ ] Preserve failure state after compile errors so callers can inspect
         status without catching exceptions as the only signal.
   - [ ] Ensure program/pipeline generation can skip failed shaders with clear
         diagnostics.

3. Add source-change notification parity.
   - [ ] Emit a `SourceChanged`-equivalent event or route invalidation through a
         backend-neutral shader source event.
   - [ ] Ensure active Vulkan programs/pipelines depending on the shader are
         invalidated when source changes.
   - [ ] Track active programs or equivalent dependency edges.

4. Align source resolution and variants.
   - [ ] Compare `GLShader.ResolveFullSource` and Vulkan shader preprocessing
         for include resolution, optimized source usage, generated source,
         backend-specific compatibility transforms, and identity hashing.
   - [ ] Add a Vulkan equivalent for prepared source variants when prewarming or
         pipeline creation needs resolved source without immediate compile.
   - [ ] Ensure shader identity includes optimizer identity, backend rewrite
         identity, macros, source file path, and generated source text.

5. Improve diagnostics.
   - [ ] Dump rewritten Vulkan source on compile failure in a stable,
         user-findable location when diagnostics are enabled.
   - [ ] Include shader name, stage, file path, entry point, include chain, and
         SPIR-V compiler message.
   - [ ] Keep exception throwing for fatal paths, but also record structured
         failure state for renderer diagnostics.

6. Decide on async compile parity.
   - [ ] Determine whether Vulkan needs a nonblocking compile queue equivalent
         to OpenGL's driver parallel shader compile path.
   - [ ] If implemented, add polling/completion state and pipeline readiness
         integration.
   - [ ] If not implemented for v1, document that Vulkan shader compile remains
         synchronous and make pipeline prewarm cover startup stalls.

## Validation

- [ ] Unit/source test: source change invalidates shader and dependent program.
- [ ] Unit/source test: type change updates Vulkan stage flags.
- [ ] Unit/source test: failed Vulkan compile records shader identity and
      compile status.
- [ ] Unit/source test: OpenGL and Vulkan source identity inputs agree for a
      representative resolved shader.
- [ ] Hardware: run Vulkan shader compilation regression tests and a default
      world prewarm pass.
