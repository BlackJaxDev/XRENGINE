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

- [x] Debug the new Vulkan black-frame regression introduced after the Vulkan
  bindless updates. The current observed repro is that deferred Sponza/3D
  geometry renders black, while zooming out shows the skybox still renders. This
  is not currently a full-frame black-until-window-resize repro. Treat it as a
  geometry, deferred material, GBuffer, lighting, or descriptor correctness
  blocker for the traditional Vulkan CPU-driven path; do not defer it behind
  GPU-indirect zero-readback, instrumented, or meshlet path work. Local fix
  landed on 2026-06-17: the final presented target was the FXAA output, and the
  Vulkan FXAA material was sampling a shader uniform name that did not match the
  material texture slot descriptor. Switching FXAA to sample `Texture0` and
  avoiding per-frame material texture-list churn made `FxaaOutputTexture` and
  the viewport nonblack in MCP captures.
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
- Changed Vulkan screenshot export to respect
  `RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeGraphicsApiKind.Vulkan)`
  instead of hard-coding no vertical flip. This makes default Vulkan/Y-up MCP
  PNGs use the same visual orientation as the presented viewport path, assuming
  the user's observed viewport is authoritative.
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
