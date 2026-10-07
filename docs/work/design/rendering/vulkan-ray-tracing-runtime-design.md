# Vulkan Ray-Tracing Runtime Design

Status: Deferred

The Vulkan backend does not currently expose a ray-tracing runtime. The former
`Features/Raytracing/VulkanRenderer.Raytracing.cs` file was excluded from
compilation and contained only stale, incomplete pseudocode. It was removed so
compiled-source organization represents implemented behavior.

## Intended Ownership

A future implementation should be introduced as explicit runtime owners rather
than another `VulkanRenderer` partial:

- a device capability owner for
  `VK_KHR_acceleration_structure`, `VK_KHR_ray_tracing_pipeline`, buffer device
  addresses, and required feature/extension chains;
- a resource owner for bottom-level and top-level acceleration structures,
  their backing allocations, build scratch storage, and deferred destruction;
- a command owner for build, update, compaction, and trace dispatch recording;
- a pipeline owner for ray-tracing pipeline layouts, shader groups, and shader
  binding tables;
- render-graph integration that declares acceleration-structure and trace
  resource dependencies explicitly.

All native objects must use the existing allocator, command-buffer resource
tracking, submission lifetime, and device-loss diagnostics systems. The
implementation must remain inside `XREngine.Runtime.Rendering.Vulkan.dll` and
must not leak Vulkan handles or leaf-assembly types through stable runtime
contracts.

## Preconditions

Implementation should begin only after device capabilities and resource
lifetime have explicit subsystem owners. It must include feature probing,
deterministic unsupported diagnostics, allocation-free steady-state command
recording, and focused tests for resource retirement and device recreation.

## ReSTIR Backend Contract

ReSTIR keeps two backend families. The OpenGL bridge (`RestirGI.Native`,
`GL_NV_ray_tracing`, behind `IRestirRayTracingBackend`) stays as a legacy
backend. The Vulkan path uses KHR acceleration structures, ray-query compute,
and KHR ray tracing pipelines. It never calls the OpenGL bridge.

Backend selection is explicit and reports a reason when a backend is not
available:

| Backend | Use |
|---|---|
| `Auto` | Selects Vulkan only when the active renderer is Vulkan and all required features are enabled. |
| `ComputeOnly` | The non-hardware compute path. |
| `OpenGLNativeBridge` | The legacy bridge, only when the active renderer makes it meaningful. |
| `VulkanRayQuery` | Compute shaders with `rayQueryEXT` for visibility rays and smoke tests. |
| `VulkanRayTracingPipeline` | Raygen, miss, and hit shaders with an SBT for material-bearing radiance-cache updates. |

A requested backend that is not available reports the missing extension,
feature, shader, descriptor, or scene resource. It does not fall back silently
to CPU tracing or to the OpenGL bridge. The GPUScene compute BVH
(`VPRC_BuildAccelerationStructure`) is not a hardware acceleration structure;
Vulkan BLAS and TLAS objects are separate.

## Reference Architecture

The [diharaw/hybrid-rendering](https://github.com/diharaw/hybrid-rendering)
sample (commit `090360e`) informs the design. Borrow the architecture, not the
code or its descriptor-size constants.

- One scene descriptor set holds the material table, instance table, TLAS,
  vertex-buffer array, index-buffer array, submesh and material-index array, and
  bindless texture array. This matches the GPUScene and material-table direction.
- Visibility-only work (AO, shadows) uses ray-query compute and writes compact
  results for later denoising.
- Material-bearing work (reflections, GI) uses RT pipelines with SBTs and
  `maxPipelineRayRecursionDepth` 1, followed by compute passes for
  accumulation, probe update, denoise, and screen sampling.
- The radiance cache is a probe grid: trace into `raysPerProbe x totalProbes`
  radiance and direction-depth images, update ping-pong irradiance and depth
  octahedral atlases, copy borders, then sample the grid from the G-buffer. A
  probe grid is one candidate; ReSTIR reservoirs stay.
- Denoising uses reprojection, moments and variance, tile lists, indirect
  dispatch, A-trous filtering, upsampling, reduced-resolution tracing,
  blue-noise sampling, and explicit history reset.
- The sample recreates resources with `wait_idle`. XRENGINE uses fence-retired
  resource lifetime instead.
