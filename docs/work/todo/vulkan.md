# Vulkan Manual Validation Guide

Last updated: 2026-04-28

This document is now the manual validation plan for making Vulkan a dependable day-to-day render API in XRENGINE. Code-side P0/P1 guard rails and the remaining source-verifiable P2 items are implemented: black-frame diagnostics, final-output checks, descriptor fallback/failure telemetry, descriptor update templates, immutable samplers, push constants, dynamic UBO pressure counters, fence-retired resource-plan replacement, swapchain recovery diagnostics, queue-ownership source coverage, pipeline miss summaries, and the engine-level pipeline prewarm manifest.

Unchecked items below are intentionally human-driven. They require a real Windows Vulkan session, validation layers, visual inspection, GPU capture, hardware stress, or before/after benchmark evidence.

## Manual Setup

Use Windows 10/11 with a current GPU driver and Vulkan validation layers installed. Capture this run header for every session:

| Field | Value |
|-------|-------|
| Date/time | |
| GPU model | |
| Driver version | |
| OS build | |
| Vulkan API version | |
| Active Vulkan profile | |
| `VulkanRobustnessSettings.SyncBackend` | |
| `VulkanRobustnessSettings.AllocatorBackend` | |
| `VulkanRobustnessSettings.DescriptorUpdateBackend` | |
| `VulkanRobustnessSettings.DynamicUniformBufferEnabled` | |
| World mode / scene | |
| Resolution / display mode | |

Run modes:

```powershell
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -- --unit-testing
```

For Unit Testing World validation, set `Assets/UnitTestingWorldSettings.jsonc` `RenderAPI` to `Vulkan`, or launch with settings that resolve the render API to Vulkan.

Useful diagnostics:

| Variable | Purpose |
|----------|---------|
| `XRE_FORCE_SWAPCHAIN_MAGENTA=1` | Forces a diagnostic swapchain clear. If visible, presentation is alive and scene output is the suspect. |
| `XRE_SKIP_IMGUI=1` | Removes ImGui overlay from the frame. |
| `XRE_SKIP_UI_PIPELINE=1` | Removes screen-space UI pipeline ops from the frame. |
| `XRE_VK_TRACE_DRAW=1` | Logs Vulkan draw recording details. |
| `XRE_VK_TRACE_SWAPDRAW=1` | Logs swapchain-targeted draw recording details. |
| `XRE_VK_TRACE_PIPECREATE=1` | Logs pipeline creation details. |
| `XRE_VK_PIPELINE_PREWARM_CAPTURE=1` | Writes observed pipeline permutations to `%LOCALAPPDATA%\XREngine\Vulkan\PipelinePrewarm\prewarm_*.json` on shutdown. |

## Fast Software Checks

Run these after Vulkan code or shader changes. They are not substitutes for the manual matrix, but they prevent already-covered regressions from wasting a hardware pass.

```powershell
dotnet build XREngine.Editor/XREngine.Editor.csproj --nologo -v minimal
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter VulkanP0ValidationTests --nologo -v minimal
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter VulkanP1ValidationTests --nologo -v minimal
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter VulkanTodoP2ValidationTests --nologo -v minimal
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter VulkanShaderCompilationRegressionTests --nologo -v minimal
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter VulkanShaderPreprocessParityTests --nologo -v minimal
```

If local machine dependencies prevent a test from running, record the exact missing framework or native dependency.

## P0 Smoke

Pass criteria: visible 3D content, visible editor UI where expected, no validation errors, no new validation warnings, no dropped draw/compute ops, and no missing scene swapchain writer frames.

- [ ] Run the default editor world with Vulkan validation layers enabled.
- [ ] Confirm visible default-world 3D scene content.
- [ ] Confirm visible ImGui/editor overlays unless intentionally disabled.
- [ ] Run Unit Testing World with Vulkan.
- [ ] Repeat the default-world and Unit Testing World sessions with `SyncBackend = Sync2`.
- [ ] Compare legacy sync and Sync2 visually on the same camera and scene.
- [ ] Validate descriptor pool reset/reuse under validation layers.
- [ ] Record GPU, driver, API profile, and active Vulkan feature profile in the run header.

Black-frame triage:

1. Enable `XRE_FORCE_SWAPCHAIN_MAGENTA=1`.
2. If magenta appears, presentation is alive. Inspect frame diagnostics for zero scene swapchain writers, dropped `MeshDrawOp`/`IndirectDrawOp`, descriptor failures, shader/pipeline creation failures, and missing pass metadata.
3. If magenta does not appear, inspect acquire/record/submit/present, swapchain layout transitions, command-buffer recording failure, and device-lost state.
4. Isolate overlays with `XRE_SKIP_IMGUI=1` and `XRE_SKIP_UI_PIPELINE=1`.
5. Capture the profiler Vulkan frame diagnostic bundle and the validation-layer message that first appears.

## P1 Stress

Descriptor pressure:

- [ ] Exhaust transient render descriptor pools on hardware.
- [ ] Exhaust transient compute descriptor pools on hardware.
- [ ] Verify descriptor pool reset/reuse recovery.
- [ ] Verify no descriptor use-after-free under validation layers.
- [ ] Benchmark descriptor update CPU time with `DescriptorUpdateBackend = Legacy`.
- [ ] Benchmark descriptor update CPU time with `DescriptorUpdateBackend = Template`.
- [ ] Record descriptor fallback/failure summaries from the profiler panel.

Dynamic UBO and push constants:

- [ ] Profile the dynamic UBO ring disabled.
- [ ] Profile the dynamic UBO ring enabled.
- [ ] Compare descriptor update CPU time, dynamic UBO allocations, allocated KB, and exhaustion counts.
- [ ] Keep dynamic UBO offsets enabled for per-draw engine constants only if the CPU reduction is measurable and no validation errors appear.

Resource lifetime and memory pressure:

- [ ] Allocate enough transient and persistent resources to grow device-local suballocator blocks.
- [ ] Exercise host-visible upload pools.
- [ ] Exercise host-cached readback pools.
- [ ] Exercise dedicated-allocation preference paths.
- [ ] Trigger and validate OOM fallback behavior without device loss.
- [ ] Verify allocation count remains comfortably below hardware limits during normal editor workflows.
- [ ] Run a long resize/recreate session and watch for leaked images, buffers, views, framebuffers, descriptor pools, and retired-resource buildup.
- [ ] Validate fence-retired resource-plan replacement in a long-running editor session with validation layers.

Swapchain and presentation:

- [ ] Repeatedly resize, maximize, restore, and minimize the editor window with validation layers enabled.
- [ ] Verify debounced swapchain recreation does not reuse stale command buffers.
- [ ] Verify `AcquireNextImage` `NotReady`, `Suboptimal`, and out-of-date paths recover.
- [ ] Capture screenshot/readback after resize and compare against the visible frame.
- [ ] Confirm dynamic rendering transitions the swapchain image to `PresentSrcKhr` exactly once per submitted frame.

## Rendering Parity Matrix

Compare each item against OpenGL using the same scene, camera, and resolution where possible.

Basic scene:

- [ ] Opaque deferred.
- [ ] Opaque forward.
- [ ] Masked forward.
- [ ] Transparent forward.
- [ ] On-top/debug rendering.
- [ ] Background/skybox.

Primitive types:

- [ ] Triangles.
- [ ] Lines.
- [ ] Line strips.
- [ ] Points.

FBO and post-processing:

- [ ] G-buffer creation/recreation.
- [ ] MSAA G-buffer resolve.
- [ ] Light combine.
- [ ] Forward pass.
- [ ] Bloom.
- [ ] Motion blur/depth of field.
- [ ] Temporal accumulation.
- [ ] Exposure update.
- [ ] FXAA/SMAA/TSR output.

UI:

- [ ] ImGui overlay.
- [ ] Screen-space UI render pipeline.
- [ ] Batched UI geometry.
- [ ] UI isolation with `XRE_SKIP_UI_PIPELINE=1`.
- [ ] UI isolation with `XRE_SKIP_IMGUI=1`.

Capture paths:

- [ ] Screenshot.
- [ ] Depth readback.
- [ ] Stencil picking/readback.
- [ ] Scene capture cubemap faces.
- [ ] Octahedral light probe encoding.
- [ ] Mirror/reflection passes.

GPU-driven paths:

- [ ] Indirect count path on supported hardware.
- [ ] Non-count fallback path.
- [ ] GPU culling.
- [ ] Occlusion culling diagnostics.
- [ ] Secondary command buffer recording.
- [ ] Parallel secondary command buffer recording thresholds.

XR / VR:

- [ ] Multiview capability logging.
- [ ] Stereo render path.
- [ ] OpenXR path.
- [ ] SteamVR/OpenVR tested path.
- [ ] Mirror-to-window behavior while in VR.

Optional high-end features:

- [ ] Ray tracing support when available.
- [ ] Graceful ray tracing degradation when unsupported.
- [ ] Vulkan memory decompression if available.
- [ ] Vulkan indirect copy if available.
- [ ] DLSS/XeSS native Vulkan path.
- [x] OpenGL-to-Vulkan upscale bridge fallback behavior. Shipped as [Vulkan Upscale Bridge](../../features/vulkan-upscale-bridge.md); remaining work is hardware validation and future expansion.

## P2 Hitch And Queue Validation

Pipeline prewarm workflow:

- [ ] Launch with `XRE_VK_PIPELINE_PREWARM_CAPTURE=1`.
- [ ] Exercise the default world, Unit Testing World, common editor panels, post-processing modes, UI, capture paths, and representative project scenes.
- [ ] Close cleanly so the semantic prewarm manifest is written under `%LOCALAPPDATA%\XREngine\Vulkan\PipelinePrewarm\`.
- [ ] Relaunch without clearing `%LOCALAPPDATA%\XREngine\Vulkan\PipelineCache\` so the driver cache and semantic manifest both load.
- [ ] Confirm profiler pipeline miss summaries trend toward zero after the warm run.
- [ ] Commit or archive only curated prewarm manifests that match the intended GPU/driver/profile scope.
- [ ] Refresh the manifest when shader interfaces, render-pass formats, material variants, MSAA state, descriptor layouts, or feature-profile gates change.

Queue overlap:

- [ ] Stress async compute plus transfer overlap on hardware that exposes separate queue families.
- [ ] Confirm queue-family ownership transfers remain validation-clean.
- [ ] Profile default editor scenes for oversynchronization and redundant layout transitions.
- [ ] Prototype split barriers with `vkCmdSetEvent2` / `vkCmdWaitEvents2` only for measured candidate workloads.
- [ ] Adopt split barriers only where captures show a real latency-hiding benefit.

## GPU Capture Recipes

Nsight Graphics:

1. Start the editor from Nsight with Vulkan selected and validation layers enabled.
2. Capture a frame after the scene has warmed for at least two frames.
3. Inspect the queue submission list for acquire, render, optional compute/transfer work, and present.
4. Verify swapchain image layout transitions end at `PresentSrcKhr`.
5. Check pipeline creation events and correlate any hitch with profiler pipeline miss summaries.
6. Inspect descriptor sets on a suspect draw when fallback counters are nonzero.

Radeon GPU Profiler / Radeon GPU Analyzer:

1. Capture the same scene used for OpenGL comparison where possible.
2. Check barrier cost, wait time, cache flushes, render pass load/store behavior, and async compute overlap.
3. Look for long pipeline creation or shader compilation bubbles during camera movement or UI interaction.
4. Compare Sync2 and legacy sync captures on the same workload.
5. Record whether queue overlap improves frame time or only adds ownership-transfer overhead.

Tiler-oriented checklist:

- [ ] Prefer transient/lazily allocated attachments where the hardware supports them.
- [ ] Check color/depth load ops for avoidable full-frame loads.
- [ ] Check store ops for attachments that are not consumed later.
- [ ] Watch bandwidth on MSAA resolve, bloom, temporal accumulation, and light probe passes.
- [ ] Validate that resize/recreate does not force unnecessary layout clears or full attachment reloads.

Profiler symptom map:

| Symptom | Where to look |
|---------|---------------|
| Missing swapchain writes | Vulkan frame diagnostics: scene/overlay/diagnostic writer counts and frame-op list. |
| Dropped frame ops | Dropped op counters plus first-failure pass, target, material, shader, and exception. |
| Descriptor fallback rendering | Descriptor fallback/failure summaries and validation messages for the same frame. |
| Oversynchronization | Barrier planner counters, queue ownership transfer count, GPU capture wait/flush events. |
| Bandwidth-heavy load/store choices | GPU capture attachment load/store ops, MSAA resolves, post-process chain, tiler memory events. |
| Pipeline compilation hitches | Pipeline cache miss summaries, `XRE_VK_TRACE_PIPECREATE=1`, driver pipeline cache warm bytes, prewarm manifest coverage. |

## Promotion Criteria

Vulkan can move from opt-in to regular development use when:

- [ ] P0 smoke passes on at least one NVIDIA, one AMD, and one Intel or laptop-class GPU where available.
- [ ] Sync2 and legacy sync are visually equivalent on the default world and Unit Testing World.
- [ ] P1 stress passes without validation errors, device loss, or unbounded allocation growth.
- [ ] Rendering parity matrix has no unexplained visual differences from OpenGL.
- [ ] Pipeline miss summaries are quiet after warmup in common editor workflows.
- [ ] GPU captures show no obvious avoidable oversynchronization or bandwidth blowups in default scenes.
- [ ] Black-frame diagnostics have not been needed to explain routine failures across repeated validation runs.

Keep legacy allocator/sync fallbacks for one stable milestone after promotion, then remove them only after repeated validation shows they are no longer needed.
