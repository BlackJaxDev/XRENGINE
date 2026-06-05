# XRMesh Vulkan Parity TODO

Last Updated: 2026-06-05
Status: Active.

## Goal

Make `XRMesh` behavior equivalent between backends even though there is no
standalone `GLMesh` or `VkMesh` wrapper. Mesh GPU behavior is currently owned
through `GLMeshRenderer` / `VkMeshRenderer` and `GLDataBuffer` /
`VkDataBuffer`.

## Source Inventory

Shared mesh:

- `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh*.cs`
- `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs`
- `XREngine.Runtime.Rendering/Buffers/XRDataBuffer.cs`

Backend owners:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Buffers.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Buffers.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Buffers/GLDataBuffer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkDataBuffer.cs`

## Current Architecture Decision

There is no separate mesh wrapper to port. The parity contract should either:

- remain explicitly renderer-owned, with docs/tests proving mesh behavior is
  covered by mesh-renderer and data-buffer wrappers; or
- introduce a real backend mesh resource wrapper if renderer-owned logic keeps
  duplicating or diverging.

## Generation Contract

Because `XRMesh` has no standalone OpenGL or Vulkan wrapper, it should not grow
an ambiguous mesh-level `IsGenerated` concept. Generation belongs to the owning
API objects: mesh renderers, data buffers, and any future mesh resource wrapper.
As in OpenGL, generated means an API object/ID/handle exists; index or vertex
data readiness must be tracked by the buffers and renderer preparation state.

## Missing Parity TODO

1. Document and lock the no-standalone-wrapper decision.
   - [ ] Add a short note to `vulkan-renderer.md` or `RenderingCodeMap.md`
         explaining that `XRMesh` has no API wrapper.
   - [ ] List the owning wrappers for index buffers, vertex buffers, runtime
         deformation buffers, and mesh data invalidation.
   - [ ] Revisit this decision only if mesh resource lifetime becomes
         duplicated across direct, indirect, and meshlet paths.

2. Align mesh invalidation propagation.
   - [ ] Ensure `XRMesh.DataChanged` dirties Vulkan buffers, descriptors, and
         pipelines where OpenGL invalidates VAO/program state.
   - [ ] Ensure `Mesh.Buffers.Changed` and `MeshRenderer.Buffers.Changed`
         produce the same effective buffer set on both backends.
   - [ ] Ensure mesh replacement unsubscribes the old mesh's events.

3. Align index-buffer generation.
   - [ ] Verify triangle, line, and point calls to `XRMesh.GetIndexBuffer`
         request the same primitive type, target, callback, and element-size
         semantics.
   - [ ] Keep async callback behavior backend-neutral: first request may return
         null, completion must refresh the renderer without blocking the render
         thread.
   - [ ] Add tests for byte, ushort, and uint index sizes, including Vulkan's
         `indexTypeUint8` unsupported path.

4. Align vertex-buffer semantics.
   - [ ] Ensure `AttributeName`, `BindingIndexOverride`, component type,
         component count, `Integral`, `Normalize`, `InstanceDivisor`, and
         `InterleavedAttributes` produce equivalent shader input bindings.
   - [ ] Verify Vulkan's reflection-driven location mapping covers the same
         by-name binding cases as OpenGL.
   - [ ] Add tests for interleaved attributes, missing attributes, explicit
         binding overrides, and instanced attributes.

5. Align mesh deformation buffer ownership.
   - [ ] Verify compute-skinned output buffers are collected under the same
         shader names and binding indices as OpenGL.
   - [ ] Verify precombined blendshape buffers are exposed only when the
         matching render settings and runtime validity flags are active.
   - [ ] Verify mesh-deform source buffers are available to Vulkan when OpenGL
         binds them as SSBO aliases.

6. Align hot-path allocation behavior.
   - [ ] Audit Vulkan mesh buffer collection and pipeline input generation for
         per-frame allocations when mesh state is unchanged.
   - [ ] Cache reflection and binding lookups at the same or better granularity
         as OpenGL VAO/program binding caches.

## Validation

- [ ] Source test: no standalone `VkMesh` / `GLMesh` wrapper exists and mesh
      parity is covered by renderer/data-buffer tests.
- [ ] Unit/source test: identical mesh buffer definitions produce matching
      OpenGL attribute locations and Vulkan vertex input descriptions.
- [ ] Hardware: draw a mesh using triangle, line, point, interleaved, instanced,
      skinned, and blendshape buffers in both backends.
