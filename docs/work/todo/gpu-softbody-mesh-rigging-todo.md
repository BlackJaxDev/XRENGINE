# GPU Softbody Mesh Rigging TODO

Last Updated: 2026-03-19
Current Status: phase 3 shape-matching cluster runtime implemented
Scope: implement the GPU softbody and mesh-rigging architecture defined in the [design plan](../design/gpu-softbody-mesh-rigging-plan.md).

## Current Reality

What exists now:

- `XRMeshRenderer` already has a mesh-to-mesh deformation path based on per-vertex influence weights, deformer rest positions, and live deformer position buffers.
- `MeshDeformVertexShaderGenerator` already generates a dedicated vertex shader path for deformation from another mesh rather than classic bone matrices.
- The current mesh-deform path uses weighted displacement from deformer rest positions, not local-frame skinning or cluster-transform skinning.
- Compute-shader skinning and blendshape prepasses already exist and already output GPU-resident deformed buffers for rendering.
- `PhysicsChainComponent` (with `UseGPU = true`) and `GPUPhysicsChainDispatcher` already provide a batched GPU particle simulation path with capsule collisions and compute integration tests. GPU-specific code lives in the `PhysicsChainComponent.GPU.cs` partial file.
- PhysX softbody-related low-level interop exists, but there is no engine-facing softbody component or runtime authoring pipeline.
- No dedicated softbody compute shaders exist.
- No shape-matching or cluster-transform implementation exists.
- Focused `XRMeshRenderer` tests now exist for vec4 influence packing, SSBO influence packing, compute-skinned-source propagation, and interleaved compute-source fallback copying.
- The compute-skinned deformer source path now supports separate compute-skinned position/normal/tangent sources directly and supports interleaved compute output through the CPU-visible fallback copy path. Direct GPU aliasing for interleaved compute output is still deferred.

What this todo is for:

- harden the existing mesh-to-mesh rigging prototype,
- add a dedicated GPU softbody simulation path,
- evolve render deformation from displacement-only deformation to cluster-based soft skinning,
- add authoring, debug, and validation support.

## Target Outcome

At the end of this work:

- the engine has a first-class GPU softbody subsystem built around coarse simulation proxies,
- capsule colliders can drive or collide with softbody regions,
- a detailed render mesh can be skinned to simulated soft clusters instead of only to bone matrices,
- the render path consumes GPU-resident deformed data without CPU readback on the main visible path,
- the existing mesh-to-mesh deformation path is either hardened as a supported mode or absorbed into the generalized soft-deformation binding system,
- targeted tests cover authoring, compute dispatch, deformation correctness, and regression cases.

## Non-Goals

- Do not start with full FEM or physically exact material simulation.
- Do not simulate final render meshes directly unless a mesh is intentionally tiny.
- Do not depend on CPU readback for the main visible rendering path.
- Do not use PhysX or Jolt softbody as the primary solver. PhysX has low-level hooks in the repo but no engine-facing softbody component; Jolt has no softbody API in the current binding. Both are CPU-side solvers that would require data round-tripping to feed the GPU render pipeline. The custom GPU path builds directly on the existing compute-dispatch and compute-skinning infrastructure. Physics-engine softbody evaluation is deferred to the optional future phase.
- Do not replace the existing compute skinning infrastructure; build on top of its patterns.

---

## Phase 0 - Existing Mesh-Deform Hardening

Outcome: the existing mesh-to-mesh rigging path becomes a validated, supportable baseline instead of an unfinished prototype.

### 0.1 Audit and Document Current Binding Contract

- [x] Document the expected semantics of `XRMeshRenderer.SetupMeshDeformation(...)`
- [x] Document influence-count limits, vec4 optimization behavior, and SSBO fallback behavior
- [x] Document how normals and tangents are expected to behave when deformer data is present or absent
- [ ] Document current limitations of displacement-only deformation vs. future cluster skinning

### 0.2 Fix Compute-Skinned Deformer Source Propagation

- [x] Fix `XRMeshRenderer.UpdateDeformerPositions()` so a deformer with `SkinnedPositionsBuffer` actually feeds mesh-deform input data instead of only marking the buffers dirty
- [x] Decide whether to alias GPU buffers directly, copy via GPU path, or rebuild the deformer-position SSBO from the compute output
- [x] Apply the same review to normals and tangents if compute-skinned normals/tangents are present
- [x] Verify the chosen path does not regress the non-compute deformer case

### 0.3 Add Focused Tests For Mesh Deformation

- [x] Add setup/buffer-population tests for mesh-deform input buffers
- [x] Add tests for vec4-optimized influence packing
- [x] Add tests for SSBO influence packing
- [x] Add tests for compute-skinned-source deformation hookup
- [x] Add tests for deformer normal/tangent propagation
- [ ] Add regression tests for missing normals, missing tangents, and empty influence lists

### 0.4 Add Minimal Runtime Validation

- [x] Add editor-visible diagnostics when deformer/source mesh vertex counts or assumptions do not match expectations
- [x] Add a debug overlay or inspector summary for mesh-deform state
- [ ] Add a narrow validation scene or harness for visible sanity-checking of mesh-to-mesh deformation

Acceptance criteria:

- [x] The mesh-to-mesh path works with both raw source mesh data and compute-skinned source data.
- [x] Dedicated unit tests exist for mesh-deform setup and runtime propagation.
- [ ] The current deformation path is documented clearly enough to use as a baseline for later phases.

---

## Phase 1 - Softbody Runtime Scaffold

Outcome: the engine has a dedicated softbody compute subsystem with GPU buffer management and render-thread orchestration, but without final cluster skinning yet.

### 1.1 Add Softbody Runtime Types

- [x] Add runtime data types for particles, constraints, clusters, cluster members, colliders, and render-binding metadata
- [x] Decide project placement for shared contracts vs. engine implementation code
- [x] Keep hot-path layouts allocation-conscious and ready for SoA conversion if needed

### 1.2 Add Softbody Compute Dispatcher

- [x] Add `GpuSoftbodyDispatcher`-style runtime dispatcher parallel to `GPUPhysicsChainDispatcher`
- [x] Add registration, submission, and frame-dispatch lifecycle similar to the existing compute simulation pattern
- [x] Support multiple active softbodies in a single frame without per-instance ad hoc dispatch plumbing
- [x] Decide whether the first version batches all instances into shared buffers or starts one-instance-at-a-time behind the same API

### 1.3 Add Initial Compute Shader Family

- [x] Add `Build/CommonAssets/Shaders/Compute/Softbody/Integrate.comp`
- [x] Add `Build/CommonAssets/Shaders/Compute/Softbody/CollideCapsules.comp`
- [x] Add `Build/CommonAssets/Shaders/Compute/Softbody/SolveDistance.comp`
- [x] Add `Build/CommonAssets/Shaders/Compute/Softbody/Finalize.comp` or equivalent final-state pass
- [x] Add a `README.md` or taxonomy note under the new `Softbody/` compute directory only if the existing compute shader taxonomy requires it

### 1.4 Add Component-Level API

- [x] Add a scene component for softbody simulation ownership and lifecycle
- [x] Expose simulation proxy data, runtime parameters, and collider bindings through the component
- [x] Keep the public API neutral enough to support future cluster skinning and optional future PhysX-backed implementations

Acceptance criteria:

- [x] A dedicated softbody compute family exists in the shader tree.
- [x] A softbody component can register with the dispatcher and submit GPU data.
- [x] The runtime scaffolding follows the engine's current compute-dispatch conventions.

---

## Phase 2 - Particle Simulation And Capsule Collision

Outcome: a coarse softbody particle proxy simulates on the GPU with stable collision against capsules.

### 2.1 Particle Integration

- [x] Implement Verlet or PBD-style particle integration in compute
- [x] Store current position, previous position, rest position, inverse mass, and radius per particle
- [x] Add damping and external force support
- [x] Add fixed-timestep or substep policy consistent with engine simulation expectations

### 2.2 Structural Constraints

- [x] Implement distance constraints between particles
- [x] Decide first-pass solver strategy: graph coloring, Jacobi accumulation, or per-particle adjacency solve
- [x] Validate stability under realistic iteration counts
- [x] Add stiffness/compliance controls suitable for later authoring

### 2.3 Capsule Colliders

- [x] Add softbody-facing capsule collider data path
- [x] Implement closest-point-on-segment projection and penetration correction in compute
- [x] Support moving collider velocity for kinematic driving, not just passive collision
- [x] Add friction/tangential drag tuning points

### 2.4 Debug And Diagnostics

- [x] Add debug drawing for particles, links, and capsule contacts
- [x] Add dispatch counters and simple timing diagnostics
- [x] Add overflow or invalid-data diagnostics for bad buffer submissions

### 2.5 Tests

- [x] Add shader compilation tests for the softbody compute family
- [x] Add integration tests for simple particle motion
- [x] Add integration tests for capsule push-out behavior
- [x] Add integration tests for solver stability over several frames

Acceptance criteria:

- [x] A coarse particle softbody can simulate on GPU and collide against capsules.
- [x] The simulation is stable enough for debug visualization without obvious explosive failure in the basic test scenes.
- [x] Targeted compute tests cover the first-pass simulation path.

---

## Phase 3 - Shape-Matching Clusters

Outcome: the simulation produces stable cluster transforms suitable for soft skinning instead of only raw displacement fields.

### 3.1 Cluster Authoring Data

- [x] Add cluster definitions and cluster-member ranges to the runtime data model
- [x] Add rest-space local offsets for cluster members
- [x] Add support for overlapping clusters
- [ ] Add import/build pipeline hooks for generating clusters from a simulation proxy

### 3.2 Cluster Solve

- [x] Implement cluster center-of-mass solve
- [x] Implement covariance accumulation and best-fit rotation extraction
- [x] Implement shape-matching goal-position solve for overlapping clusters
- [x] Blend cluster corrections back into particle state

### 3.3 Cluster Transform Output

- [x] Add a GPU output buffer for cluster transforms
- [x] Define transform format for rendering use: translation plus quaternion or matrix equivalent
- [x] Add debug visualization for cluster centers and orientations

### 3.4 Tests

- [x] Add tests for cluster membership packing and runtime buffer generation
- [x] Add tests for simple cluster rotation recovery cases
- [x] Add tests for overlapping-cluster stability on a synthetic sample

Acceptance criteria:

- [x] The simulation produces stable cluster transforms from particle motion.
- [x] Cluster transforms are GPU-resident and ready to drive a render mesh.
- [x] The first shape-matching pass has focused tests and debug visualizations.

---

## Phase 4 - Cluster-Based Render Skinning

Outcome: the detailed render mesh is deformed from simulated soft clusters using a skinning-like pipeline.

### 4.1 Render Binding Model

- [ ] Add render-vertex binding data for soft-cluster indices, weights, local position offsets, and local normals
- [ ] Define how this binding data coexists with existing bone-skinning and mesh-deform systems
- [ ] Support 4-influence baseline and leave room for higher influence counts if needed

### 4.2 Compute Skinning Pass

- [ ] Add `BuildClusterTransforms.comp` if not already covered in Phase 3
- [ ] Add `SkinRenderMesh.comp` to output deformed positions, normals, and tangents
- [ ] Reuse compute output buffer conventions already used by `SkinningPrepassDispatcher`
- [ ] Ensure render meshes can consume softbody-generated buffers with no CPU readback

### 4.3 Render Integration

- [ ] Add runtime selection between displacement-only deformation and cluster skinning where appropriate
- [ ] Ensure bounds, culling, and render buffer lifetime work with the new deformation path
- [ ] Ensure the design coexists with or layers over the existing compute-skinning and blendshape pipeline cleanly

### 4.4 Tests

- [ ] Add tests for render binding data generation
- [ ] Add tests for compute output buffer creation and consumption
- [ ] Add regression tests comparing cluster-skinning behavior to the old displacement-only path on representative samples

Acceptance criteria:

- [ ] A high-detail render mesh can be skinned from soft-cluster transforms on the GPU.
- [ ] Visible rendering does not depend on CPU readback.
- [ ] Buffer lifetime and culling integration are validated by targeted tests.

---

## Phase 5 - Authoring Pipeline And Tooling

Outcome: artists and engine users can build, inspect, and tune softbody rigs intentionally rather than relying on hard-coded data.

### 5.1 Proxy Generation

- [ ] Add tooling to generate a coarse simulation proxy from a source mesh
- [ ] Support at least one initial workflow: sampled particles, coarse cage, or tetra proxy
- [ ] Preserve enough rest-space metadata to build cluster bindings deterministically

### 5.2 Binding Generation

- [ ] Add tooling to bind render vertices to soft clusters using nearest-cluster or inverse-distance weighting as the first implementation
- [ ] Store rest local offsets and normals per influence
- [ ] Add validation for invalid or weak bindings

### 5.3 Editor Tooling

- [ ] Add inspector or editor UI for softbody parameters
- [ ] Add visualization toggles for particles, links, clusters, and capsule colliders
- [ ] Add region controls for stiffness, damping, and collision radius masks if practical in the first tooling pass

### 5.4 Documentation

- [ ] Promote the final supported workflow from work docs into stable docs once behavior is real
- [ ] Document authoring constraints, expected topology assumptions, and runtime costs

Acceptance criteria:

- [ ] A softbody rig can be authored or generated without bespoke code changes.
- [ ] Runtime parameters and debug state are inspectable in the editor.
- [ ] Supported workflow is documented beyond the design doc.

---

## Optional Future Phase - Alternate Solver Backends

Outcome: the architecture can support non-custom solver backends without forcing a redesign.

### Future Investigation Items

- [ ] Evaluate whether PhysX softbody interop should become a backend option once the custom path is stable (low-level hooks exist in `PhysxScene.CopySoftBodyData` / `ApplySoftBodyData` and `PhysxTetrahedronMesh`)
- [ ] Evaluate whether Jolt adds a deformable-body API in a future binding update and whether it would be practical as an alternative backend
- [ ] Evaluate tetrahedral proxy generation as a higher-fidelity alternative to particle-only proxies
- [ ] Evaluate whether exact volume constraints or XPBD compliance should replace or augment the first-pass solver
- [ ] Evaluate whether some softbody regions should remain displacement-only for cheaper secondary motion (cage positions + normals can drive lightweight deformation without cluster transforms when full skinning quality is not needed)

This phase is intentionally deferred until the custom GPU path is proven.

## Suggested Execution Order

1. Finish Phase 0 first so the existing mesh-to-mesh rigging work becomes trustworthy baseline infrastructure.
2. Build Phase 1 and Phase 2 together tightly so the new runtime scaffold immediately exercises real simulation.
3. Add cluster shape matching before investing in final render skinning.
4. Add cluster-based render skinning before investing heavily in editor tooling.
5. Only consider alternate backends after the custom path is validated.

## Validation Baseline

Before implementation starts, the relevant known-green baseline is:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter Phase2_HostContracts_ArePresent`

Recommended targeted validation as phases land:

- Phase 0: focused `XRMeshRenderer` tests for mesh-deform setup and compute-skinned-source propagation
- Phase 1: softbody dispatcher/component tests and compute shader compilation tests
- Phase 2: softbody compute shader compilation and minimal integration tests
- Phase 3: cluster-solve correctness tests
- Phase 4: render integration and buffer-consumption tests
- Phase 5: authoring/tooling smoke tests plus nearest runtime validation scene

## Exit Criteria

- [ ] The mesh-to-mesh deformation baseline is hardened and covered by tests.
- [ ] A dedicated GPU softbody subsystem exists in engine code and shader assets.
- [ ] Capsule-driven softbody particle simulation works on the GPU.
- [ ] Shape-matching clusters produce stable transforms suitable for rendering.
- [ ] A detailed render mesh can be skinned from soft-cluster transforms with GPU-resident outputs.
- [ ] The editor or tooling surface can author and inspect supported softbody rigs.
- [ ] The supported workflow is documented clearly enough to use without re-reading the design doc.

## Reference

Design source: [gpu-softbody-mesh-rigging-plan.md](../design/gpu-softbody-mesh-rigging-plan.md)