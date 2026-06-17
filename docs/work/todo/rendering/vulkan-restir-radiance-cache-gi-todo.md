# Vulkan ReSTIR Radiance Cache GI TODO

Last Updated: 2026-06-17
Owner: Rendering
Status: Planning
Target Branch: create in Phase 0

## Goal

Implement ReSTIR radiance-cached global illumination as a Vulkan-native path
while preserving the current OpenGL/NV native bridge as a legacy backend.

The Vulkan path should use hardware ray tracing through KHR acceleration
structures and ray query first. Full ray tracing pipelines and shader binding
tables can follow when material closest-hit shaders justify the added
complexity.

## Non-Goals

- Do not remove `RestirGI.Native.dll`, `RestirGI.Native.cpp`, or the existing
  OpenGL `GL_NV_ray_tracing` bridge during the Vulkan bring-up.
- Do not make the Vulkan path silently fall back to CPU tracing or the OpenGL
  bridge when Vulkan ray tracing is explicitly requested.
- Do not treat the existing compute BVH pass as a Vulkan hardware acceleration
  structure implementation.
- Do not block ordinary OpenGL ReSTIR experiments on Vulkan RT availability.

## Current Local Facts

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRaytracing.cs`
  is commented-out legacy scaffolding. Rebuild this subsystem in the current
  `XREngine.Rendering.Vulkan` style instead of uncommenting it.
- `PhysicalDevice.ProbeVulkanRayTracingSupport()` records ray tracing
  availability, but logical-device creation does not yet enable KHR ray
  tracing extensions, features, or extension dispatch handles.
- `RestirGI.cs` P/Invokes `RestirGI.Native.dll`; the native implementation is
  OpenGL `GL_NV_ray_tracing`, not Vulkan.
- `VPRC_ReSTIRPass` already owns the pipeline slot, reservoir buffers,
  compute fallback path, and composite handoff needed for a Vulkan backend.
- `VPRC_DispatchRays` is currently shaped around authored numeric pipeline/SBT
  identifiers and should not be the Vulkan API shape.
- `VPRC_BuildAccelerationStructure` builds the engine GPUScene compute BVH for
  culling and software traversal. Vulkan BLAS/TLAS objects must be separate.
- `EShaderType`, shader compilation, and Vulkan shader-stage mapping do not
  yet include raygen, miss, hit, intersection, or callable shader stages.
- The current ReSTIR shaders under
  `Build/CommonAssets/Shaders/Compute/GI/RESTIR/` are prototype compute
  shaders and `InitialSampling.comp` uses `GL_NV_ray_tracing` syntax.

## Backend Contract

Keep two explicit backend families:

- OpenGL legacy bridge: existing `RestirGI.Native` and `GL_NV_ray_tracing`
  entry points. This remains available for OpenGL/NV experiments and existing
  native interop smoke tests.
- Vulkan native path: new KHR acceleration structure and ray-query
  implementation owned by the Vulkan renderer. This path must not call the
  OpenGL bridge.

Backend selection should be explicit and diagnostic:

- `Auto` may select Vulkan only when the active renderer is Vulkan and all
  required Vulkan features are enabled.
- `OpenGLNativeBridge` may select the legacy bridge only when the active
  renderer/context makes that bridge meaningful.
- `VulkanRayQuery` requires Vulkan KHR acceleration structure plus ray query.
- `VulkanRayTracingPipeline` is reserved for the later SBT pipeline path.
- If a requested backend is unavailable, report the missing extension, feature,
  shader, descriptor, or scene-resource reason in logs and frame diagnostics.

## Phase 0 - Work Branch And Safety Rails

- [ ] Create a dedicated branch, for example
      `feature/vulkan-restir-radiance-cache-gi`.
- [ ] Add this TODO to the active work-doc index.
- [ ] Document the backend split in code comments near `RestirGI.cs` and the
      Vulkan backend entry point once implementation starts.
- [ ] Add a renderer-facing enum for ReSTIR ray tracing backend selection:
      `Auto`, `ComputeOnly`, `OpenGLNativeBridge`, `VulkanRayQuery`,
      `VulkanRayTracingPipeline`.
- [ ] Preserve native interop smoke tests for the OpenGL bridge.
- [ ] Add Vulkan-specific tests that assert the Vulkan path does not P/Invoke
      the OpenGL bridge.

Acceptance criteria:

- [ ] The repo still builds with the OpenGL bridge present.
- [ ] The default behavior is unchanged for OpenGL.
- [ ] Vulkan backend selection failures are visible and actionable.

## Phase 1 - Vulkan RT Capability Bring-Up

- [ ] Add optional device extensions:
      `VK_KHR_acceleration_structure`,
      `VK_KHR_ray_tracing_pipeline`,
      `VK_KHR_ray_query`,
      `VK_KHR_deferred_host_operations`,
      `VK_KHR_spirv_1_4`,
      `VK_KHR_shader_float_controls`.
- [ ] Query and enable
      `PhysicalDeviceAccelerationStructureFeaturesKHR`.
- [ ] Query and enable
      `PhysicalDeviceRayQueryFeaturesKHR`.
- [ ] Query and enable
      `PhysicalDeviceRayTracingPipelineFeaturesKHR` only for the future full
      RT pipeline path.
- [ ] Query acceleration-structure and ray-tracing-pipeline properties,
      including scratch alignment, shader group handle size, and SBT alignment.
- [ ] Load `KhrAccelerationStructure`, `KhrRayTracingPipeline`, and
      `KhrDeferredHostOperations` dispatch handles.
- [ ] Expose a compact capability snapshot from `VulkanRenderer`:
      acceleration structures, ray query, ray tracing pipeline, device address,
      descriptor indexing, and failure reasons.
- [ ] Add startup logs and profiler counters for requested versus enabled RT
      features.

Acceptance criteria:

- [ ] Validation layers report no feature-chain or extension-enable errors.
- [ ] Unsupported hardware reports a precise capability miss.
- [ ] Supported hardware reports enabled KHR acceleration structure and ray
      query without requiring full RT pipeline support.

## Phase 2 - Vulkan Acceleration Structure Resources

- [ ] Replace the legacy commented `VulkanRaytracing.cs` body with current-style
      Vulkan RT resource classes or partials.
- [ ] Add `VulkanBottomLevelAccelerationStructure` with:
      storage buffer, scratch buffer, build sizes, geometry ranges, lifetime
      ownership, and debug name.
- [ ] Add `VulkanTopLevelAccelerationStructure` with:
      instance buffer, storage buffer, scratch buffer, build/refit state,
      device address, and debug name.
- [ ] Ensure AS storage, scratch, instance, SBT, vertex, and index buffers use
      correct Vulkan usage bits and buffer device address allocation.
- [ ] Add resource retirement through existing Vulkan frame/fence retirement
      paths.
- [ ] Add build barriers for
      `AccelerationStructureBuildKHR` read/write and shader read.
- [ ] Add debug labels around BLAS and TLAS build commands.

Acceptance criteria:

- [ ] A minimal BLAS/TLAS smoke scene builds without validation errors.
- [ ] Resource destruction is fence-safe and does not require routine
      `DeviceWaitIdle`.
- [ ] RenderDoc can identify named BLAS/TLAS build commands and resources.

## Phase 3 - Scene Geometry And Instance Mapping

- [ ] Define the Vulkan RT scene data owner, for example `VulkanRayTracingScene`.
- [ ] Map GPUScene mesh data to BLAS build inputs:
      vertex address, index address, stride, format, primitive count, and
      transform source.
- [ ] Choose initial geometry scope:
      static opaque and masked triangle meshes first.
- [ ] Add a clear policy for dynamic and skinned meshes:
      skip with diagnostics, rebuild BLAS, refit BLAS, or trace against packed
      skinned output buffers.
- [ ] Map TLAS `instanceCustomIndex` to draw, mesh, material, and transform
      identifiers already used by GPUScene.
- [ ] Include material flags needed for GI sampling:
      alpha test, emissive, roughness, base color, double sided, and
      transmissive policy.
- [ ] Add invalidation hooks for mesh upload, material changes, transform
      changes, visibility changes, and scene unload.

Acceptance criteria:

- [ ] TLAS instance count matches expected scene RT-eligible object count.
- [ ] Instance custom indices resolve back to GPUScene material/draw data.
- [ ] Unsupported geometry is skipped loudly, not silently rendered wrong.

## Phase 4 - Ray-Query Compute Smoke Path

- [ ] Add Vulkan descriptor support for
      `DescriptorType.AccelerationStructureKhr`.
- [ ] Extend command-scoped descriptor binding so compute passes can receive a
      TLAS descriptor.
- [ ] Add a tiny Vulkan ray-query compute shader that writes hit/miss distance
      or instance id to a debug image.
- [ ] Dispatch the smoke shader from a Vulkan-only command path.
- [ ] Add image and buffer barriers for ray-query reads and debug image writes.
- [ ] Add debug visualization or capture output for the ray-query result.

Acceptance criteria:

- [ ] The smoke pass shows stable hit/miss output in Unit Testing World.
- [ ] Moving the camera changes the ray-query output as expected.
- [ ] The pass does not use the OpenGL bridge or full SBT pipelines.

## Phase 5 - Vulkan ReSTIR Initial Sampling

- [ ] Replace `InitialSampling.comp` for Vulkan with KHR ray-query syntax.
- [ ] Feed the shader G-buffer inputs:
      depth, normal, base color, material id, motion vectors, and camera data.
- [ ] Feed the shader scene inputs:
      TLAS, material table, light data, transform data, and optional emissive
      mesh data.
- [ ] Write initial reservoirs into the existing ReSTIR reservoir buffer shape
      or introduce a versioned Vulkan reservoir layout.
- [ ] Add visibility rays against the TLAS for candidate validation.
- [ ] Keep the existing compute fallback path available when ray tracing is not
      selected.

Acceptance criteria:

- [ ] Vulkan ReSTIR initial sampling writes nonzero stable reservoirs.
- [ ] Direct-light and cache-candidate PDFs are recorded or debuggable.
- [ ] Missing TLAS or descriptor state reports a backend failure, not a black
      frame.

## Phase 6 - Temporal And Spatial Reuse

- [ ] Split reservoir history into explicit ping-pong buffers.
- [ ] Reproject previous reservoirs using motion vectors and depth.
- [ ] Reject history on disocclusion, normal mismatch, material mismatch, and
      excessive depth delta.
- [ ] Expand spatial reuse beyond the current four-neighbor prototype while
      keeping per-frame allocation at zero.
- [ ] Add variance or confidence fields needed for temporal stability.
- [ ] Add debug views for reservoir age, weight, chosen sample distance,
      rejection reason, and visibility result.

Acceptance criteria:

- [ ] Camera motion does not smear across disocclusions.
- [ ] Static scenes converge rather than flicker.
- [ ] Reservoir buffers do not allocate during steady-state frames.

## Phase 7 - Radiance Cache

- [ ] Define the persistent radiance cache representation:
      world position, normal, radiance, variance/moments, age, validity,
      material flags, and optional probe/surfel radius.
- [ ] Choose cache indexing:
      camera-centered grid, hashed world cells, clipmap, or surfel pool.
- [ ] Add allocation, eviction, and compaction policy with no hot-path heap
      allocation.
- [ ] Add an update pass that traces a bounded number of cache rays per frame.
- [ ] Add cache candidate generation for ReSTIR:
      sample cache entries, compute PDF, estimate contribution, validate
      visibility, and merge into reservoirs.
- [ ] Add cache invalidation for scene edits, transform edits, material edits,
      and lighting changes.
- [ ] Add cache debug views:
      occupancy, age, radiance, variance, invalid entries, and update budget.

Acceptance criteria:

- [ ] Cache survives across frames and updates incrementally.
- [ ] ReSTIR can sample cache entries as indirect GI candidates.
- [ ] Cache invalidation is visible and bounded after scene edits.

## Phase 8 - Composite, Denoising, And XR

- [ ] Composite Vulkan ReSTIR output through the existing `RestirGITexture` and
      `RestirCompositeFBO` path.
- [ ] Add temporal clamp and variance-aware spatial filtering.
- [ ] Decide how ReSTIR GI feeds TSR/TAA history.
- [ ] Validate mono editor rendering first.
- [ ] Add stereo policy:
      per-eye reservoirs/cache queries, shared radiance cache, and eye-specific
      reprojection.
- [ ] Validate OpenVR path because it is the currently tested XR runtime.
- [ ] Add settings for quality tiers:
      rays per pixel, cache update budget, spatial reuse radius, temporal
      history length, and debug mode.

Acceptance criteria:

- [ ] Mono Vulkan output is visually stable in Unit Testing World.
- [ ] Stereo output has no eye mismatch or history leak between eyes.
- [ ] Quality settings are bounded and visible in diagnostics.

## Phase 9 - Full RT Pipeline Optional Path

Do this only if ray-query compute cannot support material or performance goals.

- [ ] Add ray shader stages to `EShaderType`.
- [ ] Extend shader type resolution for `.rgen`, `.rmiss`, `.rchit`, `.rahit`,
      `.rint`, and `.rcall`.
- [ ] Extend shader cross compilation and Vulkan shader-stage mapping.
- [ ] Add RT pipeline creation through `vkCreateRayTracingPipelinesKHR`.
- [ ] Add shader group layout and shader binding table creation.
- [ ] Add SBT buffer usage, device address, alignment, and debug labels.
- [ ] Replace numeric authored pipeline/SBT ids with renderer-owned Vulkan RT
      pipeline handles.
- [ ] Keep RT pipelines as `VkPipeline` even if graphics/compute later gain a
      shader-object backend.

Acceptance criteria:

- [ ] A raygen/miss/closest-hit smoke pipeline renders a debug output.
- [ ] SBT layout validates on NVIDIA and AMD hardware where available.
- [ ] Ray-query compute remains selectable for simpler GI paths.

## Phase 10 - Tests, Docs, And Validation

- [ ] Add unit tests for Vulkan RT capability classification.
- [ ] Add tests for backend selection and failure diagnostics.
- [ ] Add shader compile tests for Vulkan ray-query ReSTIR shaders.
- [ ] Add descriptor layout tests for acceleration-structure descriptors.
- [ ] Add no-bridge tests proving Vulkan ReSTIR does not call
      `RestirGI.Native`.
- [ ] Add RenderDoc capture instructions to the Vulkan validation guide once a
      frame renders.
- [ ] Add user/developer docs for settings, diagnostics, hardware requirements,
      and known limitations.
- [ ] Run targeted Vulkan validation tests.
- [ ] Build the editor.
- [ ] Iterate on the editor with MCP screenshots and logs until the visual issue
      list is understood or resolved.

Acceptance criteria:

- [ ] Tests cover capability, shader, descriptor, and backend selection logic.
- [ ] Manual Vulkan validation has at least one recorded supported-GPU run.
- [ ] Docs explain how to force compute-only, OpenGL bridge, Vulkan ray-query,
      and future RT pipeline modes.

## Finalization

- [ ] Review profiler output for hot-path allocations in AS update, descriptor
      resolution, reservoir processing, and radiance-cache update paths.
- [ ] Review warnings and fix low-risk warnings in touched files.
- [ ] Keep the OpenGL bridge until a later explicit removal task says otherwise.
- [ ] Merge the dedicated branch back into `main` after implementation,
      validation, and owner review.

## Validation Commands

Use the narrowest useful validation for each phase:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter Vulkan
```

For visual validation, use the Unit Testing World with Vulkan and MCP enabled:

```powershell
dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467
```

Use RenderDoc for AS, descriptor, ray-query, reservoir, and radiance-cache
inspection once the smoke path renders.
