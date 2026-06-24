# Vulkan ReSTIR Radiance Cache GI TODO

Last Updated: 2026-06-18
Owner: Rendering
Status: Planning
Target Branch: create in Phase 0

## Goal

Implement ReSTIR radiance-cached global illumination as a Vulkan-native path
while preserving the current OpenGL/NV native bridge as a legacy backend.

The Vulkan path should use KHR acceleration structures, ray-query compute for
cheap visibility rays, and full KHR ray tracing pipelines for material-bearing
radiance-cache updates when closest-hit shading is needed.

## Reference Audit

Primary reference: [diharaw/hybrid-rendering](https://github.com/diharaw/hybrid-rendering),
commit inspected locally: `090360e`.

Relevant takeaways from the sample:

- The sample is a Vulkan deferred hybrid renderer with ray traced shadows,
  ambient occlusion, reflections, and DDGI-style global illumination. Its
  README requires `VK_KHR_ray_tracing_pipeline`,
  `VK_KHR_acceleration_structure`, and `VK_EXT_descriptor_indexing`.
- It uses one scene descriptor set for material data, instance data, TLAS,
  vertex-buffer array, index-buffer array, submesh/material-index array, and
  bindless texture array. This is close to XRENGINE's GPUScene/material-table
  direction and is a better target than one-off per-pass descriptors.
- Visibility-only work uses ray-query compute. AO and shadows include
  `GL_EXT_ray_query`, trace against the TLAS from compute, and write compact
  ray results that later passes denoise.
- Material-bearing work uses full RT pipelines. Reflections and DDGI build
  raygen/closest-hit/miss pipelines with SBTs and `maxPipelineRayRecursionDepth`
  set to 1, then use compute passes for accumulation, probe updates, denoising,
  and screen-space sampling.
- The DDGI path is a practical radiance-cache model:
  ray trace into `raysPerProbe x totalProbes` radiance and direction/depth
  images, update ping-pong irradiance/depth probe atlases, run border-copy
  passes for octahedral probe textures, then sample the probe grid from the
  G-buffer into a screen-space GI image.
- Denoising and stability are not a single blur. The sample uses reprojection,
  moments/variance, tile lists, indirect dispatch, A-trous filtering, upsample
  passes, low-resolution ray tracing scales, blue-noise sampling, and explicit
  first-frame/history reset state.

Reference constraints for XRENGINE:

- Borrow the architecture, not the framework. Do not import sample code or its
  descriptor-size constants blindly.
- Avoid sample-style `wait_idle` resource recreation in steady state. Prefer
  XRENGINE's existing fence-retired Vulkan resource lifetime model.
- Treat DDGI probe atlases as one radiance-cache candidate, not as a mandate to
  abandon ReSTIR reservoirs.
- Keep the OpenGL bridge as a supported legacy backend until a later explicit
  removal task says otherwise.

## Non-Goals

- Do not remove `RestirGI.Native.dll`, `RestirGI.Native.cpp`, or the existing
  OpenGL `GL_NV_ray_tracing` bridge during Vulkan bring-up.
- Do not make the Vulkan path silently fall back to CPU tracing or the OpenGL
  bridge when Vulkan ray tracing is explicitly requested.
- Do not treat the existing compute BVH pass as a Vulkan hardware acceleration
  structure implementation.
- Do not block ordinary OpenGL ReSTIR experiments on Vulkan RT availability.

## Current Local Facts

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Raytracing/VulkanRenderer.Raytracing.cs`
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
- Vulkan native path: new KHR acceleration structure, ray-query, and RT
  pipeline implementation owned by the Vulkan renderer. This path must not call
  the OpenGL bridge.

Backend selection should be explicit and diagnostic:

- `Auto` may select Vulkan only when the active renderer is Vulkan and all
  required Vulkan features are enabled.
- `ComputeOnly` uses the existing non-hardware-RT compute fallback.
- `OpenGLNativeBridge` may select the legacy bridge only when the active
  renderer/context makes that bridge meaningful.
- `VulkanRayQuery` uses compute shaders with `rayQueryEXT`. Use this for
  visibility validation, AO/shadow-style rays, and simple smoke tests.
- `VulkanRayTracingPipeline` uses raygen/miss/hit shaders and an SBT. Use this
  for radiance-cache updates that need closest-hit material evaluation.
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
      `VK_KHR_buffer_device_address`,
      `VK_EXT_descriptor_indexing`,
      `VK_KHR_spirv_1_4`,
      `VK_KHR_shader_float_controls`.
- [ ] Treat `VK_KHR_pipeline_library` as optional future acceleration for RT
      pipeline management, not a Phase 1 blocker.
- [ ] Query and enable
      `PhysicalDeviceBufferDeviceAddressFeatures`.
- [ ] Query and enable
      `PhysicalDeviceDescriptorIndexingFeatures` needed by the scene descriptor
      array model.
- [ ] Query and enable
      `PhysicalDeviceAccelerationStructureFeaturesKHR`.
- [ ] Query and enable
      `PhysicalDeviceRayQueryFeaturesKHR`.
- [ ] Query and enable
      `PhysicalDeviceRayTracingPipelineFeaturesKHR`.
- [ ] Query acceleration-structure and ray-tracing-pipeline properties,
      including scratch alignment, shader group handle size, shader group base
      alignment, max recursion depth, and SBT alignment.
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
- [ ] Supported hardware reports enabled KHR acceleration structure, ray query,
      RT pipeline, device address, and descriptor indexing when available.

## Phase 2 - Vulkan Acceleration Structure Resources

- [ ] Replace the legacy commented
      `Features/Raytracing/VulkanRenderer.Raytracing.cs` body with
      current-style Vulkan RT resource classes or partials.
- [ ] Add `VulkanBottomLevelAccelerationStructure` with:
      storage buffer, scratch buffer, compacted-size query support, build sizes,
      geometry ranges, lifetime ownership, and debug name.
- [ ] Add `VulkanTopLevelAccelerationStructure` with:
      instance buffer, storage buffer, scratch buffer, build/refit state,
      device address, and debug name.
- [ ] Ensure AS storage, scratch, instance, SBT, vertex, and index buffers use
      correct Vulkan usage bits:
      `AccelerationStructureStorageBitKhr`,
      `AccelerationStructureBuildInputReadOnlyBitKhr`,
      `StorageBufferBit`,
      `ShaderDeviceAddressBit`,
      `ShaderBindingTableBitKhr`,
      and transfer bits where uploads/copies require them.
- [ ] Select build flags per resource:
      `PreferFastTrace` for static BLAS,
      `AllowUpdate` for refittable dynamic BLAS/TLAS,
      and `AllowCompaction` only when compaction is implemented.
- [ ] Add scratch-buffer pooling sized by queried alignment and build sizes.
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

## Phase 3 - Scene Descriptor And Instance Mapping

- [ ] Define the Vulkan RT scene data owner, for example
      `VulkanRayTracingScene`.
- [ ] Define one RT scene descriptor layout modeled on the reference:
      material table, instance table, TLAS, vertex-buffer array, index-buffer
      array, submesh/material range array, and texture array.
- [ ] Use descriptor indexing features intentionally:
      partially bound arrays, runtime descriptor arrays where supported, and
      `nonuniformEXT` for per-hit/per-material indexing in shaders.
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
      alpha test, emissive, roughness, base color, normal map, metallic,
      double sided, and transmissive policy.
- [ ] Add invalidation hooks for mesh upload, material changes, transform
      changes, visibility changes, and scene unload.

Acceptance criteria:

- [ ] TLAS instance count matches expected scene RT-eligible object count.
- [ ] Instance custom indices resolve back to GPUScene material/draw data.
- [ ] Closest-hit shaders and ray-query compute can both consume the same
      scene descriptor contract.
- [ ] Unsupported geometry is skipped loudly, not silently rendered wrong.

## Phase 4 - Ray-Query Visibility Smoke Path

Use this for efficient visibility rays and as the fastest validation route for
TLAS correctness.

- [ ] Add Vulkan descriptor support for
      `DescriptorType.AccelerationStructureKhr`.
- [ ] Extend command-scoped descriptor binding so compute passes can receive a
      TLAS descriptor.
- [ ] Add shared shader helpers similar to the reference `ray_query.glsl`:
      `QueryVisibility(origin, direction, tMax, rayFlags)` and
      `QueryDistance(...)`.
- [ ] Add a tiny Vulkan ray-query compute shader that writes hit/miss distance
      or instance id to a debug image.
- [ ] Add an AO/shadow-style visibility pass that reconstructs world position
      from depth, offsets by normal bias, traces against TLAS, and writes a
      compact result.
- [ ] Evaluate bit-packed low-resolution output for visibility-only rays, where
      one workgroup writes a compact mask before denoising.
- [ ] Dispatch the smoke shader from a Vulkan-only command path.
- [ ] Add image and buffer barriers for ray-query reads and debug image writes.
- [ ] Add debug visualization or capture output for the ray-query result.

Acceptance criteria:

- [ ] The smoke pass shows stable hit/miss output in Unit Testing World.
- [ ] Moving the camera changes the ray-query output as expected.
- [ ] The pass does not use the OpenGL bridge or SBT pipelines.
- [ ] Visibility-only rays can run at full, half, or quarter resolution without
      per-frame allocation.

## Phase 5 - RT Pipeline And Material-Hit Smoke Path

Do this before radiance-cache production work. The reference uses full RT
pipelines for GI because closest-hit shaders need material, texture, normal, and
emissive evaluation.

- [ ] Add ray shader stages to `EShaderType`.
- [ ] Extend shader type resolution for `.rgen`, `.rmiss`, `.rchit`, `.rahit`,
      `.rint`, and `.rcall`.
- [ ] Extend shader cross compilation and Vulkan shader-stage mapping.
- [ ] Add RT pipeline creation through `vkCreateRayTracingPipelinesKHR`.
- [ ] Add shader group layout and shader binding table creation.
- [ ] Add SBT buffer usage, device address, alignment, and debug labels.
- [ ] Add a max recursion depth of 1 for the first smoke path.
- [ ] Add a raygen/miss/closest-hit smoke shader that writes hit material id,
      emissive, albedo, normal, or distance to a debug image.
- [ ] Replace numeric authored pipeline/SBT ids with renderer-owned Vulkan RT
      pipeline handles.
- [ ] Keep RT pipelines as `VkPipeline` even if graphics/compute later gain a
      shader-object backend.

Acceptance criteria:

- [ ] A raygen/miss/closest-hit smoke pipeline renders a debug output.
- [ ] SBT layout validates on NVIDIA and AMD hardware where available.
- [ ] Closest-hit material lookup uses the same RT scene descriptor as raster
      material lookup where possible.
- [ ] Ray-query compute remains selectable for simpler visibility paths.

## Phase 6 - Vulkan ReSTIR Initial And Visibility Sampling

- [ ] Replace `InitialSampling.comp` for Vulkan with a backend-specific shader
      path:
      ray-query for visibility validation and RT pipeline for candidate
      radiance when material closest-hit shading is needed.
- [ ] Feed the shader G-buffer inputs:
      depth, normal, base color, material id, roughness, motion vectors, and
      camera data.
- [ ] Feed the shader scene inputs:
      TLAS, material table, light data, transform data, blue-noise textures,
      and optional emissive mesh data.
- [ ] Add reservoir layouts that record enough source information for
      validation:
      sample kind, target position or cache id, PDF, radiance, visibility,
      depth, normal/material compatibility, and random seed state.
- [ ] Add direct-light candidates and radiance-cache candidates as separate
      candidate kinds.
- [ ] Add visibility rays against the TLAS for candidate validation.
- [ ] Keep the existing compute fallback path available when ray tracing is not
      selected.

Acceptance criteria:

- [ ] Vulkan ReSTIR initial sampling writes nonzero stable reservoirs.
- [ ] Direct-light and cache-candidate PDFs are recorded or debuggable.
- [ ] Missing TLAS, descriptor, or shader state reports a backend failure, not a
      black frame.
- [ ] The shader can run at reduced resolution without changing reservoir
      history semantics.

## Phase 7 - Radiance Cache

Start with a DDGI-inspired probe grid because the reference proves this shape is
efficient and debuggable. Keep the design open for later surfel/hash-grid
variants.

- [ ] Define the persistent radiance cache representation:
      world position, normal or probe basis, radiance, distance moments,
      variance, age, validity, material flags, and optional probe/surfel radius.
- [ ] Choose initial cache indexing:
      scene-bounds probe grid, camera-centered grid, hashed world cells,
      clipmap, or surfel pool.
- [ ] Implement a probe-grid cache candidate with:
      grid start, grid step, probe counts, rays per probe, irradiance oct size,
      depth oct size, normal bias, max distance, hysteresis, visibility-test
      toggle, and energy-preservation scalar.
- [ ] Allocate ray-trace radiance and direction/depth images shaped like:
      `raysPerProbe x totalProbes`.
- [ ] Allocate ping-pong irradiance and depth atlases with one-pixel probe
      borders and one-pixel outer texture padding.
- [ ] Add RT pipeline cache update pass:
      raygen launches one ray per `(rayId, probeId)`, closest-hit evaluates
      material/direct light/emissive/optional previous cache irradiance, miss
      samples sky/environment, and outputs radiance plus direction/depth.
- [ ] Add compute probe update passes:
      gather radiance rays into irradiance octahedral texels, gather depth rays
      into distance moments, use shared memory chunks for ray batches, apply
      hysteresis, and respect first-frame resets.
- [ ] Add border-update passes for irradiance and depth probe atlases.
- [ ] Add screen-space sample pass that reads the G-buffer and cache atlases,
      applies trilinear probe blending, backface weighting, moment visibility,
      normal bias, and writes `RestirGITexture` or an intermediate cache GI
      texture.
- [ ] Add cache candidate generation for ReSTIR:
      sample cache entries/probes, compute PDF, estimate contribution, validate
      visibility, and merge into reservoirs.
- [ ] Add cache invalidation for scene edits, transform edits, material edits,
      lighting changes, and probe-grid setting changes.
- [ ] Add cache debug views:
      occupancy, age, radiance, depth moments, visibility weight, invalid
      entries, probe borders, and update budget.

Acceptance criteria:

- [ ] Cache survives across frames and updates incrementally.
- [ ] ReSTIR can sample cache entries as indirect GI candidates.
- [ ] Cache invalidation is visible and bounded after scene edits.
- [ ] Probe update, border update, and screen-space sample passes run without
      steady-state heap allocation.

## Phase 8 - Temporal, Spatial, Denoising, And Work Compaction

- [ ] Split reservoir history into explicit ping-pong buffers.
- [ ] Reproject previous reservoirs using motion vectors and depth.
- [ ] Reject history on disocclusion, normal mismatch, material mismatch, and
      excessive depth delta.
- [ ] Add moments/variance to radiance and reservoir history.
- [ ] Add neighborhood clamping or AABB clipping before temporal accumulation.
- [ ] Expand spatial reuse beyond the current four-neighbor prototype while
      keeping per-frame allocation at zero.
- [ ] Add tile-list generation for pixels that need denoising or copy/resolve.
- [ ] Add indirect dispatch buffers for denoise/copy work so expensive filters
      do not run across empty/full-trust regions.
- [ ] Add A-trous or comparable edge-stopping filter using depth, normal,
      roughness, variance, and reservoir confidence.
- [ ] Add reduced-resolution tracing plus depth/normal-aware upsample.
- [ ] Add debug views for reservoir age, weight, chosen sample distance,
      rejection reason, visibility result, tile list, variance, and filter
      iteration.

Acceptance criteria:

- [ ] Camera motion does not smear across disocclusions.
- [ ] Static scenes converge rather than flicker.
- [ ] Reservoir, tile, and denoise buffers do not allocate during steady-state
      frames.
- [ ] GPU captures show denoise work is bounded by active tile lists where
      tile compaction is enabled.

## Phase 9 - Composite, Quality Settings, And XR

- [ ] Composite Vulkan ReSTIR output through the existing `RestirGITexture` and
      `RestirCompositeFBO` path.
- [ ] Decide how ReSTIR GI feeds TSR/TAA history.
- [ ] Validate mono editor rendering first.
- [ ] Add stereo policy:
      per-eye reservoirs/cache queries, shared radiance cache, and eye-specific
      reprojection.
- [ ] Validate OpenVR path because it is the currently tested XR runtime.
- [ ] Add settings for quality tiers:
      backend, ray scale, rays per pixel, rays per probe, probe distance,
      cache update budget, spatial reuse radius, temporal history length,
      denoise iterations, and debug mode.
- [ ] Add diagnostics showing active backend, active resolution scale, TLAS
      instance count, BLAS count, rays dispatched, cache probes, reservoir
      count, denoise tile count, and fallback reason.

Acceptance criteria:

- [ ] Mono Vulkan output is visually stable in Unit Testing World.
- [ ] Stereo output has no eye mismatch or history leak between eyes.
- [ ] Quality settings are bounded and visible in diagnostics.
- [ ] Requested Vulkan RT failure is explicit and does not silently use the
      OpenGL bridge.

## Phase 10 - Tests, Docs, And Validation

- [ ] Add unit tests for Vulkan RT capability classification.
- [ ] Add tests for backend selection and failure diagnostics.
- [ ] Add shader compile tests for Vulkan ray-query ReSTIR shaders.
- [ ] Add shader compile tests for `.rgen`, `.rchit`, and `.rmiss` shaders.
- [ ] Add descriptor layout tests for acceleration-structure descriptors and RT
      scene descriptor arrays.
- [ ] Add SBT layout tests for shader group handle size and alignment.
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

- [ ] Tests cover capability, shader, descriptor, SBT, and backend selection
      logic.
- [ ] Manual Vulkan validation has at least one recorded supported-GPU run.
- [ ] Docs explain how to force compute-only, OpenGL bridge, Vulkan ray-query,
      and Vulkan RT pipeline modes.

## Finalization

- [ ] Review profiler output for hot-path allocations in AS update, descriptor
      resolution, reservoir processing, radiance-cache update, tile-list
      generation, and denoise paths.
- [ ] Review warnings and fix low-risk warnings in touched files.
- [ ] Keep the OpenGL bridge until a later explicit removal task says
      otherwise.
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

Use RenderDoc for AS, descriptor, ray-query, SBT, reservoir, denoise, and
radiance-cache inspection once the smoke path renders.
