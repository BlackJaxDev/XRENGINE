# Render Queries And Occlusion Validation

Scope: Validate render queries and the occlusion paths that use them. This includes hardware queries, CPU async query occlusion, CPU masked software occlusion, GPU Hi-Z occlusion, collect-visible stall evidence, and visual correctness.

Architecture: [Render Queries](../../../developer-guides/rendering/render-queries.md), [CPU Query Async Occlusion](../../../developer-guides/rendering/cpu-query-async-occlusion.md), [GPU Hi-Z Occlusion Culling](../../../architecture/rendering/gpu-hiz-occlusion-culling.md), [CPU Software Occlusion](../../../architecture/rendering/cpu-software-occlusion.md), [Frame Lifecycle And Dispatch Paths](../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md), [GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md)  
Code todos: [CPU Async Query Camera Motion](../../todo/rendering/cpu-async-query-camera-motion-todo.md), [Masked Software Occlusion Culling](../../todo/rendering/masked-software-occlusion-culling-todo.md), [GPU-Driven Occlusion Culling Architecture](../../todo/rendering/gpu/gpu-driven-occlusion-culling-architecture-todo.md), [Vulkan Core Frame Loop Master TODO](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md), [Vulkan Core Hardening TODO](../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md#9-make-occlusion-modes-bounded-and-effective)

## Setup

Use the smallest path that exercises the feature under review.

- Build task: `Build-Editor`.
- Runtime tasks: `Start-Editor-NoDebug`, `Start-Editor-UnitTesting-OpenXR-Monado-NoDebug`, and `Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug`.
- Measurement tasks: `Measurement-P3-CpuDirect-Census`, `Measurement-P3-CpuDirect-Census-NoOcclusion`, `Measurement-Baseline-GpuIndirectZeroReadback`, `Measurement-P3-ZeroReadback-Census`, and `Measurement-P3-ZeroReadback-Census-NoOcclusion`.
- Benchmark task: `Build-RenderBench`. Use `Benchmark-Vulkan-Clean-OpenXR` when the hardware is available.
- Launch profiles: `Editor (Unit Testing World)`, `Editor (Unit Testing World, Validation Layers)`, `Editor (Unit Testing OpenXR SteamVR)`, and `Editor (Renderer Development)`.
- World settings: use `Assets/UnitTestingWorldSettings.jsonc` and set `WorldKind` to `MathIntersections` for the occlusion rigs.
- Environment variables: `XRE_WORLD_MODE=UnitTesting`, `XRE_UNIT_TEST_WORLD_KIND=Default` or `MathIntersections`, `XRE_UNIT_TEST_VR_MODE=OpenXR` or `MonadoOpenXR`, `XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS=1`, `XRE_FORCE_MESH_SUBMISSION_STRATEGY=CpuDirect` or `GpuIndirectZeroReadback`, `XRE_OCCLUSION_CULLING_MODE=Disabled`, `XRE_CPU_SOC_OCCLUSION=1`, `XRE_PROFILER_ENABLED=1`, `XRE_P3_LOGGING=1`, `XRE_VULKAN_VALIDATION=1`, and `XRE_GL_DEBUG=1`.
- Editor panels: use the ImGui Occlusion panel and the profiler data source. Use the Math Intersections Test Controls for the three occlusion rigs.
- RenderDoc is optional. Use it only when screenshots, telemetry, and logs do not explain a backend-specific failure.

## Checks

### Render Query Capability And Backend Contract

Architecture: [Render Queries](../../../developer-guides/rendering/render-queries.md)
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Capture query capabilities. | Start Vulkan once with query logging enabled. Record device features, queue query features, loaded extensions, timestamp period, timestamp valid bits, and transform-feedback query support. | The report is machine-readable and names unsupported features explicitly. | Open | none |
| Validate query recording. | Run the editor with Vulkan validation and synchronization validation enabled. Exercise query recording in a query-enabled scene. | No begin/end, reset, pool, index, render-scope, inheritance, command-buffer, pool-lifetime, or synchronization VUID appears. | Open | none |
| Validate query families. | Run at least one backend execution test for each supported query family when the hardware and API support it. | Supported families return decoded results. Unsupported families return explicit unsupported status. | Open | none |
| Compare timestamp and elapsed query results. | Use controlled CPU/GPU ordering on live hardware and compare raw ticks, converted nanoseconds, and elapsed duration. | Results stay within documented tolerance. No device ticks are reported as nanoseconds without conversion. | Open | none |
| Confirm nonblocking query behavior. | Run the query-enabled desktop path and inspect telemetry. | The render path has no global waits, no steady-state resource growth, and no new compiler warnings or device loss. | Open | none |
| Run broad regression validation. | Run `dotnet restore`, `dotnet build XRENGINE.slnx`, and the full unit-test project when a query-system change needs broad coverage. | Commands pass, or unrelated failures are recorded separately. | Open | none |

### CPU Async Query Occlusion

Architecture: [CPU Query Async Occlusion](../../../developer-guides/rendering/cpu-query-async-occlusion.md)
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Run focused CPU query tests. | Run `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter "FullyQualifiedName~CpuRenderOcclusionCoordinatorTests|FullyQualifiedName~Occlusion" --no-restore` and `dotnet build XREngine.Editor/XREngine.Editor.csproj --no-restore`. | CPU coordinator, temporal policy, and occlusion tests pass. Report unrelated failures separately. | Open | none |
| Validate still desktop CPU query occlusion. | Start the Unit Testing World with `CpuDirect` and `CpuQueryAsync`. Hold the camera still until the query state converges. | Nonzero tested, submitted, resolved, and culled counts appear after warmup. No visible false occlusion appears. | Open | 2026-07-20 stationary Vulkan validation passed. |
| Validate slow translation and rotation. | Move the desktop camera slowly and steadily in the occlusion scene. Include lateral translation and rotation. | `CpuRendered / CpuTested` does not return to about `1.0` after warmup. Culling remains useful. | Open | 2026-07-20 translation and rotation Vulkan validation passed. |
| Validate fast motion and camera cuts. | Run fast camera motion and a camera cut beyond configured cut thresholds. | The path fails visible during unsafe reuse and recovers within configured frames. | Open | none |
| Validate projected growth and viewport-edge reveals. | Move toward an occluded object. Move an occluded proxy toward a viewport edge. | Forced-visible telemetry reports projected growth, edge risk, or related fallback reasons. Yellow bounds only mark current `Skip` or `ProbeOnly` decisions. | Open | none |
| Validate command-set mutation, resize, and pipeline recreation. | Add or remove objects, resize the view, and recreate the pipeline while CPU query occlusion is active. | No stale query result hides new or changed geometry. Telemetry reports forced-visible or cleanup reasons. | Open | none |
| Validate scene-only sequential stereo. | Run scene-only emulated VR with sequential views and CPU query occlusion enabled. Capture left and right draw, cull, and query counts. | Each physical eye owns safe evidence. Objects visible in either eye remain visible. | Open | none |
| Validate OpenVR two-pass. | Run an OpenVR two-pass smoke when the hardware and runtime are available. | No stereo eye popping or false occlusion appears. Query counts remain bounded. | Open | none |
| Validate OpenVR single-pass stereo. | Run an OpenVR single-pass stereo smoke when available. | Shared or single-pass stereo keeps an object visible when either eye can see it. | Open | none |
| Validate OpenXR smoke. | Run a Monado-backed or vendor OpenXR smoke when available. Record predicted-to-late pose delta and active collect-visible pose policy. | HMD motion keeps useful culling after warmup. No stereo eye popping appears. | Open | none |
| Compare query cost. | Compare query cost and proxy draw count with the earlier path for the same scene. | Cost is equal or lower. Sequential VR does not unexpectedly double the cost. | Open | none |
| Force fallback reasons. | Force each fallback-to-visible case. | Telemetry reports each case. | Open | none |

### CPU Async Query Camera Motion

Architecture: [CPU Query Async Occlusion](../../../developer-guides/rendering/cpu-query-async-occlusion.md)
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Inspect yellow debug bounds. | Enable bounds diagnostics during safe camera motion. | Yellow bounds correspond to current `Skip` or `ProbeOnly` decisions. | Open | none |
| Verify continuous-motion throughput. | Capture screenshots and telemetry during safe translation and rotation. | Query submission, resolution throughput, and latency remain bounded. Result-age expiry does not cause an all-visible feedback loop. | Open | 2026-07-20 translation and rotation evidence passed for throughput and latency. |
| Re-capture from multiple positions. | Capture the same test from multiple camera positions. | Artifacts move with the view. A static artifact is treated as stale or uninitialized camera evidence. | Open | 2026-07-20 multiple-position capture completed. |
| Review backend logs. | Review `log_rendering.log` and the active backend log after the run. | No validation errors or unexplained query-policy diagnostics appear. Shutdown-only noise is separated. | Open | 2026-07-20 logs had no Vulkan validation errors in the accepted runs. |
| Prepare RenderDoc only when needed. | If screenshots and logs do not explain a backend failure, verify the RenderDoc Vulkan layer before capture. Keep captures under the run root. | A capture explains the backend failure. Replay sessions are closed. | Open | 2026-07-20 RenderDoc was not needed. |

### CPU Masked Software Occlusion

Architecture: [CPU Software Occlusion](../../../architecture/rendering/cpu-software-occlusion.md)
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Run targeted SOC tests. | Run `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter "FullyQualifiedName~MaskedSoftwareOcclusionCullingTests|FullyQualifiedName~MeshOptimizerInteropTests" --no-restore /p:UseSharedCompilation=false`. | Targeted selector, buffer, rasterizer, AABB, stereo, shader, and interop tests pass. | Open | 2026-05-13 build path passed; 2026-05-29 run was blocked before execution by an unrelated `MemoryPack` source-generator limit. |
| Run rendering-adjacent tests. | Run the rendering-adjacent subset after SOC selector or rasterizer changes. | Existing CPU-query behavior is preserved when SOC is disabled. | Open | none |
| Smoke disabled SOC. | Launch the editor with SOC disabled. | CPU-query behavior matches the current baseline. | Open | none |
| Smoke forced-visible SOC. | Launch the editor with SOC enabled and `CpuSocDebugForceVisible=true`. | Behavior matches disabled SOC while telemetry and occluder buffers are built. | Open | none |
| Smoke scalar SOC. | Launch the editor with SOC enabled and force-visible off. Test traditional mesh rendering. | No visual false occlusion appears. SOC tested and culled counts are nonzero in an occluded view. | Open | none |
| Smoke meshlet SOC visibility. | Run meshlet rendering with SOC enabled. | Meshlet command visibility consumes the CPU visibility mask correctly. | Open | none |
| Verify default-off behavior. | Reload settings and restart the editor. | SOC remains disabled by default unless `GpuOcclusionCullingMode=CpuSoftwareOcclusion`, `EnableCpuSoftwareOcclusionCulling`, or `XRE_CPU_SOC_OCCLUSION=1` enables it. | Open | none |
| Compare two-Sponza captures. | Capture the two-Sponza diagnostic with SOC disabled and then with scalar SOC enabled. | `Visible(query)` drops from the recorded `10/6` range toward `<= 3` in the target view. No false occlusion appears. | Open | none |
| Validate camera and near-plane motion. | Move the editor camera and near plane through the SOC scene. | No false occlusion appears during motion. | Open | none |
| Profile scalar SOC cost. | Capture scalar SOC timing in the target diagnostic scene. | Total SOC CPU cost stays `<= 1.5 ms`. | Open | none |
| Validate SIMD, if retained. | Profile retained vector widths and compare p50, p95, p99, bytes, vector iterations, tails, and hot-path allocations. | The selected path reaches `<= 0.6 ms` total SOC cost on the matched target scene and improves full-frame p95. Otherwise SOC remains opt-in with documented limits. | Open | none |
| Validate stereo and OpenVR SOC. | Run mono, editor stereo, and OpenVR captures. | Mono, stereo, and OpenVR do not share stale camera state. An object visible in either eye remains visible. | Open | none |
| Publish disposition evidence. | Record false-occlusion risk, false-visible rate, CPU cost, selector timing, rasterizer timing, AABB-test timing, launch-flow smoke results, and end-to-end timing. | The disposition decision has enough evidence to retain, promote, keep diagnostic-only, or retire SOC. | Open | none |

### GPU Hi-Z And GPU-Driven Occlusion

Architecture: [GPU Hi-Z Occlusion Culling](../../../architecture/rendering/gpu-hiz-occlusion-culling.md), [GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md), [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md)
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Capture GPU occlusion baselines. | Use Vulkan with `GPURenderDispatch=true` and DevParity and ShippingFast profiles. Capture FPS, cull dispatch time, Hi-Z stage stats, draw counts with `GpuHiZ` on and off, BVH-ready count, flat-fallback count, and dirty-bypass frames during 30 seconds of camera motion. | Baseline data separates cull, occlusion, and draw-count effects. | Open | none |
| Capture RenderDoc pass order. | Capture one frame per configuration if screenshots and logs are not enough. | The capture shows cull, early draw, pyramid build, late test, and indirect submission order. | Open | none |
| Validate editor camera motion. | Run Vulkan GPU dispatch with slow orbit, fast flight, and still camera. Capture screenshots from at least two camera positions each. | No false occlusion or popping appears. Dirty passthrough frames trend to zero after the dirty-bypass path is removed. | Open | none |
| Validate camera cut recovery. | Make a camera cut in the GPU Hi-Z rig. | Full visibility recovers in one frame through the late recovery pass. | Open | none |
| Validate live model import. | Add or remove a model while GPU Hi-Z is active. | New commands are visible until valid history exists. No frame-loop CPU readback appears in zero-readback modes. | Open | none |
| Verify RenderDoc barriers. | Inspect the capture against the GPU Hi-Z architecture contract. | Barriers appear between cull writes, early raster, depth pyramid build, late preparation, late raster, and consumers. | Open | none |
| Verify GPU readback bytes. | Inspect `Stats.GpuReadback` in zero-readback modes. | Production zero-readback modes have zero CPU readback bytes per frame for visibility, count, and overflow state. | Open | none |
| Verify command reuse counters. | Inspect warmed primary rerecord counters. | GPU-written visibility, command, and count changes do not cause primary rerecords. Every miss names a topology, capacity, binding, pipeline, or resource generation. | Open | none |
| Compare scaling curves. | Capture matched low-, medium-, and high-count scaling curves for CPU recording and GPU execution. | The high-count and high-occlusion crossover is approved before promotion. | Open | none |
| Run OpenGL parity. | Run the same scene on OpenGL instrumented and zero-readback paths where the backend supports it. | OpenGL either matches the supported contract or reports an explicit unsupported limitation. | Open | none |
| Qualify Math Intersections GPU rig. | Run the GPU Two-Pass Hi-Z + GPU BVH rig in the Math Intersections world. | The rig reports `PASS` only when two-phase GPU Hi-Z, zero-readback submission, and GPU BVH are active. | Open | 2026-08-03 correctly failed the old single-phase path. |

### Collect-Visible Wait Decoupling

Architecture: [Frame Lifecycle And Dispatch Paths](../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Capture a clean collect-visible profile. | After render-thread stalls are fixed, capture a clean scene where `EngineTimer.CollectVisibleThread.DispatchCollectVisible` is the hot path. | `DispatchCollectVisible` is hot. `WaitForRender` is not the hot path. | Open | none |
| Separate command collection costs. | Measure CPU-direct and GPU-driven command collection separately in the same capture. | The capture identifies CPU-direct and GPU-driven collection costs independently. | Open | none |

## Hardware Matrix
| Path | Hardware or runtime | Requirement | Status | Last evidence |
|---|---|---|---|---|
| Vulkan CPU query | Vulkan device with occlusion query support | Validate desktop mono, validation layers, and query latency. | Open | 2026-07-20 desktop Vulkan motion subset passed. |
| OpenGL CPU query | OpenGL 4.6 | Validate hardware query behavior and GPU-dispatch CPU query path where supported. | Open | none |
| OpenVR two-pass | SteamVR/OpenVR HMD | Validate sequential VR occlusion and SOC stereo smoke. | Open | none |
| OpenVR single-pass stereo | SteamVR/OpenVR HMD with SPS path | Validate both-eye visibility proof. | Open | none |
| OpenXR | Monado or vendor OpenXR runtime | Validate predicted-to-late pose delta and stereo occlusion. | Open | none |
| GPU Hi-Z | Vulkan GPU-dispatch path | Validate two-pass Hi-Z, zero-readback, and GPU BVH. | Open | 2026-08-03 GPU rig failed until two-pass landed. |

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
| GPU Two-Pass Hi-Z + GPU BVH rig | 2026-08-03 run reported `single-phase-current-depth` and zero late draws. | [Math Intersections Occlusion Qualification Investigation](../../investigations/rendering/archive/math-intersections-occlusion-qualification-2026-08-03.md), [GPU-driven occlusion todo](../../todo/rendering/gpu/gpu-driven-occlusion-culling-architecture-todo.md) |
| GPU rig Vulkan shader activation | 2026-08-03 run hit an existing `DeferredLightingDir` rewrite or compile failure, so blank viewport captures were not accepted as occlusion evidence. | [Math Intersections Occlusion Qualification Investigation](../../investigations/rendering/archive/math-intersections-occlusion-qualification-2026-08-03.md) |
| Masked SOC targeted test run | 2026-05-29 targeted test execution was blocked before tests by `MemoryPack` member limit in `RenderStatsPacket`. | [Masked Software Occlusion Culling TODO](../../todo/rendering/masked-software-occlusion-culling-todo.md) |
