# Meshlet Import Production Closeout — 2026-08-20

## Scope And Environment

This pass records the completed import-cook, standalone persistence, Vulkan
submission, stale-cache, malformed-cache, mixed-routing, and
runtime-without-cooker validation that can be completed independently of the
still-inactive broad model binary cache. It also records completed closeout
Gates 1–7: Sponza debug color, conservative Hi-Z, three-view parity, mixed
routing/cache/lifetime closure, the zero-readback strategy-switch fix, parallel
graphics/non-graphics command workers, and uncapped ShippingFast performance/
mouse-pressure characterization, followed by targeted tests and documentation/
resident-stream handoff. Only the conditional broad model-cache provider is not
claimed complete.

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
- Gate 3/4 and strategy-switch evidence root:
  `Build/_AgentValidation/20260822-023044-meshlet-gates3-4-switch/`
- Gate 5/6 evidence root:
  `Build/_AgentValidation/20260822-122200-meshlet-gates5-6/`
- Gate 3/4 acceptance machine: NVIDIA GeForce RTX 3090, driver `610.88`, base
  commit `fd422fbb9b7af02059530386fd103f2686f4cc14` plus the recorded dirty
  worktree. The earlier 4070/581.57 evidence above remains separately labeled.
- Gate 5/6 machine: the same RTX 3090 desktop, Ryzen 9 7950X3D (16C/32T),
  48 GiB RAM, Windows 11 Pro build 26200, and 2560×1440 at 144 Hz. These results
  are not combined with the RTX 4070 laptop evidence.
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
- originally quarantined graphics and non-graphics command-chain workers after
  isolating their deterministic Vulkan device loss; Gate 5 later fixed primary-
  range execution and artifact/descriptor retirement ownership, passed the full
  matrix, and removed that quarantine; and
- enables mesh-task Hi-Z only for views supported by its conservative complete-
  footprint/depth-range test. Uncertain, clipped, near-plane, stale, and
  sequential-stereo/multiview cases remain visible; traditional GPU Hi-Z,
  meshlet frustum culling, and meshlet cone culling remain available.

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

The settings-UI `CpuDirect` → `GpuMeshletZeroReadback` black frame was a
material-table ABI defect, not missing mesh-task geometry. The generated GLSL
declares an array of `std430` structs containing `vec4` fields, so every element
has a 16-word/64-byte stride. The CPU row contained only its 13 logical words
(52 bytes). Material zero appeared valid, while later material IDs read shifted
or zero rows and blacked out the production material path. The binding-layout
authority now computes the maximum member alignment, aligns and hashes the row
stride, pads `GPUMaterialEntryWords` to 16 words, and asserts the generated row
count and native size at material-table construction.

Gate 3 exposed a separate dense-scene capacity defect. Sponza had 10,836
eligible resident meshlets but the task-record buffer was fixed at 8,192.
Expansion correctly reported overflow and the sealed batch correctly rolled
back, but that routed the whole pass traditionally. Capacity now uses the larger
of the command estimate or twice the current resident meshlet population, with
the existing safety bounds, and is regenerated when resident population
changes. The accepted Sponza buffer grew to 25,002 records without overflow.

Gate 4 found two lifecycle/fixture defects. `RenderableMesh` had no persistent
local material override, and the zero-highlight path cleared the render
command's override every frame. The component now owns a notified
`MaterialOverride`; debug highlighting restores that local value. Cooked
`XRMesh.Reload` now replaces only the owner-validated meshlet payload of the
resident mesh. GPUScene observes payload changes only for resident meshes,
coalesces them, swaps the atlas generation at the command-buffer frame boundary,
and leaves old buffers on the existing fence-retirement path.

## Validation Results

| Scenario | Result | Key evidence |
| --- | --- | --- |
| Final pulled-build static cold | Pass | `Build/_AgentValidation/20260820-180507-meshlet-diagnostics-acceptance/reports/final-static-cold-current-accepted/summary.json`: parser 1, builder 3, build 551.296 ms / 8,086,992 bytes, generated LODs 2, payloads 3, meshlets 80, task records 49, cumulative delayed groups 9,065, requested 1,792 = consumed 1,792, VUIDs/fallback/readback/maps 0. |
| Final-binary static warm after conservative Hi-Z safeguard | Pass | `Build/_AgentValidation/20260820-180507-meshlet-diagnostics-acceptance/reports/final-static-warm-hiz-conservative-current/summary.json`: parser 0, builder 0, hydrations 3, task records 24, cumulative delayed groups 3,456, requested 1,408 = consumed 1,408, VUIDs/fallback/readback/maps 0. |
| Final-binary mixed static + skinned/morph cold | Pass for planned routing | `Build/_AgentValidation/20260820-180507-meshlet-diagnostics-acceptance/reports/final-mixed-cold-hiz-conservative-current/summary.json`: parser 2, builder 4, generated LODs 2, payloads 4, task records 49, requested 1,632 = consumed 1,632, and VUIDs/fallback/readback/maps 0. Eligible opaque work used mesh tasks while skinned/morph and unsupported passes remained explicitly traditional GPU. |
| Mixed standalone warm closure | Open | `Build/_AgentValidation/20260820-180507-meshlet-diagnostics-acceptance/reports/final-mixed-warm-hiz-conservative-current/summary.json` was stable and rendered exactly once, but correctly failed the warm-cache gate: the three static LOD payloads hydrated while the animated source parsed/built once. Broad mixed/model-cache hydration is not claimed complete. |
| Gate 5 parallel command workers | Pass — accepted 2026-08-22 | `Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate5-postfix-matrix.csv`: all 16 graphics/non-graphics × forced/clean × 0/1/2/4-worker cells passed with zero worker failure/timeout, device loss, VUID, readback, or fault-log match. Clean worker/chain/primary reuse crossed frame 15,000; the quarantine was then removed. |
| RenderDoc EXT event proof | Pass for submission and resident inputs | `Build/_AgentValidation/20260820-180507-meshlet-diagnostics-acceptance/renderdoc/fresh-static-meshlet-frame40.rdc`: EID 514 is `vkCmdDrawMeshTasksIndirectCountEXT` with indirect `<24,1,1>`, live task/mesh/pixel stages, and the expected resident meshlet, atlas, transform, material-table, indirect/count, and attachment bindings. High-severity assertion count was zero. Gate 3 later closes final-frame parity. |
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
| Sponza production submission | Pass for route and atlas binding | The frame plan contained an accepted `OpaqueDeferred` EXT mesh-task indirect-count operation. After tier-aware binding, `mcp-captures/RenderPipeline_AlbedoOpacity_20260820_150004.png` contains the production result. The user confirmed that the apparent few-pixel result is the very small Sponza in that framing and that moving the camera reveals it. Gate 3 later closes three-view final parity. |
| Sponza per-meshlet colors | Pass | `Build/_AgentValidation/20260821-104532-meshlet-closeout/mcp-captures/` contains close-camera viewport and `AlbedoOpacity` pairs from two consecutive frames, a nearby view, and a warm DevParity restart. Neighboring production meshlets are clearly different colors. The accepted normal-profile frame had 393 eligible commands / 12,707 meshlets, one EXT indirect-count mesh-task op, exact 3,960 requested/emitted/consumed draws, and zero overflow/fallback/prohibited work/maps/readbacks/descriptor failures/VUIDs. |
| Conservative mesh-task Hi-Z | Pass — Gate 2 accepted 2026-08-22 | `Build/_AgentValidation/20260821-180447-meshlet-gate2/` contains controlled full, partial, near-plane/oblique, normal/reversed-Z, OpenXR/Monado stereo-fallback, and three-view Sponza Hi-Z on/off evidence. The final `ShippingFast` run had zero generic/diagnostic readback, mapped bytes, CPU/forbidden/descriptor fallback, skipped/dropped work, and validation messages/VUIDs. |
| CPU-direct → GPU-meshlet settings switch | Pass | The corrected 16-word material-table ABI produced the same nonblack `AlbedoOpacity` float hash `798A7D...BB217` for CPU baseline, the first UI switch, and a repeated GPU→CPU→GPU sequence. The pre-fix black capture was `33C9...0C7B`; fallback/readback diagnostics remained zero after the fix. |
| Gate 3 three-view Sponza parity | Pass — accepted 2026-08-22 | `reports/gate3-three-view-parity.json`: 22 eligible commands / 10,836 meshlets, dynamic capacity 25,002, and 54 requested/emitted/consumed. Each production albedo capture matched its traditional reference; only 2–6 final pixels exceeded one LSB. Debug-color hashes changed by pose. Overflow, fallback, maps, readback, VUIDs, and dropped work were zero. |
| Gate 3 final RenderDoc attribution | Pass | `renderdoc/gate3-production-accepted_frame1245.rdc`: EID 139 is `vkCmdDrawMeshTasksIndirectCountEXT` with `<9620,1,1>`. Exported G-buffer/depth attachments were inspected and the resource chain reaches lighting, composition, post, and swapchain EID 561. |
| Gate 4 mixed routing | Pass | The seven-command fixture combined opaque meshlets with missing payload, masked, opaque-forward, transparent/OIT, and a local material override. Baseline was 42 requested/consumed; isolated toggles produced the expected 18/30/36 totals and restored the exact `8F8325...0BC72` albedo hash. Stable state/missing-range reasons explained every traditional route. |
| Gate 4 optional cache-state matrix | Pass | `reports/gate4-cache-state-matrix.json`: `Disabled`/`Empty` cold/warm round-trip, changed provenance stays runtime-compatible while current-cooker stale, corrupt optional state repairs once without a source/native-builder call, and read-only repair remains resident with the explicit republish warning. |
| Gate 4 hot reload, churn, and LOD | Pass | `reports/gate4-runtime-lifetime-matrix.json`: eight remove/reload cycles alternated 464/19,200 live bytes and settled with zero retired bytes. Near/oblique LOD1 used 26 eligible meshlets, far LOD2 used nine, and return restored LOD1 plus exact `D0FF2D...B9B0` albedo. Every view was 14 requested/consumed with zero overflow/fallback/readback/maps/VUIDs/dropped work. |
| Gate 5 ownership/root cause | Pass | The numeric `FrameOp` path had omitted planned non-graphics worker secondaries; cached worker artifacts could outlive their output/pool owner; ImGui descriptor resources bypassed lifetime authority. Typed-primary execution and ordered cache/descriptor retirement fixed the root defects without retry, delay, broad device-idle waits, or CPU fallback. |
| Gate 6 ShippingFast comparison | Pass as characterization | `Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate6-shipping-fast-final100/summary.json`: meshlet vs traditional render p50/p95 `11.589/13.649` vs `6.829/7.686 ms`, GPU command-buffer `11.464/15.311` vs `3.322/4.606 ms`, and frame-slot wait `4.102/5.196` vs `0.019/0.026 ms`; generic readback/maps and CPU/forbidden fallback remained zero. |
| Gate 6 task/cull/residency supplement | Pass | `Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate6-task-cull-diagnostics/summary.json`: 12,500 task groups/records, 570 cone culls, 361 Hi-Z culls, 0 frustum culls, 6,554,112 resident/live bytes, zero retired bytes, and zero rebuilds/retires during capture. The 56,928 fence-delayed diagnostic bytes are explicitly separate from the ShippingFast baseline. |
| Gate 6 mouse-pressure classification | Pass | Synchronized `Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate6-gpu-saturation-final100.csv` windows measured meshlets at 97.3% mean / 98% p95 utilization versus 59.63% / 72% traditional. Submit/present remained sub-millisecond while frame-slot waits rose to 4.102/5.196 ms, classifying the user-observed system-wide jitter as mesh-task GPU execution/queue saturation. No cap was used or retained. |
| Gate 7 deterministic regression suite | Pass — accepted 2026-08-22 | `Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate7-tests/gate7-final-targeted.trx`: 86/86 Release tests cover cache states, bounded payload validation, small/invalid transforms, complete meshoptimizer/meshlet interop contracts, mixed routing, generation-safe payload replacement, Vulkan capability, conservative NV/EXT Hi-Z, debug-color stability, command-worker lifetime/recording order, and zero-readback hardening. The suite exposed and verified the dense-compaction terminal-padding fix. |
| Gate 7 final uncapped Vulkan smoke | Pass | `Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate7-final-smoke/summary.json`: 131 retained ShippingFast samples, real production meshlet/Vulkan mesh-task frame ops, 7,074 requested = 7,074 consumed Vulkan draws (10,638 = 10,638 all Vulkan paths), and zero readback, maps, CPU/forbidden fallback, VUIDs, or capture-window meshlet rebuilds/retires. Render p50/p95 was 11.112/12.356 ms and no refresh cap was set. |
| Culling isolation | Hi-Z/cone/frustum ruled out as the tiny-framing cause | Separate bounded runs disabled Hi-Z, cone culling, and frustum culling; each retained the same small center result. Temporary shader isolation edits were reverted. |
| Release build | Pass | Final `dotnet build XREngine.Editor/XREngine.Editor.csproj --configuration Release --nologo`: 0 warnings, 0 errors. Final `git diff --check`: no whitespace errors (line-ending advisories only). |

The user explicitly cleared Gate 7 test and closeout work after all live gates
passed. No test work preceded that clearance.

## Current Sponza Visual Checkpoint

The production debug-color, conservative-Hi-Z, and three-view parity gates are
complete. At camera
`(-20.08,0.055,0.0)` looking at `(-19.80,0.055,0.0)`, Sponza fills a useful
part of the viewport and both the G-buffer and final output show stable,
distinct colors across neighboring meshlets. The same palette is visible in a
consecutive frame and after a warm DevParity restart; a nearby offset view
changes the image normally while retaining per-meshlet coloring.

The center, edge, and oblique debug-off production captures now have matching
traditional zero-readback references. The tiny remaining final-frame deltas are
bounded raster differences: at most six pixels exceed one LSB. The accepted
RenderDoc frame connects the production mesh-task attachments to the presented
frame, so material and final-frame parity are no longer inferred from Gate 1 or
Gate 2 evidence.

A temporary 10 Hz render cap was tested because the user reported system-wide
mouse jitter while Sponza rendered. It did not solve the problem and was fully
reverted; `RenderFPS` remains `0.0` and editor-camera render-on-demand remains
disabled. Gate 6 reproduced the same uncapped workload while synchronized GPU
monitoring recorded 97.3% mean / 98% p95 utilization for meshlets, versus
59.63% / 72% for the traditional reference. Meshlet frame-slot wait reached
4.102/5.196 ms p50/p95 while submit/present stayed sub-millisecond. The symptom
is therefore classified as mesh-task GPU execution/queue saturation, not a
render-cap or CPU present/submit problem. The physical cursor symptom remains a
user observation; the automated evidence records the renderer pressure without
synthesizing cursor telemetry.

## Parallel Command-Worker Acceptance — 2026-08-22

Gate 5 retained the first failing graphics/non-graphics logs under
`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/logs/` and then changed
one ownership boundary at a time. The numeric `FrameOp` migration had preserved
worker-side non-graphics recordings but removed the primary path that executed
their planned secondary ranges. Separately, output/pool teardown could retire
the owner before destroying cached worker artifacts, and the ImGui font-atlas
descriptor pool/layout bypassed lifetime authority and left a live tracked
object eligible for a second destroy.

The accepted fix restores planned non-graphics range execution from the typed
primary operation, scopes descriptor-generation capture to the active recording
batch, abandons failed worker recordings, cancels workers and destroys caches
before owner teardown, routes ImGui descriptor destruction through lifetime
authority, and makes clean-primary reuse respect forced rerecord. It does not
add retries, sleeps, broad `DeviceWaitIdle`, or CPU fallback.

The post-fix report
`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate5-postfix-matrix.csv`
contains 16 cells:
graphics/non-graphics, forced rerecord/clean reuse, and serial/1/2/4 workers.
Every shutdown reported zero device loss, worker failure/timeout, validation
error, readback, and fault-log match. Forced cells observed actual concurrency
through four workers; clean worker cells retained worker/chain/primary reuse.
The three raw graphics-clean `Ready=false` values mean that the initial worker
record occurred before profiler activation, not that worker work was absent;
their reuse counters and frame 15,015–15,473 fault-free duration close that row.

## ShippingFast Performance And Mouse Pressure — 2026-08-22

Gate 6 used a dedicated 75-payload/52.18 MiB standalone Sponza cache. Its cold
publish made 75 builder calls in 3,766.2082 ms, allocated 1,050,300,312 builder
bytes, generated 50 LODs and 17,184 meshlets, then reached 54 requested/consumed
draws with 6,554,112 resident meshlet bytes and zero fallback/readback/VUID.
The final performance pair used that warm cache, the same fixed flying camera,
Vulkan `ShippingFast`, task Hi-Z, material-table rendering, ImGui, no
locomotion/mirror, and no cap.

`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate6-shipping-fast-final100/summary.json`
is the authoritative 60-
second pair. Meshlet versus traditional render p50/p95/p99 was
`11.589/13.649/16.103` versus `6.829/7.686/8.129 ms`; Vulkan GPU command-buffer
was `11.464/15.311/16.250` versus `3.322/4.606/6.849 ms`; Vulkan frame was
`10.057/12.052` versus `5.359/6.001 ms`; and frame-slot wait was
`4.102/5.196` versus `0.019/0.026 ms`. Retained-sample command-record allocation
totals were 27,388,536 versus 42,030,040 bytes, and GPU-submission managed totals
were 2,733,168 versus 4,652,280 bytes. Both variants had zero generic readback,
maps, CPU/forbidden fallback, resource retirement, plan replacement, prune,
force flush, and VUID. The existing `binding_snapshot_ineligible` legacy-
uniform route occurred 204/348 times and remains an optimization follow-up.

The synchronized NVIDIA monitor measured meshlets at 97.3% mean / 98% p95 GPU
utilization versus 59.63% / 72% traditional. Submit/present p95 remained
`0.240/0.070 ms` and `0.190/0.050 ms`; the pressure is GPU execution/queue wait,
not CPU submission/presentation. The result explains the user-observed system-
wide cursor jitter and establishes that a render-rate cap was not a fix. It also
shows that meshlets are materially slower than traditional on this machine.

The explicit Diagnostics supplement
`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate6-task-cull-diagnostics/summary.json`
recorded 12,500 task groups/
records, 570 cone culls, 361 task Hi-Z culls, zero frustum culls, 6,554,112 live
bytes, and zero capture-window rebuilds/retires. Its 56,928 fence-delayed bytes
are separately classified; generic readback/maps and fallbacks remained zero.
The development harness's optional GPU-pipeline history dump reported that no
pipeline timing history was enabled, but the MCP render-stats payload succeeded,
the Vulkan frame/command timings remained available, and the counter gate exited
zero; this is not a missing task/cull sample.
The measurement harness now uses production-frame plus retained Vulkan mesh-task
frame-op evidence for ShippingFast/DevParity and reserves fence-delayed GPU
counters for `Diagnostics`. A short ShippingFast rerun then exited zero with two
mesh-task frame ops per frame and no readback. Gate 7 later added and passed the
targeted suite under explicit user clearance.

## Gate 7 Acceptance — 2026-08-22

The focused Release suite passed 86/86 tests in
`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate7-tests/gate7-final-targeted.trx`.
New production-closeout coverage exercises `Disabled`/`Empty` payload states,
invalid portable triangle indices, small valid uniform transforms and invalid
mirrored/nonuniform/sheared transforms, frame-boundary resident-payload
replacement, stable debug palettes across static/skinned and NV/EXT shader
contracts, and conservative Hi-Z safety markers. Existing resolver, mixed-
routing, zero-readback, model-cook fingerprint, and Vulkan command-worker tests
complete the focused set.

That suite found an off-by-padding production defect. GPUScene dense buffer
compaction calculated its triangle byte endpoint only from referenced triangle
records, so a valid 732-byte portable payload could be republished as 731 bytes.
The compactor now rounds the copied terminal range to the format's four-byte
alignment and rejects an aligned endpoint beyond the source buffer. The
replacement test proves the old generation remains visible until
`SwapCommandBuffers`, then the complete replacement range and incremented
generation publish together.

The final uncapped warm Sponza smoke used desktop Vulkan, flying camera,
`ShippingFast`, `GpuMeshletZeroReadback`, `GpuHiZ`, material-table shading, and
the Gate 5-accepted parallel command-chain configuration, with no locomotion,
mirror capture, XR runtime, or refresh cap. The retained summary is
`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate7-final-smoke/summary.json`.
It records 131 samples, render p50/p95 11.112/12.356 ms, two production meshlet
frames/two Vulkan mesh-task frame operations, exact requested/consumed draws,
and zero prohibited counters. Its optional post-run GPU timing-history request
returned no history because ShippingFast timing capture was disabled; this did
not affect the clean editor shutdown, zero harness exit, runtime counters, or
Vulkan command-buffer timing evidence.

The Release editor build and whitespace audit passed. The closeout guide and
parent tracker are complete, all unconditional meshlet gates are closed, and
Vulkan resident draw-stream Phase 1 is unblocked. Broad model/prefab cache
hydration stays explicitly conditional on its separate provider.

## Conservative Hi-Z Acceptance — 2026-08-22

Gate 2 is complete. The earlier stable-camera black frame was a Vulkan subresource
layout bug, not a failure of the conservative sphere test: transitioning a full
sampled mip view across mixed layouts caused the already-produced pyramid tail
to be treated as `Undefined`. The production fix resolves and transitions the
aliased image per mip/layer so each completed source mip remains valid while the
next destination is placed in `General`.

Both task-shader variants project all eight corners of the meshlet sphere AABB,
honor Vulkan/OpenGL clip-depth and framebuffer-Y policy, use the conservative
depth endpoint for normal or reversed Z, select only valid bounded mips, and
leave clipped, near-plane, out-of-range, stale, and unsupported multiview cases
visible. Frustum and cone behavior is unchanged.

### Controlled matrix

The accepted evidence root is
`Build/_AgentValidation/20260821-180447-meshlet-gate2/`.

- The full occlusion pose produced 97 task records and 25 task-Hi-Z culls.
  Hi-Z on/off `AlbedoOpacity` and `DepthView` hashes match
  (`48D6...499BF`, `0CB0...9830`).
- The partially visible pose retained all visible geometry while producing eight
  culls; its hashes match (`D8C3...A08E`, `334B...D4E1`).
- The near-plane/oblique pose retained the clipped geometry while producing one
  cull; its hashes match (`5036...70FF`, `1D46...EE02`).
- The reversed-Z run retained the same albedo, produced the expected reversed
  depth hash (`61C954...0393`), and reported 25 culls. Inspected active mip
  bounds, including tail widths `30, 15, 7, 3, 1`, contained valid finite data.

### Three fixed Sponza views

The Sponza fixture used Vulkan, the production material-table meshlet path,
ImGui, the flying editor camera, no character locomotion, no mirror capture, and
no render-Hz cap. Hi-Z on/off `DepthView` float hashes matched exactly:

- center/doorway `(-20.08,0.055,0)` toward `(-19.80,0.055,0)`:
  `2E145B3FC5FA89144AA560B2B416F3ABBF6F82B664C27714DC83CE8B46068596`;
- near edge `(-20.08,0.055,-0.10)` toward `(-19.80,0.055,0.08)`:
  `4D47433993AD795100A176F9AB55478939C044CCD190FF07C63887A7BEF82019`;
- level oblique `(-20.10,0.060,0.12)` toward `(-19.78,0.060,-0.06)`:
  `BA716F48A724051EB4A322DC5E7D30EECDEE11860C767BEF502DAF9FBC629419`.

### OpenXR/Monado stereo fallback

OpenXR with Monado supplied the no-headset stereo validation; OpenVR was not
used. Monado reported a headset yaw near `-167.5617` degrees. Rotating the
playspace root `+167.5617` degrees canceled that yaw and aimed both eye cameras
at the fixture instead of away from it.

Vulkan readback was corrected to use the owning viewport renderer and the active
per-eye planner generation. The MCP eye-capture path now rejects a missing eye
scope instead of silently reading a process-wide or stale allocator, and it uses
the latest published per-eye frame view set. The accepted actual render
attachments are:

- left `RenderPipeline_AlbedoOpacity_20260822_005637.png`, hash
  `C5DCE157AE588266796263DCAB53521BBD0E7D2217BF6BCE2E58FCC26668AA7A`;
- right `RenderPipeline_AlbedoOpacity_20260822_005638.png`, hash
  `059E10D1B3D50AB1D9715B3E1A24F1652229DBD474E11C9C56DA110FE47A127D`.

The images show the full layered fixture with distinct stereo parallax. Left and
right bypass counters advanced independently while CPU fallback, forbidden
fallback, generic readback, descriptor failures, skipped work, and VUIDs stayed
zero. The separate OpenXR preview-copy images remained identical and near-black;
that final presentation-copy issue is recorded explicitly and is not substituted
for the valid per-eye render attachments. The user reported the earlier main-
window resize symptom fixed, so no further resize investigation was performed.

### Production cleanup and debug interpretation

All temporary shader counters, temporary fragment bindings, dispatch traces, and
planner console prints were removed. The isolated editor rebuild passed with
zero warnings and zero errors. The final uncapped `ShippingFast` Sponza run
reproduced the center-view depth hash above with requested/effective
`GpuMeshletZeroReadback`, zero generic/diagnostic/delayed readback, zero mapped
bytes, zero CPU/forbidden/descriptor fallback, zero skipped draws/dispatches,
zero dropped operations, and zero validation messages/VUIDs.

The camera-dependent appearance of meshlet color versus material/material-ID
color is expected mixed routing, not a nondeterministic mode switch. In this
Sponza fixture, 22 of 25 opaque commands are meshlet eligible and three are
rejected only by state class, so different camera poses expose different ratios
of meshlet-colored and traditional material-table geometry. Two stationary
captures and a move-away/return capture at the same pose all had the identical
`AlbedoOpacity` hash
`84010A5D88BC7060EABDA9C1D9D269BFDD3D268F6D9750B3F45557EEE426E7A2`.

This section closes conservative Hi-Z. The separate Gate 3 and Gate 4 evidence
below closes the traditional-reference, final-frame attribution, and remaining
mixed/cache/lifetime rows. At this checkpoint no tests had been added or run;
the user later supplied the required clearance recorded under Gate 7.

## Gates 3 And 4 Acceptance — 2026-08-22

The accepted root is
`Build/_AgentValidation/20260822-023044-meshlet-gates3-4-switch/`. Runs used
Vulkan, ImGui, the flying editor camera, no character locomotion, no mirror
capture, and production `GpuMeshletZeroReadback`. OpenXR/Monado evidence from
Gate 2 supplies the stereo/multiview safety row; OpenVR was not used.

For Gate 3, production and traditional zero-readback used the Gate 2 center,
edge, and oblique camera poses. Each production `AlbedoOpacity` capture matched
its corresponding traditional float hash. The settled final comparisons had
76,778/69,060/126,606 differing LDR pixels, but only 6/2/2 pixels respectively
exceeded one LSB; mean absolute error was `0.015063`, `0.013492`, and `0.025650`.
Normal/depth deltas were confined to small raster boundaries. Production debug
hashes (`BB3B...A13C`, `5432...6327`, `404A...D4E6`) changed with the pose, ruling
out a stale target. Requested, emitted, and consumed draws were exactly 54.

For Gate 4A, the mixed fixture's active baseline had seven commands, one
eligible command/49 meshlets, two missing-payload rows, one opaque-forward, one
masked, two transparent, and one local-override command. Disabling/restoring
each class changed requested/consumed totals together and restored the exact
baseline albedo hash. Frame-operation traces showed
`ExcludeMeshletResidentRows=1` only for opaque deferred; forward, masked, and
OIT traditional scatter remained explicit.

For Gate 4B, the disposable production-code probe used the real shared codec,
model meshlet section service, and container. It proved terminal
`Disabled`/`Empty` states, portable provenance admission, checksum-rejected
optional data repaired from core exactly once, and read-only in-memory repair
without republish. Parser and native-builder deltas were zero. This proof is
limited to the optional meshlet section and does not substitute for the broad
prefab/model cache provider.

For Gate 4C, resident payload changes are now frame-boundary publications.
Removing the selected cooked LOD payload reduced eligibility to two commands
and live bytes to 464 without changing the traditional visible result; disk
reload restored three commands/26 meshlets and 19,200 bytes with the same albedo
hash. Eight repetitions ended at rebuild/retire counts 22/21 and zero settled
retired bytes. The near, oblique, far, and return sequence visibly retained all
fixture geometry while switching LOD1 → LOD2 → LOD1; return reproduced
`D0FF2D3FE1F77656A263553A5374009058BF0982D7255B8440EFA7AAC1CBB9B0`.
The final isolated Release session then reloaded the resident base cooked mesh
without first clearing its accepted payload: before/after albedo hashes both
equaled `5D726C1AD0ADE1C70CD241D13B7009E6D1271159C233279407B94DD4E6D10757`,
14 requested equaled 14 consumed, and the settled profile retained 51 eligible
meshlets/38,144 live bytes with zero retired bytes, overflow, fallback,
readback/map, render-path cooker, validation error, or dropped operation.

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

That original capture proves the event, stages, and bound resident inputs. Gate
3 adds `renderdoc/gate3-production-accepted_frame1245.rdc`: its accepted mesh
event 139 writes the inspected G-buffers/depth, which feed the captured lighting,
composition, post-process, and final swapchain event 561. Combined with the
three matching MCP reference comparisons, final-frame parity is accepted.

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
- Gates 1–7 are complete and must not be reopened without contradictory
  evidence. Their Sponza, mixed routing, optional cache-section, hot-reload,
  range-lifetime, LOD, OpenXR/Monado, worker-matrix, and ShippingFast artifacts
  are linked above.
- Parallel graphics/non-graphics command-chain worker recording is enabled. The
  serial-owner route remains a tested control; it is no longer a quarantine or
  silent fallback.
- The reported system-wide mouse pressure is classified as mesh-task GPU
  execution/queue saturation on this RTX 3090 desktop. The meshlet path remains
  materially slower than traditional and should be optimized separately; no
  frame-rate cap remains in product or validation settings.
- The diagnostic profile is intentionally intrusive and is not a shipping
  performance baseline. Its fence-delayed task/cull bytes are recorded
  separately from the zero-readback ShippingFast pair.
- Resident draw-stream Phase 1 is unblocked by the completed Gate 7 test and
  documentation handoff; its implementation remains owned by the separate
  resident-stream tracker.
