# XRMeshRenderer Vulkan Parity TODO

Last Updated: 2026-06-05
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
   - [ ] Add a `PropertyChanging` path or equivalent old-value handling when
         `XRMeshRenderer.Mesh` changes.
   - [ ] Unsubscribe the old mesh's `DataChanged` and `Buffers.Changed` events
         before subscribing the new mesh.
   - [ ] Add a source/unit test that changes `Mesh` twice and proves stale mesh
         events no longer dirty the Vulkan renderer.

2. Port OpenGL material resolution semantics.
   - [ ] Match global override, pipeline override, local override, default
         material, and invalid-material priority.
   - [ ] Port directional cascade shadow material selection:
         `DirectionalCascadeShadowMaterialKind`, instanced layered variants,
         geometry fallback, and shared-opaque fallback.
   - [ ] Port point-light shadow material selection:
         `PointShadowMaterialKind`, cubemap/depth-output detection, instanced
         layered variants, geometry fallback, and shared-opaque fallback.
   - [ ] Port `ShadowUniformSourceMaterial` assignment for selected shadow
         variants.
   - [ ] Port depth-normal prepass variant selection using
         `UseDepthNormalMaterialVariants` and `DepthNormalPrePassVariant`.
   - [ ] Preserve Vulkan pipeline-state key correctness when the selected
         material changes through shadow/depth-normal rules.

3. Match shadow draw behavior.
   - [ ] Implement triangle-only draw suppression for shadow geometry passes.
   - [ ] Expand instance count for directional cascade instanced layered shadow
         passes.
   - [ ] Expand instance count for point-light instanced layered shadow passes.
   - [ ] Upload/call the same cascade/point shadow layer uniforms that
         OpenGL provides to vertex programs.
   - [ ] Call `OnSettingShadowUniforms` in shadow paths when the material or
         source material supplies shadow handlers.

4. Add an explicit render-preparation contract.
   - [ ] Decide whether Vulkan should implement `IRenderPreparationState` or a
         backend-neutral successor.
   - [ ] Expose clear readiness state for buffers, programs, descriptors, and
         pipeline objects before a draw is recorded.
   - [ ] Keep this readiness state separate from `IsGenerated`.
   - [ ] Add diagnostics equivalent to OpenGL's preparation reason strings.
   - [ ] Avoid command-buffer churn when a renderer is known not ready.

5. Align shader/program selection and generated vertex shader behavior.
   - [ ] Audit OpenGL generated vertex shader rules for missing vertex shaders,
         skinning, blendshapes, mesh deformation, and point-shadow depth output.
   - [ ] Ensure Vulkan generated shader identity includes the same render
         settings axes that affect source shape.
   - [ ] Ensure Vulkan pipeline keys include all shader/material/render-state
         axes required by the OpenGL combined-program cache.

6. Harden topology and draw statistics.
   - [ ] Verify patch-list behavior, tessellation control points, and topology
         fallback against OpenGL `UsesPatchTopology`.
   - [ ] Ensure line/point/triangle index buffers are not all emitted in passes
         that expect one primitive class.
   - [ ] Align per-frame draw/triangle statistics with OpenGL for indexed and
         non-indexed fallback draws.

7. Match buffer binding diagnostics.
   - [ ] Emit actionable warnings when no vertex attributes bind for the active
         Vulkan pipeline.
   - [ ] Log enough shader, buffer, attribute, binding, and topology context to
         debug Vulkan-only missing geometry without GPU capture.
   - [ ] Keep logging opt-in or rate-limited for hot paths.

## Validation

- [ ] Source test: mesh replacement unsubscribes old mesh events.
- [ ] Source test: shadow/depth-normal material selection returns the same
      logical `XRMaterial` as OpenGL for representative render states.
- [ ] Source test: Vulkan draw recording suppresses line/point draws during
      shadow geometry passes.
- [ ] Hardware: compare OpenGL and Vulkan default world opaque, forward,
      shadow, and debug primitive output.
- [ ] Hardware: validate directional cascade and point-light shadow passes with
      validation layers enabled.
