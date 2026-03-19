# GPU Softbody Mesh Rigging Plan

## Goal

Design a GPU-resident softbody system that simulates a coarse soft volume or surface proxy with compute shaders and uses that result to drive a higher-detail render mesh in a skinning-like way, but without relying on a traditional bone hierarchy as the primary deformation source.

The target use case is not a fully general offline-quality FEM stack. The target is a game-ready runtime system for:

- soft tissue and flesh-like secondary motion,
- creature or blob deformation,
- cage-like mesh-to-mesh deformation,
- character add-on soft regions driven by kinematic capsule colliders,
- GPU-scalable deformation that keeps the CPU as an orchestration layer.

## Current Repository Audit

### Existing systems that overlap with this design

#### 1. Mesh-to-mesh deformation path already exists

The engine already contains a mesh-driven deformation pipeline that is conceptually close to the render-deformation half of this proposal.

Relevant code:

- [XRENGINE/Rendering/Generator/MeshDeformVertexShaderGenerator.cs](../../../XRENGINE/Rendering/Generator/MeshDeformVertexShaderGenerator.cs)
- [XRENGINE/Rendering/XRMeshRenderer.cs](../../../XRENGINE/Rendering/XRMeshRenderer.cs)

What it currently does:

- Generates a vertex shader that deforms one mesh from another mesh's vertices instead of bone matrices.
- Stores per-vertex influences as deformer vertex indices plus weights.
- Uploads deformer current positions and rest positions as SSBOs.
- Optionally uploads deformer normals and tangents.
- Computes deformation as weighted delta from deformer rest pose:

  `finalPos = basePos + sum(weight * (deformerPos - deformerRestPos))`

- Uses `XRMeshRenderer.SetupMeshDeformation(...)` to configure the deformation source and influence data.
- Switches to a dedicated mesh-deform vertex shader variant when `DeformMeshRenderer` plus influence data are present.

This is already a mesh-to-mesh rigging system in the narrow sense.

What it is not:

- not a compute-shader deformation pipeline,
- not a softbody simulation,
- not local-frame or cluster-transform skinning,
- not shape matching,
- not XPBD,
- not volume preserving,
- not tested by dedicated rendering tests in the current unit-test suite.

#### 2. Compute-shader skinning infrastructure already exists

The engine already supports compute-shader skinning and blendshape prepasses.

Relevant code:

- [XRENGINE/Rendering/Compute/SkinningPrepassDispatcher.cs](../../../XRENGINE/Rendering/Compute/SkinningPrepassDispatcher.cs)
- [XRENGINE/Rendering/Compute/SkinnedMeshBoundsCalculator.cs](../../../XRENGINE/Rendering/Compute/SkinnedMeshBoundsCalculator.cs)
- [Build/CommonAssets/Shaders/Compute/Animation](../../../Build/CommonAssets/Shaders/Compute/Animation)

This matters because a softbody solution should reuse the same render-thread compute orchestration pattern:

- persistent GPU buffers,
- one-time-per-frame dispatch guarding,
- output buffers for deformed positions, normals, and tangents,
- render-path consumption of compute-generated vertex data.

#### 3. GPU particle-chain physics already exists

The closest existing compute simulation is the GPU physics chain system.

Relevant code:

- [XRENGINE/Scene/Components/Physics/GPUPhysicsChainComponent.cs](../../../XRENGINE/Scene/Components/Physics/GPUPhysicsChainComponent.cs)
- [XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs](../../../XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs)
- [Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChain.comp](../../../Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChain.comp)
- [Build/CommonAssets/Shaders/Compute/PhysicsChain/SkipUpdateParticles.comp](../../../Build/CommonAssets/Shaders/Compute/PhysicsChain/SkipUpdateParticles.comp)
- [XREngine.UnitTests/Physics/PhysicsChainComputeIntegrationTests.cs](../../../XREngine.UnitTests/Physics/PhysicsChainComputeIntegrationTests.cs)
- [XREngine.UnitTests/Physics/GPUPhysicsChainDispatcherTests.cs](../../../XREngine.UnitTests/Physics/GPUPhysicsChainDispatcherTests.cs)
- [XREngine.UnitTests/Physics/GPUPhysicsChainComponentTests.cs](../../../XREngine.UnitTests/Physics/GPUPhysicsChainComponentTests.cs)

What it already proves:

- the engine can dispatch compute workloads from the render thread,
- the engine already batches multiple simulation instances into combined GPU buffers,
- per-particle state, Verlet-style motion, and collider processing are already established patterns,
- capsule collider support already exists in compute form,
- the project already accepts GPU simulation with async readback when needed.

What it does not provide:

- no volumetric softbody,
- no overlapping clusters,
- no shape matching or cluster transforms,
- no render-mesh skinning from simulated points or clusters,
- no zero-readback render-only deformation path.

#### 4. PhysX softbody-related bindings exist, but no engine-level feature is wired up

Relevant code:

- [XRENGINE/Scene/Physics/Physx/PhysxTetrahedronMesh.cs](../../../XRENGINE/Scene/Physics/Physx/PhysxTetrahedronMesh.cs)
- [XRENGINE/Scene/Physics/Physx/PhysxScene.cs](../../../XRENGINE/Scene/Physics/Physx/PhysxScene.cs)
- [XRENGINE/Scene/Physics/Physx/Geometry/IPhysicsGeometry.cs](../../../XRENGINE/Scene/Physics/Physx/Geometry/IPhysicsGeometry.cs)

The repository exposes tetrahedron mesh access and low-level PhysX softbody copy/apply hooks, but there is no discovered `SoftBodyComponent` or engine-facing authoring/runtime wrapper on top of those bindings.

This means the repo has low-level hooks that could become a future backend, but the current engine feature surface is still effectively custom-solver territory.

#### 5. Jolt has no softbody path in the current repo binding

Jolt is present as a rigid-body and character-controller backend. The geometry conversion layer in [XRENGINE/Scene/Physics/Physx/Geometry/IPhysicsGeometry.cs](../../../XRENGINE/Scene/Physics/Physx/Geometry/IPhysicsGeometry.cs) falls back to dummy shapes for both `TetrahedronMesh` and `ParticleSystem` geometry types when converting to Jolt, confirming that no softbody or deformable-body API is available through the current Jolt integration.

### Existing code that is especially relevant to the requested direction

#### Mesh-to-mesh rigging status

The request explicitly asked whether the repo already started a mesh-to-mesh rigging system rather than ordinary bone skinning.

The answer is yes.

The best evidence is:

- [XRENGINE/Rendering/Generator/MeshDeformVertexShaderGenerator.cs](../../../XRENGINE/Rendering/Generator/MeshDeformVertexShaderGenerator.cs)
- [XRENGINE/Rendering/XRMeshRenderer.cs](../../../XRENGINE/Rendering/XRMeshRenderer.cs)

It is already implemented as a renderable path with:

- deformer source mesh,
- per-vertex influence lists,
- rest-position buffers,
- live deformer-position updates.

However, it appears unfinished or at least not fully hardened:

- No dedicated rendering tests were found for this feature in [XREngine.UnitTests/Rendering/XRMeshRendererTests.cs](../../../XREngine.UnitTests/Rendering/XRMeshRendererTests.cs) or elsewhere in the unit-test tree.
- The current algorithm is weighted displacement skinning, which the earlier proposal explicitly identified as a good prototype stage but not the final robust solution.
- In [XRENGINE/Rendering/XRMeshRenderer.cs](../../../XRENGINE/Rendering/XRMeshRenderer.cs), `UpdateDeformerPositions()` detects when the source renderer already has `SkinnedPositionsBuffer`, but it only marks the deform path dirty and does not copy or alias those skinned positions into the deformation input buffer. That strongly suggests the compute-skinned-source path is incomplete.

### Compute shader match assessment

#### Direct matches

No existing compute shader was found that directly implements the requested softbody design:

- no XPBD softbody solver,
- no cluster shape matching solver,
- no soft-cluster transform builder,
- no render-vertex compute skinning from simulated clusters,
- no softbody-specific capsule-collision compute family.

#### Closest matches

The closest existing compute shader work is:

1. `PhysicsChain.comp`
   - particle-based
   - Verlet-style motion
   - capsule collider handling
   - GPU dispatch and batching
   - but chain-oriented rather than softbody-oriented

2. `SkinningPrepass.comp` / `SkinningPrepassInterleaved.comp`
   - compute-generated deformed vertex output
   - render-path integration
   - but bone/blendshape oriented rather than simulated-point oriented

3. The mesh-deform vertex path
   - conceptually matches mesh-to-mesh rigging
   - but currently uses vertex-stage delta deformation rather than compute-stage cluster skinning

## Recommended architecture

Use a hybrid architecture:

1. simulation mesh or particle cage on the GPU,
2. overlapping soft clusters built over that simulation mesh,
3. cluster transforms reconstructed each frame from the simulated state,
4. a high-detail render mesh skinned to those cluster transforms,
5. capsule colliders used as kinematic drivers and collision primitives.

This should be treated as:

- a softbody simulation system for the coarse representation,
- a skinning system for the detailed representation,
- and a mesh-to-mesh rigging system for authoring and binding.

## Why custom GPU over physics-engine softbodies

The repo already has two physics backends — PhysX and Jolt — but neither should be the primary softbody path for this system.

### PhysX

PhysX has a CPU-side softbody solver backed by tetrahedral meshes. The repo already exposes the low-level hooks (`CopySoftBodyData`, `ApplySoftBodyData`, `PhysxTetrahedronMesh`), but no engine-level component, authoring path, or runtime wrapper exists on top of them.

PhysX softbodies are a better fit when:

- the primary product is physical simulation correctness,
- CPU-visible simulation state is the main consumer,
- tet-mesh-quality deformation is required for gameplay.

They are a poor fit here because:

- this system is render-driven: the main consumer is the GPU render mesh, not CPU gameplay,
- CPU-to-GPU data round-tripping would be required to feed the deformation pipeline,
- the engine already has GPU compute dispatch, GPU particle simulation, and compute-skinning infrastructure that a custom solver can build on directly,
- the custom path avoids the PhysX native dependency for softbody-only use cases.

### Jolt

Jolt is present in the repo as a rigid-body and character-controller backend. As of the current JoltPhysicsSharp binding, there is no softbody API exposed in the engine. The geometry conversion layer falls back to dummy shapes for tetrahedron and particle-system geometry types, confirming that Jolt softbody is not available here.

### Recommendation

Use a custom GPU solver as the primary softbody path. Keep PhysX and Jolt for rigid collision, scene queries, and kinematic capsule sources. Treat physics-engine softbody as a possible future backend experiment once the custom GPU path is stable and the integration cost can be evaluated against a working baseline.

## Why this is preferable to pure point-displacement skinning

The current mesh-deform path is based on weighted displacement from rest positions. That is a useful starting point but has known limitations:

- poor rotational behavior,
- weak volume preservation,
- unstable normal handling under large deformation,
- limited correspondence with familiar skinning workflows.

Cluster-transform skinning keeps the strengths of classic skinning while allowing the transforms to come from simulation rather than bones.

## Proposed runtime model

### 1. Simulation representation

The simulation layer should use a low- to medium-resolution proxy rather than simulating the final render mesh directly.

Recommended representations, in order of practicality:

1. coarse particle graph,
2. surface-plus-interior sampled particles,
3. tetrahedral simulation mesh if authoring tools are ready.

Each simulation particle should store at minimum:

- current position,
- previous position,
- velocity or implicit Verlet motion state,
- inverse mass,
- rest position,
- particle radius,
- cluster membership range.

### 2. Constraint model

Recommended initial constraint mix:

- distance constraints for local structure,
- optional bend constraints for surface behavior,
- cluster shape matching for stability and volume retention,
- optional attachment constraints to kinematic sources,
- collider constraints for capsule interaction.

For first implementation, prefer:

- Verlet or PBD-style integration,
- cluster shape matching over tetrahedral FEM,
- graph-colored or Jacobi-style solve phases.

### 3. Collision model

Capsule colliders should be first-class runtime inputs.

They should support:

- penetration projection,
- moving-collider response,
- tangential drag/friction,
- optional region-specific collision masks.

This is already conceptually aligned with the existing physics-chain compute system, so the solver structure can reuse that operational pattern even if the data model changes.

### 4. Render deformation model

The render mesh should not be driven directly by particles unless the mesh is very coarse.

Recommended deformation model:

- bind each render vertex to 4 to 8 soft clusters,
- store local-space rest offsets per cluster,
- store local-space rest normals per cluster,
- reconstruct cluster transforms from simulation each frame,
- blend cluster transforms exactly like skinned-mesh influences.

This is effectively soft-bone skinning.

#### Why cage normals help but are not sufficient alone

If a simulation proxy (cage mesh) outputs per-vertex positions and normals, the normals provide useful orientation hints — they stabilize shading transfer, help debug frame reconstruction, and add a surface-awareness axis that pure positions lack.

However, a normal defines only one axis. Rotation about that axis is undefined. This means positions plus normals alone do not produce a unique local frame and cannot stably skin a detailed mesh under twisting or shearing motion.

The correct hierarchy of driver richness is:

1. **Positions only** — crude displacement prototype; no rotational fidelity.
2. **Positions + normals** — better shading and attachment hints, but still ambiguous under twist.
3. **Positions + full local frame** (normal + tangent + bitangent) — unambiguous per-point orientation, usable for point-based skinning.
4. **Cluster transforms** (translation + rotation from shape-matching solve) — the recommended target for this system. Each cluster derives a full rotation from the collective motion of its member particles, so no per-vertex frame reconstruction is needed on the cage.

Cage normals should still be stored as part of the simulation proxy data model. They are useful for:

- surface-aware attachment and collision response,
- shading-quality transfer when a cluster-free fast path is desired,
- debug visualization and authoring inspection.

But the primary render-skinning pipeline should consume cluster transforms, not raw cage normals.

## Engine-specific implementation plan

### Phase 1: Establish a dedicated softbody compute subsystem

Add a dedicated subsystem parallel to the existing compute animation and physics-chain systems.

Suggested structure:

- `XRENGINE/Rendering/Compute/GpuSoftbodyDispatcher.cs`
- `Build/CommonAssets/Shaders/Compute/Softbody/Integrate.comp`
- `Build/CommonAssets/Shaders/Compute/Softbody/CollideCapsules.comp`
- `Build/CommonAssets/Shaders/Compute/Softbody/SolveDistance.comp`
- `Build/CommonAssets/Shaders/Compute/Softbody/SolveShapeMatching.comp`
- `Build/CommonAssets/Shaders/Compute/Softbody/BuildClusterTransforms.comp`
- `Build/CommonAssets/Shaders/Compute/Softbody/SkinRenderMesh.comp`

Responsibility split:

- scene/component layer owns authoring data, lifecycle, and binding metadata,
- compute dispatcher owns GPU buffers and frame scheduling,
- render path consumes final deformed buffers with no CPU readback on the main path.

### Phase 2: Reuse existing compute-skinning conventions

The design should mirror the shape of [XRENGINE/Rendering/Compute/SkinningPrepassDispatcher.cs](../../../XRENGINE/Rendering/Compute/SkinningPrepassDispatcher.cs):

- one output position buffer,
- one output normal buffer,
- one output tangent buffer when needed,
- frame guard to prevent redundant dispatch,
- buffer reuse across frames,
- render-path fallback when compute is disabled.

This keeps the rendering integration consistent with current engine practices.

### Phase 3: Replace the current mesh-deform runtime path with a more general binding model

The current mesh-deform path is a useful prototype but should become a more general deformation binding layer.

Recommended evolution:

1. keep `SetupMeshDeformation(...)`-style API ideas,
2. abstract influence binding away from raw deformer vertices,
3. support driver type:
   - deformer vertices,
   - simulated particles,
   - soft clusters,
4. add explicit bind modes:
   - displacement-only,
   - local-frame point skinning,
   - cluster transform skinning.

This preserves existing work while making the final system suitable for softbody deformation.

### Phase 4: Avoid CPU readback on the primary rendering path

The final softbody design should not depend on GPU-to-CPU readback for visible mesh deformation.

CPU readback should be optional for:

- gameplay queries,
- editor diagnostics,
- offline capture,
- collision proxy sync only when explicitly required.

The render mesh should consume GPU-resident output buffers directly.

## Proposed data model

### Simulation particle

Suggested GPU struct shape:

```csharp
public struct SoftParticle
{
    public Vector3 Position;
    public float InvMass;

    public Vector3 PrevPosition;
    public float Radius;

    public Vector3 RestPosition;
    public float ClusterOffset;

    public Vector3 Velocity;
    public float ClusterCount;
}
```

The exact layout can move to structure-of-arrays later for bandwidth reasons.

### Cluster data

Suggested cluster metadata:

```csharp
public struct SoftCluster
{
    public int MemberStart;
    public int MemberCount;
    public float Stiffness;
    public float Padding;
}

public struct SoftClusterMember
{
    public int ParticleIndex;
    public float Weight;
    public Vector3 RestLocal;
}
```

### Render binding data

Suggested render binding shape:

```csharp
public struct SoftSkinnedVertex
{
    public Vector3 RestPosition;
    public Vector3 RestNormal;

    public Vector4 Weights;
    public Vector4Int Indices;

    public Vector3 LocalPos0;
    public Vector3 LocalPos1;
    public Vector3 LocalPos2;
    public Vector3 LocalPos3;

    public Vector3 LocalNrm0;
    public Vector3 LocalNrm1;
    public Vector3 LocalNrm2;
    public Vector3 LocalNrm3;
}
```

This intentionally mirrors the shape of bone-skinning bind data so the renderer can stay familiar.

## Frame pipeline

Recommended GPU frame sequence:

1. integrate particles,
2. apply external forces,
3. solve capsule collisions,
4. solve distance constraints,
5. solve cluster shape matching,
6. finalize particle state,
7. build cluster transforms,
8. skin render mesh into output buffers,
9. render from those output buffers.

The current `GPUPhysicsChainDispatcher` proves the repo already has a pattern for centralized multi-instance batched compute execution. That pattern should be extended rather than replaced.

## Authoring pipeline

The system needs explicit offline or import-time preprocessing.

Minimum required authoring outputs:

1. simulation proxy mesh or sampled particles,
2. particle neighbor links or graph edges,
3. cluster definitions,
4. render vertex to cluster weights,
5. rest local offsets and rest local normals.

Recommended initial bind heuristic:

- choose nearest clusters in rest pose,
- compute inverse-distance weights,
- normalize,
- store local offsets in cluster rest frames.

This is enough to get the first version working before better geodesic or cage-aware weights are added.

## Risks and known gaps

### 1. Current mesh-deform implementation is only a prototype for the final deformation model

It should be treated as prior work, not final architecture.

### 2. Compute-skinned deformer input path appears incomplete

The current runtime detects a compute-skinned source renderer but does not appear to wire its output into the downstream deformation buffer path. That should be fixed even if the broader softbody project is deferred, because it is a correctness gap in the existing mesh-to-mesh feature.

### 3. No dedicated tests were found for mesh-to-mesh deformation

The repo already tests physics-chain compute compilation and dispatcher lifecycle, but no equivalent focused tests were found for the mesh-driven deformation path. A future implementation should add:

- setup/buffer population tests,
- deformer-update propagation tests,
- compute-skinned-source compatibility tests,
- normal/tangent deformation tests,
- render-path validation tests.

### 4. PhysX softbody hooks are not yet a usable product feature

The presence of low-level bindings should not be mistaken for a shipped engine system.

## Recommended phased delivery

### Milestone 1: Harden the existing mesh-to-mesh rigging path

- add tests,
- fix compute-skinned-source propagation,
- document authoring expectations,
- verify normals and tangents under animation.

### Milestone 2: Introduce softbody particles plus capsule collisions

- reuse physics-chain dispatcher conventions,
- keep output GPU-resident,
- add debug draw and validation tests.

### Milestone 3: Introduce shape-matching clusters

- cluster solve,
- cluster transform output,
- cluster debug visualization.

### Milestone 4: Replace displacement-only deformation with cluster skinning

- bind render mesh to soft clusters,
- compute deformed positions and normals in compute,
- integrate with existing compute-skinning consumption path.

### Milestone 5: Add authoring and editor tooling

- proxy generation,
- binding inspection,
- region masks for stiffness and damping,
- collider authoring and preview.

## Bottom line

The repository does not already contain a compute-shader softbody system that matches the requested XPBD or shape-matching design.

However, it already contains most of the surrounding infrastructure needed to build one cleanly:

- compute-shader dispatch and render integration,
- GPU particle-chain simulation with capsule colliders,
- compute-shader skinning output buffers,
- a started mesh-to-mesh rigging path that already bypasses traditional bone transforms.

The most practical design for XRENGINE is therefore:

- keep the current mesh-to-mesh deformation work as the prototype lineage,
- harden it,
- then evolve it into GPU soft-cluster skinning driven by a coarse compute-simulated particle or tetra proxy.