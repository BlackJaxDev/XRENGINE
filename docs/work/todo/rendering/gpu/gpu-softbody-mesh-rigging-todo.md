# GPU Softbody Mesh Rigging TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [GPU Softbody](../../../../developer-guides/rendering/gpu-softbody.md)  Design: [GPU Softbody Mesh Rigging Plan](../../../design/rendering/gpu/gpu-softbody-mesh-rigging-plan.md)
Validation: [GPU Deformation Validation](../../../testing/rendering/gpu-deformation-validation.md#gpu-softbody)

## Current State

`XRMeshRenderer.SetupMeshDeformation(...)` supports mesh-to-mesh displacement deformation with packed influences and compute-skinned deformer sources. `GPUSoftbodyComponent`, `GPUSoftbodyDispatcher`, `GPUSoftbodyClusterMath`, and softbody compute shaders exist. Particles, distance constraints, capsule collision, cluster transforms, debug drawing, dispatch diagnostics, and focused tests exist. Render bindings upload, but no shader skins a detailed render mesh from soft-cluster transforms. No tool generates simulation proxies, clusters, or render bindings.

## Open Code Items

### Mesh-deform baseline

- [ ] Document current limitations of displacement-only deformation versus future cluster skinning. `docs/developer-guides/rendering/gpu-softbody.md`, `XRMeshRenderer`. Done when: the guide states when displacement mode is valid and when cluster skinning is required.
- [ ] Add regression tests for missing normals, missing tangents, and empty influence lists. `XREngine.UnitTests`, `XRMeshRenderer`. Done when: each edge case has a deterministic test.
- [ ] Add a narrow validation scene or harness for visible mesh-to-mesh deformation. `XREngine.Editor`, `XRMeshRenderer`. Done when: the scene or harness exercises raw and compute-skinned deformer sources.

### Cluster render skinning

- [ ] Add import or build pipeline hooks for generating clusters from a simulation proxy. `GPUSoftbodyComponent`, import tooling. Done when: generated clusters populate the runtime cluster lists deterministically.
- [ ] Add render-vertex binding data for soft-cluster indices, weights, local position offsets, and local normals. `GPUSoftbodyRenderBindingData`, mesh import or authoring code. Done when: a render mesh has deterministic soft-cluster binding data.
- [ ] Define how soft-cluster binding data coexists with bone skinning and mesh-deform systems. `XRMeshRenderer`, `SkinningPrepassDispatcher`, `GPUSoftbodyComponent`. Done when: one renderer can choose the correct deformation source without ambiguous ownership.
- [ ] Support a four-influence baseline and leave room for higher influence counts. `GPUSoftbodyRenderBindingData`, shaders. Done when: the layout validates four influences and can version a larger layout.
- [ ] Add `BuildClusterTransforms.comp` only if existing cluster output cannot feed render skinning directly. `Build/CommonAssets/Shaders/Compute/Softbody/`. Done when: render skinning has the transform data it needs without duplicate work.
- [ ] Add `SkinRenderMesh.comp` to output deformed positions, normals, and tangents. `Build/CommonAssets/Shaders/Compute/Softbody/`. Done when: the shader writes renderable buffers from cluster transforms and bindings.
- [ ] Reuse compute output buffer conventions from `SkinningPrepassDispatcher`. `GPUSoftbodyDispatcher`, `SkinningPrepassDispatcher`. Done when: lifetime, previous-frame data, and diagnostics follow the existing compute skinning pattern.
- [ ] Ensure render meshes consume softbody-generated buffers with no CPU readback. `XRMeshRenderer`, `GPUSoftbodyDispatcher`. Done when: the visible path samples GPU-resident buffers only.
- [ ] Add runtime selection between displacement-only deformation and cluster skinning. `XRMeshRenderer`, `GPUSoftbodyComponent`. Done when: each mode is explicit and diagnostics report the active mode.
- [ ] Integrate bounds, culling, and render buffer lifetime for softbody deformation. `XRMeshRenderer`, culling code, softbody dispatcher. Done when: softbody bounds are conservative and buffers stay valid for render submission.
- [ ] Ensure softbody deformation layers cleanly with compute skinning and blendshapes. `XRMeshRenderer`, `SkinningPrepassDispatcher`, blendshape shaders. Done when: the pipeline order is explicit and tested.

### Authoring and tooling

- [ ] Add tooling to generate a coarse simulation proxy from a source mesh. Import or editor tooling. Done when: at least one workflow creates a proxy without bespoke code.
- [ ] Support sampled particles, a coarse cage, or a tetra proxy as the first workflow. Import or editor tooling. Done when: the selected workflow records its source and settings.
- [ ] Preserve enough rest-space metadata to build cluster bindings deterministically. Proxy and binding tooling. Done when: the same input produces the same bindings.
- [ ] Add tooling to bind render vertices to soft clusters with nearest-cluster or inverse-distance weighting. Binding tooling. Done when: render vertices receive stable influences.
- [ ] Store rest local offsets and normals per influence. Binding tooling, `GPUSoftbodyRenderBindingData`. Done when: render skinning can reconstruct the rest-space frame.
- [ ] Add validation for invalid or weak bindings. Binding tooling. Done when: invalid bindings fail with a clear diagnostic.
- [ ] Add an inspector or editor UI for softbody parameters. Editor component UI. Done when: users can inspect and edit runtime parameters.
- [ ] Add visualization toggles for particles, links, clusters, and capsule colliders. Editor component UI, debug drawing. Done when: each debug layer can be enabled independently.
- [ ] Add region controls for stiffness, damping, and collision radius masks if practical in the first tooling pass. Editor or import tooling. Done when: authored regions drive runtime parameters.

### Tests

- [ ] Add tests for render binding data generation. `XREngine.UnitTests`. Done when: generated bindings are deterministic and reject invalid input.
- [ ] Add tests for compute output buffer creation and consumption. `XREngine.UnitTests`, softbody dispatcher. Done when: cluster skinning buffers are allocated, published, and retired correctly.
- [ ] Add regression tests comparing cluster skinning with displacement-only deformation on representative samples. `XREngine.UnitTests`. Done when: both paths meet their expected deformation behavior.

### Documentation

- [ ] Promote the final supported workflow from work docs into stable docs when behavior is real. `docs/developer-guides/rendering/gpu-softbody.md`. Done when: the guide describes the supported authoring path.
- [ ] Document authoring constraints, topology assumptions, and runtime costs. `docs/developer-guides/rendering/gpu-softbody.md`. Done when: users can author a supported rig without reading the design doc.

## Decisions Needed

- [ ] Decide whether PhysX softbody interop should become an optional backend after the custom path is stable. Owner: physics.
- [ ] Decide whether a future Jolt deformable-body API would be practical as an optional backend. Owner: physics.
- [ ] Decide whether tetrahedral proxy generation is worth the added authoring complexity. Owner: rendering and physics.
- [ ] Decide whether exact volume constraints or XPBD compliance should replace or augment the first solver. Owner: physics.
- [ ] Decide whether some softbody regions should remain displacement-only for cheaper secondary motion. Owner: rendering.

## Out Of Scope

- Full FEM or physically exact material simulation.
- Simulating final render meshes directly unless a mesh is intentionally tiny.
- CPU readback on the main visible rendering path.
- PhysX or Jolt as the primary solver.
- Replacing the existing compute skinning infrastructure.
