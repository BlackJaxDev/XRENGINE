# Retinal Visibility Cache Validation

Scope: Validate Retinal Visibility Cache (RVC) rendering, RVC debug views, OpenXR quad-view plumbing, visibility masks, frame-graph resources, fallback reporting, quality gates, and performance gates.

Architecture links:

- [Retinal Visibility Cache Rendering](../../../architecture/rendering/retinal-visibility-cache-rendering.md)
- [OpenXR VR Rendering](../../../architecture/rendering/openxr-vr-rendering.md)
- [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)

Code todo links:

- [RVC VR Debug Views TODO](../../todo/rendering/vr/retinal-visibility-cache-debug-views-todo.md)

## Setup

Use Windows with the ImGui editor. Use OpenGL for correctness checks that the engine supports. Use Vulkan for production RVC checks. Use OpenXR for quad-view and foveated checks. Use OpenVR only for stereo regression checks.

Tasks from `.vscode/tasks.json`:

- `Build-Editor`: build the ImGui editor.
- `Build-Editor-Fast`: build the editor without analyzers for local iteration.
- `Build-Editor-Release`: build the release editor for performance runs.
- `Watch-Editor-RendererDevelopment`: run the editor with `--renderer-development`.
- `Start-Editor-RendererDevelopment-NoDebug`: start the editor after `Build-Editor`.
- `Measurement-Baseline-GpuIndirectZeroReadback`: start a profiling run with `XRE_FORCE_MESH_SUBMISSION_STRATEGY=GpuIndirectZeroReadback`.
- `Measurement-GameLoopRenderPipeline-Release-All`: run release render-pipeline measurements.

Launch profiles from `.vscode/launch.json`:

- `Editor (Default World)`: start the editor.
- `Editor (Renderer Development)`: start the editor with `--renderer-development`.
- `Editor (Unit Testing World)`: start the unit-testing world with `XRE_WORLD_MODE=UnitTesting`.
- `Editor (Unit Testing OpenXR SteamVR)`: start the unit-testing world with OpenXR SteamVR settings.
- `Editor (Unit Testing World, Validation Layers)`: start with `XRE_VULKAN_VALIDATION=1` and `XRE_GL_DEBUG=1`.

Settings and environment variables:

- Use `VR.RenderPipeline=Rvc` only for explicit RVC eye validation.
- Use `VR.ViewRenderMode=SequentialViews`, `SinglePassStereo`, or `ParallelCommandBufferRecording` for mode checks.
- Use `VR.RvcPipelineMode=ForwardPlusOracle`, `VisibilityOnlyDebug`, or the requested RVC mode for the check.
- Use `XRE_WORLD_MODE=UnitTesting` for deterministic unit-world runs.
- Use `XRE_UNIT_TEST_VR_MODE=OpenXR` for OpenXR unit-world runs.
- Use `XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS=1` when preview stereo views are required.
- Use `XRE_VULKAN_VALIDATION=1` and `XRE_GL_DEBUG=1` for API validation runs.
- Use `XRE_PROFILER_ENABLED=1` for profiling runs.
- Use `XRE_FORCE_MESH_SUBMISSION_STRATEGY=GpuIndirectZeroReadback` for production performance runs.

Record the scene, render API, headset runtime, GPU, driver, settings, command or task, commit, and date for each evidence item. Do not record disposable evidence paths in this document.

## Checks

### Source Contracts

Feature architecture: [Retinal Visibility Cache Rendering](../../../architecture/rendering/retinal-visibility-cache-rendering.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Run RVC source-contract tests. | Run `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter RvcRenderingContractTests -v:minimal`. | RVC contract tests pass. Existing unrelated package warnings do not block the check. | Done in source document. | 2026-07-03 |
| Build the runtime rendering project. | Run `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -v:minimal`. | The project builds. Existing unrelated package warnings do not block the check. | Done in source document. | 2026-07-03 |
| Capture the worktree summary. | Record `git branch --show-current`, `git status --short`, and the changed RVC files. | The evidence identifies the code that was validated. | Pending. | none |
| Create one validation run record. | Record logs, captures, profiler data, and reports for the run. | Evidence for one run can be traced from command to result. | Pending. | none |

### Runtime Capability Inventory

Feature architecture: [Retinal Visibility Cache Rendering](../../../architecture/rendering/retinal-visibility-cache-rendering.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Inventory OpenXR runtime support. | Record runtime name, runtime version, enabled extensions, stereo view support, `XR_VIEW_CONFIGURATION_TYPE_PRIMARY_QUAD_VARJO`, `XR_VARJO_foveated_rendering`, `XR_KHR_visibility_mask`, depth layers, multiview, and Vulkan interop. | The support matrix shows each available and missing feature. Missing support has a visible reason. | Pending. | none |
| Inventory Vulkan production features. | Record descriptor heap or descriptor buffer, descriptor indexing, fragment shading rate, fragment density map, synchronization2, dynamic rendering, multiview, mesh shader, and timeline semaphore support. | Missing features are diagnostic. No feature silently downgrades. | Pending. | none |
| Validate quad-view emulation setup. | Enable Quad-Views-Foveated or an equivalent OpenXR layer. Run an OpenXR unit-world check. | The engine sees four views on non-Varjo hardware when the layer supports them. Eye-tracked inset movement and inset-boundary blending work. | Pending. | none |
| Capture desktop Forward+ reference. | Run `Editor (Unit Testing World)` with RVC off and capture `DesktopMono`. | Screenshot, logs, render stats, settings, and scene hash exist. | Pending. | none |
| Capture stereo Forward+ reference. | Run the stable VR path and capture `Stereo`. | Left eye, right eye, mirror, logs, render stats, settings, and scene hash exist. | Pending. | none |
| Capture foveated or quad Forward+ reference. | Run with foveation off and on where the runtime supports it. | All submitted views, mirror, profiler data, and frame logs exist. | Pending. | none |
| Define the numeric baseline. | Measure `OpaqueDense`, `AvatarMaterialDiverse`, `TransparencyFallback`, and `QuadView`. | Submitted pixels, unique fragment invocations, GPU pass time, CPU command build time, and missed-deadline counters are recorded. | Pending. | none |

### Mode And Fallback Regression

Feature architecture: [Retinal Visibility Cache Rendering](../../../architecture/rendering/retinal-visibility-cache-rendering.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Validate desktop mono with RVC off. | Run the desktop unit-testing world with RVC off. | The standard desktop pipeline, image, frame logs, and render stats are unchanged by OpenXR-eye settings. | Pending. | none |
| Validate OpenXR eyes with the Forward+ oracle. | Set `VR.RvcPipelineMode=ForwardPlusOracle` for eyes while the desktop output uses the standard pipeline. | Eyes use the oracle by request. Desktop does not switch to RVC. No fallback error appears. | Pending. | none |
| Validate unsupported eye cache modes. | Request an RVC eye cache mode on an unsupported backend. | The eye pipeline reports a visible fallback reason. Desktop output is not replaced. | Pending. | none |
| Validate OpenVR stereo behavior. | Run the stable OpenVR path. | Left/right rendering, mirror output, frame timing, and settings remain stable. | Pending. | none |
| Validate OpenXR stereo behavior. | Run `Editor (Unit Testing OpenXR SteamVR)`. | `xrBeginSession`, `xrLocateViews`, swapchain acquire/release, and submission use the active stereo view configuration. | Pending. | none |
| Validate editor preview and mirror behavior. | Run with quad-view storage enabled and inspect preview texture selection. | Preview and mirror texture selection remain stable. | Pending. | none |
| Validate OpenGL correctness-slice rejection. | Request full production RVC on OpenGL. | The engine reports `UnsupportedOpenGlProductionPath` or an equivalent diagnostic. | Pending. | none |

### OpenXR Quad-View And Visibility Masks

Feature architecture: [OpenXR VR Rendering](../../../architecture/rendering/openxr-vr-rendering.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Validate active view-configuration selection. | Enable quad view and run on supported and unsupported runtimes. | Quad view is selected only when enabled and supported. Stereo fallback records the reason. | Pending. | none |
| Validate four reported views. | Capture all submitted views and the desktop mirror. | Views 2 and 3 use runtime FOV, pose, and viewport size while sharing the eye-family scene rig. | Pending. | none |
| Validate moving inset behavior. | Capture static-gaze and moving-gaze runs. | Wide views remain valid while inset regions move. | Pending. | none |
| Validate swapchain image lifecycle. | Inspect logs or a graphics capture across success and render-failure paths. | Each acquired image is released. No per-view image-count or framebuffer array overrun occurs. | Pending. | none |
| Validate visibility-mask function lookup. | Run with `XR_KHR_visibility_mask` support present and absent. | Logs show extension support, function lookup success, or `NativeFunctionMissing`. | Pending. | none |
| Validate hidden and visible mask fetch. | Capture per-view mask data. | Vertex count, index count, revision, and missing-mesh status are recorded for each view. | Pending. | none |
| Validate visibility-mask stencil stage. | Use RenderDoc or logs for a frame that reaches visibility rendering. | The mask stage runs before visibility, or it is skipped with a visible reason. | Pending. | none |
| Validate mask invalidation. | Trigger or simulate `XrEventDataVisibilityMaskChangedKHR`. | The cached mask revision changes and mesh fetch refreshes. | Pending. | none |

### RVC Frame Graph And Resources

Feature architecture: [Retinal Visibility Cache Rendering](../../../architecture/rendering/retinal-visibility-cache-rendering.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Inspect RVC resource creation. | Inspect logs or RenderDoc resources for one RVC frame. | Per-view depth, visibility, velocity, HZB, reconstruction error, pixel-to-shadelet, transparency, final resolve, and mirror/debug resources exist. Shared buffers for source records, material rows, masks, indirect args, shadelets, light clusters, lighting, reservoirs, temporal cache, and counters exist. | Pending. | none |
| Inspect framebuffers. | Inspect RVC framebuffer setup. | Visibility, transparency, resolve, and debug framebuffer attachments exist. | Pending. | none |
| Inspect pass order. | Inspect graph labels or RenderDoc events. | The order is mask stencil, visibility, reconstruction, HZB, pixel-to-shadelet, material shadelets, foveated shading rate, head-space light clusters, shared lighting, reuse validation, temporal cache, transparency, resolve, and diagnostics. | Pending. | none |
| Validate Vulkan synchronization2 barriers. | Inspect a Vulkan RenderDoc capture. | Image and buffer barriers match attachment, storage, sampled, transfer, and OpenXR swapchain transitions. | Pending. | none |
| Validate OpenGL correctness barriers. | Inspect logs or a RenderDoc capture for the OpenGL slice. | Prototype ordering is coherent for the supported slice. | Pending. | none |
| Validate active-stage diagnostics. | Run a foundation RVC stage and a claimed production stage. | Foundation runs may show `RVC.Pass.*.KernelPending`. Claimed production stages do not show kernel-pending warnings. | Pending. | none |
| Validate RVC frame profiles. | Inspect engine stats or profile output. | Projection, viewport, previous view-projection, runtime view index, swapchain identity, pixel count, stereo mode, foveation mode, GPU timing, and fallback reason are populated. | Pending. | none |

### Visibility Source Paths

Feature architecture: [Retinal Visibility Cache Rendering](../../../architecture/rendering/retinal-visibility-cache-rendering.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Validate static mesh visibility sources. | Capture a static opaque scene. | Each accepted pixel has instance, draw or primitive, material row, transform, and editor-selection metadata. | Pending. | none |
| Validate skinned mesh visibility sources. | Capture an animated skinned scene. | Deformation/version identity is present. Stale reuse is rejected after animation changes. | Pending. | none |
| Validate zero-readback indirect visibility sources. | Run with GPU indirect sources. | Draw visibility does not read back to CPU in the render loop. | Pending. | none |
| Validate meshlet or mesh-shader visibility sources. | Run on hardware with and without mesh shader support. | Mesh shader is selected when supported. Compute meshlet expansion is selected as a visible fallback otherwise. | Pending. | none |
| Validate unsupported material fallback. | Use transparent, refractive, order-dependent, expensive alpha-test, and strongly view-dependent materials. | Fallback counters appear and Forward+ output is correct. | Pending. | none |
| Validate rapid head motion and disocclusion. | Move the HMD rapidly in stereo scenes. | Captures show no stale-HZB one-eye holes. | Pending. | none |

### Shadelets, Lighting, Temporal Cache, And Resolve

Feature architecture: [Retinal Visibility Cache Rendering](../../../architecture/rendering/retinal-visibility-cache-rendering.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Validate attribute reconstruction. | Capture reconstruction outputs. | Position, normal, tangent, UV, material row, previous position, velocity, and reconstruction-error visualizer are present. | Pending. | none |
| Validate conservative HZB and post-validation. | Capture HZB and uncertainty masks. | Uncertain, newly visible, edge, and cross-view disagreement candidates are post-validated. | Pending. | none |
| Validate shadelet map generation. | Capture shadelet debug outputs. | Pixel-to-shadelet map, tile-local dedup, global merge, material bins, density overlay, cache-miss overlay, and overflow counters exist. | Pending. | none |
| Validate compute-side material shading. | Compare RVC foveal output with Forward+. | Material rows match descriptor heap and descriptor indexing backends. Foveal output matches the oracle within tolerance. | Pending. | none |
| Validate fragment shading rate fast path. | Run a material class that is not ported to compute reconstruction. | `VK_KHR_fragment_shading_rate` path works, with near-UI and hand 1x1 overrides. | Pending. | none |
| Validate shared head-space light clusters. | Compare against per-view Forward+ tile grids. | Cluster occupancy, exact-light counts, rejected lights, and comparison output are present. | Pending. | none |
| Validate peripheral light aggregation and reservoirs. | Capture many-light scenes. | Aggregate contribution, reservoir weight, exact-vs-aggregate, and energy error overlays are present. | Pending. | none |
| Validate reuse domains. | Run intra-view, inset/wide, stereo, and temporal reuse scenarios. | Accepted and rejected reuse counters and reasons are present. | Pending. | none |
| Validate the A/B reuse harness. | Render the same frame with reuse enabled and disabled. | Side-by-side captures, per-region metrics, counters, and pass/fail report exist. | Pending. | none |
| Validate temporal cache. | Run static and moving scenes. | Confidence, age, invalidation reason, temporal hit rate, stale rejection, and no visible ghosting are proven. | Pending. | none |
| Validate foveated resolve. | Capture wide/inset and desktop mirror output. | Visibility-edge AA, foveated TAA fallback where used, wide/inset identity handling, mirror, and XR submitted images are correct. | Pending. | none |
| Validate quality thresholds. | Compare to the Forward+ oracle with `RvcQualityToleranceSet.Default`. | Fovea, guard band, mid-field, and periphery stay inside the documented error, SSIM, and FLIP gates. | Pending. | none |

### RVC Debug Views

Feature architecture: [Retinal Visibility Cache Rendering](../../../architecture/rendering/retinal-visibility-cache-rendering.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Validate independent desktop output with sequential eye RVC debug output. | Run desktop plus OpenXR eyes with sequential views and a selected debug view. | Desktop output remains independent. Eye debug output uses the correct view identity. | Pending. | none |
| Validate OpenXR and OpenVR stereo sequential debug output. | Run both VR paths with `SequentialViews`. | Left and right views show matching debug semantics. | Pending. | none |
| Validate OpenXR Vulkan single-pass stereo debug output. | Run `SinglePassStereo` on Vulkan. | Debug output states whether the effective path is true stereo, layered/multiview, or compatibility per-eye rendering. | Pending. | none |
| Validate quad-view or emulated quad-view sequential output. | Run quad-view or emulated quad-view with `SequentialViews`. | Left wide, right wide, left inset, and right inset views are selectable. | Pending. | none |
| Validate quad-view or emulated quad-view Vulkan parallel output. | Run quad-view or emulated quad-view with `ParallelCommandBufferRecording`. | Debug semantics match the sequential lane for the same scene. | Pending. | none |
| Validate OpenGL correctness slices and unsupported-mode diagnostics. | Request each unsupported debug lane on OpenGL. | Unsupported lanes fail visibly with actionable diagnostics. | Pending. | none |
| Validate debug-off versus debug-on overhead. | Measure the same scene with debug disabled and with one selected debug view. | The overhead is recorded and stays within the approved budget. | Pending. | none |
| Validate no disabled-debug allocation regression. | Profile a frame with debug views registered but disabled. | No render-loop allocation regression appears. | Pending. | none |
| Validate delayed readback latency and overflow behavior. | Enable RVC counters and inspect readback age and overflow counters. | Counter readback is delayed. Overflow state is visible. | Pending. | none |
| Capture representative debug before/after images. | Capture one visibility issue, one material reconstruction issue, one reuse issue, one lighting issue, and one resolve/composition issue. | Each capture identifies the source stage of the problem. | Pending. | none |

### Vulkan Production Hardening

Feature architecture: [Retinal Visibility Cache Rendering](../../../architecture/rendering/retinal-visibility-cache-rendering.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Validate Vulkan multiview integration. | Run stereo and four-view paths. | True stereo paths remain isolated from four-view sequential paths. View masks, layered targets, and fallback diagnostics are correct. | Pending. | none |
| Validate dynamic rendering. | Inspect RenderDoc event order and attachment state. | Dynamic rendering state is correct. | Pending. | none |
| Validate explicit synchronization. | Inspect synchronization2 barriers. | Image and buffer barriers cover RVC resources. | Pending. | none |
| Validate timeline semaphore handoff. | Inspect OpenXR swapchain handoff where applicable. | No missed or stale image usage occurs. | Pending. | none |
| Validate descriptor heap backend. | Run on hardware with descriptor heap support. | Descriptor heap is selected. Resource rows are valid. No duplicate RVC-specific texture table exists. | Pending. | none |
| Validate descriptor indexing fallback. | Run without descriptor heap support. | Descriptor indexing is selected and material/shadelet semantics match. | Pending. | none |
| Validate missing descriptor backend failure. | Disable or use hardware without heap and indexing support. | The engine reports a visible fallback. | Pending. | none |
| Validate fragment density map alternative. | Run on a runtime that supports or lacks fragment density map. | Selection or unsupported diagnostics are explicit. | Pending. | none |
| Validate `VK_EXT_mesh_shader`. | Run with and without mesh shader support. | Mesh shader expansion or indirect/compute meshlet path is selected with a visible reason. | Pending. | none |

### Performance, Counters, And Timing

Feature architecture: [Retinal Visibility Cache Rendering](../../../architecture/rendering/retinal-visibility-cache-rendering.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Capture the Vulkan `GpuIndirectZeroReadback` promotion workload. | Run desktop and both RVC eye outputs with foveation disabled and enabled where supported. | Whole-frame render p95 is at most 8.33 ms. The result does not skip an output and does not use silent fallback. | Pending. | none |
| Validate delayed GPU counter readback. | Inspect RVC counter readback. | No synchronous render-loop readback occurs. | Pending. | none |
| Validate per-view GPU timing. | Inspect frame profiles. | `GpuMilliseconds` is populated from resolved GPU timing or reports an explicit unknown state. | Pending. | none |
| Validate RVC counter categories. | Inspect counter output. | Visible, culled, uncertain, post-validated, page requests, raster lane, shadelets, material bins, cache hit/miss, reuse, temporal, and invalidation counters are present. | Pending. | none |
| Capture warm-cache performance runs. | Measure `OpaqueDense`, `AvatarMaterialDiverse`, `TransparencyFallback`, and `QuadView`. | Profiler traces, frame stats, GPU timings, submitted pixels, unique shadelets, and missed-deadline counters exist. | Pending. | none |
| Compare against the Forward+ oracle. | Build a table from Forward+ and RVC runs. | Timing and counter deltas are recorded against the foveated Forward+ baseline. | Pending. | none |

## Hardware Matrix
| Target | Required hardware or runtime | Checks |
|---|---|---|
| Desktop OpenGL correctness slice | Windows GPU with OpenGL 4.6 support | Mode fallback, OpenGL correctness barriers, desktop regression |
| Vulkan production RVC | Windows GPU and driver with required Vulkan production features | Frame graph, barriers, descriptor backend, performance |
| OpenXR stereo | OpenXR runtime with stereo views | Stereo submission, frame profiles, fallback diagnostics |
| OpenXR quad or emulated quad | Varjo quad-view or Quad-Views-Foveated layer | Four views, moving inset, quad debug views |
| Visibility masks | OpenXR runtime with `XR_KHR_visibility_mask` | Mask lookup, mesh fetch, stencil stage, invalidation |
| OpenVR stereo | SteamVR/OpenVR runtime and HMD | OpenVR stereo regression only |

## Validation Scene Matrix
| Scene | Purpose | Required Evidence |
|---|---|---|
| `DesktopMono` | Non-XR regression and baseline | Screenshot, logs, settings, frame stats |
| `Stereo` | Stable VR oracle | Left/right captures, mirror, logs, profiler |
| `OpaqueDense` | Visibility, shadelets, cache efficiency | Forward+ and RVC captures, counters, RenderDoc |
| `AvatarMaterialDiverse` | Skinned, deformation, and material diversity | Visibility records, reuse rejection, quality report |
| `TransparencyFallback` | Forward+ companion path | Fallback counters, composite capture |
| `QuadView` | Wide/inset runtime behavior | Four submitted views, moving gaze captures, frame profile |

## Quality Thresholds

Use `RvcQualityToleranceSet.Default` unless a validation note records an approved override.
| Region | Max Error | Min SSIM | Max FLIP |
|---|---|---|---|
| Fovea | `1/255` | `0.995` | `0.010` |
| Guard band | `2/255` | `0.990` | `0.015` |
| Mid-field | `4/255` | `0.975` | `0.030` |
| Periphery | `8/255` | `0.940` | `0.060` |

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
