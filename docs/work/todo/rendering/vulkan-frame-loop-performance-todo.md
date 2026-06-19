# Vulkan Frame Loop Performance Testing

Last Updated: 2026-06-18
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

- [x] Debug the Vulkan deferred-Sponza fully-black regression introduced after
  the Vulkan bindless updates. The confirmed repro was black Sponza/3D geometry
  with sky/background still visible, not a full-frame black-until-window-resize
  issue. Evidence from
  `Build\McpCaptures\vulkan-black-regression-check\Screenshot_20260618_123624.png`
  showed black Sponza geometry while `AmbientOcclusionTexture` still contained
  visible geometry. Root-cause split on 2026-06-18: enabling dense imported
  texture residency restored textured `AlbedoOpacity` and nonblack output.
  Follow-up source stabilization keeps Vulkan dense imported texture promotions
  active by default and limits Vulkan imported-texture resident transitions to
  one resident mip until the upload queue is moved off one-shot synchronous
  submits. No-env visual gates after the fix:
  `Build\McpCaptures\vulkan-visual-gate-default-dense-residency-fix\Screenshot_20260618_125652.png`,
  `Build\McpCaptures\vulkan-visual-gate-default-dense-residency-fix\RenderPipeline_AlbedoOpacity_20260618_125654.png`,
  `Build\McpCaptures\vulkan-visual-gate-preview-mip-fix\Screenshot_20260618_130243.png`,
  and
  `Build\McpCaptures\vulkan-visual-gate-preview-mip-fix\RenderPipeline_AlbedoOpacity_20260618_130246.png`.
  Remaining Sponza darkness is tracked separately below as an AO/global-ambient
  issue.
- [ ] Keep the prior startup-size/1x1 render-resource hypothesis as a secondary
  resize-stability check only, not the active Sponza-black root-cause theory.
  The 2026-06-17 run logged skipped 0x0 viewport resizes followed by 1x1
  resource generation and then 1920x1080 supersession, but the current repro
  shows skybox output and black deferred geometry without requiring a resize.
  Source mitigation started on 2026-06-17: scene-panel render-region publication
  and the scene-panel FBO adapter now reject sub-2-pixel extents before they can
  resize the real viewport or trigger default-pipeline resource generation.
- [ ] Track the remaining deferred Sponza darkness as a separate AO/global
  ambient issue, not the active framerate investigation. Current working
  observation from 2026-06-18: the skybox renders, there is no confirmed
  black-until-resize full-scene repro, no light probes are captured in this
  test scene, so GI is absent by design, and the global ambient term should
  still light dark regions. Resume this after the CPU-direct Vulkan framerate
  path is back under control.
- [ ] Use Vulkan visual-output regression gates for every CPU-direct framerate
  patch. A performance change is not complete unless a post-change MCP capture
  proves the default pipeline still produces visible Sponza geometry, or the
  capture is explicitly recorded as the same pre-existing AO/global-ambient
  issue with no worsening. Required captures: viewport screenshot from at least
  two camera positions, `AlbedoOpacity`, `Normal`, `RMSE`, `DepthView`,
  `AmbientOcclusionTexture`, `LightingAccumTexture`, `HDRSceneTex`,
  `FinalPostProcessOutputTexture`, and the active AA output (`FxaaOutputTexture`
  or `TsrOutputTexture`).
- [x] Diagnose the directional-light shadow-map flicker observed during Vulkan
  CPU-direct framerate iteration. User evidence on 2026-06-18 showed a frame
  where the directional shadow contribution appeared to flicker off while the
  rest of the deferred scene continued rendering. Matching log evidence from
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_13-24-40_pid45296\log_lighting.log`
  showed the dynamic shadow atlas repeatedly deferring 7-10 shadow requests
  after rendering only 1-4 tiles, with a 2 ms shadow budget. Root cause: dirty
  directional cascades that missed refresh could publish `ActiveFallback=Lit`,
  making receiver bindings disable the cascade for that frame. Source fix:
  directional atlas requests with a previously rendered tile now keep
  `ShadowFallbackMode.StaleTile` sampleable until their dirty refresh actually
  renders. Local validation passed through
  `ShadowAtlasManagerPhaseTests` and the editor build. Live visual confirmation
  is still required on the next MCP/editor run.
- [ ] Add a live shadow-flicker regression gate before accepting further
  Vulkan CPU-direct framerate fixes: enable directional shadow audit when
  needed, capture the directional atlas output while moving/settling the camera,
  and confirm dirty directional cascades no longer produce one-frame
  `DirectionalShadowAtlasEnabled=false`/lit fallback flashes.
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
- [x] Live-validate the latest camera-motion editor crash fix. The 2026-06-18
  crash logs point to Vulkan device loss during dense Sponza imported-texture
  upload, not ImGui itself. Source mitigation now keeps Vulkan dense imported
  texture promotions to one resident mip until a synchronized Vulkan upload
  queue/render-graph path exists. Required evidence: rebuild editor, move the
  Unit Testing World camera enough to force Sponza texture promotions, confirm
  the ImGui editor remains visible, confirm frames continue rendering, capture
  CPU/GPU profiler dumps, and verify no `ErrorDeviceLost` appears in
  `log_vulkan.log`. Short live validation completed on 2026-06-18 with editor
  PID 10844 and log session
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_14-10-25_pid10844`.
- [ ] Run a longer manual/editor soak if the user can still reproduce the
  camera-motion crash after the single-mip Vulkan dense-promotion mitigation.
  The current MCP validation covered 45 automated camera moves and did not
  reproduce device loss, but it is not a multi-minute manual navigation soak.
- [ ] Investigate Vulkan BVH picking fallback spam. Confirm whether editor
  selection/picking is forcing OpenGL-only fallback work during the default
  Vulkan profile.
- [ ] Investigate user-observed Vulkan camera-motion black frames. The settled
  frame can now be nonblack after the FXAA binding fix, but moving the camera can
  produce black frames until the camera settles again. Static MCP screenshots are
  not enough to validate this; capture during a camera transition or use a
  streamed/manual observation path. Local 2026-06-17 evidence found and fixed a
  camera-motion resource-plan replacement path; user confirmation is still
  needed because MCP captures do not include the full ImGui/frontbuffer view.
- [ ] Fix Vulkan instanced debug primitive orientation. User observation on
  2026-06-17: instanced debug shapes look Y-flipped under Vulkan. A Vulkan-only
  debug-shader Y mirror was removed locally; user confirmation is still pending.
- [ ] Add a full editor/window capture path for presentation/UI flicker
  debugging. `capture_viewport_screenshot` reads the renderer viewport/present
  source and can omit ImGui editor chrome/frontbuffer timing.
- [x] Fix Vulkan ImGui overlay cadence and corrupt/stale UI flicker. Source
  diagnosis: ImGui draw data was stored as a single latest snapshot in
  `VulkanRenderer.ImGui`, then consumed only while the reusable Vulkan primary
  swapchain command buffer was recorded. Clean command-buffer reuse could
  present the scene at normal cadence while the overlay rotated through stale
  per-swapchain-image UI snapshots. Implemented proper fix on 2026-06-18:
  allocate one primary ImGui overlay command buffer per swapchain image, record
  it every presented frame in `Vulkan.FrameLifecycle.RecordImGuiOverlay`, submit
  it after the cached scene primary and before present, and remove the old
  in-primary `RenderImGui(commandBuffer, imageIndex)` path. Vulkan dynamic
  rendering overlays now transition the swapchain image
  `PresentSrcKhr -> ColorAttachmentOptimal -> PresentSrcKhr`; legacy render-pass
  overlays use the load-preserving swapchain render pass. `VulkanFeatureProfile`
  now reports ImGui enabled for Vulkan profiles so diagnostics no longer say
  `ImGui=False` while the renderer supports it. Added source-contract coverage in
  `VulkanImGuiOverlay_RecordsOutsideReusableScenePrimary`.
  Validation:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false /v:minimal`
  passed with 0 warnings/errors; exact test
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanImGuiOverlay_RecordsOutsideReusableScenePrimary" /m:1 /nodeReuse:false /p:UseSharedCompilation=false /v:minimal`
  passed. A short Vulkan Unit Testing World smoke run stayed alive for 25s and
  was deliberately stopped; fresh log session
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_15-22-03_pid14064`
  reports `ImGui=True` and the filtered logs show no VUIDs, validation errors,
  device loss, queue-submit failures, or ImGui overlay recording failures.
  Remaining evidence needed: manual full-window observation while moving the
  editor camera, because MCP viewport screenshots do not capture the full ImGui
  frontbuffer/chrome cadence.
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
- Correction for later runs: `XRE_DEFERRED_DEBUG=1` deliberately forced raw
  G-buffer albedo while debugging the black deferred path. That mode makes
  Sponza look albedo-only by design. Framerate runs must clear it with
  `XRE_DEFERRED_DEBUG=0` or no env var and should verify
  `DeferredDebugView == 0` before treating the viewport as normally lit.
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

## Live Investigation Progress - 2026-06-18

Problem statement:

- Focus remains the traditional Vulkan CPU-driven path with
  `XRE_FORCE_MESH_SUBMISSION_STRATEGY=CpuDirect`.
- Regression guard: deferred Sponza is fully black again in the latest MCP
  evidence. Screenshots
  `Build\McpCaptures\vulkan-recordprimary-scopes\Screenshot_20260618_122533.png`
  and
  `Build\McpCaptures\vulkan-recordprimary-detail\Screenshot_20260618_123216.png`
  are full black frames. Treat nonblack scene output as a hard validation gate
  for every framerate change; do not accept a CPU/GPU timing improvement unless
  the same iteration also proves the viewport or final pipeline target is
  presentably nonblack.
- Earlier over-dark Sponza/AO/global-ambient work remains separate, but the
  current fully black regression is not paused. Keep performance patches
  constrained to CPU-direct recording unless they are explicitly debugging the
  black-output path, and validate visual output immediately after each patch.
- The editor process for the latest MCP iteration exited after profiler capture;
  local engine logs do not contain a managed exception, device-lost marker, or
  validation error at the end of the run.

Attempted solutions in the current pass:

- [x] Added skipped-resize-frame timestamp accounting in
  `Drawing.Core.cs`. Resize/zero-surface skips now call
  `MarkSkippedResizeFrameObserved(...)` before returning, so minimized or
  invalid-surface frames do not poison later "gap since last completed frame"
  diagnostics.
- [x] Added bounded per-frame retired-resource draining in
  `Drawing.ResourceRetirement.cs`. Normal frames now drain a capped number of
  retired descriptor pools, pipelines, framebuffers, buffers, and images per
  frame-slot visit, while explicit force-flush paths remain unbounded.
- [x] Added env-gated Vulkan primary-recording detail scopes in
  `CommandBuffers.cs`. `XRE_VULKAN_RECORDING_PROFILE_DETAIL=1` now splits the
  primary recording loop by frame-op category; the latest detailed run points at
  `Vulkan.RecordPrimary.Op.MeshDraw` as the dominant CPU-direct recorder cost.

Validation evidence:

- 2026-06-18:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 0 warnings and 0 errors after the skipped-frame timestamp and
  retired-resource drain-budget patches.
- Latest live run:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_11-33-15_pid45776`.
- Launch mode: Debug editor, Unit Testing World, MCP enabled, Vulkan,
  `CpuDirect`, `XRE_DEFERRED_DEBUG=0`, and no Vulkan progressive texture upload
  override.
- MCP screenshot:
  `Build\McpCaptures\vulkan-retirement-budget\Screenshot_20260618_113434.png`.
  It still shows visual AO/ambient-lighting problems, but the viewport is not a
  full black frame.
- MCP CPU dump:
  `profiler-cpu-frame-2026-06-18-11-34-34-025-54c9a7ba.log`. This dump was too
  shallow (`ThreadHistoryCount=1`, `TotalThreadMs=2.247`) and reinforces the
  need for a worst-retained-frame CPU dump mode.
- MCP GPU dump:
  `profiler-gpu-pipeline-defaultrenderpipeline-28-2026-06-18-11-34-34-031-fc438c9b.log`.
  The default pipeline GPU timing is not the bottleneck: tracked frames were
  around p50 5.2 ms and p95 6.1 ms, while render-thread wall time stayed much
  higher.

Issues found:

- The retired-resource drain cap fixed the explicit drain cliff in the sampled
  MCP state: `vulkan.frame_lifecycle.drain_retired_resources_ms` was about
  `0.005 ms` after the patch.
- The cap exposed a root-cause churn problem instead of solving it. Startup
  still created many `DefaultRenderPipeline` instances and resource generations
  before settling on `DefaultRenderPipeline#28`, including 2048x2048,
  4096x2048, 4096x4096, 1024x1024, 1x1, and finally 1537x865 profiles.
- `log_rendering.log` shows a late `FrameProfileChanged` rebuild on
  `DefaultRenderPipeline#28` with `Delta=vulkanSafe:False->True`, followed by
  incremental generation work.
- `XRRenderPipelineInstance.DestroyCache` ran on the render thread during the
  same startup window, and `log_vulkan.log` recorded
  `DeviceWaitIdle before render-pipeline physical resource destruction:
  DestroyCache` twice. This is acceptable during teardown only; during editor
  startup/profile settling it creates expensive render-thread stalls and
  resource churn.
- Several `ResourcePlanReplacement` descriptor-reference releases happened
  during startup, touching up to 150 mesh renderers and 272 materials.
- Fresh FPS-drop evidence points at `Vulkan.RecordCommandBuffer.RefreshFrameData`
  and `Vulkan.RecordCommandBuffer.RecordPrimary` as the dominant CPU-side hot
  paths, with the collect-visible thread mostly blocked in `WaitForRender`.
- The latest `profiler-fps-drops.log` still reports `RenderWorkShadowQueueDepth`
  at 9 on slow frames, but `Lights3DCollection.UpdateShadowAtlasRequests` is now
  single-digit to low-double-digit milliseconds rather than yesterday's 160 ms
  self-time.
- Latest detailed CPU attribution run:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_12-31-31_pid1252`.
  `profiler-fps-drops.log` repeatedly identifies
  `Vulkan.RecordPrimary.MainOpLoop > Vulkan.RecordPrimary.Op.MeshDraw` as the
  worst hot path, including samples around 88 ms, 135 ms, 226 ms, and 232 ms.
  Texture work queues were mostly empty at capture time, so the next
  CPU-direct optimization target is per-draw mesh recording, not GPU-indirect,
  meshlet, or progressive texture upload paths.
- The same detailed run produced no `VUID`, validation, managed exception,
  device-lost, or fatal log markers, but its MCP screenshots were fully black.
  This makes the run useful for CPU hot-path attribution only; it is not an
  acceptable rendering validation baseline.
- The 11:33 run ended without an in-engine crash signature. Windows Application
  events in the broader period show earlier `LiveKernelEvent 141` GPU watchdog
  reports at 11:19, but no matching managed `.NET Runtime` or `Application
  Error` event for the 11:35 process exit. Treat this crash as inconclusive
  until a reproducible run captures either a Vulkan device-lost marker, WER app
  crash, or GPU watchdog event at the same timestamp.
- MCP `dump_gpu_render_pipeline_profile` reports a filename with an underscore
  between date and time in its structured response, while the actual file uses
  hyphens. This is a tooling paper cut for LLM-driven log lookup.

Next implementation work to resume:

- [ ] Stop unnecessary default-pipeline cache destruction and physical-resource
  rebuilds while the editor viewport/profile is still settling. First targets:
  the `vulkanSafe:False->True` profile transition, repeated
  `DefaultRenderPipeline#N` startup instances, and render-thread
  `XRRenderPipelineInstance.DestroyCache` jobs.
- [ ] Remove or defer render-thread `DeviceWaitIdle` from
  `DestroyCache`/resource replacement paths where retire queues and fences can
  safely own destruction.
- [ ] Prevent 1x1 or pre-layout render-resource generations from creating real
  Vulkan image/FBO work for the default pipeline. The existing scene-panel guard
  is not sufficient; the 2026-06-18 run still logged a 1x1
  `DefaultRenderPipeline#28` pending generation before superseding to 1537x865.
- [ ] Add a profiler dump mode that writes the worst retained CPU frame rather
  than the most recent shallow snapshot.
- [ ] Split `Vulkan.RecordCommandBuffer.RefreshFrameData` and
  `Vulkan.RecordCommandBuffer.RecordPrimary` further if cache/rebuild churn is
  fixed but frame recording remains above budget.
- [ ] Reduce traditional Vulkan CPU-direct `MeshDraw` recording cost without
  changing shader selection, material descriptor semantics, bindless opt-in
  rules, or deferred output. First suspect: per-draw mapped uniform writes in
  `VkMeshRenderer`, which currently map/unmap host-visible buffers during
  `UpdateEngineUniformBuffersForDraw` and `UpdateAutoUniformBuffersForDraw`.
- [ ] After any `MeshDraw` optimization, immediately rerun Unit Testing World
  Vulkan `CpuDirect` with deferred debug disabled, capture MCP viewport/final
  pipeline output, and mark the attempt invalid if the frame remains fully
  black.
- [ ] Reproduce the editor exit with a fresh run and capture Windows event log,
  engine logs, and GPU watchdog timing immediately afterward.

## Live Investigation Progress - 2026-06-17 Evening

Problem statement:

- Deferred Sponza geometry is still rendering black under Vulkan in the Unit
  Testing World while debugging the default pipeline with `CpuDirect`.
- Vulkan frame-loop CPU performance was previously poor, with large render
  thread spikes and shadow-atlas work dominating sampled CPU dumps.
- GPU pipeline timing is currently unavailable in the newest live editor run:
  `dump_gpu_render_pipeline_profile` reports no captured GPU timing history.

Issues found so far:

- Stable dynamic lights were treated as unable to reuse previous shadow atlas
  tiles. `SubmitShadowAtlasRequest` forced dynamic light shadow work every frame
  even when the published allocation was resident and the content hash matched.
- Default AO pass setup was invalidating `LightCombineFBO` through
  `DependentFboNames`, deleting a resource that the resource-generation path
  expected to remain declared. This produced repeated `LightCombineFBO` missing
  warnings and skipped the deferred light-combine path.
- Vulkan readback tried to transition post-present swapchain images for startup
  luminance and MCP screenshots. Validation reported presentable images being
  used after `vkQueuePresentKHR` without a fresh acquire.
- The first post-readback-fix MCP captures were all black and the MCP camera
  call used the wrong argument shape. The tool requires scalar
  `position_x/y/z` and `look_at_x/y/z`, so those captures are not yet reliable
  multi-camera evidence.
- The final postprocess target was not black, but the final presented Vulkan
  target was `FxaaOutputTexture`, which was solid black. The FXAA shader sampled
  `PostProcessOutputTexture` while the command populated material texture slot 0
  with `FinalPostProcessOutputTexture`; Vulkan descriptor resolution could not
  use the late OpenGL-style `program.Sampler(...)` callback to fix that mismatch.
- `VPRC_FXAA` cleared and re-added the same material texture every execution.
  That increments `XRMaterialBase.BindingLayoutVersion`, which is part of the
  Vulkan pipeline hash and can contribute to repeated pipeline-cache misses.
- After the FXAA fix, the live log still shows repeated
  `Rebuilding framebuffer 'FxaaFBO' before render pass because attachment views
  or dimensions changed` warnings during a very low frame cadence run. This is
  now the active performance trail.
- User observed that moving the camera causes black frames until the camera
  settles again, despite settled captures showing nonblack output. This suggests
  a motion-dependent path such as motion vectors, temporal/history invalidation,
  command-buffer dirty/re-record cadence, or transient render-target rebuild
  state.
- User observed instanced debug shapes rendering Y-flipped under Vulkan. Treat
  debug primitive clip-space policy as a separate visual correctness bug from
  the Sponza/FXAA black-output bug.
- MCP viewport screenshots were vertically inverted relative to the user's live
  Vulkan viewport. The capture path used the renderer readback image row order
  and hard-coded `VulkanRenderer.ScreenshotRequiresVerticalFlip` to `false`,
  even though the default Vulkan/Y-up path needs PNG export correction.
- MCP viewport screenshots are not full editor-window screenshots. They capture
  the renderer viewport/present-source image and can omit ImGui editor chrome
  and frontbuffer/presentation timing. Use them for render-target evidence, not
  as sole proof for interactive-window black flashes.
- Camera movement after warmup caused a mid-run Vulkan physical resource-plan
  replacement: the planner saw only `resource-registry` changes at the same
  viewport size and pass graph, then waited for device idle and rebuilt about 20
  FBOs. The trigger was `TrackBufferBinding` registering ordinary mesh/debug
  instance buffers into the current render resource registry; instanced debug
  line buffers could therefore mutate allocation descriptors during motion.
- The physical allocation signature included the whole render-resource registry,
  including framebuffer descriptors. This made FBO metadata changes eligible to
  replace all physical images even when the image/buffer allocation shape did
  not need to change.

Solutions attempted:

- Allowed stable dynamic lights to reuse previous-frame shadow atlas tiles when
  the previous allocation is resident and its content hash matches. Local build
  validation passed for runtime rendering and the editor. Live evidence: the
  next CPU dump dropped the previous `Lights3DCollection.UpdateShadowAtlasRequests`
  dominance; later dumps can still be too shallow if captured immediately after
  profiler activation.
- Removed AO `DependentFboNames = new[] { LightCombineFBOName }` assignments from
  `DefaultRenderPipeline` AO configuration. Local editor build passed. Live
  evidence: the next run stopped reporting `LightCombineFBO` declared-resource
  missing warnings, but Sponza remained black.
- Added a renderer hook for the final window-present source and taught Vulkan
  readback to capture from the tracked final render target instead of a
  post-present swapchain image. Local editor build passed. Live evidence: MCP
  screenshots still write PNGs from the tracked source, but the captured final
  target is currently black. Validation-error scan is still in progress for the
  latest PID `29788` run.
- Changed `Build/CommonAssets/Shaders/Scene3D/FXAA.fs` to sample material slot
  `Texture0` and changed `VPRC_FXAA` to bind `Texture0`. Also guarded the FXAA
  material texture update so it does not clear/re-add the same source texture
  every frame. Local editor build passed with 0 warnings and 0 errors. Live
  evidence from PID `31076`: `FinalPostProcessOutputTexture` average RGB
  `0.55130714`, `FxaaOutputTexture` average RGB `0.5498744`, and
  `Build\McpCaptures\vulkan-frame-loop-after-fxaa-texture0\Screenshot_20260617_183359.png`
  is visibly nonblack. `log_vulkan.log` had 0 `VUID` matches and 0 `ERROR`
  matches; the FXAA descriptor now resolves `Texture0` to
  `FinalPostProcessOutputTexture`.
- Removed the Vulkan-only Y mirror from
  `Build/CommonAssets/Shaders/Common/Debug/helper/DebugPerVertex.glsl`. Local
  editor build passed with 0 warnings and 0 errors. Live screenshots remain
  useful only after the MCP capture-orientation fix below; user confirmation is
  still needed for the real viewport.
- Corrected Vulkan screenshot export after the MCP capture orientation
  regression: `ScreenshotRequiresVerticalFlip` is CPU image export row-order
  policy, not framebuffer texture UV sampling policy. Vulkan readback now
  hard-codes no additional PNG flip again, while shader paths continue using
  `RenderClipSpacePolicy.FramebufferTextureYDirection(...)`.
- Preserved existing pipeline framebuffer descriptors when Vulkan re-registers
  live framebuffers, avoiding descriptor churn from declarative
  `InternalResolution` FBOs becoming absolute-pixel descriptors.
- Split Vulkan physical-allocation signatures from full registry signatures:
  allocator replacement now hashes texture/view/buffer descriptors plus physical
  usage, not framebuffer descriptors. Planner/barrier signatures can still react
  to FBO metadata changes without forcing image replacement.
- Restricted `TrackBufferBinding` so arbitrary mesh, vertex, index, and
  instanced debug buffers are kept in the side map but are only rebound into the
  render-resource registry when a matching pipeline buffer descriptor already
  exists. This prevents camera-motion debug buffers from mutating the pipeline
  resource plan.
- Live validation after the buffer-registry fix, PID `1992`, log directory
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-17_19-21-49_pid1992`:
  a post-warm 2.5-second MCP camera move produced 0 planner signature changes,
  0 physical resource-plan changes, 0 device-idle waits, 0 physical image handle
  changes, and 0 FBO rebuilds. A second post-warm move also had 0 resource-plan
  churn and all sampled captures had average brightness around `0.55` with
  `0.0%` near-black pixels. Remaining motion-time noise was pipeline cache
  misses/async compiles for cold material/debug variants.
- Tried to regenerate MCP docs after adding the render-pipeline capture tools.
  `pwsh` is not installed locally, and the Windows PowerShell fallback script
  failed because `docs\features\mcp-server.md` is absent in this checkout.

Suggested but not yet attempted:

- Ask the user to verify whether the latest Vulkan viewport still flashes black
  during manual camera movement after the buffer-registry fix. MCP evidence no
  longer shows resource-plan/FBO churn during camera motion, but MCP does not
  capture the full ImGui/frontbuffer path.
- Ask the user to verify whether instanced debug shapes are still Y-flipped in
  the actual viewport after removing the debug shader mirror. MCP captures were
  corrected separately and are not a substitute for this human-observed issue.
- Add a true editor-window/presentation capture path if black frames are still
  visible only in the interactive ImGui viewport. Options: RenderDoc frame
  capture during motion, or a dedicated Windows/client-area screenshot tool.
- Investigate repeated Vulkan pipeline cache misses during movement. Local
  evidence suggests these are per-owner cold variants for debug lines, motion
  vectors, and material variants; they are sub-millisecond async compiles, but
  should be verified after longer warmup.
- Capture CPU profiler dumps from the fixed-black run and compare the render
  thread buckets with the earlier shadow-atlas-dominated dump.
- Capture two materially different camera positions now that the final viewport
  is nonblack, to verify the result changes with camera movement and is not a
  stale target.
- If performance logs remain ambiguous, add narrower instrumentation around FBO
  rebuild decisions and resource-plan replacement reasons.
- If final output regresses to black, capture intermediate resources in this
  order: GBuffer albedo/base color, depth, `LightingAccumTexture`,
  `FinalPostProcessOutputTexture`, and FXAA/SMAA output.
- If logs and MCP captures do not isolate the bad pass/resource, take a RenderDoc
  capture and export GBuffer, lighting accumulation, and final postprocess
  textures.

User feedback on attempted solutions:

- No explicit user report yet that any attempted fix worked or failed. Current
  local evidence says the shadow-atlas spike and `LightCombineFBO` warning were
  improved, and the Vulkan all-black presented Sponza output is fixed locally.
  Low Vulkan frame cadence and repeated `FxaaFBO` rebuild warnings remain open.
- 2026-06-17 user feedback: settled output can be nonblack, but camera motion
  still causes black frames until motion stops; instanced debug shapes appear
  Y-flipped under Vulkan. Continue validating with motion captures.
- 2026-06-17 user feedback: MCP captures were upside down compared with the live
  viewport, and the user questioned whether capturing without ImGui editor chrome
  was intentional. Treat MCP viewport captures as render-target captures, not
  full editor-window captures, until a dedicated full-window path exists.
- 2026-06-18 follow-up: MCP captures became upside down again because Vulkan
  `ScreenshotRequiresVerticalFlip` drifted back to framebuffer texture sampling
  direction. Source fix restored Vulkan screenshot export to no CPU vertical flip
  and documented that this flag is independent from shader UV policy.

Profiler capture rule for remaining Vulkan CPU-direct performance iterations:

- Before measuring framerate, verify deferred debug view is disabled. Mode `1`
  is raw G-buffer albedo in `DeferredLightCombine.fs`; leaving it enabled makes
  the scene look like unlit/albedo-only Sponza and invalidates visual lighting
  conclusions. Use `XRE_DEFERRED_DEBUG=0` for launched processes and set
  `DeferredDebugView` to `0` through preferences/MCP if a live editor reports a
  nonzero value.
- Every live perf iteration must record a CPU profiler dump. If CPU frame
  logging was disabled, the first MCP dump may only arm the logger; take a
  second post-warm dump and use that second file as the timing evidence.
- Every live perf iteration must also attempt a GPU render-pipeline profiler
  dump. If no GPU history is captured, record the exact MCP/tool error plus the
  profile-stat fields that explain it, especially
  `gpu_pipeline_profiling_enabled` and `gpu_pipeline_status`.
- Do not treat a run as fully measured when CPU evidence exists but GPU timing
  silently failed. Record it as "CPU-only; GPU profiler unavailable" and keep a
  follow-up to repair profiler enablement.

Latest Vulkan CPU-direct framerate pass:

- Added CPU attribution scopes around command `ShouldExecuteThisFrame` checks
  and container `EnsureResourcesAllocated` calls. Local editor Debug build
  passed with 0 warnings and 0 errors. Evidence from PID `10276`, log directory
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-17_20-34-42_pid10276`:
  `VPRC_RenderMeshesPass` still dominated one CPU dump at about 27.8 ms, while
  `ShouldExecuteThisFrame` and `EnsureResourcesAllocated` were each near zero.
  This rules out the simple skip-predicate/resource-ensure path as the hidden
  container self-time bottleneck.
- GPU profiling in PID `10276` was attempted but failed with
  `No GPU timing history has been captured for any render pipeline.` Profile
  stats reported `gpu_pipeline_profiling_enabled=false` and
  `gpu_pipeline_status="GPU render-pipeline command timing is disabled."`
  This run is therefore CPU-only evidence.
- PID `10276` showed the actual runtime bootstrap still requesting 4096x4096
  shadow resources, so the earlier editor unit-test lighting resolution change
  did not affect the active bootstrap path. The 80-sample window reported about
  0.99 fps, render dispatch average about 1003.8 ms, FBO bandwidth about
  1.78-1.94 GB, and allocated VRAM about 933 MB.
- Patched `BootstrapLightingBuilder` to use 1024x1024 directional shadow maps
  for Vulkan unit-testing bootstrap, matching the editor unit-test lighting
  helper. Local editor Debug build passed with 0 warnings and 0 errors.
- Post-patch evidence from PID `16520`, log directory
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-17_20-41-48_pid16520`:
  `log_rendering.log` confirmed `ShadowRenderPipeline` resources at
  1024x1024. The post-warm profile-stat window improved to about 4.96 fps,
  render dispatch average about 200.9 ms, FBO bandwidth about 335-352 MB,
  resident texture bytes about 336 MB, and allocated VRAM about 463 MB. This is
  a real local improvement, but still far from acceptable framerate.
- CPU dump
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-17_20-41-48_pid16520\profiler-cpu-frame-2026-06-17-20-43-37-660-6389f342.log`
  showed `XRWindow.RenderViewports` at about 79.6 ms in the current hierarchy,
  with `VPRC_RenderMotionVectorsPass` at about 26.1 ms and the parent
  `ViewportRenderCommandContainer.Execute` still carrying about 38.1 ms self
  time. This dump occurred immediately after a render-profile transition from
  FXAA to TSR, so motion-vector cost may be TSR/profile-churn related rather
  than the stable FXAA baseline.
- GPU profiling in PID `16520` was attempted by setting
  `Debug.EnableGpuRenderPipelineProfiling=true`, waiting for history, and
  calling `dump_gpu_render_pipeline_profile`, but it still failed with no GPU
  timing history. Profile stats never reported
  `gpu_pipeline_profiling_enabled=true`. Next profiler task: find why the MCP
  preference write does not affect the active render-pipeline GPU profiler
  switch or whether another project/runtime override disables it.
- New active performance trail: the run changed render profile mid-session
  (`Fxaa->Tsr`, later feature and HDR changes), triggering multi-second default
  pipeline rebuilds and making the CPU dump less representative of a stable
  scene. Before another framerate fix, force or stabilize the Unit Testing World
  render profile so the Vulkan CPU-direct path can be measured without TSR/HDR
  churn.
- Added profile-capture manifest fields for deferred debug state. Current
  framerate launches now pin `XRE_DEFERRED_DEBUG=0`, set MCP
  `Debug.DeferredDebugView=0`, and verify those values in
  `profiler-capture-manifest.json` before trusting the visual output. This
  answers the albedo-only Sponza question: any earlier albedo-only view came
  from the intentional deferred debug mode, not from the normal lit path.
- Added Unit Testing World `VSyncOverride = Off` plumbing through bootstrap
  settings and regenerated the unit-testing schema. Local targeted unit tests
  passed. Live MCP state in PID `34324` reported `vSync:"Off"` and
  `targetRenderHz` near 60 Hz, so the current Vulkan CPU-direct low framerate is
  no longer explained by a hidden VSync override.
- Skipped the velocity/motion-vector pass unless a velocity consumer is active:
  vendor upscale, TSR, TAA, DLAA, or motion blur. Local editor Debug build
  passed with 0 warnings and 0 errors. Live validation in PID `34324`, log
  directory
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-17_21-14-43_pid34324`,
  showed no `MotionVectors`/`VelocityFBO` rows in the GPU profile. Compared with
  PID `36124`, default-pipeline render-thread wall time improved from about
  115.7 ms average / 157.7 ms p95 to about 24.9 ms average / 40.2 ms p95.
  Warmup-excluded render-thread time improved from about 112.8 ms average /
  156.6 ms p95 to about 23.0 ms average / 36.4 ms p95.
- Remaining active bottleneck after velocity gating: repeated primary command
  buffer recording. PID `34324` profile stats show recurring
  `vulkan_command_buffer_frame_op_signature_dirty_count=1`,
  `vulkan_command_buffer_dirty_summary:"frame-ops"`, and
  `vulkan_record_command_buffer_allocated_bytes` around 1.6-2.1 MB on dirty
  frames. Adjacent clean-reuse frames exist, so command-buffer reuse works in
  principle but is invalidated by a changing frame-op signature. Next run should
  set `XRE_VULKAN_FRAMEOP_SIGNATURE_DIFF=1` with a small diff limit to identify
  the exact signature component changing.

Current stop point - 2026-06-18:

- Rendering status: normal lit Sponza is back in Vulkan CPU-direct when
  deferred debug is disabled. Latest screenshot:
  `Build\McpCaptures\vulkan-cpudirect-primary-variant-cache\Screenshot_20260618_014646.png`.
  The image is visibly lit and not the earlier albedo-only/debug view.
- Fixed frame-op state leakage that caused continuous command-buffer signature
  churn:
  - captured/restored Vulkan fixed-function state while collecting material
    program binding snapshots
  - canonicalized disabled material blend state so disabled blend no longer
    inherits stale factors
  - defaulted fullscreen/utility quad stencil to disabled when the material did
    not explicitly request a stencil state
- Evidence after the stencil/default-state fixes:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_01-03-25_pid27544`.
  A 1342-row steady tail showed 0 primary records, 1342 clean reuses,
  render dispatch p50 about 14.5 ms, Vulkan frame total p50 about 4.1 ms, and
  `RecordCommandBuffer` p50 about 3.4 ms. This proved the frame loop can reuse
  command buffers once the state signature is stable.
- Dynamic UI correctness/perf trail:
  - Reusing a single per-image dynamic UI secondary reduced CPU recording cost
    but validation showed cached primaries were submitting secondaries that had
    been re-recorded or invalidated.
  - Forcing the primary dirty whenever dynamic UI changed removed that specific
    stale-secondary behavior but regressed to constant primary recording because
    the same swapchain image alternated between two stable normalized frame-op
    sets.
  - Latest diff diagnostics showed repeated alternation between about 187 and
    239 static frame ops; this was not only batched UI text. A one-primary-per-
    swapchain-image cache is too narrow for the editor path.
- Current implementation attempt: added a small per-swapchain-image primary
  command-buffer variant cache keyed by normalized static frame-op signature and
  dynamic UI signature. Each variant owns its own dynamic UI secondary command
  buffer, and command-buffer handles are mapped back to the correct swapchain
  image so descriptor/uniform frame slots no longer fall back to image 0 for
  secondary/variant command buffers.
- Validation for that attempt:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 0 warnings and 0 errors.
- Latest live run:
  - Label: `vulkan-cpudirect-primary-variant-cache`
  - PID/log directory:
    `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_01-45-30_pid28628`
  - CPU dump:
    `profiler-cpu-frame-2026-06-18-01-46-48-573-148a0e3a.log`
  - GPU dumps:
    `profiler-gpu-pipeline-defaultrenderpipeline-11-2026-06-18_01-46-48-579-50c4ca79.log`
    plus four shadow pipeline dumps from the same timestamp.
  - Render-stats 1200-row tail: 14 records, 1186 clean reuses, 0 signature
    dirty, 0 planner dirty, 0 profiler dirty. Render dispatch p50 about
    14.8 ms / p95 about 26.1 ms. Vulkan frame total p50 about 3.4 ms.
    `RecordCommandBuffer` p50 about 2.65 ms. GPU command-buffer p50 about
    4.57 ms.
- Remaining blocker before calling the variant cache done: validation still
  reported command-buffer invalidation during warmup. The latest run showed
  `vkCmdExecuteCommands` errors for unrecorded secondary command buffers and
  `vkQueueSubmit2` errors for primaries invalidated by bound objects. These
  stopped being the dominant steady-state perf symptom, but correctness is not
  closed yet. Next iteration should inspect the early invalidated-object blocks
  in `log_vulkan.log` for the exact descriptor set / secondary command-buffer
  ownership path, then either keep those primaries dirty until descriptors stop
  churning or move the affected bindings to safe per-frame/update-after-bind
  behavior.

Additional 2026-06-18 framerate/crash iteration:

- User clarification: the over-dark Sponza issue should be treated as a
  separate ambient-occlusion/global-ambient correctness bug for now, not the
  current framerate blocker. There are currently no captured light probes, so
  no GI is expected; the global ambient term should still light dark areas.
  Keep this visual issue tracked, but do not let it distract the current
  Vulkan CPU-direct framerate work.
- Crash evidence from the dense texture upload path:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_10-53-15_pid25748`.
  The render stall log showed
  `MainThreadJobs.Normal.Coroutine:TextureStreaming.ApplyResidentData[chain_texture_bump]`
  with a dense texture upload monopolizing the render thread for about 4.9 s
  despite the 2 ms texture work budget. `log_vulkan.log` then reported
  `WaitForFences for one-shot submit failed (result=ErrorDeviceLost)` in
  `VkImageBackedTexture<T>.TransitionImageLayout` / `VkTexture2D.PushTextureData`.
- Attempted fix: generalized the existing OpenGL progressive texture upload
  path for Vulkan imported textures so mip data could be uploaded through
  `PushMipLevelRequested` instead of one dense render-thread upload. This made
  the budget shape better in principle, but live validation showed the first
  Vulkan implementation was unsafe.
- Attempted fix: disabled accidental use of NV indirect buffer-to-image copy
  uploads. `TryCopyBufferToImageViaIndirectNv` now honors
  `CanUseNvIndirectBufferCopyUploads`, and texture staging only requests
  shader-device-address staging when that same gate is enabled.
- Attempted fix: corrected progressive Vulkan image mip capacity so a source
  texture with more mips than the active locked range can create/recreate a
  full mip-chain image. This removed the `vkCmdCopyBufferToImage` out-of-range
  mip validation errors seen in
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_11-16-04_pid21068`.
- Remaining progressive-upload blocker: even after the mip-capacity fix,
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_11-18-52_pid556`
  device-lost at 2026-06-18 11:19:19 in
  `VkImageBackedTexture<T>.CopyBufferToImage` / `TransitionImageLayout` while
  running `VkTexture2D.PushMipLevel`. The progressive Vulkan path still uses
  one-shot layout transitions and copies in a way that can race the render
  graph or otherwise invalidate device execution. It must stay experimental
  until texture upload is moved to a properly synchronized Vulkan upload queue.
- Current stabilization: Vulkan progressive texture upload is now opt-in via
  `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=1`. The default Vulkan path returns
  to dense uploads plus the NV-indirect-copy gate fix. OpenGL keeps its
  progressive upload behavior.
- 2026-06-18 directional shadow flicker note: the latest user-captured bright
  frame is consistent with directional atlas cascade fallback, not full-scene
  black output. The 13:24 log repeatedly deferred shadow requests after
  rendering only part of the requested tile set. Dirty directional cascades that
  missed the refresh budget could publish a lit fallback despite having a
  previously rendered atlas tile, which made the light-combine receiver treat
  the cascade as unsampleable for that frame. Source fix in
  `ShadowAtlasManager.CreateResidentAllocation`: directional requests with a
  previous rendered tile now keep `StaleTile` fallback until refreshed. This
  intentionally preserves a potentially stale shadow for a frame instead of
  popping the shadow contribution off.
- Follow-up: rerun a live Vulkan CPU-direct editor pass with directional shadow
  audit enabled if the flicker is still visible. Confirm the deferred-bind
  audit no longer reports a transient disabled directional atlas while the
  light has resident previously rendered cascades. Do this before resuming
  framerate-only changes.
- Stable baseline validation after defaulting Vulkan progressive upload off:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 0 warnings and 0 errors.
- Live baseline run:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_11-21-43_pid45040`,
  label `vulkan-cpudirect-dense-upload-no-nv-indirect`, strategy
  `CpuDirect`, deferred debug disabled. MCP screenshot:
  `Build\McpCaptures\vulkan-dense-upload-no-nv-indirect\Screenshot_20260618_112256.png`.
  MCP dumps:
  `profiler-cpu-frame-2026-06-18-11-22-58-953-0035343c.log` and
  `profiler-gpu-pipeline-defaultrenderpipeline-28-2026-06-18-11-22-58-973-c1c38db2.log`.
- The dense baseline did not reproduce device loss or Vulkan validation errors
  before the later zero-sized-surface event. It did record a render stall:
  `Vulkan.FrameLifecycle.DrainRetiredResources` took about 364 ms on the last
  completed render, and another stall was detected while recording the primary
  command buffer. This keeps retired-resource draining on the active CPU
  framerate list.
- The latest "renderer crashed" log check found no newer XRENGINE log session
  after PID `45040`. The live PID `45040` was still responding, but its
  `log_vulkan.log` showed repeated 6.6-29.7 s frame-gap warnings followed by
  `Skipping frame while resize resources settle. Reason=Live surface size is zero`.
  Treat that as a zero-sized/minimized/closed-surface loop unless a newer crash
  log appears.
- Current performance evidence from the same dense baseline: the MCP stats at
  dump time still showed about 87.6 ms GPU-pipeline frame time, about 41.0 ms
  Vulkan total frame time, about 40.2 ms command-buffer recording time, and
  about 12.0 ms GPU command-buffer time. Later stats had clean-reuse frames
  near 6 ms Vulkan total / 5 ms record time, but dirty or warmup frames spiked
  to 77-201 ms recording with dirty reasons including
  `EnsureUniformDrawSlotCapacity` and `TryEnqueueVulkanGraphicsPipelineCompile`.
- Next CPU-direct framerate todos:
  - Continue from the default dense-upload baseline. Do not enable
    `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=1` except when explicitly testing
    the experimental upload path.
  - Design a synchronized Vulkan texture upload queue before re-enabling
    progressive texture upload by default. The fix needs render-graph/transfer
    synchronization, not more one-shot transition calls inside texture objects.
  - Reduce `Vulkan.FrameLifecycle.DrainRetiredResources` spikes; batch or
    amortize retirement work and verify resource retirement is not freeing
    objects still referenced by cached primary/secondary command buffers.
  - Pre-size uniform draw slots before frame recording so
    `EnsureUniformDrawSlotCapacity` does not invalidate cached command buffers
    during visible-frame recording.
  - Move `TryEnqueueVulkanGraphicsPipelineCompile` work out of the recorded
    frame path, or batch/warm the needed pipelines before primary command
    buffer recording starts.
  - Fix the zero-sized-surface loop so it does not repeatedly emit frame-gap
    warnings or keep reporting stale timeline values while resize resources are
    intentionally skipped.
  - Improve the CPU profiler dump so MCP can capture the retained worst render
    frame; the current dump is sometimes too shallow when invoked after the
    spike has already recovered.
  - Return to the AO/global-ambient Sponza visual issue after framerate and
    crash stability are no longer moving underfoot.

Current stop point - 2026-06-18 texture residency visual gate:

- Attempted isolation: launched a Vulkan `CpuDirect` Unit Testing World with
  dense imported texture residency enabled and progressive texture upload still
  disabled. Evidence:
  `Build\McpCaptures\vulkan-visual-gate-dense-residency\Screenshot_20260618_124957.png`
  and
  `Build\McpCaptures\vulkan-visual-gate-dense-residency\RenderPipeline_AlbedoOpacity_20260618_124959.png`.
  Result: Sponza albedo and final output were textured/nonblack. This isolated
  the fully-black Sponza regression to imported texture residency, not geometry,
  depth, AO visibility, or final presentation alone.
- Source fix: the default imported texture streaming path now allows visible
  dense promotions and gates the bound-material fallback uplift behind
  `allowPromotions`. Preview-tier resident transitions upload one resident mip
  instead of the whole mip chain. After the latest device-loss crash analysis,
  Vulkan dense resident promotions also stay single-mip until a synchronized
  Vulkan upload queue/render-graph path replaces the one-shot layout/copy
  upload path.
- Visual validation after the default dense-residency source fix, no special
  Vulkan residency env vars:
  `Build\McpCaptures\vulkan-visual-gate-default-dense-residency-fix\Screenshot_20260618_125652.png`,
  `Build\McpCaptures\vulkan-visual-gate-default-dense-residency-fix\RenderPipeline_AlbedoOpacity_20260618_125654.png`,
  and
  `Build\McpCaptures\vulkan-visual-gate-default-dense-residency-fix\RenderPipeline_FinalPostProcessOutputTexture_20260618_125700.png`.
  Result: Sponza was textured/nonblack, though still too bright/dark depending
  on exposure/AO/global ambient.
- Visual validation after the preview mip-chain reduction:
  `Build\McpCaptures\vulkan-visual-gate-preview-mip-fix\Screenshot_20260618_130243.png`
  and
  `Build\McpCaptures\vulkan-visual-gate-preview-mip-fix\RenderPipeline_AlbedoOpacity_20260618_130246.png`.
  Result: textured/nonblack Sponza remained intact. Treat the remaining
  over-dark look as the separate AO/global-ambient item, not the fully-black
  imported texture regression.
- Texture residency timing evidence improved. Before the preview mip-chain
  reduction, the 2026-06-18 12:56 run reported
  `maxUploadMs=2573.78` and `maxQueueWaitMs=2573.78` with preview transitions
  uploading full chains. After the reduction, the 13:02 run logged 64 px preview
  transitions as `includeMipChain=False mips=1` and the frame-120 summary
  reported `maxUploadMs=657.71`, `maxQueueWaitMs=657.71`,
  `visibleNoPreview=0`, `previewReady=26`, `fallback=0`, and `failed=0`.
- Performance evidence after the preview mip-chain reduction:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_13-02-12_pid41032`.
  MCP stats after settling still showed `vulkan_total_ms` about 18.27 ms,
  `gpu_command_buffer_ms` about 8.55 ms, and `record_command_buffer_ms` about
  17.06 ms for the sampled frame. CPU dumps at
  `profiler-cpu-frame-2026-06-18-13-04-47-854-aef117d9.log` showed the current
  worst thread around 1.83 ms, but retained history still had prior render
  spikes. GPU pipeline dump
  `profiler-gpu-pipeline-defaultrenderpipeline-28-2026-06-18-13-04-47-870-7bb47951.log`
  reported the default pipeline around p50 5.86 ms, p95 6.69 ms, avg 5.84 ms,
  with last-300 p95 around 6.88 ms.
- Build/test validation:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 0 warnings and 0 errors after the dense-residency and preview
  mip-chain changes. Focused tests passed:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~ImportedTextureStreamingPhaseTests|FullyQualifiedName~ImportedTextureStreamingContractTests" /p:UseSharedCompilation=false`
  with 43 passed, 0 failed. A stale source-contract assertion for
  `Texture.CacheStale` was updated because production still logs the event, but
  no longer through the old direct-call formatting.
- Remaining CPU-direct framerate trail: the GPU pipeline is not the primary
  blocker at roughly 6-7 ms p95. Render-thread wall time remains high and is
  still mostly attributed above the named Vulkan/GPU pipeline buckets under
  `XRWindow.DoRender`, so the next iteration should split present/swap pacing
  and uninstrumented `DoRender` work before chasing more GPU pass cost.
- Remaining texture-upload trail: full 1024 px dense promotions still cost
  render-thread jobs around 8-11 ms during startup. Do not re-enable
  `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=1` by default until Vulkan texture
  uploads are moved to a synchronized transfer/render-graph path.
- Tooling follow-up: `dump_gpu_render_pipeline_profile` can report a response
  filename with underscores between date/time while the actual file on disk uses
  hyphens. This does not affect profiling, but it slows LLM-driven log lookup.

Additional 2026-06-18 camera-motion crash/device-loss iteration:

- User status before this iteration: flickering appeared gone, Sponza colors
  were working, and framerate was decent, but moving the camera for a while
  caused the ImGui editor to disappear and no new frames to render.
- Latest crash log reviewed:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_13-59-22_pid36092`.
  First hard Vulkan failure occurred at 2026-06-18 14:00:32.415:
  `WaitForFences for one-shot submit failed (result=ErrorDeviceLost)`.
  Stack root:
  `VulkanRenderer.CommandsStop` ->
  `VulkanRenderer.CommandScope.Dispose` ->
  `VkImageBackedTexture<T>.TransitionImageLayout` ->
  `VkTexture2D.PushTextureData`.
- Immediate pre-loss evidence: the log created 512x512 dedicated Sponza texture
  images with `mips=10`, including `sponza_column_b_diff` and
  `sponza_column_b_bump`, plus a 4 MB staging allocation for
  `sponza_column_b_diff`. No relevant validation `VUID` appeared before device
  loss. Later `InvalidOperationException` entries such as
  `Cannot CreateBuffer after the Vulkan device was lost` are fallout from the
  lost device, not the root crash.
- Attempted source fix: `ImportedTextureStreamingManager` now routes resident
  mip-chain decisions through `ShouldIncludeResidentMipChain(...)`. For Vulkan,
  dense imported-texture promotions use one resident mip until a synchronized
  upload queue exists. OpenGL keeps the previous full-chain resident behavior.
- Validation completed for the source change:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~ImportedTextureStreamingPhaseTests|FullyQualifiedName~ImportedTextureStreamingContractTests" /p:UseSharedCompilation=false`
  passed with 43 passed, 0 failed.
- Short live editor validation completed after the source change:
  launched the editor with Unit Testing World and MCP on port 5468, drove 45
  automated camera moves through/around Sponza, captured
  `Build\McpCaptures\vulkan-camera-crash-fix-live\Screenshot_20260618_141201.png`
  and
  `Build\McpCaptures\vulkan-camera-crash-fix-live\Screenshot_20260618_141303.png`,
  and dumped CPU/GPU profiler logs under
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_14-10-25_pid10844`.
  `rg` found no `ErrorDeviceLost`, no one-shot fence failure, no
  device-lost `InvalidOperationException`, no validation `VUID`, and no MCP
  profiler validation errors in that session. The final MCP stats reported
  `validation_errors=0`, `vulkan_total_ms=8.9417`,
  `record_command_buffer_ms=7.8929`, and
  `gpu_command_buffer_ms=6.7208`.
- Visual result: both captures remained textured/nonblack. The second capture is
  very close to a red banner and still shows the known dark AO/exposure behavior
  on adjacent geometry; this does not reintroduce the fully-black Sponza
  regression.
- GPU timing after the camera-motion validation:
  `profiler-gpu-pipeline-defaultrenderpipeline-28-2026-06-18-14-13-04-497-bbf71bf9.log`
  reported default-pipeline warmup-excluded p50 about 5.0 ms, p95 about
  6.3 ms, max about 8.0 ms, and last-300 p95 about 5.1 ms. The remaining
  performance issue is still render-thread wall time/unattributed wait, not a
  default-pipeline GPU explosion.
- Follow-up user repro: zooming farther out of the scene still caused the editor
  renderer to stop after the short MCP validation. Latest log session reviewed:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_14-17-36_pid34772`.
  First hard Vulkan failure occurred at 2026-06-18 14:21:32.752:
  `WaitForFences for one-shot submit failed (result=ErrorDeviceLost)`.
  This was a different first stack than the prior imported-texture upload
  device loss:
  `VulkanRenderer.CommandsStop` ->
  `VulkanRenderer.CommandScope.Dispose` ->
  `VkImageBackedTexture<T>.TransitionImageLayout` ->
  `VulkanRenderer.UpdateAutoExposureGpu`.
- Negative evidence for the zoom-out/demotion hypothesis: the texture log near
  device loss reported `queuedDemotions=0`, `queuedPromotions=0`, `pending=0`,
  and all 39 tracked textures promoted/resident. There were earlier Sponza
  texture downscale/residency transitions and dedicated texture image creations,
  so camera distance may still be a stress correlation, but the immediate
  failure signature is auto-exposure layout synchronization rather than
  demotion.
- Attempted source fix: `VulkanAutoExposure.UpdateAutoExposureGpu` no longer
  performs out-of-band one-shot layout transitions for planner-backed
  `AutoExposureTex` images. The exposure update pass already declares
  `AutoExposureTex` as `ReadWriteTexture(...)`, so render-graph barriers own
  the GENERAL/storage-write and shader-read transitions for the default
  pipeline texture. Standalone/non-render-graph exposure textures keep the old
  explicit transition behavior.
- Validation completed for this source change:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanAutoExposure_ClampsPlannerBackedSourcesToBaseMip" /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 1 passed, 0 failed.
- Live validation of the auto-exposure graph-barrier fix found the next root
  crash rather than fully clearing the issue. Fresh run:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_14-30-56_pid44744`.
  After 120 automated near/far camera moves, the first hard failure occurred at
  2026-06-18 14:32:01.609:
  `WaitForFences for one-shot submit failed (result=ErrorDeviceLost)`.
  Stack root:
  `VulkanRenderer.CommandsStop` ->
  `VulkanRenderer.CommandScope.Dispose` ->
  `VkImageBackedTexture<T>.TransitionImageLayout` ->
  `VkTexture2D.PushTextureData`.
  The later `ForwardPlusLocalLightsBuffer` descriptor warnings happened after
  device loss and are fallout.
- Negative evidence for the demotion hypothesis in the 14:30 run: texture
  summaries around/after device loss still reported `queuedDemotions=0`,
  `queuedPromotions=0`, `pending=0`, and all tracked textures resident. The
  upload stack came through
  `GLTieredTextureResidencyBackend.ScheduleResidentLoad` ->
  `XRTexture2D.ScheduleGpuUploadInternal` ->
  `VkTexture2D.PushTextureData`, so the immediate failure is a Vulkan dense
  residency upload/resource-swap path, not a confirmed demotion transition.
- Source containment fix: `ImportedTextureStreamingManager` now freezes
  preview-ready imported texture residency on Vulkan via
  `ResolveVulkanSafeResidentSize(...)` /
  `ShouldFreezeVulkanImportedTextureResidency(...)`. This prevents Vulkan from
  promoting or demoting imported textures after their preview upload is visible,
  avoiding live backing-image replacement and one-shot layout/copy uploads while
  the frame loop is active. OpenGL keeps its existing resident promotion and
  demotion behavior.
- Superseded user visual follow-up: the preview-only containment made the editor
  visibly blurry, with texture telemetry showing `Vulkan residency: frozen`,
  `Resident=64`, `Desired=64`, and `vk-froz` on all imported Sponza textures.
  Source was adjusted so `XRE_VULKAN_IMPORTED_TEXTURE_PREVIEW_FREEZE=1` is now
  an explicit emergency kill switch instead of the default. Default Vulkan dense
  imported-texture promotions are enabled again, still limited to one resident
  mip, and `VkImageBackedTexture` now waits for all in-flight frame slots before
  retiring/recreating a dedicated imported texture image. Follow-up after user
  validation re-enabled controlled Vulkan visibility demotions so offscreen
  textures can relinquish residency after the normal grace/cooldown window.
- Source validation for the user visual follow-up:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~ImportedTextureStreamingPhaseTests|FullyQualifiedName~ImportedTextureStreamingContractTests" /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 45 passed, 0 failed; editor build
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false /v:minimal`
  passed with 0 warnings and 0 errors.
- Follow-up source validation after MCP protocol repair:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~McpServerHostProtocolTests|FullyQualifiedName~ImportedTextureStreamingPhaseTests|FullyQualifiedName~ImportedTextureStreamingContractTests" /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 54 passed, 0 failed. Editor build passed again with 0 warnings
  and 0 errors.
- Validation completed for the containment fix:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~ImportedTextureStreamingPhaseTests|FullyQualifiedName~ImportedTextureStreamingContractTests" /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 43 passed, 0 failed. Note: an accidental parallel test/editor
  build corrupted generated `AotFactoryRegistrations.g.cs`; deleting that
  generated file and rerunning sequentially fixed it. Do not run these generator
  builds in parallel.
- Editor build validation completed:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false /v:minimal`
  passed with 0 warnings and 0 errors.
- Live validation completed after the containment fix: launched Unit Testing
  World Vulkan `CpuDirect` with MCP on port 5470, kept
  `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=0`, drove 120 automated camera moves
  from close Sponza views to far zoomed-out views, and captured
  `Build\McpCaptures\vulkan-texture-freeze-live\Screenshot_20260618_144426.png`.
  Fresh log session:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_14-43-24_pid21544`.
  Grep found no `ErrorDeviceLost`, no `WaitForFences for one-shot`, no Vulkan
  `VUID`, and no render exception. MCP profiler stats reported validation
  `error_count=0`. Texture telemetry showed `previewReady=39`, `atPreview=39`,
  `promoted=0`, `pending=0`, `queuedPromotions=0`, `queuedDemotions=0`, and
  `maxResident=64`.
- [ ] Implement the real Vulkan imported-texture upload fix: a synchronized
  transfer/render-graph upload queue that owns image creation, layout barriers,
  copy commands, descriptor rebinding, and retirement before re-enabling
  runtime Vulkan texture promotions/demotions or
  `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=1`. Detailed implementation tracker:
  [Vulkan Imported Texture Streaming TODO](vulkan-imported-texture-streaming-todo.md).
- [ ] Live-validate restored high-res single-mip imported texture residency on
  Vulkan. Validation gate: close/far camera motion should keep Sponza
  colored/nonblack, allow `promoted>0`, produce no one-shot upload device-loss
  failures, and show no Vulkan validation/device-loss errors. The remaining
  synchronized upload-service work is still required before full mip-chain
  progressive uploads or demotions are considered stable.
  Partial live evidence on 2026-06-18 resolves the user-reported "still frozen"
  visual failure: launched PID `45540` with Unit Testing World Vulkan
  `CpuDirect`, MCP port `5475`,
  `XRE_VULKAN_IMPORTED_TEXTURE_PREVIEW_FREEZE=0`, and
  `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=0`. MCP texture summary reported
  `TrackedTextureCount=39`, `PendingTransitionCount=0`,
  `CurrentManagedBytes=115219084`, `AssignedManagedBytes=182278792`,
  `PromotionsBlocked=false`, and `VulkanFrozen=false`. Per-texture telemetry
  reported `count=39`; the serialized sample of 16 rows all had
  `ResidentMaxDimension=1024`, `DesiredResidentMaxDimension=1024`, and
  `PendingResidentMaxDimension=0`. Screenshot
  `Build\McpSmokeCaptures\Screenshot_20260618_175043.png` is nonblank and
  visibly textured. MCP render profiler stats reported Vulkan backend,
  validation `error_count=0`, descriptor `binding_failures=0`, dropped draw and
  compute ops `0`. Full close/far camera-motion validation remains required.
- MCP tooling fix from this iteration: `McpServerHost` now reads JSON-RPC POST
  bodies by `ContentLength64` when available and wraps early invalid requests in
  a normal JSON-RPC error response. This unblocked external MCP `initialize`,
  `tools/list`, `invoke_method`, render-state, profiler, and screenshot calls
  during the live Vulkan smoke.
- Follow-up demotion/filtering source fix: removed the temporary Vulkan
  `ShouldPreserveVulkanResidentSizeAgainstDemotion` guard so the shared
  offscreen residency policy can queue demotions again after promotion
  validation. Vulkan sampler creation now explicitly enables anisotropy on
  capable devices for image-backed textures, texture views, and explicit
  `XRSampler` objects, while keeping `AnisotropyEnable=false` if the device
  feature was not enabled. Full mip-chain/progressive imported streaming is
  still deferred to the synchronized upload-service work.
  Validation: 46 focused imported-texture tests passed, editor build passed with
  0 warnings/errors, and live PID `29572` on MCP port `5476` dropped texture
  telemetry from `CurrentManagedBytes=115219084` before moving the camera to
  `CurrentManagedBytes=602112` after moving the camera off-scene and waiting
  past the grace window. Vulkan MCP stats reported validation `error_count=0`,
  descriptor `binding_failures=0`, and dropped draw/compute ops `0`; the fresh
  log scan found no `ErrorDeviceLost`, `WaitForFences`, `VUID`, exception, or
  failure markers.

Additional 2026-06-18 cascaded directional grouped-render iteration:

- Vulkan capability evidence from
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_16-44-54_pid43976`:
  `log_vulkan.log` reported `multiViewport=True`, `shaderOutputViewportIndex=True`,
  `shaderOutputLayer=True`, and `geometryShader=True`; the instanced layered and
  geometry shader cascade pipelines both compiled. The fallback was therefore
  not a Vulkan feature or shader compilation failure.
- Root cause: `log_lighting.log` reported directional cascade render-mode
  fallback from `InstancedLayered` / `GeometryShader` to `Sequential` with
  `reason=MissingGroupedAtlasAllocation` even though the atlas solve created a
  directional cascade group. `ShadowAtlasManager.RenderScheduledTiles` only
  matched a grouped directional allocation when the current dirty request was
  the first cascade in the group. Once request sorting reached cascade 1, 2, or
  3 first, the scheduler failed the lookup and rendered tiles sequentially.
- Source fix: grouped directional lookup now matches any cascade member in the
  atlas allocation, and a grouped directional request that cannot fit the
  current tile budget is deferred as a group instead of silently degrading to
  one-tile sequential rendering. If an actual grouped render attempt fails, the
  existing safety fallback path still keeps shadows renderable.
- Follow-up root cause from the 17:13 user validation run: collection and swap
  planned the atlas path before a concrete atlas group existed, published an
  artificial `MissingGroupedAtlasAllocation` plan, and left the inspector/logs
  showing `effective=Sequential`. The solver also rejected grouped allocations
  unless the active directional cascade count was exactly four, so transient
  two- or three-cascade states during settings changes could never use the
  grouped atlas path.
- Follow-up source fix: atlas grouped collection/swap now publish the intended
  grouped atlas render plan when the backend supports it, while still collecting
  and swapping per-cascade fallback command buffers for safety. Directional
  grouped atlas reservation now accepts 2-4 cascades instead of only exactly
  four.
- Follow-up evidence from
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_17-23-38_pid38184`:
  `MissingGroupedAtlasAllocation` no longer appeared, `directionalGroups=1/4/0`
  was still produced, and one grouped directional render completed. However,
  repeated warnings still showed real sequential cascade tile renders with
  `atlasFallback=None`, so the previous patch did not fully remove the slow
  fallback path.
- Follow-up source fix: a requested grouped directional atlas render that fails
  no longer falls through to `RenderCascadeShadowAtlasTile`. It records grouped
  failure diagnostics, keeps the old atlas tiles stale for that frame, and
  retries later instead of silently using the sequential path.
- Validation completed:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=DirectionalCascadeGroupLookup_MatchesAnyCascadeMember|Name=DirectionalCascadeAtlasGroupedPath_UsesAtlasBackendAndLayeredRendering" /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 2 passed, 0 failed. `dotnet build
  .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1
  /nodeReuse:false /p:UseSharedCompilation=false /v:minimal` passed with
  0 warnings and 0 errors.
- Follow-up validation completed:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=SolveAllocations_PublishesGroupedDirectionalCascadeRecord|Name=SolveAllocations_PublishesGroupedDirectionalCascadeRecordForResidentSubset|Name=DirectionalCascadeGroupLookup_MatchesAnyCascadeMember|Name=DirectionalCascadeAtlasGroupedPath_UsesAtlasBackendAndLayeredRendering" /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 4 passed, 0 failed. Editor build passed again with 0 warnings and
  0 errors.
- Remaining live validation: run the Vulkan Unit Testing World with directional
  cascades set to `InstancedLayered` and `GeometryShader`, confirm
  `log_lighting.log` no longer reports `MissingGroupedAtlasAllocation` fallback,
  and capture the directional atlas to verify all cascades update in one grouped
  render pass.

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

- 2026-06-18:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~ImportedTextureStreamingPhaseTests|FullyQualifiedName~ImportedTextureStreamingContractTests" /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 46 passed, 0 skipped, 0 failed after re-enabling controlled
  Vulkan demotion and tightening Vulkan anisotropy sampler creation.
- 2026-06-18:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false /v:minimal`
  passed with 0 warnings and 0 errors after the same demotion/anisotropy fix.
- 2026-06-18:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~McpServerHostProtocolTests|FullyQualifiedName~ImportedTextureStreamingPhaseTests|FullyQualifiedName~ImportedTextureStreamingContractTests" /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 54 passed, 0 skipped, 0 failed after restoring default Vulkan
  imported-texture promotions and fixing MCP POST body/error-envelope handling.
- 2026-06-18:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false /v:minimal`
  passed with 0 warnings and 0 errors after the same texture/MCP fixes.
- 2026-06-18:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~McpServerAutomationTests" /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 2 passed, 0 skipped, 0 failed after restoring Vulkan MCP
  screenshot export to no additional vertical flip.
- 2026-06-18:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false /v:minimal`
  passed with 0 warnings and 0 errors after the same MCP screenshot
  orientation fix.
- 2026-06-18:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~ShadowAtlasManagerPhaseTests" /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 29 passed, 0 skipped, 0 failed after changing dirty directional
  atlas refreshes to keep stale fallback sampleable until the refresh renders.
  The first sandboxed attempt failed before tests ran because native MSBuild
  FileTracker access was denied; the escalated validation run above passed.
- 2026-06-18:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false /v:minimal`
  passed with 0 warnings and 0 errors after the same directional shadow atlas
  fallback fix.
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
- 2026-06-17:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 0 warnings and 0 errors after the FXAA Vulkan descriptor binding
  fix and material texture-list churn guard.
- 2026-06-17:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false`
  passed with 0 warnings and 0 errors after the debug-shape Y fix, Vulkan MCP
  screenshot-orientation fix, framebuffer descriptor preservation, allocation
  signature split, and `TrackBufferBinding` registry restriction.
- 2026-06-17:
  MCP docs regeneration is blocked locally. `pwsh` is unavailable, and
  `powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Reports\generate_mcp_docs.ps1`
  fell back to the source parser but failed because
  `docs\features\mcp-server.md` is absent.

Latest live GPU result:

- Release Vulkan measurement: not yet recorded in this document revision
- Manual MCP screenshots:
  `Build\McpCaptures\vulkan-frame-loop-cpu-dump-enabled\Screenshot_20260617_162115.png`
  confirms sky/procedural output renders while deferred Sponza geometry remains
  black in raw albedo/debug output.
- Manual MCP screenshots after the FXAA binding fix:
  `Build\McpCaptures\vulkan-frame-loop-after-fxaa-texture0\Screenshot_20260617_183359.png`
  is visibly nonblack. Pipeline texture captures in the same directory show
  `FinalPostProcessOutputTexture` average RGB `0.55130714` and
  `FxaaOutputTexture` average RGB `0.5498744`, confirming the final presented
  FXAA target is no longer black.
- Manual MCP screenshots after the Vulkan screenshot-orientation fix:
  `Build\McpCaptures\vulkan-frame-loop-capture-yfix\Screenshot_20260617_190305.png`
  and motion captures in the same directory are nonblack, but MCP viewport
  captures still omit full ImGui/frontbuffer UI.
- Manual MCP screenshot after restoring Vulkan screenshot export to no CPU
  vertical flip:
  `Build\McpCaptures\vulkan-screenshot-row-order-fix-warm\Screenshot_20260618_150021.png`.
  The capture is visible and oriented upright for the forced camera pose.
  Latest short-run log directory:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_14-59-51_pid42380`.
  Grep found no `ErrorDeviceLost`, no one-shot fence failure, no Vulkan `VUID`,
  no `InvalidOperationException`, and no fatal/error marker.
- Manual post-warm motion validation after the buffer-registry fix:
  `Build\McpCaptures\vulkan-frame-loop-buffer-registry-fix\` and
  `Build\McpCaptures\vulkan-frame-loop-second-postwarm-motion\`. Latest log
  directory:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-17_19-21-49_pid1992`.
  Post-warm camera moves produced 0 resource-plan replacements, 0 device-idle
  waits, 0 physical image handle changes, and 0 FBO rebuilds. Sampled captures
  stayed nonblack with average brightness around `0.55`.
- Manual MCP CPU dump after the buffer-registry fix:
  `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-17_19-21-49_pid1992\profiler-cpu-frame-2026-06-17-19-24-49-241-5d7eab0d.log`.
  Worst render-thread sample was about 3.35 ms; `Vulkan.RecordCommandBuffer.ResourcePlan`
  was about 0.58 ms. `get_time_state` reported `delta` near `0.0166668`.
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
