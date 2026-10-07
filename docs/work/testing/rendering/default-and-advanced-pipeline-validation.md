# Default And Advanced Pipeline Validation

Scope: Validate default and Advanced rendering, post processing, transparency, ambient occlusion, depth of field, antialiasing, atmospheric scattering, resource lifecycle behavior, shader source optimization, and forward depth-normal identity.

Architecture: [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md), [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md), [Transparency And OIT](../../../architecture/rendering/transparency-and-oit.md), [Render Pipeline Resource Lifecycle](../../../architecture/rendering/render-pipeline-resource-lifecycle.md), [Uber Shader Varianting](../../../architecture/rendering/uber-shader-varianting.md), [Atmospheric Scattering Component](../../../developer-guides/components/atmospheric-scattering.md), [Ambient Occlusion](../../../developer-guides/gi/ambient-occlusion.md)  Code todos: [Advanced Antialiasing TODO](../../todo/rendering/advanced-pipeline-antialiasing-todo.md), [Depth Of Field TODO](../../todo/rendering/default-pipeline-depth-of-field-todo.md), [Forward Depth-Normal TransformId TODO](../../todo/rendering/forward-depth-normal-transform-id-todo.md), [GPU Hotspots TODO](../../todo/rendering/optimization/default-pipeline-gpu-hotspots-todo.md), [Resource Lifecycle TODO](../../todo/rendering/render-pipeline-resource-lifecycle-todo.md), [Resolved Shader Source Optimization TODO](../../todo/rendering/resolved-shader-source-optimization-todo.md), [Sky And Atmosphere Follow-Ups TODO](../../todo/rendering/sky-and-atmosphere-followups-todo.md), [Transparency And OIT TODO](../../todo/rendering/transparency-and-oit-todo.md)

## Setup

Use task `Build-Editor` before live editor checks. Use task `Start-Editor-NoDebug` for the desktop editor. Use launch profile `Editor (Unit Testing World)` or set `XRE_WORLD_MODE=UnitTesting` for Unit Testing World checks. Use task `Start-Editor-UnitTesting-OpenXR-Monado-NoDebug` or `Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug` for OpenXR stereo checks when hardware is available.

Use `XRE_GL_DEBUG=1` for OpenGL debug output. Use `XRE_VULKAN_VALIDATION=1` for Vulkan validation layers. Use `XRE_ADVANCED_RENDER_PIPELINE_MODE=Disabled`, `Available`, `Required`, or `Diagnostic` to select Advanced behavior for new desktop cameras. Use `XRE_SHADER_SOURCE_OPTIMIZER=0` only to compare shader source optimizer behavior. Use task `Report-NewAllocations` for hot-path allocation audits.

## Checks

### Default And Advanced Pipeline Parity

Architecture: [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md), [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Pipeline A/B | Capture `DefaultRenderPipeline` and `AdvancedRenderPipeline` screenshots for deferred, forward, MSAA, FXAA, TSR, transparency, exact transparency, bloom, motion blur, depth of field, light probes, and debug views. | Differences match documented architecture. | Open | none |
| Advanced RenderDoc labels | Capture an `AdvancedRenderPipeline` frame in RenderDoc. | Each stage annotation is present and scoped. | Open | none |
| Exact transparency | Enable exact transparency. | PPLL and depth peeling render correctly or report a specific unsupported reason. | Open | none |
| Post effects | Toggle FXAA, bloom, weighted OIT, transparency overlays, motion blur, depth of field, TAA, TSR, tonemapping, color grading, fog, lens distortion, and chromatic aberration. | Each effect changes the image without resource errors. | Open | none |
| Light probes | Add light probes and enable probe debug views. | Probe GI and probe tetrahedra debug views render correctly. | Open | none |
| Debug views | Toggle each debug visualization at runtime. | No frame hitch from a command-chain rebuild. | Open | none |
| Backend promotion | Run Advanced on OpenGL, Vulkan, VR, and all resource-profile variants. | Each supported output binds or reports a rejection reason. | Open | none |

### Post-Process Editor

Architecture: [Post-Process Editor Extension](../../../developer-guides/rendering/post-process-editor-extension.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Rendering builds and tests | Run `dotnet build XREngine.Editor/XREngine.Editor.csproj`, `dotnet build XREngine.Runtime.Rendering/XREngine.Runtime.Rendering.csproj`, and `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter "FullyQualifiedName~Rendering"`. | Commands pass with no new warnings. | Open | none |
| Camera editor target selector | Select a scene node with a `CameraComponent`. | The camera editor shows the target pipeline selector and lists the active viewport pipeline first. | Open | none |
| Advanced target UI | Select `AdvancedRenderPipeline`. | Advanced stages are visible, and the Debug tab shows the shading debug view selector. | Open | none |
| Default target UI | Select `DefaultRenderPipeline`. | Default stages are visible. | Open | none |
| Per-pipeline state | Change a value for one target pipeline. | `CameraPostProcessStateCollection` keeps values for that pipeline ID only. | Open | none |
| Shading debug view | Change `ShadingDebugView` under Advanced. | The live viewport changes. | Open | none |
| Depth-of-field target editor | Drag a `TransformBase` or `SceneNode` into the depth-of-field focus target. | Focus tracking, undo, and redo work. | Open | none |
| Luminance weights | Click `Rec.709` for auto exposure weights. | Normalization, undo, and redo work. | Open | none |
| HDR output notice | Enable HDR output. | The tonemapping stage shows the bypass notice and disables its controls. | Open | none |

### Forward Lighting And Transform Identity

Architecture: [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Forward shader startup | Start the editor with task `Start-Editor-NoDebug`. | Shaders compile for PBR, transparent, hair, and skin forward materials. | Open | none |
| Surfel GI and forward scenes | Run task `Test-SurfelGi` and the forward-lighting visual-regression scenes. | No forward-lighting regressions. | Open | none |
| Many local lights | Capture GPU profiler traces for many shadow-casting local lights and active reflection probes. Compare to an earlier trace. | Forward-lighting cost is recorded and explained. | Open | none |
| Forward TransformId runtime | Run a scene with overlapping deferred and forward opaque or masked geometry. Inspect the `TransformId` debug output if available. | Forward geometry writes matching depth, normal, and transform ID in the shared prepass. | Open | none |
| Shader compile regression | Run shader compilation regression tests that cover common forward and deferred shaders. | Updated shaders compile and link without interface warnings. | Open | none |

### Transparency And OIT

Architecture: [Transparency And OIT](../../../architecture/rendering/transparency-and-oit.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Masked content | Load Sponza foliage, fences, lace, curtains, and fabric cutouts. | Cutouts render with correct depth and no blended artifacts. | Open | none |
| Opaque regression | Compare opaque content before and after transparency changes. | Opaque rendering is unchanged. | Open | none |
| Alpha-to-coverage | Start the editor with MSAA 2x, 4x, and 8x. Compare foliage edges to hard cutoff. | Edges are smoother with MSAA and fall back to hard cutoff when MSAA is off. | Open | none |
| Alpha-to-coverage motion | Move the camera around cutout content. | No shimmer or temporal instability appears. | Open | none |
| Alpha-to-coverage cost | Compare MSAA masked cost to standard masked rendering. | No measurable performance cliff exists. | Open | none |
| Weighted OIT glass | Render intersecting glass panes. | No order-dependent banding appears. | Open | none |
| Weighted OIT particles | Render layered particles. | Blending is smooth and does not pop. | Open | none |
| Weighted OIT bright background | Render translucent foliage over bright backgrounds. | Transparency does not wash out. | Open | none |
| Weighted OIT stereo | Compare both VR eyes. | Both eyes match. | Open | none |
| Weighted OIT A/B | Compare weighted OIT with the old `TransparentForward` path. | Differences are documented. | Open | none |
| Weighted OIT cost | Measure accumulate and resolve cost against the old sorted blend. | Cost is recorded. | Open | none |
| Exact quality | Compare exact mode with weighted OIT on glass, dense particles, and layered transparent content. | Quality and limitations are documented. | Open | none |
| Exact cost | Measure exact transparency memory and GPU time. | Cost is recorded and a ship or no-ship recommendation exists. | Open | none |
| Exact with GPU culling | Test exact transparency with GPU-driven culling active. | It works without CPU fallback. | Open | none |
| Stochastic prerequisites | Validate temporal rejection and camera jitter stability. | Ghosting is rejected before stochastic transparency is enabled. | Open | none |
| Stochastic convergence | Compare noise convergence over 4 to 16 frames. | Visual quality is compared to weighted OIT. | Open | none |
| Triangle sorting content | Identify and test the content class that needs triangle sorting. | The mode is justified by content that weighted OIT cannot handle. | Open | none |

### Depth Of Field

Architecture: [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| DoF baseline captures | Capture disabled DoF, artist DoF, physical DoF, near blur off, near blur on, and target-transform focus. | Baseline images exist for comparison. | Open | none |
| DoF baseline timings | Capture GPU timings for current full-resolution DoF at 1080p, 1440p, and Unit Testing internal resolution. | Timings include per-tap CoC recompute cost. | Open | none |
| DoF backend matrix | Record whether DoF compiles and renders in mono, stereo, OpenVR, and OpenXR. | Unsupported paths report a diagnostic. | Open | none |
| DoF live modes | Validate mono editor rendering with DoF disabled and enabled, physical mode, artist mode, near blur, and target-transform focus. | Focus distance and blur side are correct. | Open | none |
| DoF temporal order | Validate TAA and TSR with camera jitter and history stability. | No unstable history or incorrect focus appears. | Open | none |
| DoF bloom order | Validate bright highlights and bokeh highlights. | Ordering matches the documented pass order. | Open | none |
| DoF stereo policy | Validate stereo or the documented stereo diagnostic path. | Behavior matches the chosen policy. | Open | none |
| DoF performance | Compare GPU timings against baseline and run `Report-NewAllocations`. | The new path is faster at equal visible quality and adds no hot-path allocations. | Open | none |

### Advanced Antialiasing

Architecture: [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Vulkan mode transitions | After scene publication resumes, capture TAA, TSR, and MSAA from two viewpoints with still, moving, and cut comparisons. | Each mode changes mesh edges and keeps fresh scene color. | Open | 2026-09-22 |
| OpenGL stage recovery | Capture the first OpenGL rejected or aborted operation and retry after readiness. | A later frame recovers and renders fresh mesh color. | Open | 2026-09-22 |
| OpenGL generation progress | Follow one requested AA generation through materialization, activation, and retirement. | It becomes active or reports an explicit terminal result. | Open | 2026-09-22 |
| Raw MSAA sample capture | Use RenderDoc to inspect raw per-sample visibility, depth, and the canonical resolve. | Sample contents and sidecars are coherent. | Open | none |
| RenderDoc metadata warning | Reproduce or rule out the `without metadata: 100065` warning. | Cause is known or ruled out. | Open | none |
| DLAA Vulkan support | Run Vulkan DLAA on a configured vendor-capable setup. | It produces a valid final image or reports unsupported. | Open | none |
| Stereo AA | Validate ordinary stereo behavior. | Left and right eye histories and outputs are coherent. | Open | none |
| Quality comparison | Compare final TAA, TSR, MSAA, and DLAA mesh-edge quality, ghosting, and partial background coverage. | Quality sign-off records observed limits. | Open | 2026-09-24 |
| Live closeout | Run the narrowest integrated editor build and live editor path after fixes. Review logs. | No persistent validation errors remain. | Open | none |
### Ambient Occlusion And GPU Hotspots

Architecture: [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md), [Ambient Occlusion](../../../developer-guides/gi/ambient-occlusion.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| AO editor visibility | Validate editor visibility for every AO type. | User-facing AO modes are visible only where supported. | Open | none |
| HBAO+ quality | Capture indoor contact shadows, crevices, outdoor large-radius occlusion, alpha-tested foliage, screen borders, and biasing. | HBAO+ artifacts and quality are documented. | Open | none |
| HBAO+ cost | Measure GPU cost against SSAO and MVAO. | Default-readiness decision can use real cost. | Open | none |
| Non-HBAO readiness | Validate SSAO, MVAO, MSVO, GTAO, VXAO, and Spatial Hash AO. | A stable AO readiness matrix exists for user-facing modes. | Open | none |
| Spatial Hash AO motion | Move camera and geometry. Test edges, thin geometry, and low sample counts. | No visible ghosting or lagging artifacts. | Open | none |
| VXAO gates | Check shared voxel ownership, coverage, transforms, payload, update strategy, fine-detail fallback, and pipeline ownership. | VXAO remains research-only until all gates pass. | Open | none |
| Clean GPU baseline | Capture GPU dumps after disabling profiler UI and diagnostic logging overhead for desktop mono, OpenXR stereo, and mirror-off VR. | Hotspot ranking is based on clean captures. | Open | none |
| Hotspot settings | Record AO, exposure, light combine, MSAA, TSR, bloom, motion vectors, and stereo mode. | Each hotspot row is reproducible. | Open | none |
| Effect skips | Toggle or reduce effects. | Disabled or reduced effects remove corresponding GPU pass cost. | Open | none |
| GTAO VR profile | Validate GTAO resolution divisor and denoise settings in VR. | GTAO does not cost 5 to 8 ms in the standard VR profile. | Open | none |
| AO debug view | Compare AO quality and performance modes through debug views or screenshots. | Quality regressions are visible. | Open | none |
| Light combine profiling | Profile by light count, tile or cluster count, G-buffer format, MSAA state, and stereo behavior. | Light-combine costs are attributed. | Open | none |
| VR GPU budget | Capture a clean standard VR profile. | GPU time is below the selected headset budget or tradeoffs are listed. | Open | none |

### Resource Lifecycle

Architecture: [Render Pipeline Resource Lifecycle](../../../architecture/rendering/render-pipeline-resource-lifecycle.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Builds and focused tests | Build the editor and run focused resource lifecycle tests. | Commands pass with no new warnings. | Open | none |
| OpenGL and Vulkan live resize | Run Unit Testing World on OpenGL and Vulkan with MCP enabled. Resize the scene panel and main window. | No black, stale, or partially rebuilt frames appear. | Open | none |
| Feature toggles | Toggle internal resolution, HDR, AA modes, MSAA samples, bloom, AO, atmosphere, fog, transparency, temporal effects, and debug views. | Replacement generations remain complete and stable. | Open | none |
| Output profiles | Validate mono, stereo, scene capture, light probe, OpenVR, and OpenXR profiles where hardware allows. | Each profile uses correct resources and imported targets. | Open | none |
| Visual failure triage | Capture at least two camera positions for visual failures. | Scene rendering is separated from stale-resource sampling. | Open | none |
| Lifecycle logs | Inspect OpenGL, Vulkan, rendering, profiler, and resource-generation logs after each run. | Steady-state failures are separated from shutdown-only noise. | Open | none |
| RenderDoc triage | Use RenderDoc when screenshots and logs cannot identify a stale attachment, descriptor, layout, or target-selection error. | The failing resource is identified. | Open | none |
| Long resize soak | Run a long resize and feature-toggle session. | Physical resource, descriptor pool, generation, and retirement counts stay bounded. | Open | none |
| Steady-state churn | Observe a settled profile. | No routine resource churn occurs. | Open | none |
| Missing-resource errors | Review logs. | No missing declared resource warnings, skipped presentation, Vulkan validation errors, device loss, OOM, or silent CPU fallback appear. | Open | none |

### Resolved Shader Source

Architecture: [Uber Shader Varianting](../../../architecture/rendering/uber-shader-varianting.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Shader family compile | Compile optimized variants for deferred, forward, uber, compute, UI, shadow, and post-process shaders. | All representative families compile. | Open | none |
| OpenGL modes | Validate OpenGL combined-program and shader-pipeline modes. | Both consume optimized source correctly. | Open | none |
| Vulkan source rewriting | Validate Vulkan rewrites after generic optimization. | Vulkan compiles from resolved and optimized source. | Open | none |
| Cache behavior | Validate warm-cache and cold-cache behavior. | Shader identity remains stable and correct. | Open | none |
| Material textures | Confirm optimized reflection does not break material texture binding. | No fallback sampler noise appears for pruned inactive samplers. | Open | none |
| Sponza uber failure | Clear failed hashes and stale binary cache entries. Load the Sponza uber world that produced the timeout. | Large `Combined:*` sources shrink materially and no beige fallback sticks. | Open | none |
| Uber prewarm cost | Build with `Build-Editor`, open a scene that prepares many Uber variants, and compare `PrepareVariant` time and allocations. | No regression appears. | Open | none |

### Atmosphere And Sky

Architecture: [Atmospheric Scattering Component](../../../developer-guides/components/atmospheric-scattering.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Sky captures | Capture noon, sunset, and night before and after the atmosphere is enabled. | Sky output changes correctly. | Open | none |
| Fog order | Enable atmosphere and a volumetric fog volume. | Atmosphere composites before fog. | Open | none |
| Unit Testing atmosphere | Run Unit Testing World with `InitializeAtmosphericScattering` enabled. | Default atmosphere renders. | Open | none |
| Camera locations | Place the camera at ground level, high altitude, and space. Set sun to noon, sunset, and below horizon. | Each case renders correctly. | Open | none |
| Legacy sky modes | Validate skybox texture, gradient, solid-color, and dynamic-procedural modes. | Existing sky modes still render. | Open | none |
| Atmosphere timings | Capture GPU timings for sky, half-depth, half-scatter, reprojection, upscale, and composite. | Costs are recorded. | Open | none |
| Runtime logs | Read runtime logs. | No new OpenGL, shader, FBO, or texture warnings appear. | Open | none |
| OpenVR two-pass | Validate OpenVR two-pass stereo with atmosphere enabled. | Rendering is correct or unsupported diagnostics appear. | Open | none |
| Single-pass stereo | Validate OpenVR and OpenXR single-pass stereo when array variants exist. | Stereo variants render correctly. | Open | none |

### Vendor Upscaling

Architecture: [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md).

- [ ] Streamline modular runtime. Procedure: on supported NVIDIA hardware, run the Streamline vendor-upscale path on the modular runtime. Expected: the path initializes and renders, or reports an explicit unsupported diagnostic. Last evidence: none.

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
