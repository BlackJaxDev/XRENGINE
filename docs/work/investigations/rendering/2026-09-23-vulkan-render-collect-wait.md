# Vulkan render<-collect wait on the Unit Testing World

Status: CPU cause reproduced and isolated; permanent remediation and S13 validation pending.

## Symptom and controlled comparison

The September 23 Debug screenshot showed `render<-collect` near 92 ms while running the Unit Testing World under the VS Code debugger. The HUD counter measures the render thread's elapsed wait for a freshly published collect generation in `EngineTimer.WaitToRender`; it is not a Vulkan queue or GPU timer. The wait includes collect-side work that must finish before the generation gate publishes.

I ran a named isolated editor session, `render-collect-wait-0923`, against the existing AdvancedRenderPipeline, CpuDirect submission, TSR, 1920x1080 output / 1286x723 internal viewport and stationary Unit Testing World. The resident scene contained 393 canonical draws (396 GPU-scene commands including other content). Vulkan and OpenGL used the same Release binary and settings, changing only `XRE_UNIT_TEST_RENDER_API`. VSync was off, Vulkan used Immediate present with validation disabled, the VS Code debugger was detached, and the CPU profiler was active. The ignored evidence is under `Build/_AgentValidation/20260923-121421-render-collect-wait/`; the isolated session logs are under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260923-121431-render-collect-wait-0923/logs/`.

| Run | HUD `render<-collect` samples | Completed collect-thread CPU scope |
| --- | --- | --- |
| Debug Vulkan, no debugger | Mostly 61–73 ms in 20 samples | `DispatchCollectVisible` ~1.8–2.2 ms; `DispatchSwapBuffers` ~65–72 ms, of which render-pass command swap ~55–62 ms and Advanced publication ~8–10 ms. |
| Release Vulkan | 49–59 ms in 20 samples | Ten completed scopes had collection ~0.8–1.1 ms, command swap ~48–62 ms and Advanced publication ~3.0–3.5 ms. |
| Release OpenGL, same scene/build | 2.6–3.3 ms in 20 samples, with one zero sample | Completed scopes had render-pass command swap about 0.5 ms and Advanced publication about 3 ms. |

The OpenGL comparison confirms a backend-associated difference. Its 393 resident draws and Advanced pipeline matched Vulkan, although actual visible draw calls vary with camera and culling. Collected command identities and accepted visible work were not proven equivalent; resident population is not a substitute for that check. The screenshot's attached-debugger run was not repeated with an attached debugger; its exact 92 ms split is therefore not claimed. `collect<-render` is a separate wait for the previous render to finish and overlaps earlier work; it must not be added to `render<-collect`. Lifecycle samples and completed CPU scopes came from different observations, not one additive breakdown of an identical frame.

## Cause isolated by temporary instrumentation

`EngineTimer` waits for publication after the collect thread has dispatched `SwapBuffers`. `RenderCommandCollection.SwapBuffers.RenderPasses` walks its dirty command queue and calls `RenderCommandMesh3D.SwapBuffers`. That invokes `RenderCommand.OnSwapBuffers`, which `VisualScene3D` routes to `GPUScene.TryUpdateMeshCommand` for the CpuDirect GPU-scene mirror. The latter holds the GPU-scene lock and revisits mesh, material, logical registration, transform, bounds and metadata state before it can conclude that a command is unchanged.

Temporary Release profiler scopes around the callback and `TryUpdateMeshCommand` found 393 callbacks in a representative Vulkan render-pass swap. Sampled callback totals and the enclosing main swap were about 48–51 ms. In OpenGL, completed sampled histories contained only 4–6 callbacks across multiple frames, with the largest per-frame swap around 0.6–0.7 ms. Thus the large collect-side elapsed interval is inside repeated GPU-scene update callbacks. These scopes include lock acquisition, held-body work and possible descheduling; their split remains unmeasured. They do not establish that a particular dictionary, registration routine or contended lock owns the cost. The later ~3 ms Advanced publication is separately measured.

`AdvancedGpuScenePublisher.PublishSourceDrawIdentities` calls `RenderCommandMesh3D.PublishCanonicalDrawIdentities` at scene publication. That method uses `SetField` for the immutable identity snapshots. The generic `RenderCommand.OnPropertyChanged` treats the identity notification as render-state dirtiness, so the next visibility collection can enqueue an otherwise unchanged mesh command. This creates a feedback path from publication identity to command swapping and GPU-scene mirror validation.

To test causality, a temporary `RenderCommandMesh3D.IsRenderStateDirtyProperty` override excluded only the caller-member notification `nameof(PublishCanonicalDrawIdentities)` from the dirty flag, while delegating every other name to the base implementation. Both identity snapshots were still published through `SetField`. On a rebuilt Release Vulkan run, 20 warmed `render<-collect` samples fell to 3.0–5.7 ms (mean 3.51 ms). Completed CPU scopes showed empty render-pass dirty queues (about 0.001 ms) and the remaining ~3.2–3.3 ms Advanced publication. The 393 update callbacks disappeared. This is strong causal evidence that publication identity is the trigger for the measured wait. The temporary override and profiling scopes were removed after the experiment; no runtime fix is retained. It was not a validated S13b implementation.

## Evidence inventory and exact sampled results

The following numbers were read back from the saved reports during the documentation audit. Each report has twenty samples. They are diagnostic sample statistics, not all-frame distributions, observer-overhead validation or the required three matched 60-second pairs.

| Report under the task run's `reports/` directory | Render wait minimum / maximum / mean (ms) | Separate collect wait minimum / maximum / mean (ms) | Interpretation |
| --- | --- | --- | --- |
| `debug-lifecycle-samples.json` | 61.4852 / 73.1814 / 65.601110 | 38.7154 / 63.3858 / 42.843900 | Debug Vulkan without attached debugger. |
| `release-lifecycle-samples.json` | 49.1405 / 58.9100 / 54.414155 | 13.7020 / 20.9600 / 15.628020 | Release Vulkan baseline. |
| `release-opengl-lifecycle-samples.json` | 0 / 3.2981 / 2.906120 | 5.4211 / 12.2939 / 6.472590 | Includes one zero render-wait sample; its nonzero values were approximately 2.6–3.3 ms. |
| `vulkan-identity-suppressed-lifecycle.csv` | 3.0170 / 5.6852 / 3.511255 | 12.9786 / 17.9736 / 15.175235 | Temporary causal candidate, separate instrumented/rebuilt binary. |

`debug-cpu-dump-hits.json` and `release-cpu-dump-hits.json` identify the original completed CPU dumps. Later representative logs are under the isolated session log root above, then `XREngine.Editor_release/windows_x64/`:

| Variant and relative log | Completed evidence |
| --- | --- |
| `xrengine_2026-09-23_12-32-43_pid36616/profiler-cpu-frame-2026-09-23-12-33-14-387-2eda7f3e.log` | Instrumented Vulkan: collect swap 55.627 ms; main `RenderPasses` scope 50.912 ms, scope ID 2321317, 393 callback children. The whole dump contains three pass scopes and 395 GPU-scene update scopes, totaling 51.166 ms; those additional pass scopes must not be counted as 393 callbacks in every pass. |
| `xrengine_2026-09-23_12-32-43_pid36616/profiler-cpu-frame-2026-09-23-12-33-14-522-ee318956.log` and `...12-33-14-656-b40a02b0.log` | Further instrumented Vulkan collect swaps 52.208 / 52.336 ms and main pass scopes 47.864 / 47.707 ms. |
| `xrengine_2026-09-23_12-34-04_pid7276/profiler-cpu-frame-2026-09-23-12-34-38-052-a4432e2d.log` and `...190-5588c8f7.log`, `...317-eb5ce651.log`, `...446-07f782ef.log` | Instrumented OpenGL: each dump contains 3–4 collect-swap scopes and only 4–6 callback scopes across those frames. Largest pass scopes 0.693 / 0.733 / 0.599 / 0.701 ms. |
| `xrengine_2026-09-23_12-39-45_pid28464/profiler-cpu-frame-2026-09-23-12-40-22-622-f4d0a50e.log` | Identity-exclusion Vulkan: two completed collect swaps 3.328 / 3.485 ms, empty main dirty-pass scopes about 0.001 ms and no callback/GPU-scene update scopes. |

The task's `mcp-captures/` contains `Screenshot_20260923_123745_409_ad48e7a7c9e3498dbbbe8e37e49bc5d7.png` (OpenGL) and `Screenshot_20260923_124031_464_9d31172c3e3e4160a7177a476b837293.png` (Vulkan candidate). Both were viewed, but they are not a matched correctness gate: the OpenGL capture includes editor UI and a largely white/black scene region, while the Vulkan capture shows dark geometry and an environment backdrop. Neither a discriminating mutation sequence nor temporal-buffer identity was established from these images. S13 must capture matching scene views and accepted content before claiming visual parity or a correct permanent fix.

The saved reports do not supply a complete source/diff/binary manifest. Recover any available build/session metadata before reuse; otherwise record the historical identity as incomplete and freeze a new current baseline. Ignored evidence may be cleaned up. This durable record preserves the result without requiring those files to exist indefinitely.

## Validation limit and next work

The causal experiment is not a completed S13b change. A permanent fix must preserve immediate exact-publication identities while proving that real transform, material, pass, geometry, visibility, scene/world, and temporal-history changes still enqueue and publish correctly. S13a should capture paired warmed windows, dirty-cause counts, callback counts, inner `TryUpdateMeshCommand` timing/allocations and frame identities with bounded observer overhead. S13b can then separate identity freshness from render-state dirtiness and validate both rendering and mutation paths before claiming the frame-rate improvement. The remaining ~3 ms Advanced publication and separate Vulkan recording/present costs need their own attribution; the command-swap result does not close the entire Vulkan stall investigation.

The experiment proves the trigger for Vulkan's repeated callbacks, but it does not yet isolate why OpenGL's dirty queue stays nearly empty under the same high-level pipeline. That backend divergence should be measured explicitly in S13a rather than inferred from shared source code.

The isolated editor session was stopped through the session manager. The experiment changed no Unit Testing World settings and left no source-code edits. No regression tests were added or run during this feature investigation under the repository testing policy.
