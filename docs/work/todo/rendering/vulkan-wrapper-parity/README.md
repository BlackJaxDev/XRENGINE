# Vulkan Wrapper Parity TODOs

Last Updated: 2026-06-05
Status: Active audit-to-implementation tracker.

## Goal

Bring the Vulkan render-object wrappers to behavior parity with the OpenGL
wrappers for the engine-facing `XR*` types. Parity means engine code can request
the same generic object behavior and get equivalent correctness, invalidation,
diagnostics, resource lifetime, shader/material binding, and validation behavior
from either backend.

This is not a request to make Vulkan mimic OpenGL implementation details where
Vulkan has a better native model. The contract is engine-visible parity.

## Scope

| Type | Vulkan wrapper | OpenGL wrapper | TODO |
|---|---|---|---|
| `XRMeshRenderer.BaseVersion` | `VkMeshRenderer` | `GLMeshRenderer` | [xrmeshrenderer-vulkan-parity-todo.md](xrmeshrenderer-vulkan-parity-todo.md) |
| `XRMesh` | Owned through renderer/data-buffer wrappers | Owned through renderer/data-buffer wrappers | [xrmesh-vulkan-parity-todo.md](xrmesh-vulkan-parity-todo.md) |
| `XRMaterial` | `VkMaterial` | `GLMaterial` | [xrmaterial-vulkan-parity-todo.md](xrmaterial-vulkan-parity-todo.md) |
| `XRShader` | `VkShader` | `GLShader` | [xrshader-vulkan-parity-todo.md](xrshader-vulkan-parity-todo.md) |
| `XRTexture` and concrete texture types | `VkImageBackedTexture`, `VkTexture*` | `GLTexture`, `GLTexture*` | [xrtexture-vulkan-parity-todo.md](xrtexture-vulkan-parity-todo.md) |
| `XRDataBuffer` | `VkDataBuffer` | `GLDataBuffer` | [xrdatabuffer-vulkan-parity-todo.md](xrdatabuffer-vulkan-parity-todo.md) |

## Shared Rules For Implementation

- [ ] Treat `IsGenerated` consistently across backends. For OpenGL this means
      the API object has been created and has a non-zero object ID; it does not
      prove buffer contents, texture pixels, descriptors, programs, or draw
      readiness are valid. Vulkan wrappers should use the same distinction:
      generated means the relevant Vulkan handle or wrapper cache ID exists,
      while upload/readiness/descriptor validity must be tracked separately.
- [ ] Keep backend differences explicit in code comments when Vulkan chooses a
      native equivalent instead of an OpenGL-shaped implementation.
- [ ] Do not hide missing GPU/accelerated paths behind silent CPU fallbacks.
      Emit diagnostics or an intentional fallback signal.
- [ ] Add source-verifiable tests for wrapper contracts that do not require a
      live GPU.
- [ ] Add hardware validation steps for behavior that requires Vulkan command
      execution, validation layers, GPU capture, or visual comparison.
- [ ] Keep hot-path allocations out of per-frame draw, descriptor, buffer, and
      upload paths.
- [ ] Update this folder as parity gaps close; do not leave stale TODOs behind.

## Shared Readiness Vocabulary

Use the same readiness categories in every wrapper doc and diagnostic path. The
categories are intentionally backend-neutral even when one backend satisfies a
category through a native mechanism.

- `Generated`: the backend API object or wrapper cache handle exists. This is
  the only state `IsGenerated` should report.
- `Uploaded`: CPU-provided bytes or pixels have reached backend-owned storage,
  or the wrapper has a queued upload completion that can be observed.
- `Resident`: the resource has enough GPU-visible memory/pages/mips/rows for
  the requested use. Streaming and sparse paths may be generated and uploaded
  without being fully resident.
- `DescriptorReady`: descriptors, sampler/image views, buffer ranges, bindless
  handles, or equivalent binding records are valid for the active binding
  layout.
- `PipelineReady`: shader/program/pipeline objects are valid for the selected
  render state, material layout, vertex input, pass attachment metadata, and
  feature profile.
- `PassReady`: render-graph pass metadata, attachment formats, layouts, load
  and store decisions, queue ownership, and barriers are valid for command
  recording.
- `Retired`: backend resources that were replaced or destroyed are no longer
  referenced by in-flight GPU work and may be physically freed.

## Explicit Non-Parity

These are Vulkan-led capabilities. Parity work should preserve the same
engine-visible behavior and diagnostics without forcing OpenGL to expose Vulkan
handles or forcing Vulkan into OpenGL-shaped state changes.

- Descriptor indexing, update-after-bind descriptor arrays, descriptor-set
  layout fingerprints, and material-table descriptor rows.
- Buffer device address scene data and GPU-resident draw/material databases.
- `VK_EXT_mesh_shader` task/mesh dispatch, especially indirect-count dispatch
  from GPU-written task records.
- Vulkan sparse residency, memory decompression, indirect copy, ray tracing,
  Sync2, timeline/fence-retired resource lifetime, and dynamic rendering.
- Render-graph barrier planning, explicit load/store decisions, and dynamic
  rendering attachment metadata.
- Pipeline keys, prewarm manifests, persistent pipeline caches, and structured
  pipeline miss diagnostics.

## Vulkan-Native Parity Rules

Vulkan parity is engine-visible parity, not an OpenGL-shaped implementation. A
Vulkan wrapper may choose a different native model when it preserves the generic
`XR*` contract and reports the difference clearly.

- [ ] Prefer descriptor readiness, descriptor-set/layout fingerprints, and
      material binding layouts over emulating OpenGL current binding state.
- [ ] Prefer render-graph resource declarations, barrier planning, dynamic
      rendering attachment metadata, and explicit load/store decisions over
      order-dependent framebuffer side effects.
- [ ] Prefer pipeline keys, pipeline prewarm manifests, and structured pipeline
      miss diagnostics over late draw-time pipeline creation.
- [ ] Prefer timeline/fence-retired destruction over global idle points for
      wrapper resource replacement.
- [ ] Keep Vulkan-only accelerants such as descriptor indexing,
      buffer-device-address scene data, mesh-task dispatch, sparse residency,
      memory decompression, and indirect copy explicitly feature-gated and
      diagnostic.
- [ ] Treat CPU fallback from missing Vulkan GPU paths as a visible policy
      decision, never as an implicit success path.

## OpenGL Backfill For Vulkan-Led Contracts

OpenGL remains the primary tested backend, but it should converge on the same
engine-facing contracts that make Vulkan dependable.

- [ ] Validate render-graph pass metadata in OpenGL even when the executor still
      runs the existing sequential command chain.
- [ ] Report preparation/readiness state with the same categories Vulkan uses:
      buffers, shader/program, descriptors/material bindings, pipeline/render
      state, and texture residency.
- [ ] Keep OpenGL material-table and bindless texture behavior aligned with the
      same pass-declared material layout and texture-binding rung that Vulkan
      descriptor indexing consumes.
- [ ] Expose profiler counters comparable to Vulkan for readback bytes, material
      table row updates, texture binding rung, shader/program cache misses, and
      fallback reasons.
- [ ] Keep `GpuIndirectZeroReadback` and `GpuMeshletZeroReadback` no-readback
      rules backend-neutral; only instrumented strategies may read back counts
      or visibility buffers.

## Baseline Source Map

- OpenGL wrapper registration:
  `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs`
- Vulkan wrapper registration:
  `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Drawing.Core.cs`
- Vulkan renderer overview:
  `docs/architecture/rendering/vulkan-renderer.md`
- Vulkan manual validation guide:
  `docs/work/todo/vulkan.md`

## Validation Ladder

1. Source verification:
   - wrapper registration maps every generic type to the expected API wrapper;
   - event subscription/unsubscription symmetry is tested by source or unit
     checks;
   - shader/material/texture descriptor resolution has deterministic unit
     coverage where possible.
2. Narrow build:
   - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
3. Targeted tests:
   - Vulkan P0/P1 validation tests where relevant;
   - rendering wrapper source-contract tests added by each parity item.
4. Hardware validation:
   - run the default editor world with OpenGL and Vulkan using the same scene,
     camera, and resolution;
   - run Unit Testing World in Vulkan;
   - enable Vulkan validation layers and capture descriptor/buffer/image
     warnings;
   - compare visible output for opaque, forward, shadow, UI, FBO/post, and
     debug draw paths.
