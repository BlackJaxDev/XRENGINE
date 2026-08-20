# Meshlet Import Production Closeout — 2026-08-20

## Scope And Environment

This pass records the completed import-cook, standalone persistence, Vulkan
submission, stale-cache, malformed-cache, mixed-routing, and
runtime-without-cooker validation that can be completed independently of the
still-inactive broad model binary cache. It also records the still-open Sponza
visual-debug and production-readiness boundaries; this is not a claim that the
entire tracker is complete.

- Source commit before the working changes: `cf6496b560e7229db47eb81a8d7c40fb1494c9a1`
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

The low-level native wrapper was also renamed from the ambiguous
`BuildMeshlets` to `BuildNativeMeshletClusters`.

## Validation Results

| Scenario | Result | Key evidence |
| --- | --- | --- |
| Final cold publish | Pass | `reports/static-cold-r5-final/summary.json`: parser 1, builder 3, generated LODs 2, payloads 3, meshlets 80, delayed task records 49, delayed dispatch X 20,629. |
| Final exact-root warm hydration | Pass | `reports/static-warm-r5-final/summary.json`: parser 0, builder 0, hydrations 3, delayed task records 24, delayed dispatch X 8,640. |
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
| Sponza per-meshlet colors | Open | The visible Sponza is uniformly magenta, not distinctly colored by meshlet. This does not satisfy the requested debug-render proof. |
| Culling isolation | Hi-Z/cone/frustum ruled out as the tiny-framing cause | Separate bounded runs disabled Hi-Z, cone culling, and frustum culling; each retained the same small center result. Temporary shader isolation edits were reverted. |
| Release build | Pass | `dotnet build XREngine.Editor/XREngine.Editor.csproj -c Release --no-restore`: 0 warnings, 0 errors. `git diff --check`: no whitespace errors (line-ending advisories only). |

No tests were added or run. Repository policy requires complete live validation
and explicit user clearance before test work for this integration.

## Current Sponza Visual Checkpoint

The latest Sponza image must not be described as collapsed geometry. At the
fixed `(-20,5,-20)` camera looking toward `(-20,5,0)`, the model occupies only a
few pixels. The user verified in the live editor that moving the camera closer
reveals Sponza and that the whole visible model is magenta. The production route
and geometry visibility are therefore present, while the per-meshlet color
selection is still wrong or not reaching the inspected output.

The final visual pass should use the normal Hi-Z configuration, move the camera
close enough for Sponza to occupy a meaningful portion of the viewport, capture
at least three views, and compare production meshlet output with the traditional
reference. It must also show stable, visibly different colors on neighboring
meshlets before the debug-render checkbox is checked.

A temporary 10 Hz render cap was tested because the user reported system-wide
mouse jitter while Sponza rendered. It did not solve the problem and was fully
reverted; `RenderFPS` remains `0.0` and editor-camera render-on-demand remains
disabled. The cold baseline's roughly 126 ms Vulkan command-buffer time is
consistent with heavy GPU pressure, but no root-cause claim is made yet. The
named validation session `meshlet-sponza-cachefix-publish` was stopped cleanly
after the final observation.

## RenderDoc Limitation

`rdc doctor` passed, including the registered Vulkan layer. Three launch paths
were attempted (rdc trigger/keep-alive, automatic frame capture, and direct
`renderdoccmd capture`). The editor's MCP endpoint became ready in two attempts,
but RenderDoc reported a blank attached API and produced no capture; the
automatic attempt stalled before Vulkan frame logging. One screenshot from the
injected process was black. No `.rdc` exists, so task/mesh pipeline bindings are
not claimed as capture-proven. `rdc close` confirmed no remaining session.

## Remaining Boundaries

- Broad model/prefab binary-cache hydration remains disabled until its
  mesh-core/prefab-graph provider lands. The final standalone warm proof is not
  represented as a broad model-cache hit.
- Live reimport, hot reload, streaming, unload/reload, dense capacity overflow,
  stereo/multiview, and long-running meshlet range retirement/compaction still
  need their dedicated runtime matrix.
- A real RenderDoc attachment/capture remains required for event-level
  `vkCmdDrawMeshTasksIndirectCountEXT`, task/mesh stage, binding, and attachment
  inspection.
- Sponza still needs a close, useful camera framing under normal Hi-Z, a
  three-view comparison with the traditional path, and visibly distinct
  per-meshlet debug colors. Uniform magenta is not accepted as color-debug
  completion.
- The system-wide mouse jitter under the heavy Sponza workload remains a
  separate GPU-pressure/performance investigation. It is not addressed by a
  frame-rate cap, and no cap remains in the product or validation settings.
- The diagnostic profile is intentionally intrusive and is not a shipping
  performance baseline. Resident draw-stream Phase 1 should remain gated on
  the tracker’s remaining lifetime/capture work.
