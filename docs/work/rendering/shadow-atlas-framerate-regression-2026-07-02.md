# Shadow Atlas Framerate Regression Investigation - 2026-07-02

## Problem

After the shadow atlas allocation/threading implementation, the editor framerate
became unusable. The goal is to identify whether the regression is CPU-side,
GPU-side, or caused by frame-loop synchronization, using a live Unit Testing
World editor process with MCP, CPU profiler dumps, GPU timing dumps, viewport
captures, logs, and RenderDoc when available.

## Evidence Run

- Run root: `Build/_AgentValidation/20260702-122839-editor-framerate-dumps`
- Follow-up coherent stale cascade run root:
  `Build/_AgentValidation/20260702-154705-coherent-stale-cascade`
- Follow-up stale-age/atlas shader iteration run root:
  `Build/_AgentValidation/20260702-171735-dir-shadow-atlas-iteration`
- Follow-up present/exposure flicker run root:
  `Build/_AgentValidation/20260702-180006-stop-flicker-exposure`
- Follow-up expired stale cascade refresh run root:
  `Build/_AgentValidation/20260702-190927-cascade-atlas-expired-stale-refresh`
- Follow-up render-on-demand startup/exposure run root:
  `Build/_AgentValidation/20260702-194022-render-on-demand-exposure`
- Follow-up continuous-rendering stop-frame atlas run root:
  `Build/_AgentValidation/20260702-202000-dir-cascade-stop-flicker`
- Settings observed at start: Unit Testing World, Vulkan backend, desktop VR
  mode, one directional light, profiler logging disabled in JSONC.
- Main live-editor sessions:
  - `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_12-30-19_pid26524`
  - `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_12-46-56_pid50692`
  - `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_12-51-05_pid45508`
  - `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_12-56-47_pid27944`
  - `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_16-34-24_pid2864`
  - `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_17-19-21_pid31784`
  - `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_17-30-41_pid32764`
  - `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_18-51-01_pid36176`
  - `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_20-12-47_pid48772`

## Findings

- The regression is CPU/render-thread bound, not GPU bound. Repeated GPU
  pipeline dumps showed steady GPU work around 2.4-3.6 ms while render-thread
  CPU time was often 40-60 ms and sometimes above 140 ms.
- The hottest recurring path in CPU frame dumps and FPS-drop logs was
  `EngineTimer.DispatchRender -> XRWindow.RenderWindowViewports ->
  Vulkan.RecordPrimary.MainOpLoop`. The collect-visible thread was usually
  blocked behind render-thread work during drops.
- Vulkan CPU-direct `CpuQueryAsync` occlusion was the largest single cost. With
  the persisted settings requesting `Diagnostics` and
  `Occlusion=CpuQueryAsync->CpuQueryAsync`, frames recorded hundreds of mesh and
  query/proxy-related operations and allocated several MB per record. Launching
  the same scene with `XRE_OCCLUSION_CULLING_MODE=Disabled` reduced frame ops
  from roughly 235-257 to 57 and render-thread time after startup from roughly
  58-63 ms average to about 15.5 ms average, while GPU time stayed about 2.4 ms.
- Directional shadow cascades are the remaining large pressure source. Vulkan
  currently falls back to sequential cascade renders instead of the grouped
  OpenGL atlas path, so shadow refreshes produce five collection passes in the
  frame: the main view plus four cascades. FPS-drop logs showed
  `CollectCalls=5`, about 1,700 emitted commands, and `MaxCommandsPerCollect`
  around 396. `RenderWorkLastShadowMs` is misleading here because it measures
  shadow scheduling/enqueue time; the expensive work is paid later during
  visibility collection, viewport command generation, and Vulkan primary command
  recording.
- The latest post-fix live run still logged the persisted request
  `Configured=Diagnostics Active=Diagnostics ... Occlusion=CpuQueryAsync`, but
  also logged the new suppression warning for Vulkan CPU-direct hardware-query
  occlusion. That confirms the compatibility gate is active even though the
  higher-level settings fingerprint still reports the requested mode.
- Editor UI is a secondary contributor in the captured test scene. One CPU dump
  showed `VPRC_RenderScreenSpaceUI` at 7.4 ms and `EditorImGuiUI.RenderEditor`
  at 5.2 ms inside `XRWindow.RenderWindowViewports`, but the worst stalls still
  correlated with Vulkan primary recording plus repeated shadow/visibility work.
- The follow-up coherent stale cascade probe showed that Vulkan now uses the
  grouped directional cascade atlas path (`mode=InstancedLayered`,
  `backend=AtlasPage`) rather than the previous sequential fallback. The
  remaining worst movement sample still correlated with a full four-cascade
  grouped refresh plus Vulkan command/resource dirties:
  `moving_12` reported about 184 ms Vulkan frame time, 463 Vulkan ops, 414 mesh
  ops, `DirectionalCascadeForcedFreshRender=4`, and
  `DirectionalCascadeStaleSampled=4`.
- The same probe showed CPU-direct `CpuQueryAsync` is active again. MCP samples
  reported `occ_mode=CpuQueryAsync`, `occ_strategy=CpuDirect`, active CPU query
  passes, tested/cull/rendered counts, and nonzero
  `cpu_query_submitted_total`/`cpu_query_resolved_total`. The
  `cpu_query_async_*` counters remained zero in this mode because those counters
  describe the GPU-dispatch proxy-query scaffold, not the CPU-direct hardware
  query path.
- The post-stop steady samples in the same run were much lower, usually about
  11-14 ms Vulkan frame time with `primary-frame-state` as the dirty summary.
  The audit no longer showed post-stop mixed/unsampleable directional cascade
  slots in the sampled frames; the remaining visible one-frame snap needs a
  targeted frame-by-frame capture to prove whether it is a stale/fresh shadow
  transition, a present/temporal-history issue, or a missed audit window.
- The 2026-07-02 17:17 stale-age/atlas iteration reproduced the latest
  move-then-stop check after the cadence change. The stopped captures were
  visually stable, the 00:21:10-00:21:30 UTC profiler window had
  `DirectionalCascadeMixedGenerationPrevented=0`, and the steady tail returned
  to about 14.6 ms render dispatch / 8.3 ms Vulkan frame time with
  `primary-frame-state` as the dirty reason.
- The remaining directional atlas correctness issue was in shader sampling,
  not planning. `DirectionalShadowAtlasMaxStaleFrames` was applied to every
  atlas page, so a current resident cascade could age out after the new
  two-frame limit and fall through to the legacy cascade map path. Atlas
  fallback modes are authoritative in the Vulkan path, so that fallback could
  sample dummy/obsolete legacy data and present as a shadow snap.
- Post-fix editor smoke on 2026-07-02 17:30 used the already-built editor with
  the edited shader assets. Two MCP screenshots rendered the same Sponza view,
  Vulkan validation errors stayed at 0, and the final 500 profiler records had
  zero stale, mixed-generation, or forced-fresh directional cascade counters.
- The later one-frame post-camera-stop flicker was not confined to directional
  shadowed regions. The bad captured frame was a whole-frame exposure/present
  artifact: early repro logs showed `AutoExposureTex` sampling a placeholder for
  one frame, and later startup captures showed broad post-process placeholders
  while imported textures and planner-backed resources were still publishing.
- The present/exposure pass now keeps the last rendered window source available
  for render-on-demand idle frames. When a frame records no scene writer for the
  acquired swapchain image, Vulkan blits the tracked last present source instead
  of preserving the acquired swapchain contents.
- Vulkan auto-exposure history now survives temporary resource-planner switches
  into contexts that do not declare `AutoExposureTex`. The allocator retains the
  1x1 exposure image across that gap and copies it into the next active
  `AutoExposureTex` image, preventing a fresh exposure texture from resetting
  the scene for one frame.
- The JSONC `EditorCameraRenderOnDemand` startup option originally did not
  affect the live editor camera because runtime bootstrap creates the desktop
  pawn through the input bridge and never routed the editor-only property back
  to `EditorFlyingCameraPawnComponent`. The runtime/editor bridge now applies
  the setting after pawn creation.
- A follow-up MCP validation showed the render-on-demand startup toggle
  applies (`RenderOnDemand=true`, `activeViewportSuppress3DSceneRendering=true`)
  and the cached HDR scene frame is real, not black. The remaining whole-frame
  brightness was auto-exposure: before the latest fix, `AutoExposureTex`
  climbed to about 97 and the final post-process capture was white while
  `HDRSceneTex` remained sane.
- Vulkan GPU auto-exposure could still jump to the measured target when the
  planner-backed 1x1 exposure texture was reset or zeroed. The Vulkan and
  OpenGL GPU exposure shaders now lerp from the authored fallback exposure
  when the history texel is invalid instead of teleporting to the target.
- Editor render-on-demand viewports now explicitly suppress auto-exposure
  updates while the cached scene frame is invalidating, settling, or idle.
  `VPRC_ExposureUpdate` honors that viewport flag before clearing GPU exposure
  readiness, so stale/settling HDR frames cannot poison exposure during camera
  drag or the post-drag settle tail.
- Follow-up user report on 2026-07-02 narrowed the remaining flicker back to
  the case where directional cascades and directional atlasing are both enabled.
  The likely remaining root cause was the cadence stale-age policy: once
  `MaxDirectionalCascadeAtlasStaleFrames` was reached, the scheduler could keep
  publishing a resident `StaleTile` allocation, while the shader was allowed to
  reject that stale atlas sample by age and return the explicit lit/contact
  fallback for that frame. That produces a one-frame brightness/shadow change
  exactly when the camera stops or the request hash settles.
- Follow-up user report rejected the camera/exposure frame-count hold as the
  likely fix and reported that black flicker had regressed into the main view
  even with atlasing disabled. The newer evidence separated that into three
  causes instead of one exposure problem.
- The black main-view flicker was caused by unstable Vulkan physical resource
  planning. Persistent render targets such as `FinalPostProcessOutputTexture`
  and `AutoExposureTex` could be reallocated when per-pass metadata changed
  even though their descriptor/FBO usage had not changed. The allocator now
  derives the physical usage signature from descriptor use, FBO attachment use,
  storage use, and format requirements, not transient pass metadata.
- The moving-camera brightness change was partly Vulkan GPU auto-exposure
  metering. Planner-backed `HDRSceneTex` did not have a generated mip pyramid,
  so the exposure compute path sampled sparse mip-0 texels as if a reduced mip
  existed. Vulkan auto-exposure now uses a filtered block-metering path for
  mipless planner-backed sources, which kept the live `AutoExposureTex` sample
  smooth during a five-second camera move.
- The directional cascade+atlas flicker also had a forward-lighting binding
  mismatch. Some forward material passes had no resolved `CameraComponent`, so
  the CPU bind path disabled cascades/atlas for those passes while deferred
  lighting still used the desktop cascade source. Forward binding now treats a
  missing camera component as "use published desktop cascades" when the light
  has valid published cascades.
- The remaining cascade motion gap was a stale-slot preservation rule that was
  too strict. When the same physical atlas page/rect remained resident but the
  new request had not rendered yet, requiring the previous content/frame to
  equal the pending allocation could throw away the previous rendered uniforms
  for one publish frame. The publication path now preserves coherent previous
  atlas uniforms for the same physical slot and marks that sample as
  `StaleTile` while the fresh render catches up.

## Attempts

- Isolated the shadow render plan used by the render thread so rendering uses
  the published frame plan instead of reading the pending planning-thread plan.
- Stored the source request count in `ShadowAtlasRenderPlan` so render-time
  deferral accounting no longer consults the mutable manager request list.
- Added a budget cost for sequential directional cascade entries. A single
  Vulkan sequential cascade refresh now consumes the same scheduling budget as
  the full four-cascade group, preventing the default tile budget from treating
  all four sequential cascade renders as cheap independent tile work.
- Changed the default Vulkan feature profile from `Diagnostics` to `Auto` for
  new settings. Existing persisted user settings can still force
  `Diagnostics`, as seen in the live logs.
- Follow-up regression report: the Vulkan CPU-direct `CpuQueryAsync` opt-in
  gate made the user's requested occlusion mode stop working. The gate was
  removed; CPU-direct Vulkan now honors `GpuOcclusionCullingMode=CpuQueryAsync`
  and records query frame ops through the existing `VkQueryPool` path.
- Follow-up regression report: the atlas render-plan collection gate could skip
  same-frame shadow caster collection based on stale/pending plan state. That
  gate was removed so active atlas-casting lights keep their command buffers
  current; the render plan still decides whether tiles render.
- Follow-up regression report: directional camera-fit refreshes with valid
  stale tiles were treated as deferrable under the hard tile budget. With the
  Unit Testing World startup budget at one tile, a coherent 3-4 cascade group
  could stay stale across camera movement. Interactive directional dirty
  reasons now publish as `CriticalBypass`, including time-budget bypass, so a
  coherent cascade group can refresh instead of being held behind stale reuse.
- Follow-up performance fix: Vulkan no longer has an explicit grouped-cascade
  atlas disable branch. The shared capability flags already report Vulkan
  multi-viewport and shader-output viewport-index support, so Vulkan can use
  the grouped atlas path when those features are available and otherwise falls
  back for the concrete missing capability.
- Follow-up performance fix: directional cascade atlas requests now apply a
  bounded refresh cadence. Cascade 0 remains fresh on dirty frames, farther
  cascades can reuse a resident rendered tile for a staggered interval bounded
  by `MaxDirectionalCascadeAtlasStaleFrames`, and missing tiles, expired stale
  age, or large matrix jumps force fresh rendering.
- Follow-up performance fix: directional cascade visible-set collection is now
  driven by the atlas request state. Cadence-skipped and hash-cache-hit
  cascades skip `CollectVisible` and `SwapBuffers`; full dirty groups still use
  grouped rendering, while partial cadence refreshes render only the dirty
  subset.
- Updated `docs/developer-guides/rendering/cpu-query-async-occlusion.md` to
  document that Vulkan CPU-direct hardware-query occlusion is enabled by the
  requested `CpuQueryAsync` mode rather than by a separate environment opt-in.
- Follow-up correctness fix: CPU-query suppression for the forward
  depth/normal prepass is now explicit at the call site. It no longer depends
  on `UseDepthNormalMaterialVariants`, so changing material variant policy does
  not accidentally disable CPU-query occlusion for normal scene passes.
- Follow-up observability fix: the render profiler packet, profiler NDJSON,
  MCP `get_render_profiler_stats`, and profiler sender now expose occlusion
  mode/strategy and CPU-query counters, including pass skip reasons, tested/
  culled/rendered counts, pending/submitted/resolved totals, latency, and the
  CPU SOC counters.
- Follow-up shadow-lag fix: the default
  `MaxDirectionalCascadeAtlasStaleFrames` was reduced from 32 to 2 to match the
  design contract. Directional cadence now treats an expired stale age as
  "stop sampling this stale tile" instead of "force a full cascade refresh right
  now". The later continuous-rendering stop-frame report showed that the short
  stable-request window was still one frame too long: the first stopped frame
  can already carry the repeated snapped cascade hash from the final drag
  frame, so waiting 2-4 stable frames lets the pre-move atlas generation remain
  sampleable as soon as movement ends. Settled directional cascade requests now
  force fresh on the first repeated request hash.
- Follow-up directional fresh-before-stale fix: critical directional cascade
  refresh requests no longer publish the old stale tile as the active lighting
  sample while waiting for the fresh render. If a critical fresh render cannot
  commit before lighting, the slot falls back through the explicit request
  fallback instead of replaying the pre-refresh atlas tile.
- Follow-up in-flight refresh publish fix: a later user report showed the blink
  can also happen during long camera moves, not only at stop. The publish path
  reconciled dirty directional cascade requests against the resident allocation,
  and a resident allocation from an earlier cadence-skipped frame could still
  carry `ActiveFallback=StaleTile`. When the stale age exceeded
  `MaxDirectionalCascadeAtlasStaleFrames`, the shader rejected that old but
  coherent rendered sample before the fresh render committed, producing a
  random-looking one-frame lit/contact fallback. Critical directional refreshes
  now publish the last committed rendered sample as `ShadowFallbackMode.None`
  until the fresh tile commits, so the forced-fresh work still happens but
  lighting does not age-reject the in-flight sample.
- Follow-up directional atlas shader fix: forward and deferred directional
  atlas sampling now applies `DirectionalShadowAtlasMaxStaleFrames` only when
  the atlas slot's active fallback is `ShadowFallbackMode.StaleTile`. Current
  resident slots remain sampleable after the stale-tile limit, and expired
  non-legacy atlas fallbacks return their explicit lit/contact fallback instead
  of falling through to legacy cascade textures.
- Follow-up present/exposure fix: `VPRC_RenderToWindow` now tracks the source
  texture/FBO used for window presentation; Vulkan refreshes an unwritten
  swapchain image from that source on render-on-demand idle frames and records
  command-buffer cache variants that perform this refresh separately.
- Follow-up auto-exposure fix: color grading asks the renderer whether
  `AutoExposureTex` is shader-sample ready before enabling GPU exposure
  sampling, and the Vulkan renderer answers from the same descriptor snapshot
  path used by descriptor writes.
- Follow-up auto-exposure history fix: `AutoExposureTex` preservation now works
  across planner gaps where the immediate next physical plan lacks the exposure
  resource. The previous exposure image is retained, excluded from old-plan
  destruction, restored into the next active exposure image, then destroyed.
- Follow-up expired-stale cascade fix: directional cascade cadence now forces a
  fresh render when the resident rendered tile reaches the stale-age ceiling.
  It no longer publishes an expired stale atlas slot that the shader must reject
  into the lit/contact fallback path.
- Follow-up render-on-demand startup fix: the bootstrap input bridge now exposes
  `SetFlyableCameraRenderOnDemand(...)`, `BootstrapPawnFactory` calls it when
  the JSONC property is explicitly specified, and the editor bridge applies it
  to `EditorFlyingCameraPawnComponent.RenderOnDemand`.
- Follow-up exposure stability fix: `XRViewport.SuppressAutoExposureUpdates`
  lets editor render-on-demand hold auto exposure during invalidation/settle
  frames, `VPRC_ExposureUpdate` skips before touching the GPU exposure state
  when the flag is set, and both GPU exposure compute shaders interpolate from
  fallback exposure after a history reset.
- Follow-up exposure-hold removal: the arbitrary camera-motion exposure
  frame-count hold was removed from the editor camera and
  `ColorGradingSettings`. Exposure update remains suppressed only for explicit
  render-on-demand suppressed viewport frames, and color grading samples GPU
  exposure only when the renderer reports that `AutoExposureTex` is ready for
  shader sampling.
- Follow-up Vulkan resource-plan fix: persistent physical image/buffer usage
  signatures now ignore flapping render-pass metadata and are based on stable
  descriptor/FBO/storage requirements. This prevents non-atlas render targets
  such as final post-process output and auto-exposure from being torn down and
  recreated during unrelated pass transitions.
- Follow-up Vulkan auto-exposure fix: the compute path detects planner-backed
  HDR sources without mip pyramids and uses filtered mipless metering instead
  of sparse mip-0 samples. This keeps auto-exposure bounded until render-graph
  mip generation exists for that source.
- Follow-up forward directional cascade fix: forward lighting no longer
  disables directional cascade atlas binding merely because a material pass did
  not resolve a concrete camera component. If the light has published desktop
  cascades, forward passes keep using them.
- Follow-up stale cascade preservation fix: directional cascade publication
  preserves previous rendered uniforms for the same physical atlas slot while
  a fresh render is pending, marks that sample as `StaleTile`, and avoids
  publishing an unsampled lit/black fallback for the in-flight frame.

## Validation

- CPU frame dumps, GPU pipeline dumps, MCP profiler stats, viewport captures,
  and runtime logs were collected under
  `Build/_AgentValidation/20260702-122839-editor-framerate-dumps`.
- Representative pre-fix stats:
  - GPU pipeline frame time: 33-54 ms wall-clock, but actual GPU command-buffer
    time about 2.4-3.0 ms.
  - Render-thread CPU after startup: about 58-63 ms average, with p95 values up
    to about 141-164 ms.
  - Frame ops: about 235-257 total, 171-195 mesh.
- Representative occlusion-disabled A/B stats:
  - GPU pipeline frame time: 14.5 ms.
  - GPU command-buffer time: about 2.43 ms.
  - Render-thread CPU after startup: 15.5 ms average, p95 about 20.2 ms.
  - Frame ops: 57 total, 43 mesh.
- Representative post-gate stats:
  - GPU after startup: about 2.5 ms average, p95 about 2.8 ms.
  - Render-thread CPU after startup: about 44 ms average, p95 about 59 ms.
  - Frame ops: about 135-154 total, 121-140 mesh.
  - No query/proxy operation names appeared in the sampled post-gate GPU dump.
- Initial targeted tests passed:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanCpuDirectOcclusionTests|FullyQualifiedName~GpuIndirectPhase3PolicyTests|FullyQualifiedName~ShadowAtlasManagerPhaseTests|FullyQualifiedName~PointShadowAtlasStabilityTests" --no-restore`
  - Result: 50 passed, 0 failed.
- Follow-up targeted tests passed:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~ShadowAtlasManagerPhaseTests|FullyQualifiedName~DirectionalCascadeAtlasStaleFrameTests|FullyQualifiedName~VulkanCpuDirectOcclusionTests" --no-restore`
  - Result: 42 passed, 0 failed.
- Follow-up targeted tests passed after cadence/grouped-path changes:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~DirectionalCascadeAtlasStaleFrameTests|FullyQualifiedName~ShadowAtlasManagerPhaseTests|FullyQualifiedName~VulkanCpuDirectOcclusionTests" --no-restore`
  - Result: 42 passed, 0 failed.
- Build after the latest stale-age/cadence and profiler changes passed:
  - `dotnet build .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal`
  - Result: build succeeded; existing Magick.NET advisory warnings only.
- Focused tests after the profiler/occlusion telemetry changes passed:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanCpuDirectOcclusionTests|FullyQualifiedName~ProfilerProtocolTests|FullyQualifiedName~CpuRenderOcclusionCoordinatorTests|FullyQualifiedName~DirectionalCascadeAtlasStaleFrameTests|FullyQualifiedName~DirectionalShadowAtlasFallbackTests|Name=GpuOcclusionTelemetry_UsesActualPassStrategyAndSocFilter|Name=ForwardDepthNormalPrepass_UsesResolvedStrategyAndMirrorsGpuPrefilter" --no-build -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal`
  - Result: 53 passed, 0 failed.
- A broader source-test filter that included all of
  `GpuIndirectPhase7ZeroReadbackTests` still has unrelated stale source-contract
  failures in `MeshletPass_DoesNotRunCpuRenderBeforeGpuMeshlets` and
  `SkinnedBounds_ZeroReadbackPath_UsesGpuResidentDirectWriteWithoutWaitForGpu`.
- Existing unrelated validation issue observed:
  `RenderSettingsApiSeparationTests.RenderBackendSelectionAndVulkanTargetModeUseSeparatedPolicySources`
  expects `XRE_VK_RENDER_TARGET_MODE` to appear in
  `VulkanRenderTargetMode.cs`; this source-scrape expectation appears
  unrelated to the framerate regression.
- Stale-age/atlas iteration build and live validation:
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal`
  - Result: build succeeded; existing Magick.NET advisory warnings only.
  - MCP move/stop summary:
    `Build/_AgentValidation/20260702-171735-dir-shadow-atlas-iteration/mcp-output/move-stop-summary.csv`
  - Post-stop screenshots:
    `Build/_AgentValidation/20260702-171735-dir-shadow-atlas-iteration/mcp-captures/stop_01` through `stop_08`
  - Profiler window summary:
    `Build/_AgentValidation/20260702-171735-dir-shadow-atlas-iteration/reports/profile-window-002110-002130.json`
  - Window result: 616 records, `DirectionalCascadeStaleSampled` sum 192,
    `DirectionalCascadeForcedFreshRender` sum 74,
    `DirectionalCascadeMixedGenerationPrevented` sum 0, max Vulkan ops 144,
    and CPU-query submitted/resolved totals reaching 16/16.
- Shader-fix source validation:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~DirectionalCascadeAtlasStaleFrameTests|FullyQualifiedName~DirectionalShadowAtlasFallbackTests" --no-build -v:minimal`
  - Result: 22 passed, 0 failed.
  - A source-building rerun of the same atlas filter is currently blocked by
    unrelated local source under untracked `XREngine.Data/Runtime/`:
    `HotPathObjectPool<T>.LocalBucket` has `TryPush` but `Prewarm` calls
    `Push`. This is outside the shadow-atlas files changed here.
- Post-fix live-editor smoke:
  - Process: `xrengine_2026-07-02_17-30-41_pid32764`
  - Screenshots:
    `Build/_AgentValidation/20260702-171735-dir-shadow-atlas-iteration/mcp-captures/postfix/Screenshot_20260702_173243.png`
    and `Screenshot_20260702_173245.png`
  - MCP profiler samples: Vulkan frame time 7.4-9.4 ms, command-buffer record
    0.18-0.22 ms, Vulkan validation errors 0, CPU-query submitted/resolved
    counters visible.
  - Tail profiler summary:
    `Build/_AgentValidation/20260702-171735-dir-shadow-atlas-iteration/reports/postfix-profiler-tail-summary.json`
  - Tail result: 500 records, max render dispatch 29.2 ms, max Vulkan frame
    20.4 ms, max command-buffer record 0.45 ms, max Vulkan ops 68, and sums of
    `DirectionalCascadeStaleSampled`,
    `DirectionalCascadeMixedGenerationPrevented`, and
    `DirectionalCascadeForcedFreshRender` all 0.
- Present/exposure flicker validation:
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal -clp:ErrorsOnly`
  - Result: build succeeded; existing warning count unchanged at 432.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-build --filter DirectionalCascadeAtlasStaleFrame -v:minimal`
  - Result: 7 passed, 0 failed.
  - Warm Unit Testing World MCP captures after repeated camera move/stop
    cycles were visually stable:
    `Screenshot_20260702_185227.png`,
    `Screenshot_20260702_185233.png`,
    `Screenshot_20260702_185303.png`,
    `Screenshot_20260702_185308.png`, and
    `Screenshot_20260702_185313.png` under
    `Build/_AgentValidation/20260702-180006-stop-flicker-exposure/mcp-captures/`.
  - Latest warm-run Vulkan log:
    `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_18-51-01_pid36176`.
    It shows auto-exposure history retain/restore during planner switches and
    zero-op idle frames blitting from the last present source instead of logging
    `No swapchain write commands were recorded`.
- Expired-stale cascade refresh validation:
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal -clp:ErrorsOnly`
  - Result: build succeeded; existing warning count unchanged at 432.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~DirectionalCascadeAtlasStaleFrameTests -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal`
  - Result: 7 passed, 0 failed.
  - Live MCP run:
    `Build/_AgentValidation/20260702-190927-cascade-atlas-expired-stale-refresh`.
    The move window summary reported 737 profiler frames, validation error max
    0, `DirectionalCascadeMixedGenerationPrevented=0`, and directional
    `StaleSampled` paired with `ForcedFreshRender` during motion, then both
    counters dropped to zero in the stopped tail. Vulkan frame time in the
    sampled stopped tail stayed about 6.7-10.3 ms except for MCP screenshot
    capture blocking the frame loop; the screenshots from `stop_01` through
    `stop_08` were visually stable.
- Render-on-demand startup/exposure validation:
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal -clp:ErrorsOnly`
  - Result: build succeeded; existing warning count unchanged at 432.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~UnitTestingWorldModelImportSettingsTests|FullyQualifiedName~ColorGradingSettingsTests" --no-build -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal`
  - Result: 24 passed, 0 failed.
  - Pre-fix MCP evidence under
    `Build/_AgentValidation/20260702-194022-render-on-demand-exposure/mcp-captures/`:
    `RenderPipeline_AutoExposureTex_20260702_200557.png` had
    `averageRgb=97.0854`, while
    `RenderPipeline_FinalPostProcessOutputTexture_20260702_200557.png`
    was visually white (`averageRgb=34.38346`) despite sane HDR input.
  - Post-fix MCP evidence from process `pid48772`: startup
    `RenderOnDemand=true`, initial and post-cut viewport suppression both true,
    `HDRSceneTex` `averageRgb=0.23594071`,
    `FinalPostProcessOutputTexture` `averageRgb=0.34422258`/`maxRgb=0.70166016`,
    and `AutoExposureTex` stayed at 0 because the render-on-demand viewport held
    GPU exposure and color grading used fallback exposure.
  - Post-fix capture paths:
    `RenderPipeline_HDRSceneTex_20260702_201317.png`,
    `RenderPipeline_FinalPostProcessOutputTexture_20260702_201317.png`,
    `RenderPipeline_AutoExposureTex_20260702_201319.png`, and
    `Screenshot_20260702_201319.png` under the same run root.
  - Latest rendering log shows both exposure-hold paths:
    `[ExposureUpdate] Holding auto exposure while the viewport requested exposure stability.`
    and `[ExposureUpdate] Holding auto exposure during suppressed 3D viewport frame.`
- Continuous-rendering stop-frame source validation:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~DirectionalCascadeAtlasStaleFrameTests -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal`
  - Result: 7 passed, 0 failed.
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal -clp:ErrorsOnly`
  - Result: build succeeded, 432 existing warnings, 0 errors.
  - Source contracts now pin first-repeated-hash settled refresh and prevent
    critical directional fresh-before-stale requests from publishing the stale
    tile as the active lighting sample.
- In-flight directional refresh publish validation:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~DirectionalCascadeAtlasStaleFrameTests -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal`
  - Result: 7 passed, 0 failed.
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal -clp:ErrorsOnly`
  - Result: build succeeded, 432 existing warnings, 0 errors.
  - Source contracts now pin `MergeReconciledRenderedState(...)` so critical
    directional refreshes publish the previous committed rendered sample with
    `ActiveFallback=None` instead of inheriting an old resident
    `StaleTile` fallback that the shader can age-reject.
- Motion-exposure and `Fallback=None` slot-preservation validation:
  - User report: the flicker persisted and auto-exposure still changed while
    dragging with render-on-demand disabled.
  - Fix: editor camera motion now holds `XRViewport.SuppressAutoExposureUpdates`
    for a short rendered-frame tail independent of render-on-demand. Continuous
    keyboard/gamepad motion, mouse rotate/translate, scroll, and focus lerps all
    refresh the hold.
  - Fix: `DirectionalLightComponent.SetCascadeAtlasSlot(...)` now preserves
    previous cascade atlas uniforms for in-flight critical refresh allocations
    with `ActiveFallback=None` when the previous slot matches the same committed
    content version or rendered frame. This prevents a one-frame rendered-sample
    cache miss from creating an unsampled slot that binds as lit fallback.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~DirectionalCascadeAtlasStaleFrameTests|FullyQualifiedName~UnitTestingWorldModelImportSettingsTests" -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal`
  - Result: 26 passed, 0 failed.
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal -clp:ErrorsOnly`
  - Result: build succeeded, 432 existing warnings, 0 errors.
  - Live MCP smoke:
    `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_21-04-06_pid30916`
    with `XRE_DIRECTIONAL_SHADOW_AUDIT=1` and
    `XRE_SHADOW_AUDIT=1`. Scripted camera moves produced a real viewport
    capture at
    `Build/_AgentValidation/20260702-202000-dir-cascade-stop-flicker/mcp-captures/after-motion-slot-fix/Screenshot_20260702_210459.png`.
    MCP profiler after the move reported Vulkan validation errors 0,
    `frame_lifecycle.total_ms=5.2943`, command-buffer record 0.1591 ms, and
    `dirty_summary=primary-frame-state`.
  - Live logs confirmed the motion exposure hold:
    `[ExposureUpdate] Holding auto exposure while the viewport requested exposure stability.`
    Directional audit in the sampled tail showed all four desktop cascades
    `decision=RenderedSample`, `fallback=None`, `sampleable=True`,
    `DirectionalCascade.StaleSampled=0`, and
    `DirectionalCascade.MixedGenerationPrevented=0`. Deferred binding kept
    `shaderAtlasEnabled=True` with packed cascade slots
    `c0=(1,0,0,0)` through `c3=(1,0,0,3)`.
- Black-fallback and movement-brightness validation:
  - User report: the previous-frame flicker was gone, but a one-frame black
    flicker remained, and the scene still brightened while the camera moved.
  - Finding: deferred and forward CPU bind code treated a directional cascade
    atlas slot as usable only when every required cascade sampled a resident
    page. If a slot intentionally published `Lit`, `ContactOnly`, or another
    explicit non-legacy fallback with `packed.X=0`, the host disabled
    `DirectionalShadowAtlasEnabled`; the shader then fell through to
    legacy/dummy shadow samplers, which could present as black for one frame.
  - Fix: both deferred and forward bind paths now consider required
    directional atlas slots usable when each slot either samples a valid page
    or carries an explicit non-legacy fallback. A real atlas texture is required
    only when at least one required slot samples a page; fallback-only slots
    keep the atlas shader path enabled while binding the dummy atlas texture.
  - Finding: the moving-camera brightness was not necessarily auto-exposure.
    When a critical directional cascade refresh could not safely sample its
    stale tile, the atlas manager demoted the fallback to fully `Lit`, removing
    directional shadowing until the fresh tile committed.
  - Fix: unavailable directional `StaleTile` fallbacks now publish
    `ContactOnly` instead of `Lit`. Valid stale-tile reuse and local-light
    fallback behavior are unchanged.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~ShadowAtlasManagerPhaseTests|FullyQualifiedName~DirectionalShadowAtlasFallbackTests|FullyQualifiedName~DirectionalCascadeAtlasStaleFrameTests" -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal`
  - Result: 56 passed, 0 failed. Existing Magick.NET advisory warnings still
    print during restore/build.
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal -clp:ErrorsOnly`
  - Result: build succeeded, 432 existing warnings, 0 errors.
  - Live MCP smoke:
    `Build/_AgentValidation/20260702-202000-dir-cascade-stop-flicker/mcp-captures/after-contact-fallback/Screenshot_20260702_212539.png`
    and
    `Build/_AgentValidation/20260702-202000-dir-cascade-stop-flicker/mcp-captures/after-contact-fallback/Screenshot_20260702_212540.png`
    were captured shortly after a scripted camera move/stop and were visually
    stable/non-black.
  - MCP profiler after the move reported Vulkan validation errors 0,
    descriptor fallback counts 0, dropped frame ops 0, frame time 8.5251 ms,
    `frame_lifecycle.total_ms=5.2139`, and command-buffer record 0.1489 ms.
- Contact-only preservation and camera-level exposure hold validation:
  - User report: neither the black flicker nor the moving-camera brightness
    issue was fixed yet.
  - Finding: when a directional cascade refresh was pending and the active
    fallback became `ContactOnly`, the previous rendered atlas tile could still
    be coherent and sampleable. Publishing `ContactOnly` as the active slot
    discarded that coherent sample for a frame even though the prior tile,
    rendered matrix, and content generation still matched.
  - Fix: directional cascade slot publication now preserves coherent previous
    rendered samples through pending `ContactOnly` allocations. Preserved
    contact-only pending slots publish as `Fallback=None`, while true
    `StaleTile` preservation remains `StaleTile`.
  - Finding: the viewport-level exposure hold was not authoritative enough; GPU
    auto-exposure could still be reached through the camera post-process state
    shortly after camera motion.
  - Fix: `ColorGradingSettings` now owns a render-frame exposure hold and all
    CPU/GPU exposure update entry points return before clearing the last valid
    GPU exposure texture while the hold is active. The editor camera forwards
    motion/render-on-demand holds into that state, and the editor hold window
    is 12 render frames to cover input/update cadence gaps at 60-120 Hz.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~ColorGradingSettingsTests|FullyQualifiedName~UnitTestingWorldModelImportSettingsTests|FullyQualifiedName~ShadowAtlasManagerPhaseTests|FullyQualifiedName~DirectionalShadowAtlasFallbackTests|FullyQualifiedName~DirectionalCascadeAtlasStaleFrameTests" -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal`
  - Result: 84 passed, 0 failed. Existing Magick.NET advisory warnings still
    print during restore/build.
  - Final live MCP smoke:
    `Build/_AgentValidation/20260702-202000-dir-cascade-stop-flicker/mcp-captures/after-12-frame-exposure-hold/`.
    The three post-stop captures
    `Screenshot_20260702_215327.png`, `Screenshot_20260702_215329.png`, and
    `Screenshot_20260702_215331.png` were byte-identical and visually
    non-black.
  - MCP profiler after the final move reported Vulkan validation errors 0,
    descriptor fallback counts 0, dropped frame ops 0, missing scene swapchain
    writes 0, `swapchain_write_count=2`, frame time 9.7608 ms,
    `frame_lifecycle.total_ms=5.2637`, and command-buffer record 0.1402 ms.
  - Final logs:
    `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_21-52-43_pid35640`.
    They show both exposure hold guards firing:
    `[ExposureUpdate] Holding auto exposure while the viewport requested exposure stability.`
    and
    `[ExposureUpdate] Holding auto exposure while the camera requested exposure stability.`
    Directional audit shows only the startup no-sample `ContactOnly` frame
    before the first atlas render; after commit, desktop cascade provenance is
    consistently `decision=RenderedSample`, `fallback=None`,
    `sampleable=True`, with deferred atlas binding enabled and packed slots
    `c0=(1,0,0,0)` through `c3=(1,0,0,3)`.
- No-arbitrary-hold resource/exposure/cascade validation:
  - User report: all issues still existed, the arbitrary exposure frame hold
    was likely wrong, and the black flicker had become visible in the main view
    even when atlasing was off.
  - Fixes in this pass removed the arbitrary exposure hold, stabilized Vulkan
    persistent resource usage signatures, added filtered mipless Vulkan
    auto-exposure metering, kept forward directional cascade atlas binding
    enabled when published cascades exist, and preserved previous rendered
    cascade uniforms for same-slot in-flight atlas refreshes.
  - Focused validation:
    `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=DirectionalCascadeSlots_UseRenderedSampleProvenanceForStaleAtlasTiles|Name=DirectionalCascadeShaders_ReprojectStaleAtlasSamplesWithRenderedUniforms|Name=DirectionalPrimaryShadowAtlas_IsSubmittedRenderedBoundAndPreviewed|Name=DirectionalCascadeSourceFrusta_DoNotMixDesktopAndHmdSources|Name=DirectionalCascadeAtlasStaleTiles_PreserveRenderedUniformState|Name=VulkanPlanner_PersistentColorTargetsKeepStablePhysicalUsageAcrossMetadataChanges|Name=VulkanGpuAutoExposure_UsesBlockMeteringWhenMipPyramidIsUnavailable|Name=ColorGradingSettings_DoesNotUseFrameCountExposureHolds|Name=ExposureUpdate_HoldsGpuAutoExposureDuringSuppressedViewportFrames|Name=EditorFlyingCameraRenderOnDemand_Suppresses3DAndExposureOnlyWhenIdle" -p:UseSharedCompilation=false -p:NodeReuse=false -v:minimal`
  - Result: 8 passed, 0 failed. Existing Magick.NET advisory warnings still
    print during restore/build.
  - Live validation run:
    `Build/_AgentValidation/20260702-222600-stable-vulkan-resource-plan`,
    log
    `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_22-47-47_pid8284`,
    screenshot
    `Build/_AgentValidation/20260702-222600-stable-vulkan-resource-plan/mcp-captures/Screenshot_20260702_224813.png`.
  - Scripted five-second camera move sampled `AutoExposureTex` as
    `0.76026, 0.7709751, 0.7592448, 0.7326956, 0.7027876, 0.6782416,
    0.65694165, 0.6405129, 0.6394633, 0.6394633`, with no exposure spike.
  - The latest directional audit had no `ForwardBind` rows with
    `shaderAtlasEnabled=False` after startup. During motion, same-slot pending
    cascade refreshes logged `decision=PreservedPrevious`,
    `fallback=StaleTile`, `sampleable=True`, and
    `DirectionalCascade.StaleSampled=1` until the fresh tile committed.
    `DirectionalCascade.MixedGenerationPrevented` remained startup-only.
  - Vulkan allocation logs showed `HDRSceneTex`, `AutoExposureTex`, and
    `FinalPostProcessOutputTexture` allocated once at startup instead of being
    repeatedly reallocated during the move. The remaining command-buffer dirty
    and swapchain-recreate warning was startup-only in this run.

## Remaining Work

- Add a durable automated camera-move-then-stop editor scenario instead of the
  current one-off MCP script. It should capture several frames before and after
  stopping, record profiler stats for the same frames, and preserve the
  directional provenance counters alongside the screenshots.
- Get an interactive F5 confirmation for the latest no-arbitrary-hold fixes.
  MCP screenshots block the render loop while capturing, so they cannot fully
  replace the normal drag path.
- If a one-frame snap or black frame is still user-visible, capture the final
  post-move frame and the first settled frame with RenderDoc or exported
  atlas/depth/post-process targets. Prioritize final post-process,
  `HDRSceneTex`, `AutoExposureTex`, directional atlas pages, and forward/deferred
  directional bind audit rows.
- If the moving-camera brightness is still user-visible, capture both
  `AutoExposureTex` samples and per-cascade fallback/provenance on the bright
  frame. The latest evidence shows smooth exposure and sampleable preserved
  `StaleTile` cascade slots, so a remaining case is likely another lighting or
  temporal-history path.
- Investigate the startup-only command-buffer dirty/swapchain recreate warning
  separately if it becomes visible outside startup.
- Investigate primary command-buffer reuse separately. The steady post-stop
  dirty reason is still `primary-frame-state`, which is distinct from
  CPU-query proxy draws and remains present even when occlusion is not the main
  cost.
- Investigate texture upload/residency dirties separately. Many movement spikes
  still included `ImportedTextureUploadPublished` and
  `forced:image+variant:image-forced-dirty`; these can dominate the movement
  profile independently of shadow correctness.
- Keep CPU-query validation focused on the CPU-direct counters:
  `cpu_query_submitted_total` and `cpu_query_resolved_total`. The
  `cpu_query_async_*` counters are useful for the GPU-dispatch scaffold path
  and should not be used as the only "is CpuQueryAsync working?" signal in
  CPU-direct Vulkan mode.
- Clear or finish the unrelated untracked `XREngine.Data/Runtime/` pool work
  before doing source-building validation from this checkout.
