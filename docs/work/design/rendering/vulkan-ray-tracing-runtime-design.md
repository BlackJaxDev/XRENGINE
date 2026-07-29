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
