# Global Illumination Validation

## Scope

This document owns manual, runtime, visual, hardware, profiler, benchmark, and soak checks for global illumination. It covers DDGI, the modular GI contract, Radiance Cascades, Surfel GI, light propagation volumes, voxel cone tracing, VXAO, ReSTIR GI, and Vulkan deferred/probe GI.

## Architecture Links

- [Global Illumination Ownership And Selection](../../../architecture/rendering/global-illumination-ownership.md)
- [Global Illumination Providers guide](../../../developer-guides/gi/global-illumination.md)
- [DDGI guide](../../../developer-guides/gi/ddgi.md)
- [Vulkan Renderer: Deferred And Probe Resource Rules](../../../architecture/rendering/vulkan-renderer.md#deferred-and-probe-resource-rules)
- [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)

## Code Todo Links

- [DDGI](../../todo/rendering/global-illumination/ddgi-implementation-todo.md)
- [Modular GI](../../todo/rendering/global-illumination/modular-gi-architecture-todo.md)
- [Radiance Cascades](../../todo/rendering/global-illumination/radiance-cascades-runtime-completion-todo.md)
- [Surfel GI](../../todo/rendering/global-illumination/surfel-gi-repair-todo.md)
- [LPV](../../todo/rendering/global-illumination/lpvgi-implementation-todo.md)
- [VCT and VXAO](../../todo/rendering/global-illumination/voxel-cone-tracing-and-vxao-implementation-todo.md)
- [Vulkan ReSTIR](../../todo/rendering/vulkan-restir-radiance-cache-gi-todo.md)
- [Vulkan Core Frame Loop Master TODO](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#deferred-and-probe-debug-dumps)

## Setup

- Build: task `Build-Editor`, or `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`. Rendering projects: `XREngine.Runtime.Rendering`, `XREngine.Runtime.Rendering.OpenGL`, `XREngine.Runtime.Rendering.Vulkan`.
- Live runs: a named isolated MCP session through `Tools/Manage-McpEditorSession.ps1 Start|Stop -Name <name>`. Use the Unit Testing World (`--unit-testing` or `XRE_WORLD_MODE=UnitTesting`). Capture and view PNGs from more than one camera position. Read the session logs (`log_vulkan.log`, `log_lighting.log`, `log_meshes.log`).
- Pipelines and backends: Default and Advanced, each on OpenGL and Vulkan. Stereo runs use the emulated VR path or task `Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug`.
- Contract tests: `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanDeferredProbeGiFixesTests"`, `--filter "FullyQualifiedName~VulkanDynamicRenderingMigrationTests"`, `--filter "FullyQualifiedName~DDGIScaffoldingContractTests"`. A broad `--filter Vulkan` run can time out because it includes heavy shader compiler tests.
- Surfel GI tests: task `Test-SurfelGi`.
- DDGI diagnostics: the MCP `list_render_pipeline_resources` response has a `ddgi` snapshot (completed updates, warm-up, cascade, scheduled probes, update time, cursors, geometry counts). `DDGIInterruptionDiagnostics.TryArm` forces an interrupted update in development builds. `DDGIManagedAllocationDiagnostics` reports per-command managed bytes in 240-sample windows.
- Use RenderDoc for unresolved pass, descriptor, or resource failures.

## Checks

### Probe GI On Vulkan (Deferred)

The last runtime attempt lost the Vulkan device during editor GPU BVH picking. GPU BVH picking is now OpenGL-only, and device-lost guards exist. Every check below needs a fresh run.

- [ ] Deferred Sponza parity. Procedure: Unit Testing World on Vulkan with deferred Sponza and light probes. Expected: geometry is visible and shaded, with parity to OpenGL. Last evidence: none.
- [ ] Probe GI contribution. Procedure: same run. Expected: probe capture completes and probe GI lights deferred surfaces; probe ambient is not black. Last evidence: none.
- [ ] Probe structural refresh. Procedure: wait for `[ProbeGI] structural refresh ready=N`. Expected: probe textures have valid contents and sampled layouts for light combine and Forward+. Last evidence: none.
- [ ] Clean Vulkan log. Procedure: read `log_vulkan.log`. Expected: no `Descriptor binding '...' could not be matched to an engine uniform`, no `Using fallback descriptor buffer for unresolved StorageBuffer binding '...'` for probe and Forward+ buffers, no `pass=-1` for `DeferredGBufferFBO` or `ForwardPassFBO` clears, no `Undefined` G-buffer layout at the first `BeginRendering`, and no `DispatchCompute skipped ... no active render-graph pass`. Last evidence: none.
- [ ] Forward over deferred. Procedure: same run. Expected: no forward content overwrites the deferred base color. Last evidence: none.
- [ ] Validation layers. Procedure: run with Vulkan validation layers. Expected: no layout or synchronization errors on the deferred and probe paths. Last evidence: none.
- [ ] Backend comparison. Procedure: capture the same scene on OpenGL and Vulkan. Expected: visually comparable output. Last evidence: none.

### Modular GI Contract

- [ ] Disabled and unsupported selections. Procedure: select `None`, then each unavailable mode, in Default and Advanced. Expected: an explicit plan diagnostic, no GI resources in the layout, and no GI graph work. Last evidence: none.
- [ ] Exactly-once contribution. Procedure: DDGI in both pipelines, debug view off and on. Expected: one composition per frame; probe diffuse suppressed only after the first valid result; probe specular kept. Last evidence: 2026-09-21 (Vulkan mono, both pipelines).
- [ ] Transitions. Procedure: change mode, settings and layout, camera cut, volume selection, resize, scene reload, pipeline cache clear, renderer-owner replacement. Expected: no stale generation, no mixed old layout and new settings, no black frame during warm-up. Last evidence: none.
- [ ] Two viewports and two volumes. Procedure: two cameras; two authored `DDGIVolumeComponent`s with equal and unequal `SelectionPriority`. Expected: separate histories; equal priority is rejected with both IDs. Last evidence: none.
- [ ] Composed OpenXR two-pass owner and minimal-output Advanced. Procedure: run each. Expected: no nested pipeline; minimal output rejects GI with a diagnostic. Last evidence: none.
- [ ] Allocation and timing. Procedure: full 240-sample allocation window with DDGI on Vulkan; compare with a pre-module baseline. Expected: zero recurring managed bytes per command; no per-ray or per-probe CPU dispatch; no extra scene preparation per eye. Last evidence: 2026-09-21, failed (1,248 to 4,360 bytes per command).
- [ ] Post-repair visual run. Procedure: Advanced Vulkan mono DDGI after the borrowed-image and staged-import repair; eight spaced captures. Expected: correct lighting with no black region or clipping. Last evidence: none (the earlier capture set was rejected).

### DDGI

Shared scenes: Cornell box color bleed, sealed versus open room, moving geometry through probe locations, color-changing key light, reflective interior with specular IBL.
| Check | OpenGL / Default | Vulkan / Default | OpenGL / Advanced | Vulkan / Advanced |
|---|---|---|---|---|
| Emission off and on, RGB, strength, emission maps | Passed | Passed | Passed | Passed |
| Mono composition, normal and reversed depth, two cameras | Passed | Passed | Passed | Passed |
| Stereo composition through FXAA, both eyes | Passed | Open (bloom view-mask fix needs a live repeat) | Passed | Passed |
| Desktop and stereo viewport ownership, node reactivation | Passed | Open | Passed | Passed |
| Point, spot, directional lights; sky off, on, off | Passed | Passed | Passed | Passed |
| Cutout and colored transmission (raster and ray transport) | Passed | Passed | Passed | Open |
| Bone-only and morph-only deformation | Passed | Passed | Passed | Passed |
| Volume suspension, two-cascade fairness, TSR scale 1.0, 0.5, 1.0 | Passed | Passed | Passed | Open |
| Constant-emission atlas normalization (exact, doubles at strength 2, zero when off) | Passed | Passed | Passed | Open |
| Inactive-probe bake upload and reactivation | Passed | Passed | Passed | Open |
| Bake revision 2: capture, load, source-off hold, dark and obsolete and missing asset, recovery | Passed | Open | Passed | Open |
| Renderer replacement, dynamic and baked | Passed | Open | Passed | Open |
| Scene detach and reattach | Passed | Open | Passed | Open |
| Embedded-probe escape within half-cell bounds | Passed | Open | Passed | Open |
| Single and mixed-probe occlusion (distance filter) | Passed | Open | Passed | Open |
| Eight-probe topology, cache clear, specular coexistence | Passed | Open | Passed | Open (BRDF lookup is zero) |
| Interrupted-update recovery with accepted abort receipts | Open | Open | Open | Open |
| Ten DDGI command scopes at zero managed bytes | Passed | Open | Passed | Open |

Last evidence for passed cells: 2026-09-21. Details: [DDGI working-copy verification](../../investigations/rendering/2026-09-20-ddgi-working-copy-verification.md).

- [ ] Interrupted update. Procedure: arm `DDGIInterruptionDiagnostics.TryArm` on one pipeline instance in each combination. Expected: skipped visibility-stage updates never publish or advance the completed-update counter, abort receipts are accepted, and the next complete update recovers. Last evidence: 2026-09-21 (Vulkan mono, both pipelines, targeted).
- [ ] Update transaction. Procedure: remove BVH geometry or make a DDGI shader unavailable. Expected: no atlas or history update; explicit diagnostic. Last evidence: none.
- [ ] Cascade indexing. Procedure: two or more cascades; capture per-cascade ray origins and probe state during partial updates. Expected: distinct ray origins and correct state ranges. Last evidence: none.
- [ ] BRDF reset hardening on Vulkan. Procedure: reject frames and change the renderer owner. Expected: a rejected writer is retried; an accepted writer with unknown completion is quarantined; availability is invalidated on owner or texture-epoch change. Last evidence: none.
- [ ] Shared scenes. Procedure: run the five shared scenes on each combination. Expected: color bleed, no leak in the sealed room, no dark or bright leaks from moving geometry, smooth key-light change, and specular IBL kept. Last evidence: none.
- [ ] Animated occluder. Procedure: animate a light and a skinned or blend-shape occluder. Expected: indirect light follows with no stale history. Last evidence: none.
- [ ] Per-cascade memory. Procedure: read `DDGIMemoryDiagnostics` for one to four cascades. Expected: sizes recorded for the budget decision. Last evidence: none.
- [ ] Build and shader compile. Procedure: editor build and expanded DDGI shader compilation after runtime changes. Expected: no errors or warnings. Last evidence: 2026-09-21.

### Radiance Cascades

- [ ] Live radiance. Procedure: select Radiance Cascades with an empty volume after the producer lands. Expected: radiance fills without pre-populated textures. Last evidence: none.
- [ ] Two hosts. Procedure: Default and Advanced on OpenGL and Vulkan; inspect screen output, each cascade, history, and final composition. Expected: equal output semantics, applied once. Last evidence: none.
- [ ] History. Procedure: camera cuts, movement, dynamic resolution, normal and reversed depth, mono and stereo, view-owner replacement. Expected: valid history in each case. Last evidence: none.
- [ ] Material response. Procedure: diffuse, metallic, and AO fixtures. Expected: correct response; probe diffuse suppressed only after a valid result. Last evidence: none.
- [ ] Cost. Procedure: measure update cost and managed allocations. Expected: zero recurring allocations. Last evidence: none.

### Surfel GI

- [ ] Baseline failure. Procedure: one repeatable editor scene; capture `DepthView`, `Normal`, `TransformId`, and `SurfelGITexture`. Record whether `TransformId` is blank, stale, or mismatched where surfels should spawn, and whether the scene is deferred, forward, or mixed. Expected: the failure is recorded. Last evidence: none.
- [ ] Mixed deferred and forward. Procedure: forward mesh occludes deferred geometry. Expected: surfels spawn on both with correct identity. Last evidence: none.
- [ ] Moving object. Procedure: moving-object scene. Expected: object-space surfels stay attached. Last evidence: none.
- [ ] Thin wall. Procedure: thin-wall scene. Expected: self-gathering and leaks recorded or absent. Last evidence: none.
- [ ] Composite targets. Procedure: standard and MSAA-deferred modes. Expected: the composite blends into the expected HDR target. Last evidence: none.
- [ ] Debug modes. Procedure: capture each debug and color mode through MCP. Expected: each mode renders. Last evidence: none.
- [ ] Unit tests. Procedure: task `Test-SurfelGi`. Expected: pass. Last evidence: none.

### Light Propagation Volumes

- [ ] OpenGL scenes. Procedure: Cornell-box bounce, large atrium, outdoor directional bounce, thin wall, moving hero spotlight. Expected: visible bounded bounce; leaks reduced by the geometry volume. Last evidence: none.
- [ ] Composition. Procedure: debug views for direct and indirect terms. Expected: direct diffuse, direct specular, emission, and LPV diffuse stay separate in HDR. Last evidence: none.
- [ ] Two hosts. Procedure: Default and Advanced, mono and stereo. Expected: the same output semantics. Last evidence: none.
- [ ] Vulkan. Procedure: LPV scenes with validation layers. Expected: no validation errors; output matches OpenGL debug output. Last evidence: none.
- [ ] Profile. Procedure: profile representative scenes per preset. Expected: per-stage GPU times and memory recorded for preset tuning. Last evidence: none.

### Voxel Cone Tracing And VXAO

- [ ] Voxel correctness. Procedure: voxel debug view on a known scene. Expected: occupied space, empty space, and material contributions are correct and explainable. Last evidence: none.
- [ ] Mip policy. Procedure: compare leaking and AO over-darkening with the chosen mip policy. Expected: results recorded. Last evidence: none.
- [ ] Budget. Procedure: measure memory at 64³, 128³, and one higher tier. Expected: numbers recorded. Last evidence: none.
- [ ] Stability. Procedure: camera motion, scene changes, dynamic objects with full rebuilds, thin walls. Expected: artifact classes recorded. Last evidence: none.
- [ ] Two hosts and stereo. Procedure: Default and Advanced, mono and stereo, including no work when not selected. Expected: equal output; correct stereo. Last evidence: none.
- [ ] VXAO. Procedure: off-screen occluder case; hybrid blend with screen-space detail. Expected: VXAO differs from screen-space AO; the hybrid restores contact detail. Last evidence: none.

### ReSTIR GI

- [ ] Capability. Procedure: start Vulkan on supported and unsupported hardware with validation layers. Expected: no feature-chain or extension errors; a precise capability miss on unsupported hardware. Last evidence: none.
- [ ] Acceleration structures. Procedure: minimal BLAS and TLAS smoke scene. Expected: no validation errors; fence-safe destruction; named builds in RenderDoc; TLAS instance count equals RT-eligible objects. Last evidence: none.
- [ ] Ray-query smoke. Procedure: Unit Testing World, move the camera. Expected: stable hit and miss output that follows the camera. Last evidence: none.
- [ ] RT pipeline smoke. Procedure: raygen, miss, closest-hit smoke on NVIDIA and AMD where available. Expected: debug output renders; SBT layout validates. Last evidence: none.
- [ ] Initial sampling. Procedure: Vulkan ReSTIR with reservoirs debug. Expected: nonzero stable reservoirs; missing TLAS, descriptor, or shader state gives a backend failure, not a black frame. Last evidence: none.
- [ ] Radiance cache. Procedure: run, then edit the scene. Expected: the cache persists and updates incrementally; invalidation is visible and bounded; no steady-state allocation. Last evidence: none.
- [ ] Temporal and denoise. Procedure: camera motion and a static scene. Expected: no smearing at disocclusions; static convergence; denoise work bounded by tile lists in a GPU capture. Last evidence: none.
- [ ] Mono and stereo. Procedure: mono editor, then stereo and the OpenVR path. Expected: stable mono output; no eye mismatch or history leak. Last evidence: none.
- [ ] Requested backend failure. Procedure: request a Vulkan backend on hardware without it. Expected: explicit failure; no silent OpenGL bridge use. Last evidence: none.
- [ ] Allocation review. Procedure: profiler pass over AS update, descriptor resolution, reservoirs, cache update, tile lists, and denoise. Expected: no hot-path allocations. Last evidence: none.

## Hardware Matrix
| Feature | NVIDIA | AMD | Intel |
|---|---|---|---|
| Vulkan ReSTIR RT pipeline and SBT | Open | Open | Open |

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
| DDGI eight-probe specular, Vulkan / Advanced | BRDF lookup is all zero; reflection array is nonzero | [DDGI TODO: Vulkan](../../todo/rendering/global-illumination/ddgi-implementation-todo.md#vulkan) |
| Modular GI allocation and timing | 1,248 to 4,360 managed bytes per DDGI command per frame | [Modular GI TODO: Performance](../../todo/rendering/global-illumination/modular-gi-architecture-todo.md#performance) |
| DDGI reversed-depth captures | Editor grid draws through geometry | [DDGI TODO: Related Renderer Defects](../../todo/rendering/global-illumination/ddgi-implementation-todo.md#related-renderer-defects) |
