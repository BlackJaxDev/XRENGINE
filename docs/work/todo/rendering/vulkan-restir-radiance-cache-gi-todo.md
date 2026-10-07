# Vulkan ReSTIR Radiance Cache GI TODO

Last Updated: 2026-10-06
Status: Planned
Architecture: [ReSTIR GI guide](../../../developer-guides/gi/restir-gi.md), [Global Illumination Ownership And Selection](../../../architecture/rendering/global-illumination-ownership.md)  Design: [Vulkan Ray-Tracing Runtime Design](../../design/rendering/vulkan-ray-tracing-runtime-design.md)
Validation: [Global Illumination Validation](../../testing/rendering/global-illumination-validation.md#restir-gi)

## Current State

Vulkan has only capability probing. `VulkanDeviceContext.LogicalDeviceBootstrap` queries acceleration-structure, ray-tracing-pipeline, and ray-query features, and `VulkanDeviceCapabilityReporter` reports "Vulkan ray tracing backend is not implemented yet". No BLAS, TLAS, RT pipeline, SBT, or acceleration-structure descriptor code exists. The old `VulkanRenderer.Raytracing.cs` scaffolding was removed. `EShaderType` has no ray shader stages. `IRestirRayTracingBackend` and `RestirRayTracingBackendServices` hold one registered backend; the only implementation is `OpenGlRestirRayTracingBackend` (`RestirGI.Native`, `GL_NV_ray_tracing`). `VPRC_ReSTIRPass` owns reservoir buffers, an authored-id ray dispatch, an opt-in compute fallback (`AllowComputeFallback`), and composite into `RestirGITexture`. `PathTracing` is an unavailable descriptor, so no host schedules the pass. `Compute/GI/RESTIR/InitialSampling.comp` uses `GL_NV_ray_tracing`. No backend selection enum exists.

## Open Code Items

### Backend Selection

- [ ] Add a ReSTIR backend enum (`Auto`, `ComputeOnly`, `OpenGLNativeBridge`, `VulkanRayQuery`, `VulkanRayTracingPipeline`) with explicit unavailable reasons, per the [backend contract](../../design/rendering/vulkan-ray-tracing-runtime-design.md#restir-backend-contract). `RayTracingSettings`, `RestirRayTracingBackendServices`, `VPRC_ReSTIRPass`. Done when: a requested unavailable backend logs the missing extension, feature, shader, descriptor, or scene resource, and the OpenGL default is unchanged.

### Vulkan Capability

- [ ] Enable the KHR ray tracing device extensions and feature chain when supported: `VK_KHR_acceleration_structure`, `VK_KHR_ray_tracing_pipeline`, `VK_KHR_ray_query`, `VK_KHR_deferred_host_operations`, buffer device address, descriptor indexing, `VK_KHR_spirv_1_4`, `VK_KHR_shader_float_controls`. `VK_KHR_pipeline_library` stays optional. Done when: the enabled-extension list and feature chain include them on supported hardware.
- [ ] Query acceleration-structure and RT pipeline properties (scratch alignment, shader group handle size and base alignment, max recursion depth, SBT alignment) and load the `KhrAccelerationStructure`, `KhrRayTracingPipeline`, and `KhrDeferredHostOperations` dispatch handles. Done when: a capability snapshot exposes these values and the enabled-versus-requested state.

### Acceleration Structures

- [ ] Add BLAS and TLAS resource owners with storage, scratch, and instance buffers, build sizes, compaction-size queries, device addresses, debug names, correct buffer usage bits, per-resource build flags (`PreferFastTrace`, `AllowUpdate`, `AllowCompaction` only with compaction), pooled scratch, build barriers, debug labels, and fence-based retirement. Done when: a BLAS and TLAS build records with no `DeviceWaitIdle` in the steady state.
- [ ] Add a Vulkan RT scene owner with one scene descriptor layout (material table, instance table, TLAS, vertex and index buffer arrays, submesh and material ranges, texture array) using descriptor indexing and `nonuniformEXT`. Done when: ray-query compute and closest-hit shaders bind the same layout.
- [ ] Map GPUScene meshes to BLAS inputs (static opaque and masked triangles first), map `instanceCustomIndex` to GPUScene draw, mesh, material, and transform IDs, include material flags for GI, and add invalidation for mesh upload, material, transform, visibility, and scene unload. Skip unsupported geometry with a diagnostic. Done when: instance custom indices resolve to GPUScene records.

### Ray Query

- [ ] Add `DescriptorType.AccelerationStructureKhr` support in command-scoped descriptor binding, shared `QueryVisibility` and `QueryDistance` helpers, a ray-query smoke compute shader that writes hit distance or instance ID, and an AO or shadow-style visibility pass at full, half, or quarter resolution. Done when: the passes dispatch from a Vulkan-only path with no per-frame allocation and no SBT.

### RT Pipelines

- [ ] Add ray shader stages to `EShaderType`, `.rgen`, `.rmiss`, `.rchit`, `.rahit`, `.rint`, `.rcall` resolution, cross-compilation, and Vulkan stage mapping. Done when: the shaders compile to SPIR-V through the normal path.
- [ ] Create RT pipelines through `vkCreateRayTracingPipelinesKHR` with shader groups, an SBT buffer (usage, device address, alignment, debug labels), and recursion depth 1. Replace the numeric authored pipeline and SBT IDs of `VPRC_DispatchRays` and `VPRC_ReSTIRPass` with renderer-owned handles. Done when: a raygen, miss, and closest-hit smoke pipeline writes material ID, emission, albedo, normal, or distance to a debug image.

### Vulkan ReSTIR Sampling

- [ ] Add a Vulkan initial-sampling path (ray query for visibility, RT pipeline for material candidates) that reads depth, normal, base color, material ID, roughness, motion vectors, camera data, TLAS, materials, lights, transforms, and blue noise. Done when: `InitialSampling.comp` has a Vulkan variant without `GL_NV_ray_tracing`.
- [ ] Extend reservoirs with sample kind, target or cache ID, PDF, radiance, visibility, depth, normal and material compatibility, and seed state. Keep direct-light and cache candidates as separate kinds and validate both with TLAS visibility rays. Done when: the reservoir struct and shaders carry these fields.

### Radiance Cache

- [ ] Add a persistent probe-grid radiance cache (grid start, step, counts, rays per probe, octahedral sizes, normal bias, max distance, hysteresis, visibility toggle, energy preservation) with `raysPerProbe x totalProbes` trace images and ping-pong irradiance and depth atlases with borders. Done when: the resources are owned by a provider and allocate once.
- [ ] Add the cache update passes: RT raygen per `(rayId, probeId)`, closest-hit and miss shading, compute gather into irradiance and depth moments with hysteresis and first-frame reset, and border copy. Done when: the passes run in order with declared graph accesses.
- [ ] Add screen sampling of the cache into `RestirGITexture` (trilinear blend, backface weight, moment visibility, normal bias) and cache candidate generation for reservoirs. Done when: reservoirs can select cache candidates.
- [ ] Add cache invalidation for scene, transform, material, lighting, and grid-setting changes, and cache debug views (occupancy, age, radiance, depth moments, visibility weight, invalid entries, borders, budget). Done when: an edit invalidates the affected probes and the views are selectable.

### Temporal, Spatial, And Denoise

- [ ] Split reservoir history into ping-pong buffers, reproject with motion vectors and depth, reject on disocclusion, normal, material, and depth mismatch, add moments and variance, and clamp the neighborhood. Done when: history uses explicit ping-pong buffers with rejection reasons.
- [ ] Extend spatial reuse beyond the four-neighbor prototype, add tile lists with indirect dispatch for denoise and copy, an edge-stopping A-trous filter, and reduced-resolution tracing with depth- and normal-aware upsampling. Done when: denoise dispatch size comes from the tile list.
- [ ] Add debug views for reservoir age, weight, sample distance, rejection reason, visibility, tile list, variance, and filter iteration. Done when: each view is selectable.

### Integration

- [ ] Register ReSTIR as a GI module (unsupported until validated) that composites through `RestirGITexture` and `RestirCompositeFBO`, with a stereo policy (per-eye reservoirs and queries, shared cache, eye-specific reprojection). Done when: the module contributes the passes through the neutral host contract.
- [ ] Add quality settings (backend, ray scale, rays per pixel, rays per probe, probe distance, cache budget, spatial radius, history length, denoise iterations, debug mode) and diagnostics (backend, resolution scale, TLAS instance and BLAS counts, rays dispatched, cache probes, reservoirs, denoise tiles, fallback reason). Done when: the diagnostics show in the render stats panel.
- [ ] Update the ReSTIR guide with settings, diagnostics, hardware requirements, backend forcing, and limits. Done when: the guide documents each backend mode.

### Tests (Owner Clearance Required)

- [ ] Add tests for Vulkan RT capability classification, backend selection and diagnostics, ray-query and ray-stage shader compilation, acceleration-structure descriptor layouts, SBT size and alignment, and proof that the Vulkan path never calls `RestirGI.Native`. Keep the OpenGL bridge interop smoke tests. Done when: each behavior has a deterministic test.

## Decisions Needed

- [ ] Choose the policy for dynamic and skinned meshes in BLAS (skip with diagnostics, rebuild, refit, or trace packed skinned output). Owner: rendering lead.
- [ ] Choose the initial cache indexing (scene-bounds grid, camera-centered grid, hashed cells, clipmap, or surfel pool). Owner: rendering lead.
- [ ] Decide how ReSTIR GI feeds TSR and TAA history. Owner: rendering lead.

## Out Of Scope

- Removal of `RestirGI.Native`, `RestirGI.Native.cpp`, or the OpenGL `GL_NV_ray_tracing` bridge.
- Blocking OpenGL ReSTIR experiments on Vulkan RT availability.

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/rendering/vulkan-restir-radiance-cache-gi-todo.md`

- [ ] Add RT pipeline creation through `vkCreateRayTracingPipelinesKHR`.
