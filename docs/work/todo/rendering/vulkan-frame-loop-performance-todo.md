# Vulkan Frame Loop Performance Testing

Last Updated: 2026-06-17
Owner: Rendering
Status: Testing Guide
Target Branch: `vulkan-frame-loop-performance`

## Purpose

This document is the validation guide for the Vulkan frame-loop performance work.
It replaces the implementation backlog. The code paths should now expose enough
telemetry to prove whether the frame loop is healthy, whether command buffers are
being reused, and whether retired Vulkan resources are being drained steadily
instead of in large render-thread spikes.

Use this guide whenever a Vulkan performance change touches:

- command-buffer recording or reuse
- frame operation signatures, render-pass planning, or barrier planning
- retired-resource queues and physical resource replacement
- dynamic uniform ring reset, staging trim, acquire, submit, or present
- profiler packet fields, NDJSON profile capture, or measurement scripts
- AO/deferred-lighting resources that can churn render targets or framebuffers

## Implementation Coverage

The frame-loop instrumentation and guard rails now cover the original
implementation goals:

- Vulkan frame lifecycle timings are split into wait, timing-query sampling,
  retired-resource drain, acquire, bridge submit acquisition, swapchain-image
  wait, dynamic uniform reset, command-buffer recording, submit, trim, present,
  and total frame time.
- `RecordCommandBuffer` reports managed allocation bytes through runtime stats,
  profiler packets, the editor profiler data source, profiler sender, and NDJSON
  profile capture.
- Command-buffer reuse diagnostics include frame-op census, dirty reason counts,
  and a dirty summary string.
- Resource-plan replacement diagnostics include replacement counts and retired
  image/buffer counts.
- `Tools/Measure-VulkanFrameLoop.ps1` forwards the steady-state resource-churn
  and command-buffer allocation failure gates to
  `Tools/Measure-GameLoopRenderPipeline.ps1`.
- `XRE_VULKAN_DISABLE_PARALLEL_SECONDARY_RECORDING` can force single-threaded
  command-buffer recording for A/B isolation.

This coverage is source-backed, but the Release performance gates below still
require live editor runs on the target GPU.

## New Investigation Todos

These are the follow-up todos from the 2026-06-17 slow Vulkan default pipeline
capture and bindless/deferred-texturing audit. Keep these unchecked until there
is source change plus live evidence in this document.

- [ ] Debug the new Vulkan black-frame regression introduced after the Vulkan
  bindless updates. The current observed repro is that deferred Sponza/3D
  geometry renders black, while zooming out shows the skybox still renders. This
  is not currently a full-frame black-until-window-resize repro. Treat it as a
  geometry, deferred material, GBuffer, lighting, or descriptor correctness
  blocker for the traditional Vulkan CPU-driven path; do not defer it behind
  GPU-indirect zero-readback, instrumented, or meshlet path work.
- [ ] Keep the prior startup-size/1x1 render-resource hypothesis as a secondary
  resize-stability check only, not the active Sponza-black root-cause theory.
  The 2026-06-17 run logged skipped 0x0 viewport resizes followed by 1x1
  resource generation and then 1920x1080 supersession, but the current repro
  shows skybox output and black deferred geometry without requiring a resize.
  Source mitigation started on 2026-06-17: scene-panel render-region publication
  and the scene-panel FBO adapter now reject sub-2-pixel extents before they can
  resize the real viewport or trigger default-pipeline resource generation.
- [ ] Audit bindless leakage into traditional CPU-driven Vulkan draws. CPU-direct
  rendering must continue to use ordinary per-material Vulkan descriptors and
  non-bindless shaders unless the draw path explicitly opts into bindless
  material-table rendering.
- [x] Add an ImGui CPU frame dump button and MCP-accessible CPU/GPU profiler dump
  tools so LLM-driven runs can capture `profiler-cpu-frame-*.log` and
  per-pipeline `profiler-gpu-pipeline-*.log` files without manual UI clicks.
  Follow-up completed on 2026-06-17: the MCP CPU dump now enables profiler frame
  logging on demand when the editor preference has it disabled, waits briefly
  for a snapshot, and records that behavior in the dump response.
- [ ] Re-run the capture with actual Vulkan bindless enabled. The observed slow
  run was not a full-bindless run: it used diagnostics/CPU-direct paths and the
  global Vulkan material descriptor table was not reported ready. Capture with
  `XRE_VULKAN_BINDLESS_MATERIAL_MODE=Required`, `BindlessMaterialTable`, and
  `GpuIndirectZeroReadback` or an explicitly recorded alternative.
- [ ] Add sub-timers inside Vulkan command-buffer recording to split drain/sort,
  frame-op collection, resource-plan build, signature/hash comparison, per-pass
  frame-data prep, barrier planning, and actual Vulkan recording. The current
  `RecordCommandBuffer` bucket is too wide to isolate 65 ms stalls.
- [ ] Investigate command-buffer dirty churn after warmup. Record dirty-summary
  counts, frame-op signature deltas, planner revision changes, resource-plan
  replacements, FBO rebuilds, descriptor releases, and any `DeviceWaitIdle`
  calls in the same evidence block.
- [ ] Fix or bound async Vulkan pipeline compile saturation. The slow capture
  showed a saturated compile queue and long fragment-library create times; run
  A/B captures with shader pipeline compilation disabled/enabled and record
  active, completed, skipped, and worst-create-time counts.
- [ ] Fix missing declared framebuffer resources reported by the render graph,
  especially `MsaaLightingFBO`, `FullOverdrawCountFBO`, and the missing
  `LightCombineFBO` after execution.
- [ ] Implement a render-graph mip generation path, or a deliberate fallback, for
  auto exposure so it does not silently sample mip 0 when reduced mips are
  unavailable.
- [ ] Isolate the GPU pass stack by toggling TSR, GTAO, motion vectors,
  auto-exposure, bloom, skybox, and postprocess one at a time. Record CPU and
  GPU dump logs for each toggle so the expensive pass set is attributable.
- [ ] Reduce or justify motion-vector CPU and GPU cost. The slow capture showed a
  large CPU motion-vector render command bucket plus meaningful GPU cost; prove
  batching/culling behavior and avoid per-object work when no motion vectors are
  required.
- [ ] Separate profiler UI overhead from render-loop cost. Specifically validate
  whether `DrainRetiredResources` spikes occur only while the profiler panel is
  visible or while dump/graph collection is active.
- [ ] Investigate texture-streaming warmup and any `GLTieredTextureResidencyBackend`
  label or path that appears during Vulkan runs. Confirm whether this is naming
  only or an unintended GL-era residency path.
- [ ] Investigate Vulkan BVH picking fallback spam. Confirm whether editor
  selection/picking is forcing OpenGL-only fallback work during the default
  Vulkan profile.
- [ ] Narrow broad `AllCommandsBit` Vulkan barriers in stable default-pipeline
  passes. Record before/after barrier counts, affected resources, and GPU timing
  deltas.
- [ ] Capture a RenderDoc frame for a slow settled frame and export suspicious
  targets: shadow atlas/cascades, GTAO raw/blur/AO, lighting accumulation, bloom
  mips, TSR output, and final postprocess.
- [ ] Record Release A/B evidence against OpenGL and Vulkan with the same scene,
  camera, viewport size, render scale, mesh strategy, GPU clock policy, and
  warmed shader/resource cache.

## Current Wrap-Up Notes - 2026-06-17

Work completed in the current local pass:

- [x] Added MCP-accessible CPU profiler dump behavior that self-starts CPU frame
  logging when needed. Verified through MCP with
  `dump_cpu_frame_profile`; the first self-start dump wrote
  `profiler-cpu-frame-2026-06-17-16-21-18-691-ccb8c8c9.log`, and a later steady
  dump wrote `profiler-cpu-frame-2026-06-17-16-22-01-797-da216adf.log`.
- [x] Verified GPU per-pipeline dumps through MCP with
  `dump_gpu_render_pipeline_profile`. The latest focused dump is
  `profiler-gpu-pipeline-defaultrenderpipeline-28-2026-06-17-16-22-01-834-a0d7e0d3.log`.
- [x] Removed several hot-path LINQ/order-dependent operations from traditional
  Vulkan CPU descriptor/signature code:
  `VkMeshRenderer.Descriptors.cs`, `VkMeshRenderer.Uniforms.cs`,
  `VkMeshRenderer.cs`, and `VkRenderProgram.cs`.
- [x] Added gated Vulkan material descriptor and material auto-uniform diagnostic
  hooks under `XRE_VULKAN_MATERIAL_BINDING_DIAG=1`.
- [x] Kept the Vulkan resource-planner active-pass/resource filtering work in
  place so inactive framebuffer usages do not unnecessarily participate in the
  physical resource plan.
- [x] Built the editor after the above changes:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 0 warnings and 0 errors.

Fresh MCP/debug evidence:

- Scene mode: Unit Testing World, Vulkan, default render pipeline, forced
  `CpuDirect`, `XRE_DEFERRED_DEBUG=1`, `XRE_VK_ENABLE_AUTO_UNIFORM_REWRITE=1`,
  `XRE_VULKAN_MATERIAL_BINDING_DIAG=1`.
- Screenshot:
  `Build\McpCaptures\vulkan-frame-loop-cpu-dump-enabled\Screenshot_20260617_162115.png`.
  Result is still incorrect: sky/procedural background renders, but deferred
  Sponza geometry is black in raw albedo/debug output.
- Latest log directory:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-17_16-20-44_pid27836`.
- Latest GPU dump summary for `DefaultRenderPipeline#28`: GPU is not the primary
  bottleneck in this run. Overall p50 was about 2.95 ms, p95 about 3.37 ms, and
  average about 2.90 ms, while render-thread wall time averaged about 501 ms and
  p95 was about 981 ms.
- Steady CPU dump summary: the worst thread history is the render thread
  (`Thread 2`), latest about 191 ms, average about 163 ms, max about 1723 ms.
  The sampled hierarchy was dominated by
  `Lights3DCollection.UpdateShadowAtlasRequests` at about 162 ms total /
  160 ms self, with additional recurring `RenderCommand.Render` cost.
- Render stall logs repeatedly point at
  `Vulkan.RecordCommandBuffer.RecordPrimary`, with recovered hot paths often
  reporting `Vulkan.FrameLifecycle.DrainRetiredResources`.
- The material descriptor/auto-uniform diagnostics did not produce
  `[VkMaterialDescriptor]` or `[VkMaterialAutoUniform]` lines in the live run,
  even with `XRE_VULKAN_MATERIAL_BINDING_DIAG=1`; this needs a follow-up to
  confirm whether the traditional CPU-driven deferred material path bypasses the
  instrumented code or whether the diagnostic sink/key is not reached.

Next implementation work to resume:

- [ ] Patch or reconfigure shadow atlas reuse for the unit-testing scene. Current
  suspicion: `SubmitShadowAtlasRequest` sets `canReusePreviousFrame` to false
  for all `ELightType.Dynamic` lights, so stable test lights can force dirty
  shadow-atlas work every frame. `BuildShadowContentHash` already includes light
  movement version plus view/projection state, so the next patch should allow
  resident dynamic-light atlas tiles to reuse when the content hash is unchanged,
  or convert non-animated unit-test lights to `DynamicCached`/`Static`.
- [ ] After the shadow reuse patch, rerun MCP CPU/GPU dumps and confirm
  `Lights3DCollection.UpdateShadowAtlasRequests`, `RenderWorkLastShadowMs`, and
  render-thread p95 drop materially.
- [ ] Add narrower timers inside shadow atlas update/solve/render/publish if the
  reuse patch does not explain the 100-200 ms shadow spikes.
- [ ] Fix the material diagnostic gap so the traditional Vulkan CPU-driven
  deferred material path reports `Texture0` descriptor resolution and
  `BaseColor`/opacity auto-uniform writes.
- [ ] Continue the black Sponza investigation after diagnostics are visible.
  First split: texture descriptor failure vs auto-uniform/base-color failure vs
  shader/output-layout issue. Raw-albedo remaining black means lighting is not
  the only suspect.
- [ ] Re-run after the hot-path fingerprint cleanup with a longer settled
  capture to determine whether order-independent descriptor/frame-op hashing
  reduced command-buffer dirty churn.
- [ ] Consider enhancing `dump_cpu_frame_profile` to optionally wait for a
  minimum number of profiler snapshots or dump the worst retained frame; the
  first dump after self-enabling frame logging can be too shallow.

## Source Validation

Run these before live profiling:

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore /p:UseSharedCompilation=false
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanP1ValidationTests" /p:UseSharedCompilation=false
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~RuntimeRenderingHostServicesTests|FullyQualifiedName~RenderPipelineResourceLifecycleTests" /p:UseSharedCompilation=false
```

Expected result:

- Runtime rendering build has 0 warnings and 0 errors.
- Vulkan P1 validation tests pass. A workflow-existence assertion may skip when
  `.github/workflows/vulkan-tests.yml` is absent in the local checkout.
- Runtime host-service and render-pipeline resource lifecycle tests pass.

## Test Scene Requirements

Use a deterministic Unit Testing World scene:

- render API set to Vulkan
- default render pipeline
- one stable camera and viewport
- fixed viewport size or stable window size after warmup
- no continuous asset import, shader hot reload, scene reload, or resizing during
  capture
- no RenderDoc capture active unless running the RenderDoc escalation section
- GPU clocks in a known policy, recorded with the evidence

When comparing OpenGL and Vulkan, use the same scene, camera, viewport, render
scale, and mesh-submission strategy. Change only the render API unless the test
explicitly says otherwise.

## Release Measurement

Build the editor first:

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet build .\XREngine.Editor\XREngine.Editor.csproj -c Release /p:UseSharedCompilation=false
```

If this fails because `OpenVR.NET.dll` is missing under the Release submodule
output, build the dependency once:

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet build .\Build\Submodules\OpenVR.NET\OpenVR.NET\OpenVR.NET.csproj -c Release /p:UseSharedCompilation=false
```

Run the focused Vulkan frame-loop profile:

```powershell
pwsh .\Tools\Measure-VulkanFrameLoop.ps1 `
  -Configuration Release `
  -CacheMode Warm `
  -WarmupSec 25 `
  -CaptureSec 60 `
  -Repetitions 3 `
  -Strategies GpuIndirectZeroReadback `
  -FailOnSteadyStateResourceChurn `
  -FailOnSteadyStateCommandBufferAllocations `
  -MaxSteadyStateRecordCommandBufferAllocatedBytes 0 `
  -RunLabel vulkan-frame-loop-release
```

Optional isolation pass:

```powershell
$env:XRE_VULKAN_DISABLE_PARALLEL_SECONDARY_RECORDING = '1'
pwsh .\Tools\Measure-VulkanFrameLoop.ps1 `
  -Configuration Release `
  -CacheMode Warm `
  -WarmupSec 25 `
  -CaptureSec 60 `
  -Repetitions 3 `
  -Strategies GpuIndirectZeroReadback `
  -FailOnSteadyStateResourceChurn `
  -FailOnSteadyStateCommandBufferAllocations `
  -MaxSteadyStateRecordCommandBufferAllocatedBytes 0 `
  -RunLabel vulkan-frame-loop-release-single-thread-recording
Remove-Item Env:\XRE_VULKAN_DISABLE_PARALLEL_SECONDARY_RECORDING -ErrorAction SilentlyContinue
```

## Debug Comparison

Debug builds are useful for attribution but are not performance gates. Run a
shorter comparison only after Release is clean:

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet build .\XREngine.Editor\XREngine.Editor.csproj -c Debug /p:UseSharedCompilation=false

pwsh .\Tools\Measure-VulkanFrameLoop.ps1 `
  -Configuration Debug `
  -CacheMode Warm `
  -WarmupSec 15 `
  -CaptureSec 30 `
  -Repetitions 1 `
  -Strategies GpuIndirectZeroReadback `
  -RunLabel vulkan-frame-loop-debug
```

Use Debug only to confirm that the same buckets dominate. Do not compare absolute
Debug frame times against Release pass/fail thresholds.

## Metrics To Record

For every run, record p50, p95, p99 when available, max when available, total
counts, and the profile output path.

Required timing metrics:

- `vulkan_frame_total_ms`
- `vulkan_frame_wait_fence_ms`
- `vulkan_frame_sample_timing_queries_ms`
- `vulkan_frame_drain_retired_resources_ms`
- `vulkan_frame_acquire_image_ms`
- `vulkan_frame_acquire_bridge_submit_ms`
- `vulkan_frame_wait_swapchain_image_ms`
- `vulkan_frame_reset_dynamic_uniform_ring_ms`
- `vulkan_frame_record_command_buffer_ms`
- `vulkan_frame_submit_ms`
- `vulkan_frame_trim_ms`
- `vulkan_frame_present_ms`
- `vulkan_frame_gpu_command_buffer_ms`

Required churn and reuse metrics:

- `vulkan_record_command_buffer_allocated_bytes`
- `vulkan_command_buffer_forced_dirty_count`
- `vulkan_command_buffer_frame_op_signature_dirty_count`
- `vulkan_command_buffer_planner_dirty_count`
- `vulkan_command_buffer_profiler_dirty_count`
- `vulkan_command_buffer_dirty_summary`
- `vulkan_retired_resource_plan_replacements`
- `vulkan_retired_resource_plan_images`
- `vulkan_retired_resource_plan_buffers`
- frame-op total, per-kind counts, unique pass count, unique context count, and
  unique target count

Required correctness evidence:

- `log_vulkan.log`, `log_rendering.log`, and validation-layer messages from the
  run directory
- final viewport screenshots from at least two camera positions
- whether AO, lighting accumulation, bloom, TSR, and final postprocess look
  stable
- any RenderDoc export paths when RenderDoc is used

## Pass/Fail Gates

A Release Vulkan run passes only when all of these are true in the settled
capture window:

- no Vulkan validation errors
- no forbidden CPU fallback or GPU readback fallback events
- no steady-state resource-plan replacements
- no repeated command-buffer dirty reason caused by stable camera, stable scene,
  stable resource plan, or profiler state
- `vulkan_record_command_buffer_allocated_bytes` total is 0 unless the run is an
  explicitly documented cold-start or first-use allocation test
- `VulkanRecordCommandBufferP95Ms` is under 5 ms
- `VulkanDrainRetiredResourcesP95Ms` is under 1 ms
- submit, present, acquire, dynamic uniform reset, and trim are not the dominant
  CPU-side buckets
- screenshots from multiple camera positions show the same scene without black
  AO bands, stale render targets, or flickering post-process inputs

If these thresholds are too strict for a known low-end GPU, record the hardware,
driver, and measured baseline before changing the gate. Do not weaken a gate
because of an unexplained regression.

## Manual Editor Validation

Use this when the measurement scripts show a regression or when screenshots are
needed for visual correctness.

1. Build the editor in the configuration being tested.
2. Launch the Unit Testing World with MCP:

   ```powershell
   dotnet .\Build\Editor\Release\AnyCPU\Release\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467
   ```

   For Debug, replace both `Release` path segments with `Debug`.

3. Use MCP to set a stable camera view and capture a screenshot.
4. Move the camera to a second materially different view and capture again.
5. Inspect the PNGs, not just the tool return values.
6. Close the editor and inspect the newest log directory under `Build\Logs\`.
7. Compare the render-pass sequence and validation messages against the
   measurement output.

## RenderDoc Escalation

Use RenderDoc when MCP screenshots and logs do not identify the resource or pass
that failed.

```powershell
rdc doctor
New-Item -ItemType Directory -Force Build\RenderDoc | Out-Null
rdc capture -o Build\RenderDoc\xrengine-vulkan-frame-loop.rdc -- dotnet .\Build\Editor\Release\AnyCPU\Release\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467
```

First resources to inspect:

- directional shadow atlas/cascade depth
- `GTAORawTexture`
- `GTAOBlurIntermediateTexture`
- `AmbientOcclusionTexture`
- `LightingAccumTexture`
- bloom blur mips
- `TsrOutputTexture`
- final post-process output

Export suspicious render targets to PNG and record their paths in the evidence
log.

## Regression Triage

If `RecordCommandBuffer` regresses:

- inspect `vulkan_record_command_buffer_allocated_bytes`
- inspect command-buffer dirty counts and `vulkan_command_buffer_dirty_summary`
- compare frame-op totals and unique pass/context/target counts between good and
  bad runs
- check whether profiler activation, planner revision, render area, framebuffer
  target, or frame-op signature changed during the settled capture
- rerun once with `XRE_VULKAN_DISABLE_PARALLEL_SECONDARY_RECORDING=1` to separate
  threading overhead from recording content

If `DrainRetiredResources` regresses:

- inspect retired resource-plan replacement, image, and buffer counts
- search `log_vulkan.log` for resource-plan signature deltas
- check whether AO, light combine, bloom, TSR, or swapchain-size resources are
  being replaced after warmup
- search for unexpected `DeviceWaitIdle` calls outside startup, resize, teardown,
  or explicit capture/tooling workflows

If visual AO artifacts return:

- capture two camera positions; camera-invariant corruption usually means stale
  or uninitialized data
- inspect AO generation resources before light combine
- compare `GTAORawTexture`, blur intermediate, `AmbientOcclusionTexture`,
  `LightingAccumTexture`, and final post-process output
- verify render area, viewport, scissor, descriptor image layout, and physical
  image identity for the AO resources

## Evidence Log Template

Copy this block into the test report or PR description for each meaningful run:

```text
Date:
Commit:
Configuration:
GPU / driver:
Scene:
Camera / viewport:
Render API:
Render scale:
Mesh strategy:
GPU clock policy:
Command:
Profile output:
Log directory:
Screenshots:
RenderDoc capture:

Metrics:
- Frame total p50/p95/p99:
- RecordCommandBuffer p50/p95/p99:
- RecordCommandBuffer allocated bytes total:
- DrainRetiredResources p50/p95/p99:
- Resource-plan replacements/images/buffers:
- Command-buffer dirty summary:
- Submit/present/acquire p95:
- GPU command-buffer p95:

Decision:
Follow-up:
```

## Current Local Validation

Update this section whenever the local source validation is run.

Latest source-backed result:

- 2026-06-17:
  `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore /p:UseSharedCompilation=false`
  passed with 0 warnings and 0 errors.
- 2026-06-17:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanP1ValidationTests" /p:UseSharedCompilation=false`
  passed with 11 passed, 1 skipped, 0 failed. The skipped test was
  `P1Coverage_IsIncludedInVulkanFocusedCiLane`.
- 2026-06-17:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~RuntimeRenderingHostServicesTests|FullyQualifiedName~RenderPipelineResourceLifecycleTests" /p:UseSharedCompilation=false`
  passed with 29 passed, 0 skipped, 0 failed.
- 2026-06-17:
  `dotnet build .\Build\Submodules\OpenVR.NET\OpenVR.NET\OpenVR.NET.csproj -c Release /p:UseSharedCompilation=false`
  passed and produced the Release `OpenVR.NET.dll` required by the editor
  Release build. The submodule emitted existing warnings, including ImageSharp
  vulnerability warnings and missing XML documentation warnings.
- 2026-06-17:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -c Release /p:UseSharedCompilation=false`
  passed with 2 warnings and 0 errors. The warnings were existing unused-field
  warnings in `XREngine.Runtime.Core\Core\Diagnostics\Debug.cs`.
- 2026-06-17:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /p:UseSharedCompilation=false`
  passed with 0 warnings and 0 errors after adding the CPU/GPU profiler dump UI
  and MCP tools.
- 2026-06-17:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /p:UseSharedCompilation=false`
  passed with 0 warnings and 0 errors after adding the scene-panel 1x1 startup
  guard and nested Vulkan command-buffer recording profiler scopes.
- 2026-06-17:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~RuntimeRenderingHostServicesTests|FullyQualifiedName~RenderPipelineResourceLifecycleTests" /p:UseSharedCompilation=false`
  passed with 29 passed, 0 skipped, 0 failed after the same changes.
- 2026-06-17:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 0 warnings and 0 errors after the Vulkan descriptor/signature
  hot-path cleanup and MCP CPU-dump self-start behavior.

Latest live GPU result:

- Release Vulkan measurement: not yet recorded in this document revision
- Manual MCP screenshots:
  `Build\McpCaptures\vulkan-frame-loop-cpu-dump-enabled\Screenshot_20260617_162115.png`
  confirms sky/procedural output renders while deferred Sponza geometry remains
  black in raw albedo/debug output.
- Manual MCP GPU dump:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-17_16-20-44_pid27836\profiler-gpu-pipeline-defaultrenderpipeline-28-2026-06-17-16-22-01-834-a0d7e0d3.log`.
  Default pipeline GPU cost was about p50 2.95 ms, p95 3.37 ms, avg 2.90 ms;
  render-thread wall time was still about avg 501 ms and p95 981 ms.
- Manual MCP CPU dump:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-17_16-20-44_pid27836\profiler-cpu-frame-2026-06-17-16-22-01-797-da216adf.log`.
  Dominant sampled CPU cost was
  `Lights3DCollection.UpdateShadowAtlasRequests` at about 162 ms total /
  160 ms self.
- RenderDoc capture: not run; only required if logs/screenshots are ambiguous
