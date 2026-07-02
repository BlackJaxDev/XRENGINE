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
- Settings observed at start: Unit Testing World, Vulkan backend, desktop VR
  mode, one directional light, profiler logging disabled in JSONC.
- Main live-editor sessions:
  - `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_12-30-19_pid26524`
  - `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_12-46-56_pid50692`
  - `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_12-51-05_pid45508`
  - `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_12-56-47_pid27944`
  - `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_16-34-24_pid2864`

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
- Follow-up shadow-lag fix in progress: the default
  `MaxDirectionalCascadeAtlasStaleFrames` was reduced from 32 to 2 to match the
  design contract. Directional cadence now treats an expired stale age as
  "stop sampling this stale tile" instead of "force a full cascade refresh right
  now", and a settled full refresh now waits for a short stable-request window
  instead of firing on the first repeated request hash.

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

## Remaining Work

- Run the editor iteration loop again after the latest stale-age/cadence change
  and compare against coherent stale run 7. The key checks are:
  post-stop frame captures, `DirectionalCascadeForcedFreshRender`,
  `DirectionalCascadeStaleSampled`, mixed-generation audit lines, Vulkan frame
  ops, and CPU-query submitted/resolved totals.
- Add or script an automated camera-move-then-stop editor scenario that captures
  several frames before and after stopping. The current audit samples did not
  catch a post-stop mismatch, so the validation needs frame-by-frame screenshots
  plus the directional provenance log for the same frames.
- If the one-frame snap still appears, capture the final post-move frame and
  the first settled frame with RenderDoc or exported atlas/depth targets. Decide
  whether the visible error is a stale/fresh cascade transition, a present-order
  issue, a temporal-history issue, or a missing provenance case.
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
