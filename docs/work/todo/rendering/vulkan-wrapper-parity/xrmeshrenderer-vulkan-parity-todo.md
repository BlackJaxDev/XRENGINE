# XRMeshRenderer Vulkan Parity TODO

Last Updated: 2026-06-07
Status: Active.

## Goal

Make `VkMeshRenderer` expose the same engine-facing render behavior as
`GLMeshRenderer` for `XRMeshRenderer.BaseVersion`: lifecycle, material
selection, shader/program readiness, buffer binding, topology, uniforms,
shadow/depth-normal variants, diagnostics, and validation behavior.

## Source Inventory

OpenGL:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Lifecycle.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Buffers.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Shaders.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Rendering.cs`

Vulkan:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Buffers.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Pipeline.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Descriptors.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Drawing.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Uniforms.cs`

## Current Parity Already Present

- Vulkan has a wrapper for `XRMeshRenderer.BaseVersion`.
- Triangle, line, and point index buffers are resolved from `XRMesh.GetIndexBuffer`.
- Async index-buffer completion marks Vulkan buffers dirty for a later refresh.
- Runtime deformation buffers are collected for compute skinning and
  blendshape paths.
- Vertex input location mapping has a name-resolution path through shader
  reflection.
- Image descriptor resolution in the mesh-renderer descriptor path prefers
  sampler names before material texture indices.

## Generation Contract

`IsGenerated` must not mean the renderer is ready to draw. In OpenGL it means
the VAO/API object exists and has an ID; readiness is tracked separately through
buffer, program, and preparation state. Vulkan should keep the same contract:
`IsGenerated`/`IsActive` may mean the Vulkan wrapper cache object exists, while
pipeline, descriptor, buffer upload, and draw readiness need separate checks.

## Missing Parity TODO

1. Fix mesh lifecycle event symmetry.
   - [x] Add a `PropertyChanging` path or equivalent old-value handling when
         `XRMeshRenderer.Mesh` changes.
   - [x] Unsubscribe the old mesh's `DataChanged` and `Buffers.Changed` events
         before subscribing the new mesh.
   - [x] Add a source/unit test that changes `Mesh` twice and proves stale mesh
         events no longer dirty the Vulkan renderer.

2. Port OpenGL material resolution semantics.
   - [x] Match global override, pipeline override, local override, default
         material, and invalid-material priority.
   - [x] Port directional cascade shadow material selection:
         `DirectionalCascadeShadowMaterialKind`, instanced layered variants,
         geometry fallback, and shared-opaque fallback.
   - [x] Port point-light shadow material selection:
         `PointShadowMaterialKind`, cubemap/depth-output detection, instanced
         layered variants, geometry fallback, and shared-opaque fallback.
   - [x] Port `ShadowUniformSourceMaterial` assignment for selected shadow
         variants.
   - [x] Port depth-normal prepass variant selection using
         `UseDepthNormalMaterialVariants` and `DepthNormalPrePassVariant`.
   - [x] Preserve Vulkan pipeline-state key correctness when the selected
         material changes through shadow/depth-normal rules.

3. Match shadow draw behavior.
   - [x] Implement triangle-only draw suppression for shadow geometry passes.
   - [x] Expand instance count for directional cascade instanced layered shadow
         passes.
   - [x] Expand instance count for point-light instanced layered shadow passes.
   - [x] Upload/call the same cascade/point shadow layer uniforms that
         OpenGL provides to vertex programs.
   - [x] Call `OnSettingShadowUniforms` in shadow paths when the material or
         source material supplies shadow handlers.

4. Add an explicit render-preparation contract.
   - [x] Decide whether Vulkan should implement `IRenderPreparationState` or a
         backend-neutral successor.
   - [x] Expose clear readiness state for buffers, programs, descriptors, and
         pipeline objects before a draw is recorded.
   - [x] Keep this readiness state separate from `IsGenerated`.
   - [x] Add diagnostics equivalent to OpenGL's preparation reason strings.
   - [x] Avoid command-buffer churn when a renderer is known not ready.

5. Align shader/program selection and generated vertex shader behavior.
   - [x] Audit OpenGL generated vertex shader rules for missing vertex shaders,
         skinning, blendshapes, mesh deformation, and point-shadow depth output.
   - [x] Ensure Vulkan generated shader identity includes the same render
         settings axes that affect source shape.
   - [x] Ensure Vulkan pipeline keys include all shader/material/render-state
         axes required by the OpenGL combined-program cache.

6. Harden topology and draw statistics.
   - [ ] Verify patch-list behavior, tessellation control points, and topology
         fallback against OpenGL `UsesPatchTopology`.
   - [x] Ensure line/point/triangle index buffers are not all emitted in passes
         that expect one primitive class.
   - [ ] Align per-frame draw/triangle statistics with OpenGL for indexed and
         non-indexed fallback draws.

7. Add Vulkan-native submission diagnostics.
   - [ ] Report selected mesh submission strategy, requested strategy, fallback
         reason, and backend capability snapshot for Vulkan mesh draws.
   - [ ] Confirm `GpuIndirectZeroReadback` and `GpuMeshletZeroReadback` draw
         paths do not read count, visibility, or indirect buffers in steady
         state.
   - [ ] Confirm Vulkan meshlet dispatch uses GPU-written task records and
         indirect-count dispatch on `VK_EXT_mesh_shader` hardware.
   - [ ] Keep missing production meshlet support as a visible downgrade to the
         resolver-selected non-meshlet path, not an implicit CPU direct draw.

8. Match buffer binding diagnostics.
   - [x] Emit actionable warnings when no vertex attributes bind for the active
         Vulkan pipeline.
   - [x] Log enough shader, buffer, attribute, binding, and topology context to
         debug Vulkan-only missing geometry without GPU capture.
   - [x] Keep logging opt-in or rate-limited for hot paths.

## Vulkan-Native Acceptance Additions

- [x] Make render preparation a hard pre-record gate for Vulkan. If buffers,
      descriptors, material layout, shader artifacts, pipeline key, vertex
      input, or pass metadata are not ready, skip command-buffer emission for
      that renderer and record the precise not-ready reason.
- [ ] Include dynamic rendering attachment formats, depth/stencil formats,
      MSAA state, multiview/layer count, topology, tessellation state, vertex
      input signature, shader artifact identity, descriptor layout signature,
      material layout hash, render options, push constants, specialization
      constants, and feature-profile gates in Vulkan pipeline keys. Dynamic
      rendering formats, depth/stencil, MSAA, topology, vertex input,
      shader/material identity, descriptor schema, pass metadata, render
      options, and feature-profile gates are wired; multiview/layer count,
      explicit tessellation control-point state, and specialization constants
      remain open.
- [x] Treat selected material changes as pipeline-readiness changes when they
      affect shader variants, descriptor layouts, material-row layouts, render
      options, shadow/depth-normal variants, or immutable pipeline state.
- [x] Emit renderer readiness diagnostics before command recording so repeated
      not-ready renderers do not create command-buffer churn.
- [ ] Record per-renderer Vulkan submission metadata: selected mesh submission
      strategy, pass intent, state class, material-table path, descriptor
      fallback count, pipeline cache hit/miss, and zero-readback compliance.
- [ ] Keep meshlet dispatch and indirect-count draw paths explicitly
      capability-gated. Unsupported production meshlet dispatch should downgrade
      through the resolver before draw recording, never inside the draw op.

## OpenGL Backfill Additions

- [x] Add an OpenGL readiness report with the same categories Vulkan exposes:
      buffer data, shader/program, material/texture bindings, render state,
      pipeline-like cache state, texture residency, and pass metadata.
- [x] Add a diagnostic "pipeline-state key" for OpenGL made from program, VAO,
      render options, selected material, material layout hash, pass state,
      topology, and vertex input signature.
- [ ] Report OpenGL program/VAO/material binding cache hits and misses in the
      same profiler shape as Vulkan pipeline and descriptor readiness.
- [x] Validate OpenGL shadow/depth-normal material selection against the same
      source tests used for Vulkan so backend differences are limited to native
      binding and draw mechanics.
- [ ] Keep OpenGL zero-readback strategy diagnostics aligned with Vulkan:
      production indirect and meshlet paths must not read count, visibility, or
      indirect buffers in steady state.

## Validation

- [x] Source test: mesh replacement unsubscribes old mesh events.
- [x] Source test: shadow/depth-normal material selection returns the same
      logical `XRMaterial` as OpenGL for representative render states.
- [x] Source test: Vulkan draw recording suppresses line/point draws during
      shadow geometry passes.
- [ ] Hardware: compare OpenGL and Vulkan default world opaque, forward,
      shadow, and debug primitive output.
- [ ] Hardware: validate directional cascade and point-light shadow passes with
      validation layers enabled.
