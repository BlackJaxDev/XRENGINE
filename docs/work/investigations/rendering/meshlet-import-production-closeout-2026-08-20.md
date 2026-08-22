# Meshlet Import Production Closeout — 2026-08-20

## Scope And Environment

This pass records the completed import-cook, standalone persistence, Vulkan
submission, stale-cache, malformed-cache, mixed-routing, and
runtime-without-cooker validation that can be completed independently of the
still-inactive broad model binary cache. It also records the still-open Sponza
visual-debug and production-readiness boundaries; this is not a claim that the
entire tracker is complete.

- Source commit before the original closeout changes: `cf6496b560e7229db47eb81a8d7c40fb1494c9a1`
- Latest pulled base used for final integration acceptance:
  `0af39a775db0501d9d0c70713e7b7e72ae0b1eee`
- Branch: `vulkan-refactor`
- GPU: NVIDIA GeForce RTX 4070 Laptop GPU (`00000000:01:00.0`)
- Driver: `581.57`; VBIOS `95.06.31.00.a2`
- SDK: .NET `10.0.400`
- RenderDoc CLI: upstream `1.41`; installed `renderdoccmd` `1.44`
- Settings: `Build/_AgentValidation/20260820-meshlet-production-closeout/scratch/static-settings.jsonc`
- Deterministic source: `XREngine.UnitTests/TestData/Gltf/meshlet-static-single-instance.gltf`
  plus `large-production-scene.bin`, Vulkan, deferred opaque, two generated
  LODs, camera `(0,6,8)` looking at the origin.
- Evidence root: `Build/_AgentValidation/20260820-meshlet-production-closeout/`
- Final pulled-build acceptance root:
  `Build/_AgentValidation/20260820-180507-meshlet-diagnostics-acceptance/`
- Sponza evidence root:
  `Build/_AgentValidation/20260820-120000-meshlet-sponza-closeout/`
- Sponza settings:
  `scratch/sponza-meshlet-debug.jsonc`; Vulkan, deferred rendering,
  `GpuMeshletZeroReadback`, meshlet debug enabled, Sponza translated to
  `(-20,0,0)` at scale `0.01`, and no render-Hz cap.

The primary measurement command was:

```powershell
pwsh Tools/Measure-GameLoopRenderPipeline.ps1 `
  -Strategies GpuMeshletZeroReadback -Configuration Release `
  -RenderBackend Vulkan `
  -UnitTestingWorldSettingsPath Build/_AgentValidation/20260820-meshlet-production-closeout/scratch/static-settings.jsonc `
  -CacheMode <Cold|Warm> `
  -MeshletStandaloneCookedCacheRoot Build/_AgentValidation/20260820-meshlet-production-closeout/scratch/standalone-cache-static-r5 `
  -ZeroReadbackMaterialDrawPath MaterialTable `
  -CameraPositionX 0 -CameraPositionY 6 -CameraPositionZ 8 `
  -CameraLookAtX 0 -CameraLookAtY 0 -CameraLookAtZ 0 `
  -RenderScale 0.67 -WindowWidth 1280 -WindowHeight 720 `
  -VulkanGpuDrivenProfile Diagnostics `
  -VulkanCommandChains Enabled `
  -VulkanParallelCommandChainRecording Enabled `
  -VulkanParallelSecondaryRecording Enabled `
  -OcclusionCullingMode Disabled -ProfileMode Diagnostics
```

Cold used three seconds warmup, a two-second stability window, and four
seconds capture. Warm used the same values. Scenario-specific rejection runs
used `-NoStabilityGate`; their nonzero harness exit is expected because the
production harness deliberately requires positive meshlet work, while the
scenario requires the invalid asset to be rejected before submission.

## Root Causes And Fixes

The earlier delayed GPU evidence was zero because Vulkan submitted the
diagnostic copy before recording/submitting the deferred mesh-task producer.
The fence therefore proved completion of a stale copy. Diagnostics profile
also accidentally enabled synchronous pass mappings on a zero-readback path.

The fix now:

- snapshots meshlet stats and dispatch arguments immediately after the exact
  deferred producer operation in the same ordered frame stream;
- submits host-visible diagnostic copies only after the accepted graphics
  submission, preserving same-queue producer-before-copy order;
- keeps asynchronous meshlet evidence separate from synchronous instrumented
  diagnostics, so generic readback/mapping gates remain zero;
- removes expansion-shader races by using an atomic task-count load,
  reset-owned Y/Z dispatch values, and an atomic dispatch-count store;
- gives the standalone proof cache a semantic request identity covering the
  source file, local glTF buffer/image dependencies, canonical import settings,
  LOD/meshlet settings, and topology-changing post-import flags; and
- keeps local cooker provenance out of runtime admission so compatible baked
  payloads hydrate with `meshoptimizer.dll` absent;
- upgrades payload-bearing cooked meshes to lossless source streams so payload
  ownership validation compares the exact source data rather than lossy SNORM16
  positions;
- binds the production meshlet vertex streams once per populated GPUScene atlas
  tier and filters task/mesh work by the same `ActiveAtlasTier`, fixing Sponza's
  previous use of the legacy dynamic-atlas aliases despite residing in the
  static atlas; and
- suppresses the legacy direct meshlet debug overlay after production meshlet
  submission is available, so it can no longer hide production-path failures.

Final integration hardening additionally:

- records diagnostic snapshot copies only with their ordered producer/resource
  dependencies and accepted-recording receipt, eliminating stale or uninitialized
  task/dispatch evidence;
- isolates the renderer-owned bindless material array from local graphics
  descriptor preparation while preserving dynamic-buffer descriptors;
- makes multi-tier mesh-task enqueue atomic so any post-seal failure rolls back
  the entire tier batch before rebuilding the full traditional GPU stream;
- quarantines graphics and non-graphics command-chain worker recording after it
  was isolated as the necessary trigger for deterministic Vulkan device loss;
  serial-owned command chains and secondary reuse remain enabled; and
- disables the current mesh-task Hi-Z test at the program uniform boundary. Its
  center-only sphere sample is not conservative under Vulkan zero-to-one depth;
  traditional GPU Hi-Z, meshlet frustum culling, and meshlet cone culling remain
  available.

The low-level native wrapper was also renamed from the ambiguous
`BuildMeshlets` to `BuildNativeMeshletClusters`.

Gate 1 closeout on 2026-08-21 found one additional production eligibility bug.
`GPUScene.HasUniformPositiveScale` reused its `1e-4` relative uniformity
tolerance as an absolute squared-axis cutoff. Sponza's valid small uniform
import scale was therefore marked `Dynamic`, excluding all 393 opaque rows from
meshlet expansion. The fix separates a `1e-12` degeneracy threshold from the
relative tolerance, rejects non-finite/sheared bases explicitly, and continues
to reject mirrored or non-uniform transforms. An on-demand, CPU-mirror-only MCP
eligibility snapshot made the rejection exact without adding per-frame work,
maps, or readbacks.

## Validation Results

| Scenario | Result | Key evidence |
| --- | --- | --- |
| Final pulled-build static cold | Pass | `Build/_AgentValidation/20260820-180507-meshlet-diagnostics-acceptance/reports/final-static-cold-current-accepted/summary.json`: parser 1, builder 3, build 551.296 ms / 8,086,992 bytes, generated LODs 2, payloads 3, meshlets 80, task records 49, cumulative delayed groups 9,065, requested 1,792 = consumed 1,792, VUIDs/fallback/readback/maps 0. |
| Final-binary static warm after conservative Hi-Z safeguard | Pass | `Build/_AgentValidation/20260820-180507-meshlet-diagnostics-acceptance/reports/final-static-warm-hiz-conservative-current/summary.json`: parser 0, builder 0, hydrations 3, task records 24, cumulative delayed groups 3,456, requested 1,408 = consumed 1,408, VUIDs/fallback/readback/maps 0. |
| Final-binary mixed static + skinned/morph cold | Pass for planned routing | `Build/_AgentValidation/20260820-180507-meshlet-diagnostics-acceptance/reports/final-mixed-cold-hiz-conservative-current/summary.json`: parser 2, builder 4, generated LODs 2, payloads 4, task records 49, requested 1,632 = consumed 1,632, and VUIDs/fallback/readback/maps 0. Eligible opaque work used mesh tasks while skinned/morph and unsupported passes remained explicitly traditional GPU. |
| Mixed standalone warm closure | Open | `Build/_AgentValidation/20260820-180507-meshlet-diagnostics-acceptance/reports/final-mixed-warm-hiz-conservative-current/summary.json` was stable and rendered exactly once, but correctly failed the warm-cache gate: the three static LOD payloads hydrated while the animated source parsed/built once. Broad mixed/model-cache hydration is not claimed complete. |
| Command-chain worker quarantine | Pass as correctness containment | Fresh serial-owner runs retained command-chain secondary reuse and reached frames 180–420 with no device loss, VUID, or render exception, crossing the prior deterministic loss frames 32/43/46. Parallel worker recording remains quarantined pending an isolated lifetime/root-cause matrix. |
| RenderDoc EXT event proof | Pass for submission and resident inputs; visual parity open | `Build/_AgentValidation/20260820-180507-meshlet-diagnostics-acceptance/renderdoc/fresh-static-meshlet-frame40.rdc`: EID 514 is `vkCmdDrawMeshTasksIndirectCountEXT` with indirect `<24,1,1>`, live task/mesh/pixel stages, and the expected resident meshlet, atlas, transform, material-table, indirect/count, and attachment bindings. High-severity assertion count was zero. |
| Original closeout cold publish | Pass | `reports/static-cold-r5-final/summary.json`: parser 1, builder 3, generated LODs 2, payloads 3, meshlets 80, delayed task records 49, cumulative delayed dispatch groups 20,629. |
| Original closeout exact-root warm hydration | Pass | `reports/static-warm-r5-final/summary.json`: parser 0, builder 0, hydrations 3, delayed task records 24, cumulative delayed dispatch groups 8,640. |
| Post-review submission attribution | Pass | `reports/static-warm-r6-attribution/summary.json`: after rejected-attempt cleanup and source-frame tagging, parser 0, builder 0, hydrations 3, task records 24, delayed dispatch X 10,080, generic readback/maps/fallbacks 0, requested draws 1,760 = consumed draws 1,760. The optional MCP GPU-timing dump had no timing history, but the engine/harness counters and Vulkan command-buffer timings were captured normally. |
| Runtime without cooker | Pass | `reports/static-warm-no-cooker-r5-final/summary.json`: `meshoptimizer.dll` was moved out of only the validated editor output and restored in `finally`; parser 0, builder 0, hydrations 3, task records 24, dispatch X 6,528. |
| Zero-readback contract | Pass | All three final runs report generic GPU readback bytes 0, mapped buffers 0, forbidden fallbacks 0, render-path source hash/disk/cooker calls 0, and equal requested/consumed draws. Fence-delayed evidence bytes are separately classified diagnostics. |
| Equivalent cold imports | Pass | Independent cold generations produced identical meshlet payload SHA-256 values for all LODs: `B049…4FFB`, `96D7…6070`, `7B6C…7986`. The comparison deserialized each `XRMesh` and hashed only `SerializeMeshletPayloadToBytes`, excluding random asset/generation IDs. |
| Changed LOD setting | Pass | The old three-payload warm generation was rejected with hydration 0; a fresh cold one-LOD request produced exactly builder 2, generated LODs 1, payloads 2 (`reports/static-cold-changed-settings-r3/summary.json`). |
| Changed external glTF buffer | Pass | One bit in a disposable copy of `large-production-scene.bin` changed SHA-256 from `727170…FF9E` to `05295A…9CAC`; warm admission rejected the generation with parser 0, builder 0, hydration 0 (`reports/static-warm-changed-source/summary.json`). |
| Malformed cooked asset | Pass | A disposable cached asset had its Zstd frame magic corrupted from `28B52FFD` to `29B52FFD`; bounded load rejected the whole generation before GPUScene with parser 0, builder 0, hydration 0 (`reports/static-warm-corrupt-r5-final/summary.json`). |
| Mixed static + skinned/morph | Pass for planned routing | `reports/mixed-cold/summary.json`: two sources, four payloads, meshlet task records 49, delayed dispatch X 29,841, requested draws 2,250 = consumed draws 2,250, readback/maps/fallbacks 0. Unsupported passes remained explicitly `TraditionalGpu`. |
| Three camera positions | Pass for geometry continuity | Normal isolated Vulkan session `meshlet-closeout-view` captured front/right/left views under `mcp-captures/`; the fixture silhouette remained present and changed with the view. Its intentionally missing fixture material is magenta, so these images prove geometry/culling continuity, not material parity. |
| Sponza cold import | Pass | `reports/sponza-cold-baseline/summary.json`: parser 1, builder 393, payloads 393, meshlets 12,707, GPU task records 11,010, delayed dispatch X 2,113,920 across 194 accepted meshlet dispatches, and render-path source hash/disk/cooker calls 0. |
| Sponza warm payload use | Pass for persistence/hydration | The warm live profile observed all 393 payloads hydrate with zero parser/builder calls before GPUScene registration. This was inspected through MCP rather than emitted as a standalone summary report. |
| Sponza production submission | Pass for route and atlas binding; visual parity open | The frame plan contained an accepted `OpaqueDeferred` EXT mesh-task indirect-count operation. After tier-aware binding, `mcp-captures/RenderPipeline_AlbedoOpacity_20260820_150004.png` contains the production result. The user confirmed that the apparent few-pixel result is the very small Sponza in that framing and that moving the camera reveals it. |
| Sponza per-meshlet colors | Pass | `Build/_AgentValidation/20260821-104532-meshlet-closeout/mcp-captures/` contains close-camera viewport and `AlbedoOpacity` pairs from two consecutive frames, a nearby view, and a warm DevParity restart. Neighboring production meshlets are clearly different colors. The accepted normal-profile frame had 393 eligible commands / 12,707 meshlets, one EXT indirect-count mesh-task op, exact 3,960 requested/emitted/consumed draws, and zero overflow/fallback/prohibited work/maps/readbacks/descriptor failures/VUIDs. |
| Culling isolation | Hi-Z/cone/frustum ruled out as the tiny-framing cause | Separate bounded runs disabled Hi-Z, cone culling, and frustum culling; each retained the same small center result. Temporary shader isolation edits were reverted. |
| Release build | Pass | `dotnet build XREngine.Editor/XREngine.Editor.csproj -c Release --no-restore`: 0 warnings, 0 errors. `git diff --check`: no whitespace errors (line-ending advisories only). |

No tests were added or run. Repository policy requires complete live validation
and explicit user clearance before test work for this integration.

## Current Sponza Visual Checkpoint

The production debug-color gate is complete. At camera
`(-20.08,0.055,0.0)` looking at `(-19.80,0.055,0.0)`, Sponza fills a useful
part of the viewport and both the G-buffer and final output show stable,
distinct colors across neighboring meshlets. The same palette is visible in a
consecutive frame and after a warm DevParity restart; a nearby offset view
changes the image normally while retaining per-meshlet coloring.

The next visual pass is conservative mesh-task Hi-Z footprint/depth-range math,
followed by three fixed debug-off production/reference comparisons. Material and
final-frame parity are not inferred from the debug-color image.

A temporary 10 Hz render cap was tested because the user reported system-wide
mouse jitter while Sponza rendered. It did not solve the problem and was fully
reverted; `RenderFPS` remains `0.0` and editor-camera render-on-demand remains
disabled. The cold baseline's roughly 126 ms Vulkan command-buffer time is
consistent with heavy GPU pressure, but no root-cause claim is made yet. The
named validation session `meshlet-sponza-cachefix-publish` was stopped cleanly
after the final observation.

## Conservative Hi-Z Checkpoint — 2026-08-21

Gate 2 is in progress and remains unchecked. Both mesh-task shader variants now
contain conservative full-footprint projection and clip/depth policy handling,
and the hardcoded task-Hi-Z suppression has been removed in the working slice.
The implementation has not yet earned production acceptance.

The controlled vertical two-grid fixture at camera `(-1.25,1.25,0)` with
identity rotation rendered while the camera was moving, then became black after
temporal motion settled. Ordered GPU probes showed a normal-Z nearest depth of
approximately `0.9799`, a sampled conservative Hi-Z value of `0`, selected mip
`5`, and a populated-center mask of `0x3F`: mips `0..5` contained depth while
mips `6..9` were zero. Temporal phase one kept both draws visible during motion;
after settling it submitted no phase-one draws and the zero Hi-Z sample falsely
culled both.

The bounded trace session `meshlet-closeout-hiz-trace` recorded every Hi-Z
reduction with the expected state: source/destination mips `0->1` through
`8->9`, destination sizes `480x270`, `240x135`, `120x67`, `60x33`, `30x16`,
`15x8`, `7x4`, `3x2`, and `1x1`, with corresponding dispatch groups. This
ruled out omitted dispatches, mutable snapshot reuse, and stale per-dispatch
uniforms. The session was stopped cleanly; its log is under
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260821-165515-meshlet-closeout-hiz-trace/logs/`.

The remaining root cause is a Vulkan layout-range defect for a texture sampled
from one mip while the next mip is bound as storage. The full sampled view spans
mixed per-mip layouts; the old range lookup therefore failed and the transition
fell back to `Undefined` for the entire pyramid, invalidating already-generated
source contents. The current unvalidated fix transitions an aliased compute
image per mip/layer before changing only the storage destination to `General`.
It compiled in the targeted Vulkan project with zero warnings/errors, but the
post-fix live run was intentionally deferred at the user's wrap-up request.

Temporary GPU counter probes and the temporary `VulkanHiZDispatch` trace were
removed. The post-cleanup targeted Vulkan build passed with zero warnings and
zero errors. Direct GLSL syntax validation also passed for the two occlusion
compute shaders and both task variants; the engine-managed loose-uniform Vulkan
rewrite still requires the next live editor run for end-to-end shader proof.
No MCP editor or RenderDoc session is active. Resume with the exact five-step
Gate 2 checklist in the closeout work guide; do not check its boxes or begin
Sponza parity until stable-camera visibility and nonzero controlled Hi-Z culls
are both proven.

## RenderDoc Evidence

`rdc doctor` passed, including the registered Vulkan layer. A later bounded
capture succeeded and produced multiple real `.rdc` files under the final
acceptance root. The open-work-close inspection of
`renderdoc/fresh-static-meshlet-frame40.rdc` found EID 514 as
`vkCmdDrawMeshTasksIndirectCountEXT` with indirect arguments `<24,1,1>`, live
task and mesh shader stages, and the expected resident meshlet descriptors,
vertex/triangle references, atlas buffers, mesh data, transforms, material
table, indirect/count buffers, and attachment state. Relevant outputs were
exported to PNG and visually inspected; `rdc assert-clean` reported zero
high-severity findings, and `rdc close` ended the session.

This proves the event, stages, and bound resident inputs. The exported final
image is not sufficient to claim final-frame parity with the traditional path,
so the visual-comparison gate remains open.

Two 2026-08-21 Sponza capture attempts under the current evidence root produced
no `.rdc` and were cleaned up. The first queued absolute frame 600 after the
editor had already passed it. The launcher now has an explicit `--trigger`
mode; that retry still could not complete one RenderDoc-instrumented Sponza
frame inside 120 seconds. Both exact injected PIDs were terminated in `finally`,
ports 5471/5472 closed, and no partial capture was retained. This is recorded as
GPU-pressure evidence, not as a RenderDoc visual-parity pass.

## Remaining Boundaries

- Broad model/prefab binary-cache hydration remains disabled until its
  mesh-core/prefab-graph provider lands. The final standalone warm proof is not
  represented as a broad model-cache hit.
- Live reimport, hot reload, streaming, unload/reload, dense capacity overflow,
  stereo/multiview, and long-running meshlet range retirement/compaction still
  need their dedicated runtime matrix.
- RenderDoc/MCP still need a useful-camera final-frame comparison against the
  traditional path; event/stage/binding proof itself is complete.
- Sponza still needs conservative mesh-task Hi-Z and a debug-off three-view
  comparison with the traditional path. Close-camera per-meshlet debug colors
  are complete and must not be reopened without contradictory evidence.
- Parallel command-chain worker recording remains quarantined. Re-enable it only
  after isolated graphics/non-graphics recording, ownership, lifetime, and
  submission validation identifies and fixes the device-loss interaction.
- The system-wide mouse jitter under the heavy Sponza workload remains a
  separate GPU-pressure/performance investigation. It is not addressed by a
  frame-rate cap, and no cap remains in the product or validation settings.
- The diagnostic profile is intentionally intrusive and is not a shipping
  performance baseline. Resident draw-stream Phase 1 should remain gated on
  the tracker’s remaining lifetime/capture work.
