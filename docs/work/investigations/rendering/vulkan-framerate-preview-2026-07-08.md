# Vulkan Framerate And VR Pickup Preview Investigation - 2026-07-08

## Problem

The Vulkan renderer becomes extremely slow on the desktop mirror and VR eyes. The VR pickup camera preview UI region is drawn, but the sampled camera view can appear black or incorrect.

## Evidence

Profiler/log artifacts for this investigation are under:

- `Build/_AgentValidation/20260708-120000-vulkan-framerate-preview/reports/`
- `Build/_AgentValidation/20260708-120000-vulkan-framerate-preview/logs/`

Useful profiler runs:

| Run | Render P50 | Render P95 | GPU P50 | GPU P95 | Vulkan frame P50 | Plan replacements | Command chains scheduled/reused |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `2026-07-08_12-24-51` | 70.531 ms | 969.777 ms | 108.623 ms | 1165.658 ms | 25.470 ms | 10 | 200 / 36 |
| `2026-07-08_12-29-03` | 35.602 ms | 1050.512 ms | 33.322 ms | 1034.596 ms | 21.022 ms | 85 | 40 / 0 |
| `2026-07-08_12-31-28` | 35.647 ms | 865.777 ms | 38.458 ms | 922.995 ms | 21.148 ms | 93 | 80 / 0 |
| `2026-07-08_12-37-30` direct-state experiment | 146.859 ms | 653.463 ms | 172.673 ms | 711.957 ms | 73.596 ms | 9 | 40 / 0 |
| `2026-07-08_12-49-34` query baseline | 25.033 ms | 526.662 ms | 24.961 ms | 994.967 ms | 15.828 ms | 136 | 200 / 0 |
| `2026-07-08_12-50-33` `XRE_VK_SKIP_OCCLUSION_QUERY_OPS=1` | 35.411 ms | 1073.499 ms | 26.800 ms | 1172.583 ms | 21.287 ms | 100 | 1128 / 0 |
| `2026-07-08_12-52-23` `XRE_OCCLUSION_CULLING_MODE=Disabled` | 25.966 ms | 448.601 ms | 29.965 ms | 532.971 ms | 15.832 ms | 164 | 2397 / 0 |
| `2026-07-08_12-54-40` forced chain trace | 28.443 ms | 108.879 ms | 29.759 ms | 896.390 ms | 19.544 ms | 102 | 3490 / 0 |
| `2026-07-08_13-05-44` per-image guard baseline | 32.115 ms | 575.952 ms | 37.267 ms | 987.137 ms | 19.548 ms | 131 | 48 / 0 |
| `2026-07-08_13-09-04` per-image guard `XRE_VK_SKIP_OCCLUSION_QUERY_OPS=1` | 27.078 ms | 919.943 ms | 33.371 ms | 919.942 ms | 16.500 ms | 135 | 550 / 0 |
| `2026-07-08_13-11-22` global guard `XRE_VK_SKIP_OCCLUSION_QUERY_OPS=1` | 264.774 ms | 842.076 ms | 261.169 ms | 450.694 ms | 143.875 ms | 56 | 0 / 0 |
| `2026-07-08_13-12-26` global guard baseline | 41.983 ms | 1313.567 ms | 40.160 ms | 1923.801 ms | 28.167 ms | 85 | 40 / 0 |
| `2026-07-08_13-21-29` third-pass cache boundary fix | 36.199 ms | 514.453 ms | 57.696 ms | 954.650 ms | 15.020 ms | 12 | 40 / 0 |
| `2026-07-08_13-25-23` third-pass cache + resource-ready fix | 40.884 ms | 1543.622 ms | 55.984 ms | 1023.271 ms | 19.648 ms | 9 | 40 / 0 |
| `2026-07-08_13-34-03` registry-aware metadata prune | 88.616 ms | 1335.819 ms | 209.766 ms | 526.947 ms | 36.400 ms | 9 | 24 / 0 |
| `2026-07-08_13-37-03` rejected span-lookup prune experiment | 68.306 ms | 1414.990 ms | 68.303 ms | 2409.366 ms | 30.402 ms | 15 | 0 / 0 |
| `2026-07-08_13-44-59` external feature-key stability | 31.666 ms | 605.877 ms | 37.840 ms | 1360.543 ms | 15.509 ms | 15 | 24 / 0 |
| `2026-07-08_13-49-48` same-key invalidation clean validation | 56.467 ms | 575.914 ms | 69.684 ms | 1096.180 ms | 20.373 ms | 12 | 40 / 0 |
| `2026-07-08_13-52-34` pre-materialization stale discard | 64.701 ms | 551.328 ms | 98.780 ms | 210.067 ms | 35.088 ms | 4 | 0 / 0 |
| `2026-07-08_14-52-06` descriptor slot baseline | 336.071 ms | 829.754 ms | n/a | n/a | 154.837 ms | 0 | 0 / 0 |
| `2026-07-08_14-56-44` descriptor slot fix, short warmup | 660.510 ms | 1244.001 ms | n/a | n/a | 305.394 ms | 4 | 0 / 0 |
| `2026-07-08_14-59-25` descriptor slot fix, long warmup | 118.102 ms | 466.535 ms | n/a | n/a | 61.564 ms | 0 | 0 / 0 |
| `2026-07-08_15-01-12` descriptor fix + skipped QueryOps diagnostic | 611.866 ms | 1504.265 ms | n/a | n/a | 269.783 ms | 4 | 0 / 0 |

Log observations:

- `ScreenUIDraw` confirms the VR pickup preview quad is submitted to the desktop UI region.
- The preview target resources (`UIViewportColor_*`, `UIViewportDepthStencil_*`, and `FinalPostProcessOutputTexture` at 300x170) were repeatedly allocated/replanned, so the black/incorrect preview is upstream of UI placement.
- No `VK_ERROR_*` or renderer exceptions occurred in the profiled runs after the current fixes.
- `Frame contains occlusion QueryOps; recording inline instead of command chains` appears in the baseline. Skipping the QueryOps confirms they are a suppressor, but not the root cause: chains then schedule in much larger numbers and still reuse zero times.
- The default pipeline keeps changing resource registry and pass metadata signatures in steady state, so resource plans are still replaced even after planner-state pruning stopped.
- Forced command-chain trace shows the post-warmup dirty reason is usually `ResourcePlan` or `ResourcePlan, DescriptorGeneration` even when structural signatures match. Example chain rows include GTAO quad blits and final postprocess blits where `previousSig == currentSig`, but `resource-plan-revision` and descriptor generations changed.
- Command-buffer dirty logs are dominated by `ReleaseDescriptorReferencesForPhysicalResourceDestruction`, `NotifyRenderResourcesChanged`, `CollectBuffers`, `MarkIndexBuffersDirty`, and late `TryEnqueueVulkanGraphicsPipelineCompile`.
- The global guard run with `XRE_VK_SKIP_OCCLUSION_QUERY_OPS=1` proves the guardrail can cap command-chain work completely (`0 / 0` scheduled/reused in capture), but that mode is not a valid frame-rate proxy: stale/conservative occlusion caused median draw calls to jump from about 20-28 to 187 and median triangles from about 352-360 to 185,518.
- The final normal baseline had no Vulkan errors or device-lost/out-of-memory signatures, but still had high p95 GPU time and 85 resource plan replacements.
- The third-pass cache boundary fix reduced plan replacements to 12 and removed the repeating steady-state allocator storm.
- The resource-ready fail-closed fix removed `RenderToWindow skipped` and rendering-log missing-resource warnings for the pickup preview path. The editor now skips frames while managed resources catch up instead of drawing with partial legacy resources.
- The registry-aware metadata prune removed the Vulkan missing-declared-resource warning storm for disabled/optional branches. The accepted validation run had `references missing declared=0`, no `VK_ERROR`, no device lost, no OOM, no `RenderToWindow skipped`, and 9 resource plan replacements.
- The rejected span-lookup variant also kept missing-resource warnings at zero, but regressed plan churn to 15 replacements and command-chain scheduling to `0 / 0`; it was backed out in favor of the dictionary-lookup version.
- The external eye feature-mask flip is resolved. The latest runs have `features:0x1000->0x9000=0` and `features:0x9000->0x1000=0`; same-key external invalidations now skip because the active layout matches.
- The pre-materialization stale guard converts the startup `aa=Tsr` pending generations into early discards. Latest validation had `Pending generation is stale at commit=0`, `Pending generation is stale before materialization=2`, and no Vulkan errors, device loss, OOM, validation errors, missing declared resources, or skipped window presents.
- The descriptor pool-miss blocker was isolated to captured frame-source resources. `Skybox.FullscreenTriangle` / `Skybox.DynamicProcedural` had stable descriptor schemas (`sameS=4`) but no matching resource fingerprint (`sameR=0`) because descriptor resources vary per desktop/eye/frame slot.
- The per-slot descriptor resource fingerprint fix removed steady descriptor pool churn after warmup. In `2026-07-08_14-59-25`, the timestamp-correct capture window had `ResourcePlanReplacementsTotal=0`, `RetiredDescriptorPoolsTotal=0`, and the last 80 rows had no plan, descriptor-pool, buffer, or image retirements.
- The short-warmup descriptor-slot validation looked bad if read from the summary alone because the capture overlapped startup resource generation. Reading by `CaptureStartUtc`/`CaptureEndUtc` showed startup churn during frames 49-64, then zero plan/pool churn from frames 65-182.
- The remaining steady-state primary command-buffer recording aligns with active CPU occlusion query frames. In the long-warmup tail, every other frame had `cpu_query_passes_active=6`, `vulkan_primary_command_buffers_recorded=2`, and no command-chain scheduling; non-query frames had zero primary records and clean desktop command-buffer reuse.
- `XRE_VK_SKIP_OCCLUSION_QUERY_OPS=1` is still only a diagnostic. The latest skipped-QueryOps run fell back into startup resource-plan churn, so it should not be treated as a production fix or a clean performance proxy.
- `2026-07-08_15-08-35_pid3196` validated the OpenXR primary miss diagnostics. The profiler capture window itself missed active rendering, but the full/tail `profiler-render-stats.ndjson` rows now show `openxr-primary-miss:frame-ops-query` twice on query-active frames, matching the two eye primary command buffers.
- The same diagnostic run also shows `openxr-primary-miss:image-layout` and `primary-frame-state:image-layout-start` during streaming/layout churn, so query-driven signature misses are not the only possible primary reuse blocker.

## Changes Tried

- Increased cached frame-op planner state capacity from 6 to 12 to stop immediate churn under desktop + VR + preview + shadows.
- Changed texture upload descriptor publication to clear command-chain schedule caches instead of always dirtying all command buffers when command-chain caches exist.
- Removed transient `RenderResourceRegistry` and pass metadata object identities from the frame-op planner state key.
- Removed active pass/resource-set signatures from the frame-op planner state key; those still drive planner signatures, but no longer multiply cache buckets.
- Changed pipeline context identity to `XRRenderPipelineInstance.InstanceId`.
- Removed nested activate/store during per-key frame-op planner preparation, so one state preparation no longer writes a different recomputed key.
- Tried skipping the merged all-ops planner and preparing per-context planner states directly. This reduced resource-plan replacements from ~90 to 9, but regressed GPU/render median badly, so it was backed out. This shows allocator churn is a real problem, but the recorder still depends on the merged planner/barrier state.
- Added diagnostic toggle `XRE_VK_SKIP_OCCLUSION_QUERY_OPS=1` / editor option "Skip Occlusion Query Ops". It short-circuits Vulkan occlusion QueryOp begin/end enqueue only; proxy draw workload remains, and query results stay stale/conservative. This is for measuring the command-chain ceiling, not a production fix.
- Added command-chain stability guard `XRE_VULKAN_COMMAND_CHAIN_STABILITY_GUARD` (enabled by default, set to `0` to disable). It records inline when a target's resource-plan revision changes, and it adds a renderer-wide backoff when recent command-chain schedules record with zero reuse. Trace/validation modes disable the guard so diagnostics can still force full chain lowering.
- Stopped the transient merged all-frame resource planner from activating/storing into the per-context frame-op planner state cache. The cache key intentionally omits registry shape, so the merged registry was overwriting stable viewport/eye/capture states and causing each context to rebuild physical plans again.
- Added a managed-resource readiness guard to `XRRenderPipelineInstance`: pipelines that declare a resource layout no longer render with the legacy registry when an initial generation is stale or absent. This fixes the black/incorrect preview failure mode where full pass metadata was paired with partial legacy resources.
- Added registry-aware pruning to the active render-pass metadata filter. Vulkan planning now removes `tex::`, `buf::`, and `fbo::` usages that are absent from the active `RenderResourceRegistry`, including the full-metadata/no-active-pass-set path used before per-context frame-op filtering. The filter cache now keys on registry identity and descriptor revision so a layout-specific prune is not reused across resource layouts.
- Removed a duplicated `XRRenderPipelineInstance` render-graph validation block that appeared during this pass and broke the targeted editor build.
- Made the OpenXR Vulkan external swapchain safe-path resource key deterministic by removing temporal-history resources from that feature mask. That command path disables history-based temporal effects for external per-eye swapchains, so the generation key no longer depends on the renderer's transient external-swapchain flag.
- Passed the known `XRViewport` through resize and physical-resource invalidation generation requests so external swapchain keys do not fall back to ambient render state.
- Added a first-difference diagnostic for same-key layout mismatches. The latest validation did not report any same-key layout drift.
- Added a pre-materialization stale check for pending generations. Obsolete startup generations are discarded before allocating physical resources instead of being materialized and then rejected at commit.
- Changed captured descriptor reuse to track resource fingerprints per descriptor frame slot. Reusable descriptor allocations can now refresh only the active mutable slot for frame-source resources instead of requiring one global allocation fingerprint to match every desktop/eye/frame resource binding.
- Made OpenXR primary-cache reuse misses visible in normal profiler stats when `XRE_OPENXR_VULKAN_PRIMARY_REUSE=1`, using compact reasons such as `openxr-primary-miss:frame-ops-query`, `openxr-primary-miss:image-layout`, and mirror-prefixed equivalents. The default disabled path remains quiet unless OpenXR Vulkan tracing is enabled.

## Current Status

Partially improved. The allocator/root-state churn is much better contained, cached planner-state poisoning is fixed, disabled-branch metadata no longer feeds missing undeclared resources into Vulkan planning, captured descriptor reuse now handles per-frame-slot resources, and the command-chain optimizer is guarded so it cannot keep recording thousands of zero-reuse secondaries. External eye resource keys are now stable, and stale startup generations are discarded before materialization. The preview path now fails closed during resource generation instead of rendering from partial resources. The renderer is still not healthy:

- Latest long-warmup validation had zero resource plan replacements and zero descriptor pool retirements during capture, with no external feature-mask flips and no stale-at-commit generation warnings.
- Render P95 is still 466 ms and Vulkan frame P95 is still 239 ms in the long-warmup run, so eliminating resource churn did not eliminate the frame-time spikes.
- `VulkanRecordCommandBufferAllocatedBytesTotal` dropped to about 162 MB in the long-warmup capture, down from 2.62 GB in the pre-descriptor-slot validation, but steady OpenXR primary recording remains expensive.
- Vulkan missing-declared-resource warnings from disabled feature branches are resolved in the accepted fourth-pass validation run.
- Command-chain reuse remains zero on query-active frames because QueryOps force inline recording. This part is intentional for Vulkan correctness today: query brackets cannot be split across secondaries without query inheritance support and careful begin/end ownership.
- OpenXR primary reuse misses are no longer a blind spot. The current evidence says query-active frames miss by frame-op signature (`openxr-primary-miss:frame-ops-query`), while layout churn can independently miss with `openxr-primary-miss:image-layout`.
- Command chains are currently harmful under this instability. The guardrail mitigates the optimizer tax, but does not fix the resource churn or p95 GPU stalls.
- The preview UI placement is correct, and the previous `RenderToWindow skipped` symptom did not recur in the third-pass validation run.
- A direct per-context planner path is not safe as a narrow patch; it needs a recorder/barrier-plan refactor so each context can keep allocator state without losing the global barrier assumptions.

## Next Work

- First target: make OpenXR primary reuse misses visible in normal profiler captures. The desktop path reports `primary-frame-state:query-ops`, but OpenXR primary cache misses are currently mostly silent unless `XRE_OPENXR_VULKAN_TRACE=1` is enabled.
- Keep the command-chain guardrail in place while planner churn is being fixed; it is a damage limiter, not the root-cause fix.
- Design the query-aware root fix: either isolate CPU occlusion QueryOps into their own legal Vulkan recording/submission path, or add query-inheritance-aware command-chain partitioning so unrelated eye rendering can reuse command buffers while query brackets remain correct.
- If `primary-frame-state:image-layout-start` persists after the query path is isolated, add a first-difference diagnostic for layout start/end snapshots.
- Re-run the editor iteration loop with MCP screenshots to visually confirm the VR pickup preview content after the planner churn is eliminated.

## Fifth Pass: CPU Query Occlusion Suppression On Vulkan (2026-07-08 evening)

### Change

`CpuQueryAsync` hardware occlusion is now suppressed by default on the Vulkan backend, because occlusion QueryOps force fresh primary command buffers and disable command-chain reuse on every query frame, and the earlier `XRE_OCCLUSION_CULLING_MODE=Disabled` run was already equal-or-better than the query baseline. The diagnostic opt-in `XRE_VULKAN_ALLOW_CPU_QUERY_OCCLUSION=1` (or `RenderDiagnosticsFlags.SetVkAllowCpuQueryOcclusion(true)` at runtime) restores hardware queries.

- `RenderCommandCollection.ShouldSuppressCpuQueryOcclusionForCurrentView` now suppresses all views on Vulkan (one-time log in `log_rendering.log`), keeping the previous `EditorDesktopWhileVr` logic for the opt-in path.
- `VulkanFeatureProfile.ResolveOcclusionCullingMode` downgrades `CpuQueryAsync` to `Disabled` on Vulkan for the GPU-dispatch path unless the opt-in is set.
- Note: the user's persisted `engine_defaults.asset` has `GpuOcclusionCullingMode: CpuQueryAsync` (engine default is `GpuHiZ`); the suppression makes that setting inert on Vulkan by default.

### A/B validation (Monado OpenXR VR active, desktop mirror + preview, Debug, `XRE_PROFILE_CAPTURE=1`, last 60 frames after ~100 s warmup)

| Run | render_dispatch P50 | render_dispatch P95 | Vulkan frame P50 | Vulkan frame P95 | cpu query passes | draws P50 | tris P50 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| A `2026-07-08_16-27-53` fix active (queries suppressed) | **241.6 ms** | **382.5 ms** | 114.5 ms | 181.6 ms | 0 | 173 | 185,558 |
| B `2026-07-08_16-30-40` `XRE_VULKAN_ALLOW_CPU_QUERY_OCCLUSION=1` (old behavior) | 385.2 ms | 786.7 ms | 120.2 ms | 196.9 ms | 186 | 173 | 185,558 |

Identical rendered workload (same draws/triangles — the queries culled nothing from this view) with ~37% better P50 and ~51% better P95. The queries were pure overhead in this scenario.

### Stale bloom / frozen desktop mirror (user-reported, traced but NOT yet fixed)

User observation: the red bloom band on the desktop view is the bloom from the first camera position and never re-renders as the camera moves.

Traced mechanism with `XRE_DIAG_POSTPROCESS=1` bloom diagnostics (now permanently available behind that flag):

- Pre-VR startup: the desktop pipeline instance (#29, 1537x865, full feature mask) executes its chain, `ShouldUseBloom()` evaluates true twice (16:13:39-40), and the bloom downsample/upsample FBOs record exactly once — writing the camera-position-1 bloom into `BloomBlurTexture`.
- At OpenXR session activation (16:13:41) the eye instances (#31/#32, 896x1007, `ExternalSwapchainInitial`) execute their chain once with `safePath=True` (bloom generation skipped by design on the external-swapchain safe path).
- After that, no desktop/eye CPU chain re-executions occur at all; Vulkan replays retained frame ops every frame (`BeginRendering` census: all steady FBOs ~28 recordings in the window, all `Bloom*FBO` exactly 1). Only scene-capture/light-probe contexts keep re-executing chains (`sceneCapture=True` evals).
- The post-process composite samples `BloomBlurTextureName` unconditionally, so every replayed frame re-samples the stale startup bloom. The auto-exposure freeze has the same shape (`[ExposureUpdate] Skipping auto exposure during scene-capture pass` is the only exposure path still running).
- The safe-path resource layout already declares a 1x1 black `BloomBlurTexture` fallback (`DefaultRenderPipeline.Resources.cs` ~line 552), but the composite is not resolving it in this scenario; the stale full-size texture is bound instead.

Open design decision for the fix: either (a) make the OpenXR-active desktop mirror composite bind the declared black fallback when bloom generation is disabled for the executing context, or (b) make chain execution's safe-path evaluation strictly per-viewport (never `LastWindowViewport` ambient fallback) so the desktop context keeps generating bloom/exposure while VR is active. Option (b) restores desktop visual quality but re-adds desktop bloom GPU cost during VR; option (a) matches the current safe-path intent but makes the mirror bloom-less.

### Other fixes landed this pass

- `RenderPipelineResourceLifecycleTests` compile errors fixed (`BuildResourceFeatureMaskForGenerationKey(instance, viewport)` signature) and stale scrape paths updated to `Pipelines/Types/Default/`. Remaining pre-existing failures (3): `DefaultRenderPipeline_GtaoScratchResources_FollowResolutionFeatureMask` (GTAORawTexture not in layout dictionary), `DefaultRenderPipeline_StereoPostProcessSettingsUseEffectiveCamera` (stale literal `RuntimeEngine.Rendering.State.RenderingCamera`), `QuadBlit_PostProcessOutputMetadata_SamplesDepthStencilWithoutAttachingDestinationDepth` (`tex::DepthView` resource usage contract drift). These predate this pass.
- Bloom bake/evaluation diagnostics added behind `XRE_DIAG_POSTPROCESS=1` (`[BloomDiag] AppendBloomPass bake` and `[BloomDiag] ShouldUseBloom`).

### Remaining

- `vulkan_command_buffer_clean_reuse_count` is still 0 with queries suppressed, so primary reuse is still blocked by non-query misses (image-layout / dynamic UI); the doc's existing Next Work items for layout-start diagnostics still apply.
- Fix the stale bloom/exposure composite per the design decision above, then re-validate the desktop mirror and pickup preview visuals with MCP screenshots.

## Sixth Pass: Per-Instance CPU Query Occlusion + Per-Viewport Safe Path (2026-07-08 late evening)

Direction from the user: do not disable CpuQueryAsync on Vulkan; make it work correctly and independently for the desktop render, both VR eye renders, and the VR pickup camera render — isolated per `XRRenderPipelineInstance`.

### Changes

- Reverted the fifth-pass blanket Vulkan suppression entirely: removed `XRE_VULKAN_ALLOW_CPU_QUERY_OCCLUSION`, the `RenderDiagnosticsFlags.VkAllowCpuQueryOcclusion` flag, the `VulkanFeatureProfile.ResolveOcclusionCullingMode` CpuQueryAsync downgrade, and `RenderCommandCollection.ShouldSuppressCpuQueryOcclusionForCurrentView` (including the old `EditorDesktopWhileVr` suppression, so the desktop view also culls while VR is active).
- `OcclusionViewKey` now includes `PipelineInstanceId` (from `XRRenderPipelineInstance.InstanceId` via `CurrentRenderingPipeline`), so every pipeline instance — desktop, each eye, preview/capture — tracks fully independent per-command occlusion state. This also removes the cross-view result poisoning family (preview culled by main-view results).
- Added per-frame probe-slot rotation in `CpuRenderOcclusionCoordinator.SelectProbeCandidates`: at most one pipeline instance issues hardware query probes per frame, rotating fairly across all instances that requested probes on the previous frame. Non-owning instances keep culling from their existing state and probe on later frames. This bounds the Vulkan cost: query brackets force a fresh primary only for the one probing context per frame instead of every query-active context.
- Fix (b) for the stale bloom family: `DefaultRenderPipeline.IsOpenXrExternalSwapchainTargetPass` no longer falls back to `LastWindowViewport`. Chain generation (no active viewport) now always bakes the full superset chain, and per-frame ConditionEvaluators decide per context using only the actively rendering viewport.

### Validation (run C, `2026-07-08_16-59-21`, same Monado VR + mirror + preview scenario, Debug, last 40 frames after warmup + camera moves)

| Metric | Run B (old behavior) | Run A (suppressed) | Run C (per-instance + staggered) |
| --- | ---: | ---: | ---: |
| render_dispatch P50 | 385.2 ms | 241.6 ms | **132.7 ms** |
| Vulkan frame P50 | 120.2 ms | 114.5 ms | **49.4 ms** |
| Vulkan frame P95 | 196.9 ms | 181.6 ms | **75.4 ms** |
| occlusion mode | CpuQueryAsync | Disabled | CpuQueryAsync |
| culled per frame (P50) | 0 effective | n/a | 265 |

- Steady state shows `cpu_query_passes_active` alternating 6/12 (multiple instances tested per frame) with ~265 of ~360-440 tested commands culled per frame — per-instance culling live on desktop, eyes, and preview simultaneously.
- The frozen desktop view did not recur: MCP captures at two camera positions render live, camera-dependent content with no stale red bloom band. No `VK_ERROR`, device loss, or OOM in `log_vulkan.log`.
- render_dispatch P95 (610.8 ms) still spikes; the un-investigated P95 spike family (late pipeline compiles, image-layout reuse misses) remains open.
- Note: heavy blue volumetric-fog/atmosphere haze is visible in the fresh captures; unclear whether this is expected scene content or a previously-frozen pass now running with stale settings — needs a user look.

### Remaining (updated)

- Primary clean reuse is still 0 on non-probing frames; image-layout / dynamic-UI misses remain the blocker (existing Next Work items).
- The full query-isolation design (probe brackets recorded into a dedicated per-frame command buffer so the main primary signature excludes them entirely) is still the root fix for probing-context re-records; the probe-slot rotation bounds the damage to one context per frame in the meantime.
- Single-pass stereo (true multiview) still forces visible unless `CpuQueryOcclusionStereoMode=StereoPairShared`; per-eye sequential (current compatibility path) culls per eye.

