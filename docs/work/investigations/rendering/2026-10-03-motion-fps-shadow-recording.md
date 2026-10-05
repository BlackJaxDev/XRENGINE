# Camera-motion frame rate and directional shadow-caster recording (S15a)

Status: S15a attribution complete; one exact-equivalence lowering correction
retained (+17% fresh frames per second in camera motion); the 100 Hz motion
target is **not met** (about 40 fresh frames per second; see the closeout).
October 3, 2026.

Owner item: [S15a](../../progress/rendering/vulkan-stall-remediation-results.md#shadow-recording-results)
of the Vulkan stall remediation TODO. Target set by the user: above 100 Hz with
Vulkan, the Advanced render pipeline and CpuDirect submission rendering Sponza
with a single directional light, without removing features.

## Workload and method

- Fixture: the S14 unit-world settings (Sponza2 OBJ `Uber` materials, scale
  0.01, one shadow-casting directional light at yaw -120 / pitch -55 degrees,
  27-probe model grid, IMGUI editor, Vulkan, AdvancedRenderPipeline, CpuDirect,
  `PresentationProfile: Uncapped`, `VSyncOverride: Off`). Random environment
  map (unchanged; it only affects the sky in the `camA` view).
- Release isolated MCP session. The repository `global.json` pins workload set
  `10.0.401.1`, which is not installed on the measurement host; builds set
  `MSBuildEnableWorkloadResolver=false` for the build process only. No SDK or
  workload installation was changed.
- A/B variants differ only in `XREngine.Runtime.Rendering.Vulkan.dll`, swapped
  into one session's editor output and restarted without rebuilding. Hashes:
  base `12284D5D…`, retained correction `7E785344…`.
- Frame rate: 20 s stationary at camera A, then 20 s of continuous motion that
  chains 2 s eased moves between camera A `(-20,2,4)->(-20,2,-8)` and camera B
  `(-15,3,2)->(-15,3,-10)`. **Fresh frames** are counted from the new
  `frame_lifecycle.outcome_counts.completed` counter. Run-to-run spread of the
  FPS metric is about ±3 FPS in motion and ±15% stationary, so record-path
  changes are compared with the engine CPU-stage timers instead (±1% per call).
- Attribution: EventPipe sample-profiler traces were used only for coarse
  subtrees. Their leaf frames are biased toward GC/suspension safe points
  (`Monitor.Enter_Slowpath`, `PollGCWorker` and `BulkMoveWithWriteBarrier`
  showed 35/29/24% while runtime counters reported ~1% GC pause and no lock
  contention). Leaf attribution uses `XRE_VULKAN_RECORDING_PROFILE_DETAIL=1`
  stage timers and `dump_cpu_frame_profile` scopes instead.

## Baseline

| Build | Stationary fresh FPS | Motion fresh FPS |
| --- | ---: | ---: |
| Base (HEAD `887e9f0fb`) | 137–161 | 33.6 (six windows, 33.4–33.8) |

During motion, 40–50% of frames refresh the grouped directional cascades under
the existing motion-cadence policy (unchanged here). A refresh records about 400
shadow-caster draws once each into three atlas cascades (instanced layered
cascades), costing about 50 ms of render-thread CPU per refresh frame. Ordinary
motion frames cost about 7 ms.

Per refresh frame (407 caster draws), unbiased detail scopes:

| Scope | ms / refresh frame | µs / draw |
| --- | ---: | ---: |
| Command-chain packet lowering (before the fix) | ~13 | — |
| `MeshDraw.Bind.AutoUniforms` (legacy per-member block writes) | 8.2 | 20.2 |
| `mesh_draw_binding_snapshot_copy` (per-draw binding snapshot capture) | ~7 | 18 |
| `MeshDraw.Prepare.EnsureDescriptors` | 3.4 | 8.4 |
| `MeshDraw.Bind.Ensure` (second descriptor ensure per draw) | 2.1 | 5.2 |
| `MeshDraw.EnsurePipeline` | 1.6 | 3.9 |
| `vkEndCommandBuffer` | 2.6–3.7 | — |
| Frame-data prewarm, cohort staging, hole materialization | ~13 | — |

The generic per-renderer draw path costs about 45 µs per draw just in the
recording loop (the 13 stationary non-Advanced draws cost the same per draw),
and about 100–125 µs per draw end to end.

## Findings

### Retained: packet lowering scanned runs that could never form a packet

Shadow casters carry a material override and usually form compatible runs up to
the 16-draw shadow cap, but `LimitMeshPacketToRecordedIdentityCapacity` truncates
every run below the 10-draw minimum: a packet key holds at most 16 vertex-buffer,
16 auxiliary-buffer and 16 descriptor-set identities. A temporary probe counted
zero shadow packets formed and ~68K capacity truncations per 3 s. Every start
index still ran the full compatibility scan (struct copies of `FrameOpContext`,
signatures, descriptor snapshots), about 13 ms per refresh frame.

The correction captures each mesh operation's identity demand once per lowering
pass (`MeshPacketIdentityDemand`) and rejects a start position before the scan
when capacity alone cannot reach the minimum. The capacity limiter uses the same
captured demand. The accepted packet set is unchanged by construction.

| Variant | Motion fresh FPS | Stationary fresh FPS |
| --- | ---: | ---: |
| Base | 33.6, 33.6 | 156.9, 154.9 |
| Corrected | 39.4, 39.3 (later 40.1, 40.2, 38.4, 39.8) | 149–161 |

Static views are pixel-identical to base (mean absolute difference ≤0.13/255;
`camA` differs only by the random sky). Zero rejected frames. Live checks: 20
root moves, deactivation (396 → 3 resident commands) and reactivation (→ 396),
continuous motion and shader reload; images viewed. Focused existing tests
(`VulkanStablePacketAndDescriptorTests`, `VulkanDesktopPlanStabilityTests`,
`CommandChain*`): 175 passed / 111 failed, identical to a detached HEAD worktree
baseline (no new failures). No tests were added or modified.

### Rejected: raising the identity capacities

Raising vertex/descriptor identity capacity to 64 lets shadow packets form, and
the render-frame counter rose to 65 FPS, but those frames were **rejected**:
every refresh dirties more cold command chains than progressive publication
admits, so the frame replays the last complete scene. Fresh frames fell to about
25 FPS and shadow casters went missing (verified against an inline
`XRE_VULKAN_COMMAND_CHAINS=0` reference). With
`XRE_VULKAN_COMMAND_CHAIN_BENCHMARK_FORCE_RERECORD=1` half of all frames are still
rejected, even stationary. The command-chain path is not a viable shadow lane in
its current form. Reverted.

This also showed that `render_frame_number` deltas overstate the rendered rate
whenever frames are rejected; the outcome counters below were added for that.

### Rejected: scope-invariant shadow uniforms with a relevance-keyed share key

A scope-invariant shadow-uniform event for the light's shadow-map parameters
plus the caster target mask in `MaterialBindingSnapshotCacheKey` enabled the
existing per-scope snapshot share for shadow casters. Every lookup still missed
with an identical key: the share cache lives on `VkRenderProgram`, and every
mesh renderer generates its own program instance (the generated program identity
includes the mesh and renderer name). No FPS change. Reverted.

### Rejected: lease fast path for the mapped frame-arena recording lease

Sampled traces attributed ~1.7 ms per average motion frame to the global
lifetime lock in `TryAcquireMappedFrameArenaRecordingLease`. A per-recording
fast path changed `primary_mesh_operation` by 0% (44.1–45.0 µs per draw both
ways). Sampling bias; reverted.

### Rejected: typed auto-uniform writer for shadow casters

Allowing the typed (`AutoUniformMaterialWritePlan`) writer for shadow casters
whose material owns its bindings cut legacy block writes from 820 to 98 per
refresh frame, but removed most courtyard shadowing. The October 2 RenderDoc
investigation established that Sponza's roof overhang genuinely shadows most of
the courtyard at this sun angle, so base is correct. Material-frequency storage
is shared per material while shadow casters publish per-caster values (target
mask), so later casters overwrite earlier ones. Reverted.

### Outcome counters (retained diagnostic)

`get_render_profiler_stats.vulkan.frame_lifecycle.outcome_counts` now reports
cumulative per-outcome counts of published frame roots and their command-record
stage. It is allocation-free (one interlocked increment per published frame).
Documented in the MCP user guide.

## Why 100 Hz is not reached

With ~40–50% of motion frames refreshing ~400 casters, 100 Hz requires a refresh
frame of about 15 ms, i.e. under ~20 µs of CPU per caster across materialization,
preparation and recording. The generic per-renderer path costs ~100 µs per caster,
and its largest owners are structural: per-renderer generated programs defeat
every program-scoped cache, binding snapshots are captured per draw, and the
material-owned auto-uniform blocks need per-caster storage. Incremental fixes on
this path are each worth a few percent.

The main view already draws the same 393 meshes through the Advanced canonical
visibility-raster lane at about 1 µs per draw under CpuDirect (global geometry
atlas, one shared pipeline, per-draw push constants). A read-only survey found no
architectural blocker to giving directional cascades their own canonical lane
family: per-cascade views and viewport rects, a depth-only/moment fragment
variant, a pipeline variant for the D24 atlas format, CastShadow filtering and a
per-view CPU candidate mask in the stable bins, per-tile clears, and plan
integration after Advanced deformation with the generic path as fallback. The
code already rejects shadow outputs with "shadow outputs require their own
canonical visibility family". Main risks: moving atlas rendering out of
`GlobalPreRender`, GPU cost without per-view culling, masked-material parity, and
Vulkan/Advanced-only scope. This is a multi-part feature, not a night's patch.

## First-movement descriptor step

The S13i record saw 320 mesh descriptor sets created once at the first camera
movement in every binary. Fresh sessions, camera untouched since startup:

| Step | Mesh allocation variants | Mesh descriptor sets |
| --- | ---: | ---: |
| Settled after load (60 s) | 410-414 | 2,210-2,230 |
| After a net-zero light rotation (cascades refresh, camera untouched) | +32 | +320 |
| After a later camera move | +0 | +0 |

The step follows the first directional-cascade refresh after load, not camera
movement. A temporary probe that logged each variant registration (removed)
showed:

- At load the main view's 393 meshes use the canonical lane (no per-renderer
  sets). The 410 variants are the 365 opaque casters on the shared instanced
  cascade override, the 32 masked casters on per-material geometry-shader
  cascade variants (`directionalShadow=AtlasGeometryShader`, Uber fragment
  program), and 13 non-Advanced passes.
- At the refresh, only the 32 masked casters register new variants. Each new
  variant matches its load-time one in program, layout, material, view family,
  owner slot and binding identity. Only the resource fingerprint differs, and
  within it only the auto-uniform buffer component: identical across all 32
  renderers at load, renderer-specific after the refresh.
- The load-time variant stays live. In one run it was retired at the refresh and
  re-created on the next frame; in another it was kept. Either way each masked
  caster holds two variants of 2 sets x 5 descriptor frame slots, so 32 x 10 =
  320 sets. Nothing grows afterwards.

It belongs to the same generic per-renderer caster path as the recording cost.
Directional casters on a canonical lane (S15b) would remove it, as would an
allocation key that does not include the identity of per-frame auto-uniform
arena views.

## Shader reload bound retired pipelines (fixed)

Validation of the lowering correction found a latent pipeline-lifetime defect.
Shader reload under camera motion (`reload_renderer_shaders`, then continuous
motion) either made the renderer terminal ("Command buffer … attempted to record
retired resource Pipeline … generation N", disposition `RendererTerminal`) or
crashed the editor with an access violation in `vkCmdBindPipeline` from
`VkMeshRenderer.RecordDrawNoLock`. With the lowering correction alone, 4 of 5
reload runs failed; base passed 3 of 3, but the code path is identical, so the
correction only changed timing.

Cause, confirmed with a temporary probe: each `VkMeshRenderer` keeps a local
`_pipelines` lookup keyed partly by the native pipeline-layout handle and the
program's link generation. Program-scope invalidation removes retired pipelines
from the shared cache, but the renderer's local lookup is cleared only on a
detected same-interface relink. Re-created programs restart at link generation
1, and the driver reuses a destroyed layout's handle value for the replacement
layout, so the stale local entry matched the new key. The probe caught local
hits returning `PendingRetirement` and `Destroyed` pipelines for the shared
fullscreen-triangle bloom renderers (layout generation newer than the pipeline's).

Fix: `VulkanPipelineManager.SharedGraphicsPipelineRetirementGeneration` advances
whenever shared graphics pipelines leave the cache for retirement (program-scope
invalidation and teardown drain). Each renderer drops its local lookup once when
that value changes and refetches from the shared cache. Cost: one volatile read
per pipeline ensure. With the fix, 12 reload cycles in two sessions completed
with zero retired-pipeline hits and no failures; further sessions are recorded
in the closeout below.

## Closeout (October 3)

Retained changes:

- Packet-lowering identity-capacity precheck.
- Shared graphics-pipeline retirement generation.
- Frame-outcome counters.

Final measurements on the retained build, idle machine, two 20 s windows each:

| Build | Stationary fresh FPS | Stationary frame p50 / p99 | Motion fresh FPS | Motion frame p50 / p90 / p95 / p99 / worst |
| --- | ---: | ---: | ---: | ---: |
| Base (HEAD Vulkan binary) | 159.9 | 5.6 / 6.7 ms | 34.1 | 6.5 / 61.7 / 63.1 / 68.5 / 69.6 ms |
| Retained | 158.5-159.5 | 5.5 / 6.7 ms | 40.1, 40.1 | 6.8 / 50.6-51.0 / 51.7-52.6 / 57.3-57.4 / 60.6-65.0 ms |

Frame figures are render-thread whole-frame times over the engine's last 512
frames, which covers every frame of the motion window. Motion is bimodal:
ordinary frames take about 7 ms (about 145 Hz), and directional-cascade refresh
frames take about 51 ms, down from about 62 ms. Zero rejected frames in all
windows.

Validation:

- **Reload stress on the retained build:** 3 fresh sessions x 6 cycles of
  shader reload under continuous motion; all completed, editor alive, no
  retired-pipeline recording and no renderer-terminal disposition.
- **Focused tests:** 1,593 tests whose names contain `Vulkan`, `VkMesh`,
  `Pipeline`, `CommandChain`, `Telemetry` or `Mcp`. 457 fail in the current tree
  against 458 in a detached HEAD worktree, with no new failures; one unrelated
  Monado preview-window test passes only in the current tree. No tests were
  added or modified.
- **Images:** stationary, motion, light-motion and object-motion images match
  base as recorded above.

Not done: the S15 temporal checks (TAA/TSR history across motion, cuts and
resizes) were not rerun. The retained correction leaves the accepted packet set
and every recorded command unchanged.

The user target of above 100 Hz while the camera moves is **not met**. Stationary
rendering runs at about 159 Hz and ordinary motion frames at about 145 Hz.
The about 45% of motion frames that refresh the directional cascades take about
51 ms each, and that holds the average at 40 fresh frames per second.
Reaching 100 Hz without lowering cascade refresh frequency, resolution or
cascade count needs the refresh frame under about 15 ms. That requires
directional casters on a batched lane: S15b, the Advanced canonical lane for
cascades, as designed in the TODO.

## Evidence

Evidence root (ignored, disposable): `Build/_AgentValidation/20261003-015628-vk-100hz/`
(`reports/fps-*.json`, `reports/stages-*.json`, `reports/tests/*.trx`,
`mcp-captures/`, `traces/`). Required conclusions are recorded here.
