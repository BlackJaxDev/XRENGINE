# GPU Deformation Validation

[Work docs index](../../README.md) · [Testing docs](../README.md)

## Scope

This document owns validation for GPU mesh deformation. It covers skinning palette efficiency, blendshape compression and upload policy, GPU-driven animation, and GPU softbody deformation. It does not own code backlog.

Architecture links: [Skinning](../../../developer-guides/rendering/skinning.md), [Blendshaping](../../../developer-guides/rendering/blendshaping.md), [GPU Softbody](../../../developer-guides/rendering/gpu-softbody.md), [Mesh submission strategies](../../../architecture/rendering/mesh-submission-strategies.md). Design links: [GPU-driven animation design](../../design/rendering/gpu/gpu-driven-animation.md), [GPU softbody design](../../design/rendering/gpu/gpu-softbody-mesh-rigging-plan.md).

Code todo links: [Skinning GPU efficiency](../../todo/rendering/gpu/skinning-gpu-efficiency-followups-todo.md), [Blendshape compression and GPU efficiency](../../todo/rendering/gpu/blendshape-compression-and-gpu-efficiency-todo.md), [GPU-driven animation](../../todo/rendering/gpu/gpu-driven-animation-todo.md), [GPU softbody mesh rigging](../../todo/rendering/gpu/gpu-softbody-mesh-rigging-todo.md).

## Setup

Use the `Build-Editor` task before editor validation. Use `Editor (Default World)`, `Editor (Renderer Development)`, or `Editor (Unit Testing World)` launch profiles for live editor checks. Use `Editor (Unit Testing World, Validation Layers)` when graphics validation layers must run. Relevant settings are `CalculateBlendshapesInComputeShader` and `EnableBlendshapePcaBasisCompression`. Relevant environment variables are `XRE_WORLD_MODE=UnitTesting`, `XRE_GL_DEBUG=1`, and `XRE_VULKAN_VALIDATION=1`.

Use targeted unit-test commands only when code changes require them. Do not use these commands as hardware or visual acceptance.

## Checks

### Skinning

Architecture: [Skinning](../../../developer-guides/rendering/skinning.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| FP16 palette candidate accuracy | Exercise identity, long chains, non-uniform scale, large translations, tiny bones, inverted scale, mirrored scale, and chain-driven palettes on the direct vertex path and the compute path. | FP16 output stays within the approved error gates, or the renderer keeps FP32 with a diagnostic. | Open | none |
| Reduced palette LOD | Run a skinned asset with a reduced `BoneRemap` tier on the direct vertex path and the compute path. | The lower tier deforms correctly and reports its error metric. | Open | none |
| Rigid fallback | Run a distant or crowd mesh with `SkinningLodTier.AllowRigidFallback`. | The renderer skips per-vertex skinning only for a valid rigid or near-rigid tier. | Open | none |
| Shared dispatch reuse | Render several instances with the same mesh and palette hash. | The profile records one compute skinning dispatch for matching instances and falls back on pose divergence or hash collision. | Open | none |

### Blendshapes

Architecture: [Blendshaping](../../../developer-guides/rendering/blendshaping.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Dense and sparse parity | Run the same animated weights on the dense path and sparse path, with `MaxBlendshapeAccumulation` on and off, on the direct vertex path and compute path. | Vertex output is bitwise-identical for the same weights. | Open | none |
| Quantized normal and tangent quality | Compare quantized position, normal, and tangent output against FP32 reference output after skinning. | Position error and normal angle stay within the selected profile threshold, or the mesh keeps uncompressed deltas. | Open | none |
| Basis compression quality | Sweep expression, viseme, face, body, clothing, and corrective shapes on an accepted basis-compressed mesh. | Visual diffs and reported maximum and average errors stay within the profile threshold. | Open | none |
| Static shape baking | Render static-baked and original meshes with dynamic and streamed shapes. | Static offsets do not double-apply. Remaining deltas use the rebased rest mesh. | Open | none |
| Streamed and dynamic upload policy | Toggle streamed-only and dynamic shapes while profiler counters are enabled. | Streamed-only frames upload the compact slice and do not rebuild the dynamic active list. | Open | none |

### GPU-driven animation

Architecture: none yet. Design: [GPU-driven animation design](../../design/rendering/gpu/gpu-driven-animation.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| GPU backend eligibility | Run clip and state-machine assets through the future eligibility report. | CPU-only targets, root motion, callbacks, IK, and reflected targets give explicit fallback reasons. | Open | none |
| CPU and GPU pose parity | Compare one-bone, two-bone, branching hierarchy, transitions, blend trees, blendshape weights, and animated uniforms against CPU reference output. | GPU output matches the approved tolerance and does not mutate shared CPU clip or state-machine data. | Open | none |
| Readback and staging audit | Inspect visible animation frames with diagnostics for readback, `PushSubData`, and CPU staging of GPU-originated data. | Visible animation does not wait on GPU readback. Diagnostics explain any fallback. | Open | none |
| Temporal palette pages | Exercise motion vectors or another temporal consumer on GPU animation output. | Current and previous pose or palette pages swap by render frame ID and stay valid. | Open | none |

### GPU softbody

Architecture: [GPU Softbody](../../../developer-guides/rendering/gpu-softbody.md). Design: [GPU softbody design](../../design/rendering/gpu/gpu-softbody-mesh-rigging-plan.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Mesh-to-mesh visible sanity | Open a narrow validation scene or harness that uses `XRMeshRenderer.SetupMeshDeformation(...)`. Exercise raw deformer data and compute-skinned deformer data. | The mesh deforms from the source, diagnostics show valid assumptions, and missing normals or tangents fail visibly. | Open | none |
| Softbody shader and host contract | Run targeted softbody compute shader compilation, dispatcher, component, particle, capsule, and solver tests. | The softbody compute family compiles, and host contracts pass. | Open | none |
| Particle and capsule runtime | Run a debug scene with particles, links, capsule contacts, damping, and moving colliders. | The simulation stays stable and debug drawing matches collider motion. | Open | none |
| Cluster transform runtime | Run an overlapping-cluster sample with debug frames enabled. | Cluster centers and orientations are stable and GPU-resident. | Open | none |
| Cluster render skinning | Run a high-detail render mesh bound to soft clusters after the render path lands. | The mesh skins from soft-cluster transforms without CPU readback. Bounds and culling stay correct. | Open | none |
| Authoring tooling smoke | Generate or author a proxy, clusters, and render bindings. Inspect particles, links, clusters, capsule colliders, and parameters in the editor. | A supported softbody rig can be created without bespoke code changes. | Open | none |
| Alternate backend review | Review PhysX, Jolt, tetrahedral proxies, volume constraints, and displacement-only regions after the custom GPU path is proven. | The review records whether each option is useful and does not block the custom GPU path. | Open | none |

## Hardware Matrix
| Feature | Backend or device | Required coverage | Last evidence |
|---|---|---|---|
| Skinning | OpenGL and Vulkan paths | Direct vertex and compute paths. | none |
| Blendshapes | OpenGL and Vulkan paths | Direct vertex and compute paths. | none |
| GPU-driven animation | GPU backend with compute support | Future backend parity and fallback checks. | none |
| GPU softbody | GPU backend with compute support | Simulation, cluster output, and future render skinning. | none |

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
