# Vulkan Wrapper Parity TODO

Last Updated: 2026-06-22
Status: Active tracker; source-contract coverage reconciled, hardware validation remains open.

## Goal

Bring the Vulkan render-object wrappers to engine-facing behavior parity with
the OpenGL wrappers for the same generic `XR*` types. Parity means engine code
can request the same generic object behavior and get equivalent correctness,
invalidation, diagnostics, resource lifetime, shader/material binding, and
validation behavior from either backend.

This document lists remaining work after the 2026-06-22 source-contract
reconciliation. Completed items from the previous per-wrapper parity trackers
were intentionally omitted during consolidation; the coverage note below records
the source-level checks that now carry parity guardrails.

## Scope

| Type | Vulkan wrapper | OpenGL wrapper |
|---|---|---|
| `XRMeshRenderer.BaseVersion` | `VkMeshRenderer` | `GLMeshRenderer` |
| `XRMesh` | Owned through renderer/data-buffer wrappers | Owned through renderer/data-buffer wrappers |
| `XRMaterial` | `VkMaterial` | `GLMaterial` |
| `XRShader` | `VkShader` | `GLShader` |
| `XRTexture` and concrete texture types | `VkImageBackedTexture`, `VkTexture*` | `GLTexture`, `GLTexture*` |
| `XRDataBuffer` | `VkDataBuffer` | `GLDataBuffer` |

## Source-Contract Coverage Reconciled 2026-06-22

The focused wrapper parity source tests live in:

- `XREngine.UnitTests/Rendering/XRMeshAndMeshRendererVulkanParityContractTests.cs`
- `XREngine.UnitTests/Rendering/XRMaterialAndShaderVulkanParityContractTests.cs`
- `XREngine.UnitTests/Rendering/XRTextureVulkanParityContractTests.cs`
- `XREngine.UnitTests/Rendering/VkDataBufferParityContractTests.cs`

Current source-contract coverage includes:

- Wrapper registration, the no-standalone-`XRMesh` backend-wrapper decision,
  mesh replacement/event symmetry, Vulkan mesh preparation readiness separate
  from `IsGenerated`, material resolution parity, shadow draw suppression, and
  geometry layout signatures feeding both OpenGL diagnostics and Vulkan
  pipeline keys.
- Shared material texture binding resolution, Vulkan material descriptor
  resolution, expanded Vulkan material uniform serialization coverage,
  shader source/type invalidation, Vulkan shader compile status/artifact
  diagnostics, prepared source, and async shader compile plumbing.
- Texture event subscription symmetry, generated/uploaded/layout/descriptor
  readiness separation, sampler-driven descriptor invalidation, rectangle and
  child texture resize tracking, texture-buffer source upload ordering, and
  texture-view invalidation.
- `VkDataBuffer` generation/readiness semantics, event symmetry with
  `GLDataBuffer`, memory flag/flush/growth diagnostics, program binding
  resolution, device-address/fallback diagnostics, barrier-planner coverage,
  and timeline-retired buffer destruction.
- Material table descriptor-index source contracts, generated Vulkan
  `nonuniformEXT` material-table sampling, and zero-readback policy guardrails.

## Shared Remaining TODOs

- [ ] Keep `IsGenerated` semantics consistent across backends: generated means
      the backend API object or wrapper cache handle exists, not that data,
      descriptors, programs, pipelines, or draw readiness are valid.
- [ ] Keep backend differences explicit in code comments when Vulkan chooses a
      native equivalent instead of an OpenGL-shaped implementation.
- [ ] Do not hide missing GPU or accelerated paths behind silent CPU fallbacks;
      emit diagnostics or an intentional fallback signal.
- [ ] Extend source-verifiable tests only for wrapper contracts not covered by
      the reconciled parity suite above.
- [ ] Add hardware validation for behavior that requires Vulkan command
      execution, validation layers, GPU capture, or visual comparison.
- [ ] Keep hot-path allocations out of per-frame draw, descriptor, buffer, and
      upload paths.

## Shared Readiness Vocabulary

Use the same readiness categories in wrapper diagnostics and tests. The
categories are backend-neutral even when one backend satisfies them through a
native mechanism.

- `Generated`: the backend API object or wrapper cache handle exists.
- `Uploaded`: CPU-provided bytes or pixels have reached backend-owned storage,
  or a queued upload completion can be observed.
- `Resident`: the resource has enough GPU-visible memory, pages, mips, or rows
  for the requested use.
- `DescriptorReady`: descriptors, sampler/image views, buffer ranges, bindless
  handles, or equivalent binding records are valid for the active binding
  layout.
- `PipelineReady`: shader/program/pipeline objects are valid for the selected
  render state, material layout, vertex input, pass attachment metadata, and
  feature profile.
- `PassReady`: render-graph pass metadata, attachment formats, layouts,
  load/store decisions, queue ownership, and barriers are valid for command
  recording.
- `Retired`: backend resources that were replaced or destroyed are no longer
  referenced by in-flight GPU work and may be physically freed.

## Shared Vulkan-Native TODOs

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

## Shared OpenGL Backfill TODOs

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

## `XRMeshRenderer.BaseVersion`

- [ ] Verify patch-list behavior, tessellation control points, and topology
      fallback against OpenGL `UsesPatchTopology`.
- [ ] Align per-frame draw and triangle statistics with OpenGL for indexed and
      non-indexed fallback draws.
- [ ] Report selected mesh submission strategy, requested strategy, fallback
      reason, and backend capability snapshot for Vulkan mesh draws.
- [ ] Confirm `GpuIndirectZeroReadback` and `GpuMeshletZeroReadback` draw paths
      do not read count, visibility, or indirect buffers in steady state.
- [ ] Confirm Vulkan meshlet dispatch uses GPU-written task records and
      indirect-count dispatch on `VK_EXT_mesh_shader` hardware.
- [ ] Keep missing production meshlet support as a visible downgrade to the
      resolver-selected non-meshlet path, not an implicit CPU direct draw.
- [ ] Finish Vulkan pipeline-key coverage for multiview/layer count, explicit
      tessellation control-point state, and specialization constants.
- [ ] Record per-renderer Vulkan submission metadata: selected mesh submission
      strategy, pass intent, state class, material-table path, descriptor
      fallback count, pipeline cache hit/miss, and zero-readback compliance.
- [ ] Keep meshlet dispatch and indirect-count draw paths explicitly
      capability-gated; unsupported production meshlet dispatch should downgrade
      through the resolver before draw recording.
- [ ] Report OpenGL program, VAO, and material binding cache hits and misses in
      the same profiler shape as Vulkan pipeline and descriptor readiness.
- [ ] Keep OpenGL zero-readback strategy diagnostics aligned with Vulkan:
      production indirect and meshlet paths must not read count, visibility, or
      indirect buffers in steady state.
- [ ] Hardware: compare OpenGL and Vulkan default world opaque, forward, shadow,
      and debug primitive output.
- [ ] Hardware: validate directional cascade and point-light shadow passes with
      validation layers enabled.

## `XRMesh`

- [ ] Use the geometry layout signature as the stable input to Vulkan vertex
      input generation, descriptor requirements, GPU scene database records,
      indirect draw records, meshlet task records, and pipeline keys. Vertex
      input generation, descriptor diagnostics, and direct pipeline keys are
      wired; GPU scene database, indirect records, and meshlet task records
      remain open.
- [ ] Require Vulkan GPU-driven paths to consume the same mesh layout contract
      across CPU direct, GPU indirect, material-table, and meshlet submission.
- [ ] Record layout diagnostics for unsupported meshlet dialect and indirect
      count source fallback.
- [ ] Report OpenGL VAO cache hits/misses, attribute binding decisions,
      instancing divisors, interleaved layouts, and deformation-buffer aliases
      using the same layout signature fields.
- [ ] Keep OpenGL meshlet diagnostics tied to the shared geometry layout even
      when the available OpenGL mesh-shader path remains diagnostic-only.
- [ ] Preserve the no-standalone-wrapper decision until duplicated geometry
      layout lifetime across direct, indirect, and meshlet paths proves that a
      real backend mesh resource is cleaner.
- [ ] Hardware: draw a mesh using triangle, line, point, interleaved, instanced,
      skinned, and blendshape buffers in both backends.

## `XRMaterial`

- [ ] Audit descriptor-array shader variants outside the generated
      material-table path and require `nonuniformEXT` or an equivalent validated
      shader variant wherever per-draw, per-material, or GPU-written values
      index descriptor arrays.
- [ ] Verify local render-options overrides take priority over material options
      like OpenGL.
- [ ] Include render-option state in pipeline keys when it affects immutable
      Vulkan pipeline state.
- [ ] Align std140/std430 layout packing with the reflected descriptor block
      layout instead of hardcoded type sizes.
- [ ] Extend material-table dirty-range validation so editing one material proves
      it does not rewrite unrelated rows in OpenGL or Vulkan table paths.
- [ ] Report active texture binding rung, descriptor-indexing availability,
      material-table layout hash, and fallback reason in renderer diagnostics.
- [ ] Keep OpenGL bindless handle tables and Vulkan descriptor-indexed texture
      arrays on the same logical material/texture index contract.
- [ ] Warn when a material program has no parameter or sampler bindings after
      descriptor resolution.
- [ ] Add rate-limited texture-risk diagnostics equivalent to OpenGL runtime
      material texture checks.
- [ ] Make the material binding layout a first-class Vulkan artifact derived
      from pass intent, shader reflection, material parameters, texture slots,
      engine uniforms, shadow binding source, descriptor-indexing availability,
      and material-table policy.
- [ ] Have `VkMaterial` populate a prepared binding plan instead of rediscovering
      descriptor rules during draw recording. The plan should include descriptor
      set layout, descriptor writes, uniform/storage block offsets, push
      constants, texture array indices, fallback resources, and material row
      layout hash.
- [ ] Treat descriptor layout signature, material row layout hash, texture
      binding rung, render options, shader artifact identity, and shadow source
      material as pipeline/material readiness inputs.
- [ ] Validate std140/std430/scalar layout offsets from reflection data before
      serializing material parameters into Vulkan uniform/storage buffers.
- [ ] Keep material table updates dirty-range based and report the exact rows,
      byte ranges, texture indices, and descriptor writes touched by a material
      edit.
- [ ] Make placeholder texture use visible by role and reason: missing asset,
      not resident, unsupported format, descriptor allocation failure,
      incompatible sampler/view, or warmup not complete.
- [ ] Give OpenGL bindless and material-table paths the same pass-declared
      material layout hash used by Vulkan descriptor-indexed material paths.
- [ ] Treat classic OpenGL sampler binding as one texture binding rung under the
      shared material contract, not as the conceptual source of material texture
      identity.
- [ ] Update OpenGL material tables with dirty rows and dirty byte ranges so
      editing one material does not rewrite unrelated rows.
- [ ] Report OpenGL bindless handle table state, residency, fallback role,
      material row index, texture logical index, and binding rung in profiler
      counters comparable to Vulkan descriptor diagnostics.
- [ ] Keep OpenGL shader uniform/block packing diagnostics comparable to Vulkan
      reflection-based material parameter serialization diagnostics.
- [ ] Hardware: compare ordinary, shadow, depth-normal, FBO/post, and
      bindless-material paths against OpenGL.

## `XRShader`

- [ ] Hardware/profile the Vulkan prepared-source and async compile path against
      OpenGL parallel compile behavior, including warmup latency and completion
      polling.
- [ ] Finish backend-neutral pipeline readiness diagnostics for any remaining
      callers that still report pending Vulkan shader compile as a generic
      program or pipeline miss.
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
- [ ] Feed successful shader artifacts into the Vulkan pipeline prewarm manifest
      so warmup can cover shader variants without waiting for a late draw-time
      miss.
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
- [ ] Unit/source test: OpenGL and Vulkan source identity inputs agree for a
      representative resolved shader.
- [ ] Hardware: run Vulkan shader compilation regression tests and a default
      world prewarm pass.

## `XRTexture`

- [ ] Extend property-driven sampler recreation to future per-texture anisotropy
      and depth-stencil-mode knobs if they are exposed.
- [ ] Implement equivalents for OpenGL `OnPrePushData` and `OnPostPushData`.
- [ ] Support per-layer, per-face, and OVR multiview attachment requests where
      the engine exposes them.
- [ ] Add a Vulkan equivalent to OpenGL detail-preserving 2D compute mipmap path
      or document why generic blit is the accepted Vulkan v1 behavior.
- [ ] Validate non-filterable formats and fallback behavior.
- [ ] Ensure mip-level counts and visible mip ranges match OpenGL for
      progressive and sparse streaming cases.
- [ ] Add VRAM budget checks and allocation accounting comparable to OpenGL
      texture storage paths.
- [ ] Rate-limit repeated Vulkan warnings.
- [ ] Include texture name, type, dimensions, mip count, layer count, format,
      usage flags, and descriptor view type in failures.
- [ ] Require every Vulkan texture allocation or import path to declare its
      intended image usage up front: sampled, storage, attachment, transfer
      source/destination, transient, sparse, and any mutable-view requirement.
- [ ] Validate `VkFormatFeatureFlags` before choosing upload, blit, mipmap,
      storage-image, attachment, depth/stencil, filtering, linear-tiling,
      sampled-image, and texel-buffer paths.
- [ ] Move layout transitions toward render-graph/pass ownership. Texture
      `BindRequested` should not be the hidden authority for image layouts when
      pass metadata can declare the sampled, storage, or attachment use.
- [ ] Track layout readiness and queue-family ownership separately from
      descriptor readiness so sampled, storage, transfer, and attachment uses
      can report different not-ready reasons.
- [ ] Include image usage, current/expected layout, queue ownership, format
      feature support, residency tier, sparse page state, and mutable-view
      compatibility in texture diagnostics.
- [ ] Keep sparse residency, partial mip residency, and memory decompression
      paths feature-gated and diagnostic; missing support should visibly select
      the non-sparse/non-decompressed residency path.
- [ ] Treat render-target textures as pass resources with explicit load/store
      decisions, not only as FBO-like attachment side effects.
- [ ] Report OpenGL texture readiness with the shared categories from this doc:
      generated, uploaded, resident, descriptor/binding ready, and pass ready.
- [ ] Add sampler fingerprint and texture-view compatibility diagnostics that
      match the Vulkan descriptor/image-view readiness report shape.
- [ ] Extend `log_textures.log` entries so OpenGL and Vulkan both report
      residency tier, upload route, fallback texture role, VRAM pressure, and
      descriptor/bindless binding rung.
- [ ] Validate OpenGL FBO/post textures through the same pass-declared
      attachment intent that Vulkan barrier planning consumes, even while the
      OpenGL executor remains sequential.
- [ ] Keep OpenGL sparse/progressive streaming behavior on the same logical
      residency contract planned for Vulkan sparse or partial-residency paths.

### `XRTexture1D`

- [ ] Validate upload of empty/missing mip levels against OpenGL behavior.

### `XRTexture1DArray`

- [ ] Validate array-layer upload ordering and per-layer missing-data handling.

### `XRTexture2D`

- [ ] Port or explicitly replace OpenGL sparse texture streaming behavior.
- [ ] Port or replace progressive mip upload behavior.
- [ ] Add detail-preserving compute mipmap parity or document a Vulkan-native
      alternative.
- [ ] Validate video-frame upload behavior against OpenGL import/update paths.
- [ ] Align storage, external/imported texture, runtime-managed progressive
      range, and diagnostics behavior.

### `XRTexture2DArray`

- [ ] Add OVR multiview attach/detach event parity.
- [ ] Add per-layer attach/detach event parity.

### `XRTextureRectangle`

- [ ] Confirm rectangle-specific sampler constraints map to Vulkan 2D image view
      behavior.
- [ ] Validate no-mipmap behavior where rectangle textures should not mipmap.

### `XRTexture3D`

- [ ] Validate 3D image upload row/slice layout against OpenGL.

### `XRTextureCube`

- [ ] Add face attach/detach event parity.
- [ ] Validate face ordering and layer indices match OpenGL cubemap face
      behavior.
- [ ] Ensure depth-only and per-face attachment views work for point shadows.

### `XRTextureBuffer`

- [ ] Validate uniform texel buffer and storage texel buffer descriptors.
- [ ] Add diagnostics when the source buffer lacks required Vulkan usage flags.

### `XRTextureView`

- [ ] Support depth/stencil view mode parity where Vulkan aspect masks differ
      from OpenGL `DepthStencilTextureMode`.

### Texture Validation

- [ ] Unit/source test: texture view compatibility matrix matches OpenGL.
- [ ] Hardware: compare default pipeline FBO/post textures, point shadows,
      cascaded shadows, cube captures, 2D array captures, UI textures, texture
      buffers, and texture views against OpenGL.

## `XRDataBuffer`

- [ ] Fill remaining steady-state counters after the current source contracts:
      staging reuse, host-visible writes, host-cached reads, descriptor-binding
      fallbacks, and zero-readback violations.
- [ ] Hardware: run compute skinning, GPUScene, indirect draw, readback, UI
      PBO/webview, and texture-buffer paths with Vulkan validation layers.
- [ ] Hardware: validate the Vulkan device-address consumer path and confirm the
      equivalent OpenGL path still renders through the shared buffer identity
      contract.

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

1. Source verification: wrapper registration, event symmetry, shader/material
   descriptor resolution, readiness state transitions, and deterministic
   source-contract tests.
2. Narrow build: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
3. Targeted tests: Vulkan P0/P1 validation tests and rendering wrapper
   source-contract tests.
4. Hardware validation: run matching OpenGL and Vulkan scenes, Unit Testing
   World in Vulkan, validation layers, GPU captures, and visual comparisons for
   opaque, forward, shadow, UI, FBO/post, and debug draw paths.
