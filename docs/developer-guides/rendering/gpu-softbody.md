# GPU Softbody

This guide describes the GPU softbody runtime and the mesh-to-mesh deformation path. Design: [GPU Softbody Mesh Rigging Plan](../../work/design/rendering/gpu/gpu-softbody-mesh-rigging-plan.md). Open work: [GPU Softbody Mesh Rigging TODO](../../work/todo/rendering/gpu/gpu-softbody-mesh-rigging-todo.md).

## Mesh-To-Mesh Deformation

- `XRMeshRenderer.SetupMeshDeformation(...)` deforms a render mesh from another mesh with per-vertex influence weights, deformer rest positions, and live deformer position buffers.
- `MeshDeformVertexShaderGenerator` generates the vertex shader. Deformation is a weighted displacement from deformer rest positions. It is not local-frame or cluster-transform skinning.
- Influences pack into `vec4` attributes when the count allows, and into an SSBO otherwise.
- A compute-skinned deformer feeds its separate position, normal, and tangent outputs directly. Interleaved compute output goes through a CPU-visible copy. Direct GPU aliasing of interleaved output is not implemented.
- The editor shows diagnostics when deformer and source vertex counts do not match, and an inspector summary of the mesh-deform state.

## Softbody Runtime

- `GPUSoftbodyComponent` owns one softbody. It holds particles, distance constraints, clusters, cluster members, capsule colliders, and render bindings, plus `Gravity`, `ExternalForce`, damping, collider margin, and `DebugDrawEnabled`. `SubmitCurrentFrameData()` submits the lists to the dispatcher.
- `GPUSoftbodyDispatcher` batches all registered softbodies into shared buffers and dispatches once per frame. It clamps substeps to 8 per frame and solver iterations to 16 per substep, and uses the largest requested counts across instances.
- The data layouts are in `GPUSoftbodyTypes.cs` (`GPUSoftbodyParticleData`, `GPUSoftbodyDistanceConstraintData`, `GPUSoftbodyClusterData`, `GPUSoftbodyClusterMemberData`, `GPUSoftbodyColliderData`, `GPUSoftbodyRenderBindingData`, `GPUSoftbodyDispatchData`).
- Passes per substep, in order (shaders under `Build/CommonAssets/Shaders/Compute/Softbody/`):
  1. `Integrate.comp`: position-based integration from current and previous positions, with damping, gravity, and external force.
  2. `CollideCapsules.comp` (only when colliders exist): closest-point-on-segment push-out against capsules, with moving-collider displacement, tangential drag, and friction.
  3. `SolveDistance.comp` (only when constraints exist): distance constraints with stiffness controls, once per solver iteration, with a particle buffer swap after each iteration.
  4. When clusters exist: `Finalize.comp` computes each cluster transform (center of mass and best-fit rotation from the member covariance), then `ApplyClusterShapeMatching.comp` moves members toward their goal positions (cluster position plus rotated rest offset). Clusters can overlap.
- After the last substep, `Finalize.comp` runs once more to publish the final cluster transforms.
- `TryGetClusterTransformOutput(...)` returns the GPU buffer, offset, and count of one softbody's cluster transforms. `GPUSoftbodyClusterMath` holds the CPU reference math.
- Diagnostics: per-frame totals, invalid-data counts (`LastInvalid*Count`), dispatched substeps and iterations, and CPU dispatch time. Debug drawing shows particles, links, contacts, and cluster frames.

## Limits

- Render bindings (`GPUSoftbodyRenderBindingData`: vertex, cluster, weight, local position, local normal) upload to the GPU, but no shader skins a render mesh from cluster transforms yet.
- No tool generates simulation proxies, clusters, or render bindings. Callers fill the lists in code.
- PhysX and Jolt softbody solvers are not used.

## Tests

`GPUSoftbodyComponentTests`, `GPUSoftbodyDispatcherTests`, and the `XRMeshRenderer` mesh-deform tests cover influence packing, compute-skinned source propagation, softbody compute shader compilation, particle motion, capsule push-out, solver stability, cluster packing, rotation recovery, and overlapping clusters.
