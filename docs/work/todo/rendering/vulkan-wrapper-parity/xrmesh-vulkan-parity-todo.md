# XRMesh Vulkan Parity TODO

Last Updated: 2026-06-07
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
   - [x] Add a short note to `vulkan-renderer.md` or `RenderingCodeMap.md`
         explaining that `XRMesh` has no API wrapper.
   - [x] List the owning wrappers for index buffers, vertex buffers, runtime
         deformation buffers, and mesh data invalidation.
   - [x] Revisit this decision only if mesh resource lifetime becomes
         duplicated across direct, indirect, and meshlet paths.

2. Align mesh invalidation propagation.
   - [x] Ensure `XRMesh.DataChanged` dirties Vulkan buffers, descriptors, and
         pipelines where OpenGL invalidates VAO/program state.
   - [x] Ensure `Mesh.Buffers.Changed` and `MeshRenderer.Buffers.Changed`
         produce the same effective buffer set on both backends.
   - [x] Ensure mesh replacement unsubscribes the old mesh's events.

3. Align index-buffer generation.
   - [x] Verify triangle, line, and point calls to `XRMesh.GetIndexBuffer`
         request the same primitive type, target, callback, and element-size
         semantics.
   - [x] Keep async callback behavior backend-neutral: first request may return
         null, completion must refresh the renderer without blocking the render
         thread.
   - [x] Add tests for byte, ushort, and uint index sizes, including Vulkan's
         `indexTypeUint8` unsupported path.

4. Align vertex-buffer semantics.
   - [x] Ensure `AttributeName`, `BindingIndexOverride`, component type,
         component count, `Integral`, `Normalize`, `InstanceDivisor`, and
         `InterleavedAttributes` produce equivalent shader input bindings.
   - [x] Verify Vulkan's reflection-driven location mapping covers the same
         by-name binding cases as OpenGL.
   - [x] Add tests for interleaved attributes, missing attributes, explicit
         binding overrides, and instanced attributes.

5. Align mesh deformation buffer ownership.
   - [x] Verify compute-skinned output buffers are collected under the same
         shader names and binding indices as OpenGL.
   - [x] Verify precombined blendshape buffers are exposed only when the
         matching render settings and runtime validity flags are active.
   - [x] Verify mesh-deform source buffers are available to Vulkan when OpenGL
         binds them as SSBO aliases.

6. Align hot-path allocation behavior.
   - [x] Audit Vulkan mesh buffer collection and pipeline input generation for
         per-frame allocations when mesh state is unchanged.
   - [x] Cache reflection and binding lookups at the same or better granularity
         as OpenGL VAO/program binding caches.

## Vulkan-Native Acceptance Additions

- [x] Add or document a backend-neutral `MeshGeometryLayout` / geometry layout
      signature before introducing any standalone mesh wrapper. The signature
      should describe vertex buffers, index buffers, interleaved attributes,
      instancing, deformation buffers, meshlet buffers, primitive classes, and
      draw-count sources.
- [ ] Use the geometry layout signature as the stable input to Vulkan vertex
      input generation, descriptor requirements, GPU scene database records,
      indirect draw records, meshlet task records, and pipeline keys. Vertex
      input generation, descriptor diagnostics, and direct pipeline keys are
      wired; GPU scene database, indirect records, and meshlet task records
      remain open.
- [x] Keep geometry layout identity separate from buffer upload readiness:
      changing bytes should dirty upload/readiness, while changing layout should
      dirty descriptors, vertex input, scene database records, and pipelines.
- [ ] Require Vulkan GPU-driven paths to consume the same mesh layout contract
      across CPU direct, GPU indirect, material-table, and meshlet submission.
- [ ] Record layout diagnostics for unsupported index type, missing attributes,
      unsupported meshlet dialect, deformation buffer mismatch, and indirect
      count source fallback. Unsupported index type, missing attributes, and
      deformation-buffer alias diagnostics are wired; meshlet dialect and
      indirect count source fallback diagnostics remain open.

## OpenGL Backfill Additions

- [x] Use the same geometry layout signature as OpenGL VAO/program binding
      input so Vulkan and OpenGL compare layout identity rather than parallel
      ad-hoc buffer lists.
- [ ] Report OpenGL VAO cache hits/misses, attribute binding decisions,
      instancing divisors, interleaved layouts, and deformation-buffer aliases
      using the same layout signature fields. Layout signatures now appear in
      buffer/attribute diagnostics; explicit cache hit/miss profiler parity
      remains open.
- [ ] Keep OpenGL meshlet diagnostics tied to the shared geometry layout even
      when the available OpenGL mesh-shader path remains diagnostic-only.
- [ ] Preserve the no-standalone-wrapper decision until duplicated geometry
      layout lifetime across direct, indirect, and meshlet paths proves that a
      real backend mesh resource is cleaner.

## Validation

- [x] Source test: no standalone `VkMesh` / `GLMesh` wrapper exists and mesh
      parity is covered by renderer/data-buffer tests.
- [x] Unit/source test: identical mesh buffer definitions produce matching
      OpenGL attribute locations and Vulkan vertex input descriptions.
- [ ] Hardware: draw a mesh using triangle, line, point, interleaved, instanced,
      skinned, and blendshape buffers in both backends.
