# Vulkan Core Validation

## Scope

This document owns Vulkan core runtime validation. It covers startup, descriptor and material state, resource lifetime, swapchain and resize behavior, frame contracts, synchronization, OpenXR handoff, resident recording, source structure, unsafe data, Advanced pipeline cohorts, dynamic rendering, modern Vulkan backends, stall remediation, benchmarks, and promotion checks.

Architecture: [Vulkan Renderer](../../../architecture/rendering/vulkan-renderer.md), [Vulkan Command Recording](../../../architecture/rendering/vulkan-command-recording.md), [Frame Loop Design](../../../architecture/rendering/frame-loop-design.md), [Vulkan Scene Preparation And Publication](../../../architecture/rendering/vulkan-scene-preparation-and-publication.md), [Vulkan Pipeline Compilation](../../../architecture/rendering/vulkan-pipeline-compilation.md), [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md), [OpenXR VR Rendering](../../../architecture/rendering/openxr-vr-rendering.md).

Code todos: [Vulkan frame-loop master](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md), [Vulkan core hardening](../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md), [Vulkan OpenXR and Advanced rendering](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md), [Vulkan stall separate findings](../../todo/rendering/vulkan-stall-separate-findings-todo.md).

Related validation: [Vulkan Backend Parity Validation](vulkan-backend-parity-validation.md), [Window And Render Thread Validation](window-and-render-thread-validation.md). Diagnostics how-to: [Vulkan Diagnostics](../../../developer-guides/rendering/vulkan-diagnostics.md).

## Setup

Use the smallest run mode that can show the behavior. Do not run the editor from this document during documentation-only work.

Tasks from `.vscode/tasks.json`:
| Task | Purpose |
|---|---|
| `Build-Editor` | Build the editor before a runtime validation run. |
| `Start-Editor-NoDebug` | Start the default editor world outside the debugger. |
| `Start-Editor-RendererDevelopment-NoDebug` | Start the editor with renderer-development mode. |
| `Start-Editor-UnitTesting-OpenXR-Monado-NoDebug` | Start the Unit Testing World with Monado OpenXR. |
| `Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug` | Start the Unit Testing World with SteamVR OpenXR. |
| `Test-VulkanPhase3-Regression` | Run the Vulkan frame-loop regression cohort. |
| `Benchmark-Vulkan-Quick` | Run a short Vulkan benchmark. |
| `Benchmark-Vulkan-Clean-Desktop` | Run a clean desktop Vulkan benchmark. |
| `Benchmark-Vulkan-Clean-OpenXR` | Run a clean OpenXR Vulkan benchmark. |
| `Benchmark-Vulkan-Compare-Desktop` | Compare desktop Vulkan benchmark data. |
| `Benchmark-Vulkan-Gate` | Run the Vulkan benchmark gate. |

Launch profiles from `.vscode/launch.json`:
| Profile | Purpose |
|---|---|
| `Editor (Default World)` | Run the editor with the default world. |
| `Editor (Renderer Development)` | Run the editor with `--renderer-development`. |
| `Editor (Unit Testing World)` | Run the Unit Testing World with `XRE_WORLD_MODE=UnitTesting`. |
| `Editor (Unit Testing OpenXR SteamVR)` | Run the Unit Testing World with SteamVR OpenXR variables. |
| `Editor (Unit Testing World, Validation Layers)` | Run the Unit Testing World with Vulkan and OpenGL validation variables. |

Run modes:

```powershell
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -- --unit-testing
```

For Unit Testing World runs, set `Rendering.RenderBackend` to `Vulkan` in `Assets/UnitTestingWorldSettings.jsonc`. Regenerate settings and schema with `Tools/Generate-UnitTestingWorldSettings.ps1` after a settings type change.

Render-target mode smoke:

```powershell
$env:XRE_VK_RENDER_TARGET_MODE='DynamicRendering'
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -- --unit-testing
$env:XRE_VK_RENDER_TARGET_MODE='LegacyRenderPass'
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -- --unit-testing
Remove-Item Env:XRE_VK_RENDER_TARGET_MODE
```

Useful environment variables:
| Variable | Purpose |
|---|---|
| `XRE_VULKAN_VALIDATION=1` | Enable Vulkan validation layers. |
| `XRE_VULKAN_SYNC_VALIDATION=1` | Enable synchronization validation. |
| `XRE_VK_RENDER_TARGET_MODE=DynamicRendering|LegacyRenderPass|Auto` | Select the Vulkan render-target mode for one process. |
| `XRE_VULKAN_BINDLESS_MATERIAL_MODE` | Select the bindless material policy. |
| `XRE_FORCE_SWAPCHAIN_MAGENTA=1` | Force a magenta swapchain clear for presentation triage. |
| `XRE_SKIP_IMGUI=1` | Skip ImGui overlay recording. |
| `XRE_SKIP_UI_PIPELINE=1` | Skip screen-space UI pipeline recording. |

Fast software checks do not replace manual validation:

```powershell
dotnet build XREngine.Editor/XREngine.Editor.csproj --nologo -v minimal
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter VulkanP0ValidationTests --nologo -v minimal
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter VulkanP1ValidationTests --nologo -v minimal
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter VulkanTodoP2ValidationTests --nologo -v minimal
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter VulkanShaderCompilationRegressionTests --nologo -v minimal
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter VulkanShaderPreprocessParityTests --nologo -v minimal
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter VulkanDesktopFrameLoopPolicyTests --nologo -v minimal
```

Tools:

- Use `Tools/Manage-McpEditorSession.ps1 Start|Stop -Name <name>` for isolated editor sessions.
- Use `Tools/Invoke-Mcp.ps1 -Session <name> -Method <method>` for MCP calls.
- Use `Tools/Measure-VulkanFrameLoop.ps1` for frame-loop measurements.
- Use `Tools/Collect-VulkanLifecycleEvidence.py` for lifecycle evidence.
- Use `Tools/Reports/VulkanCoreStructuralExceptions.json` for source-structure exceptions.
- Use `Install-Monado`, `Test-OpenXR-Monado-Smoke`, and `Start-Editor-UnitTesting-OpenXR-Monado-NoDebug` for Monado lanes.

Historical hardening evidence files in this folder:

- [vulkan-core-hardening-phase21-validation-2026-07-09.json](vulkan-core-hardening-phase21-validation-2026-07-09.json)
- [vulkan-core-hardening-phase4-validation-2026-07-09.json](vulkan-core-hardening-phase4-validation-2026-07-09.json)
- [vulkan-core-hardening-phase5-validation-2026-07-09.json](vulkan-core-hardening-phase5-validation-2026-07-09.json)
- [vulkan-core-hardening-phase521-validation-2026-07-09.json](vulkan-core-hardening-phase521-validation-2026-07-09.json)
- [vulkan-core-hardening-phase522-validation-2026-07-09.json](vulkan-core-hardening-phase522-validation-2026-07-09.json)
- [vulkan-core-hardening-phase523-validation-2026-07-10.json](vulkan-core-hardening-phase523-validation-2026-07-10.json)
- [vulkan-core-hardening-phase524-validation-2026-07-10.json](vulkan-core-hardening-phase524-validation-2026-07-10.json)
- [vulkan-core-hardening-phase524a-validation-2026-07-10.json](vulkan-core-hardening-phase524a-validation-2026-07-10.json)
- [vulkan-core-hardening-phase524b-validation-2026-07-10.json](vulkan-core-hardening-phase524b-validation-2026-07-10.json)

### Stall Change Protocol

Use this procedure for each stall or frame-loop performance change. Results history is in [Vulkan Stall Remediation Results](../../progress/rendering/vulkan-stall-remediation-results.md).

1. Validate one change at a time. Do not start the next change until the current change passes its build, live behavior, performance, and regression checks.
2. Before you edit, record the entry evidence, the owning code path, one hypothesis that a check can reject, the affected workloads, and the acceptance budget. Use the measured baseline variability to set the budget.
3. Record thread ownership, lock order, cancellation, publication, and retirement dependencies before you change concurrent or native lifetimes.
4. Build the owning project with no new warnings. Then run the isolated editor path. A build, a quiet log, or `git diff --check` alone does not close a runtime change.
5. Run the original trigger and the cold, warm, pending, failure, mutation, and lifetime cases. View the saved images when the change affects rendering or UI.
6. Compare baseline and changed captures under matching conditions. Confirm that the target mechanism changed, the budget passed, and no neighboring stage took the removed cost.
7. Check regressions, warnings, allocation and retention growth, and teardown. Explain each new diagnostic.
8. Record pass or fail, evidence, and remaining risks.

Failure rules:

- If a check fails, stop. Repair the same change and run its checks again. If the check rejects the hypothesis, record the result and find the next owner.
- A flaky or unclear result is not a pass. Repeat with a better capture, or mark the check Blocked.
- If the entry condition is false, mark the item Deferred with the evidence and the condition to reopen it.
- Roll back only the edits of the failed change. Do not reset the worktree or switch branches to get a baseline.
- After a change to instrumentation or measurement settings, capture a new comparable baseline.
- Do not add or change regression tests until live validation passes and the user gives test clearance.

Comparable workload rules:

- Record the source revision, local diff, binary hashes, build configuration, device, driver, power state, backend, submission mode, validation layers, debugger, observers, and logging.
- Record scene readiness, camera path, output and internal resolution, antialiasing mode, executed features, and native draw coverage. A HUD draw count is not the full native workload.
- Keep cold process, persisted-cache restart, warmed stationary, and controlled motion conditions separate. Use at least three matched runs per condition and at least 60 seconds per warmed window. Do not delete user caches to force a cold run.
- Record all-frame counts, p50, p95, p99, max, successful presents, missing GPU samples, dropped events, queue delay, loading time, and backlogs. Count fresh frames from `frame_lifecycle.outcome_counts.completed`.
- Follow the [frame-loop measurement contract](../../../architecture/rendering/frame-loop-design.md#measuring-the-frame-loop). For detailed attribution, account for at least 99% of the root interval and show each unattributed gap of 50 µs or more.
- Do not add overlapping CPU and GPU intervals. Do not subtract unrelated medians to get an exclusive cost.

## Checks

### Smoke

Procedure: run the default editor world and the Unit Testing World on Vulkan with `XRE_VULKAN_VALIDATION=1`. Pass means visible 3D content, visible editor UI, no validation errors, no new warnings, no dropped draw or compute operations, and no missing scene swapchain writer frames.

- [ ] Default editor world renders. Expected: visible 3D scene and ImGui overlays. Last evidence: none.
- [ ] Unit Testing World renders on Vulkan. Expected: same pass criteria. Last evidence: none.
- [ ] Repeat both worlds with `SyncBackend = Sync2` and compare with legacy sync on the same camera. Expected: visually equal. Last evidence: none.
- [ ] Descriptor pool reset and reuse runs under validation layers. Expected: no VUIDs. Last evidence: none.
- [ ] Explicit `DynamicRendering` and `LegacyRenderPass` start in both worlds under standard and synchronization validation. Expected: both render with no VUIDs. Last evidence: none.

### Descriptors And Dynamic Uniforms

Procedure: use a stress scene with high material and compute diversity. Read the profiler descriptor summaries.

- [ ] Exhaust transient render and compute descriptor pools on hardware. Expected: pools reset and recover with no use-after-free under validation. Last evidence: none.
- [ ] Measure descriptor update CPU time with `DescriptorUpdateBackend = Legacy` and `Template`. Expected: both values reported and bounded. Last evidence: none.
- [ ] Record descriptor fallback and failure summaries. Expected: fallback counters stay at zero after material and texture warmup in default scenes. Last evidence: none.
- [ ] Profile the dynamic UBO ring off and on. Compare descriptor update CPU time, dynamic UBO allocations, allocated KB, and exhaustion counts. Expected: keep the ring on only if the CPU reduction is measurable and there are no validation errors. Last evidence: none.

### Resource Lifetime And Memory Pressure

- [ ] Allocate enough resources to grow device-local suballocator blocks, and use host-visible upload pools, host-cached readback pools, and dedicated-allocation paths. Expected: no validation errors. Last evidence: none.
- [ ] Force an out-of-memory fallback. Expected: diagnostic fallback without device loss. Last evidence: none.
- [ ] Watch allocation count in normal editor workflows. Expected: well below the hardware limit. Last evidence: none.
- [ ] Run a long resize and recreate session. Expected: no leaked images, buffers, views, framebuffers, descriptor pools, or retired-resource buildup. Last evidence: none.
- [ ] Run a long editor session with validation layers. Expected: fence-retired resource-plan replacement is validation-clean. Last evidence: none.
- [ ] Timeline- and fence-retired resources are destroyed only after the owning frame slot completes. Procedure: GPU capture plus retirement telemetry. Last evidence: none.

### Swapchain, Presentation, And Resize

- [ ] Resize, maximize, restore, and minimize repeatedly with validation layers. Expected: debounced recreation does not reuse stale command buffers. Last evidence: none.
- [ ] Force `AcquireNextImage` `NotReady`, `Suboptimal`, and out-of-date results. Expected: each path recovers. Last evidence: none.
- [ ] Capture a screenshot after resize. Expected: it matches the visible frame. Last evidence: none.
- [ ] Capture a dynamic-rendering frame. Expected: the swapchain image moves to `PresentSrcKhr` exactly once per submitted frame. Last evidence: none.
- [ ] Run at least 30 minutes of interactive resize with random drag extents, maximize and restore, minimize and restore, monitor or DPI moves, and concurrent camera, light, asset, and editor activity. Expected: no crash, hang, device loss, mixed extent or generation, or unbounded recreation. Last evidence: none.
- [ ] Drag the window border on the native window (modal drag loop). Expected: rendering continues and recovers after release. Programmatic resize does not cover this check. Last evidence: none.

### Steady-State Frame Contract

Procedure: Release build, warm caches, default and Sponza scenes, profiler counters.

- [ ] `GpuIndirectZeroReadback` and `GpuMeshletZeroReadback` steady frames. Expected: zero readback bytes, buffer maps, query retrievals, diagnostic copies, and readback waits. Last evidence: none.
- [ ] Strict Vulkan GPU-driven profiles. Expected: no implicit CPU mesh fallback. Last evidence: none.
- [ ] Renderers that preparation diagnostics mark not ready. Expected: no command recording for them. Last evidence: none.
- [ ] Descriptor resolution, material row lookup, vertex input generation, draw-operation emission, and barrier planning. Expected: zero managed allocation per frame after warmup. Last evidence: none.
- [ ] Resize, recreate, and retirement paths. Expected: no `vkDeviceWaitIdle` except explicit emergency or device-lost recovery. Last evidence: none.
- [ ] Each instrumented strategy compared with its zero-readback pair. Expected: same visual, draw, and task identity, with bounded source-tagged diagnostics and no current-frame waits. Last evidence: none.
- [ ] Saturate the diagnostic readback ring. Expected: only diagnostics drop. With diagnostics off, there are no reservations, copies, decoder tasks, or variants. Last evidence: none.

### Descriptors, Materials, And Pipelines

- [ ] Descriptor-indexed material texture arrays. Expected: the same logical material table as OpenGL bindless handle tables. Last evidence: none.
- [ ] Dynamic material row layouts, layout hashes, and generated shader variants. Expected: they match the pass-declared material binding layout. Last evidence: none.
- [ ] Pipeline prewarm in default editor workflows. Procedure: [prewarm workflow](../../../developer-guides/rendering/vulkan-diagnostics.md#pipeline-prewarm-workflow). Expected: pipeline miss summaries reach zero after warmup. Last evidence: none.
- [ ] Inspect pipeline keys in a capture. Expected: keys include render-pass formats, MSAA state, dynamic rendering formats, shader identity, material layout hash, descriptor layout, specialization and push-constant axes, and render-state axes. Last evidence: none.
- [ ] Warm pipeline compatibility keys. Expected: bounded by actual format, sample, and view-mask permutations with zero steady compile-required misses. Last evidence: none.

### Synchronization And Queue Overlap

- [ ] Compare Sync2 and legacy sync captures. Expected: equal output, and Sync2 shows no extra waits, redundant ownership transfers, or full-pipeline barriers. Last evidence: none.
- [ ] GPU captures of default scenes. Expected: avoidable layout transitions and full-frame barriers are removed or justified. Last evidence: none.
- [ ] Stress async compute and transfer overlap on hardware with separate queue families. Expected: queue-family ownership transfers are validation-clean. Last evidence: none.
- [ ] Prototype split barriers (`vkCmdSetEvent2`, `vkCmdWaitEvents2`) only for measured candidates. Expected: adopt only where captures show a real latency benefit. Last evidence: none.

### Tiler And Bandwidth

- [ ] Transient or lazily allocated attachments are used where the hardware supports them. Last evidence: none.
- [ ] Color and depth load operations have no avoidable full-frame loads, and store operations do not store attachments that are not read later. Last evidence: none.
- [ ] Measure bandwidth on MSAA resolve, bloom, temporal accumulation, and light probe passes. Last evidence: none.
- [ ] Resize and recreate do not force unnecessary layout clears or full attachment reloads. Last evidence: none.

### Vulkan-Only Acceleration

- [ ] At least one production Vulkan draw path consumes a resolved `VkDataBuffer.DeviceAddress` for a scene-database buffer. Last evidence: none.
- [ ] Vulkan meshlet dispatch uses `VK_EXT_mesh_shader` with GPU-written task records and indirect-count dispatch on supported hardware. Last evidence: none.
- [ ] Memory decompression, indirect copy, sparse residency, and ray tracing stay gated and report diagnostics when unavailable. Expected: no silent CPU fallback. Last evidence: none.
- [ ] Ray tracing works when available and degrades with a diagnostic when unsupported. Last evidence: none.
- [ ] XeSS native Vulkan path. See ARP-V63. Last evidence: none.

### XR And VR On Vulkan

- [ ] Multiview capability logging, stereo render path, OpenXR path, and SteamVR/OpenVR path. Last evidence: none.
- [ ] Mirror-to-window output while in VR. Expected: synchronized with the submitted eye frame, with no extra GPU readback or queue idle. Last evidence: none.
- [ ] VR render-target array layers, layouts, and attachment metadata. Expected: validation-clean. Last evidence: none.
- [ ] OpenXR and OpenVR compositor submission timing. Expected: acquire, render, submit, and present or compositor stages are recorded separately. Last evidence: none.
- [ ] Stereo and multiview frame diagnostics. Expected: the same fields as mono (scene writer count, dropped operations, descriptor fallback, pipeline miss summary). Last evidence: none.
- [ ] Run the OpenXR paths against a live supported runtime after each frame-loop structural change. Last evidence: none.

<a id="openxr-submission-and-lifecycle-checks"></a>

### OpenXR Submission And Lifecycle

Procedure: Monado through the Monado tasks, then one hardware runtime. Entry points: `OpenXrVulkanSubmissionTracker`, `VulkanCommandRuntime.OpenXrSubmission`, `VulkanXrGraphicsBinding`, `OpenXRAPI.SwapchainLifecycle`. Evidence: [mirror and placement investigation](../../investigations/rendering/arp-mirror-placement-2026-09-14.md), [OpenXR and Advanced investigation](../../investigations/rendering/vulkan-phase67-implementation.md). Service fault injection needs an owned Monado service.

- [ ] XR-V04: run the three-command `[left, right, publish]` path. Expected: both eye renders and the publish command are in the accepted receipt and retire once. Last evidence: none.
- [ ] XR-V05: external-target submission. Expected: output captures and exact ownership and settlement for its path from the XR-A01 inventory. Last evidence: none.
- [ ] XR-V09: change eye resolution during rendering. Expected: safe in-session replacement, dimension read-back, pacing resumes, and no device-wide idle. Test the configured runtime-refresh exception separately. Last evidence: none.
- [ ] XR-V10: force replacement failure after detachment. Expected: rendering or recreation resumes, or a safe terminal outcome, with no leaked partial children. Last evidence: none.
- [ ] XR-V11: repeat session start, stop, and restart. Expected: new output, zero invalid-handle destruction, and zero ownership and retirement counts after each cycle. Last evidence: none.
- [ ] XR-V20: LOSS_PENDING with pending work. Expected: safe session and child retirement and a documented recovery outcome. Last evidence: none.
- [ ] XR-V21: device-loss teardown. Expected: explicit abandonment, and no stale ownership reported as normal completion. Last evidence: none.
- [ ] XR-V13: correlate frame ID and display time, queue-submit interval, completion and forced wait, in-flight age, and per-eye image reuse age. Expected: the trace matches receipt values with no synthetic provenance. Last evidence: none.
- [ ] XR-V15: measure pressure recovery waits against the XR deadline. Expected: short counted waits only after safe reuse or defer paths, truthful missed, late, and reprojected counters, and preserved `xrWaitFrame` pacing. Last evidence: none.
- [ ] XR-V16: run desktop and XR together. Expected: nonblocking desktop acquire, no transferred compositor or completion stall, and per-output timing and captures. Last evidence: none.
- [ ] XR-V17: one hardware runtime. Expected: named runtime and device, eye output, lifecycle evidence, and a documented release-before-completion fallback. Record if a fence-ring fallback is necessary. Last evidence: none.
- [ ] F0-08: exact OpenXR deadline and fallback behavior on Monado and one hardware runtime. Last evidence: none.

### OpenXR Stereo And Desktop Regression

Procedure: physical Vulkan/OpenXR strict stereo with the CpuDirect Sponza fixture, and Monado. Evidence: [hardware record](../../investigations/rendering/2026-10-03-retained-rendering-hardware.md), [Monado investigation](../../investigations/rendering/2026-10-05-vulkan-stall-monado.md). Simulator results do not close headset and comfort checks.

- [ ] Both eyes through head motion and Sponza and sky transitions. Expected: correct freshness, eye assignment, orientation, projection, history, exposure, and no flicker or ghosting. Get headset and comfort feedback. Last evidence: none.
- [ ] On the headset, check whether the reported TSR ghosting changed after the history-depth order fix. Last evidence: none.
- [ ] Repeat enable and disable, resize, visibility and focus changes, failure, and normal teardown. Expected: balanced image ownership, drained retired generations, bounded retention, no device loss, and no silent sequential fallback. Last evidence: 2026-10-05 (Monado scoped pass only).
- [ ] Attribute warm pacing, black or no-layer output, and frame-data-slot refusals. Separate CPU, GPU completion, reservation, and slot ownership. Last evidence: none.
- [ ] Measure manifest changes and cursor resets through cold and warm startup and Play entry. Expected: no long `No compatible Vulkan render program is available yet` plateau after a fresh Play copy. Last evidence: none.
- [ ] Attribute the recorded 24-second `Renderer.RenderWindow` stall at exit and the solid-magenta Sponza wall in a third-entry eye preview. Last evidence: none.
- [ ] XR process memory. Expected: at or below 8 GB. Last evidence: 2026-10-05, 13.5 GB private and 5.52 GB device-local (fails).
- [ ] Run the OpenGL/OpenVR hardware path. Expected: backend-specific results recorded before shared XR acceptance closes. Last evidence: none.

### Desktop Frame Loop

<a id="from-vulkan-runtime-code-organization-todomd"></a>

- [ ] Run the targeted tests, the runtime-rendering build, the editor build, a Vulkan startup, and the OpenXR smoke lanes after a structural change. Expected: no new warnings or validation errors. The SteamVR lane needs a connected HMD; without one, `xrGetSystem` returns `ErrorFormFactorUnavailable`. Last evidence: none.
- [ ] Measure allocations for the frame attempt and stage results, and inspect generated code where needed. Expected: no context copy, boxing, closure, task, or heap allocation per frame. Last evidence: none.
- [ ] Review migrated source-contract tests. Expected: no test passes only because it stopped reading moved code. Last evidence: none.
- [ ] Start the Vulkan editor and the Vulkan Unit Testing World with MCP. Capture and view at least two camera positions. Compare final output, ImGui, dynamic text, viewport size, and startup presentation with a known-good build. Last evidence: none.
- [ ] Exercise texture uploads and command-buffer invalidation while frames record. Use fault injection for rare deferred or dirty paths. Last evidence: none.
- [ ] Run OpenXR Vulkan with the desktop mirror active. Expected: OpenXR never drains the active desktop slot, and session start does not race desktop callback completion. Cross-check MCP captures with a native-window or eye-preview capture. Last evidence: none.
- [ ] On supported NVIDIA hardware, run native and Streamline/DLSS frame-generation swapchains. Record unsupported hardware as not validated. Last evidence: none.
- [ ] Force acquire and present out-of-date, suboptimal, surface-lost, timeout or not-ready, submit failure, and device loss. Use fault injection where a real driver event is not deterministic. Expected: each result maps to its typed outcome. Last evidence: none.
- [ ] Run `Tools/Measure-VulkanFrameLoop.ps1` with a fixed scene and settings. Compare p50/p95/p99, allocations, queue-submit count, present time, and retired-resource backlog. Last evidence: none.
- [ ] Run an OpenVR smoke, or record why the desktop frame loop cannot affect that path. Last evidence: none.
- [ ] For desktop camera-motion acceptance, compare an independent wall-clock `frame_outputs.frame_id` delta with the reported render rate with no input. Expected: `scene_rendered=true`, `work_disposition=FreshRender`, `skipped=false`. Exclude screenshot and readback intervals. Last evidence: none.

<a id="from-vulkan-frame-loop-performance-todomd"></a>

### Frame-Loop Rendering Regressions

- [ ] Deferred Sponza with no light probes. Expected: the global ambient term lights dark regions. Last evidence: none.
- [ ] Move and settle the camera with the directional shadow audit on, and capture the directional atlas. Expected: no one-frame `DirectionalShadowAtlasEnabled=false` or lit-fallback flash. Last evidence: none.
- [ ] Capture a performance run with `XRE_VULKAN_BINDLESS_MATERIAL_MODE=Required`, `BindlessMaterialTable`, and `GpuIndirectZeroReadback`. Last evidence: none.
- [ ] Run a multi-minute camera-navigation soak in the Unit Testing World. Expected: no device loss and no black frames during motion. Last evidence: none.
- [ ] Instanced debug primitives. Expected: same orientation on Vulkan and OpenGL. Last evidence: none.
- [ ] Capture a RenderDoc frame of a slow settled frame. Export the shadow atlas and cascades, GTAO raw, blur, and AO, lighting accumulation, bloom mips, TSR output, and final output. Last evidence: none.
- [ ] Record Release A/B evidence for OpenGL and Vulkan with the same scene, camera, viewport size, render scale, mesh strategy, GPU clock policy, and warm caches. Last evidence: none.
- [ ] After a shadow-atlas reuse change, capture CPU and GPU profiler dumps. Expected: `Lights3DCollection.UpdateShadowAtlasRequests`, `RenderWorkLastShadowMs`, and render-thread p95 decrease. Last evidence: none.
- [ ] If the editor exits unexpectedly, capture the Windows event log, engine logs, and GPU watchdog timing at once. Last evidence: none.
- [ ] Move the camera near and far from Sponza on Vulkan. Expected: textures stay colored, `promoted>0`, and no upload device loss or validation errors. Last evidence: none.

<a id="from-vulkan-headless-mcp-component-profiling-todomd"></a>

### Component Profiling And RenderBench

Guide: [Dedicated Vulkan RenderBench](../../../developer-guides/diagnostics/profiler.md#dedicated-vulkan-renderbench). Evidence: [Vulkan Component Profiling](../../progress/rendering/vulkan-component-profiling.md). Code items: [Editor Profiler And UI Render Cost TODO](../../todo/rendering/optimization/editor-profiler-ui-render-cost-todo.md).

- [ ] Record editor-process baselines for one static Deferred cohort, one forced-dirty recording cohort, and one RVC cohort. Expected: the baselines quantify the editor, window, and observer costs that RenderBench removes. Last evidence: none.
- [ ] Measure MCP-disabled, MCP-idle, and MCP-active overhead on the same cohort. Expected: MCP-idle and MCP-disabled are equal within the observer-overhead threshold. Last evidence: none.
- [ ] Record the effect of dense timestamps on primary and secondary dirty reasons, records, reuse, CPU time, and frame pacing. Last evidence: none.
- [ ] Run the same render-graph fixture in presentationless and desktop modes. Expected: stable output identity within documented format differences. Last evidence: none.
- [ ] Presentationless steady state. Expected: no managed allocation, resource creation, shader compilation, or device-wide wait unless the recipe requests churn. Aggregate CPU stage mode keeps its observer budget. Last evidence: none.
- [ ] Measure timestamp observer overhead for small and large passes on controlled hardware. Expected: measured overhead that does not contaminate clean captures. Last evidence: none.
- [ ] Escalate one component recipe through the subsystem fixture, presentationless, desktop WSI, and the required OpenXR cohort. Last evidence: none.
- [ ] Show one accepted optimization whose component improvement also reduces full-frame p95, with no regression in correctness, allocation, readback, churn, or tail latency. Last evidence: none.

<a id="presentation-independent-renderer"></a>

### Presentation-Independent Renderer

Architecture: [Presentation-Independent Hosts](../../../architecture/rendering/vulkan-renderer.md#presentation-independent-hosts). Progress and accepted baselines: [presentation-independent refactor progress](../../progress/rendering/vulkan-presentation-independent-renderer-refactor-progress.md). Accepted baseline (2026-08-13): 64x64 presentationless RenderBench with zero capture-thread and fixture-worker allocations; three stable windowless `DefaultRenderPipeline` lifecycles; desktop readbacks on queue slots 0 and 1 without diagnostics. RenderDoc does not write an `.rdc` for presentationless runs because there is no WSI frame boundary.

- [ ] Run presentationless Deferred and Uber fixtures and desktop equivalents with the same scene, camera, resolution, format, seed, and frame count. Expected: output identity matches within documented format and color-space differences. Last evidence: 2026-08-13 (presentationless only).
- [ ] Run standard and synchronization validation on those fixtures. Expected: no new messages. Last evidence: none.
- [ ] Inspect logs. Expected: presentationless has no surface, swapchain, acquire, or present operation; headless WSI has acquire and no-op present; desktop has compositor presentation. Last evidence: none.
- [ ] Exercise desktop resize, minimize and restore, HDR selection, and surface-loss recovery. Last evidence: none.
- [ ] Exercise OpenXR session start, frame acquisition and release, and shutdown. Last evidence: none.
- [ ] Repeat create, render, and destroy cycles for desktop WSI, headless WSI, and OpenXR. Last evidence: 2026-08-13 (presentationless only).
- [ ] Warm the presentationless renderer and measure the submission interval. Expected: zero managed allocation, no per-frame resource or shader creation, no per-frame `vkDeviceWaitIdle`, and no current-frame readback. Last evidence: none.
- [ ] Compare command-buffer cache hits and rebuilds with desktop, and record CPU and GPU frame-time distributions for the same fixture. Last evidence: none.

<a id="from-advanced-pipeline-gpu-attribution-todomd"></a>

### Advanced Pipeline GPU Attribution

Investigation: [cumulative publication validation](../../investigations/rendering/2026-10-01-cumulative-publication-validation.md). Code items: [Editor Profiler And UI Render Cost TODO](../../todo/rendering/optimization/editor-profiler-ui-render-cost-todo.md#open-code-items). These checks allow no quality reduction and make no GPU speedup claim.

- [ ] Freeze an accepted `AdvancedRenderPipeline` fixture, source, and observer binary. Keep row coverage, AA, output and internal resolution, and quality. Last evidence: none.
- [ ] Compare dense timestamp overhead with the coarse observer in repeated stationary and motion pairs. Keep query identity, availability, and coverage. Do not count a repeated query result as a new frame. Last evidence: none.
- [ ] Measure one effect at a time: AO, exposure, bloom, temporal reconstruction, and required lighting. Restore each setting before the next comparison. Last evidence: none.
- [ ] After a fix, check the editor, desktop, and multi-view paths again. Report physical XR separately. Last evidence: none.

### Telemetry And Attribution

<a id="f1-01"></a><a id="f1-02"></a><a id="f1-03"></a><a id="f1-04"></a><a id="f1-05"></a><a id="f1-06"></a><a id="f1-07"></a><a id="f1-08"></a><a id="f1-09"></a><a id="f1-10"></a><a id="f1-11"></a><a id="f1-12"></a><a id="f1-13"></a><a id="f1-14"></a>

Record conclusions as measured findings or as hypotheses from source review. The 2026-09-01 incident supports attribution and transaction ownership. It does not prove which encoding change will win.

- [ ] F1-01: capture matched static and moving desktop baselines for `CpuDirect`, `GpuIndirectZeroReadback`, and `GpuMeshletZeroReadback`. Keep OpenXR baselines separate. Last evidence: none.
- [ ] F1-02: capture separate Streamline/DLSS frame-generation evidence. Last evidence: none.
- [ ] F1-03: attribute every compute, transfer, submit, present, worker, and external-runtime interval above 0.1 ms. Last evidence: none.
- [ ] F1-04: attribute at least 99% of detailed frame-root wall time, identify every gap of 50 µs or more, and measure observer overhead. Last evidence: none.
- [ ] F1-05: run the frame-slot, Mailbox, FIFO, reduced-resolution, compiler, streaming, secondary-window, and editor-diagnostic A/B matrix. Last evidence: none.
- [ ] F1-06: every recurring slot wait has an exact producer or timeline owner. Expected: uncapped GPU-headroom slot-wait p95 is about zero. Last evidence: none.
- [ ] F1-07: report `PrimaryFrameDataManifest`, `PrimaryPrewarm`, `PrimaryEncodingSetup`, `PrimaryOperationLoop`, `PrimaryFinalization`, and `PrimaryEndCommandBuffer` separately at p50/p95/p99/max with allocation, operation count, lane, and frame and output identity. Last evidence: none.
- [ ] F1-08: separate secondary encoder wall time, summed worker execution, worker wait, merge, and command-buffer-end publication. Do not charge frontend preparation or submission validation to native encoding. Last evidence: none.
- [ ] F1-09 and F1-10: count live `VkMeshRenderer.RecordDraw` calls, prepared-draw encoder calls, dependency-track attempts, unique dependencies, bind-state lookups and locks, tracking-batch locks, descriptor-heap binds and skips, and native commands by type. Publish `DependencyTrackAttempts / UniqueRecordingDependencies`. Last evidence: none.
- [ ] F1-11 and F1-12: run one matched Release isolation ladder (live draw plus full tracking; prepared draw plus full tracking; prepared draw plus sealed tracking; CPU-built indirect ranges; GPU-built indirect ranges) on small, medium, dense, material-diverse, and moving-camera cohorts. Retain raw profiles, native command counts, tracking counters, output parity, and critical paths. Last evidence: none.
- [ ] F1-13: keep a raw-pinned or sampled-full-validation diagnostic rung only long enough to measure the safety layer. Last evidence: none.
- [ ] Inject a bounded CPU delay into each lifecycle stage, worker path, queue gateway, and external wait. Expected: that stage becomes the dominant exclusive and critical-path contributor without double counting. Last evidence: none.
- [ ] Aggregate and targeted capture after warmup. Expected: zero managed allocation, no string formatting on measured threads, and overflow and failure reports when a ring or budget is exhausted. Last evidence: none.
- [ ] Editor, runtime-stat, MCP, JSON, CSV, and trace outputs. Expected: the same stable IDs, outcomes, counts, and intervals from one schema. Last evidence: none.
- [ ] Inspect the editor frame tree and timeline for static, moving, resize, mixed output, OpenXR, slow-frame, and rejected-frame cases. Expected: it shows the dominant exclusive stage, wait reason, output, generation, and critical path without raw logs. Last evidence: none.
- [ ] Review the frame spine from wake and acquire through settlement. Expected: every early return has a typed outcome and settles acquire, frame-slot, upload, worker, output-image, and timeline ownership exactly once. Last evidence: none.

### Frame Transactions And Present-Now

<a id="f0-03"></a><a id="f0-06"></a><a id="f0-07"></a><a id="f0-09"></a>

- [ ] F0-03: reproduce 344 pre-drain readiness retries and the 8,193-of-8,192 overflow. Expected: queued authoring work stays bounded, and the accepted plan contains only its own frame transaction. Last evidence: none.
- [ ] F0-06: change camera and scene state while preparation is blocked. Expected: the accepted epoch stays immutable, and exactly one captured epoch is submitted. Last evidence: none.
- [ ] F0-07: reproduce the 221-request and 836-request shapes, then exceed one main-scene lane. Expected: one bounded `FramePlanCapacityExceeded` record with configured, required, accepted, and rejected counts. Last evidence: none.
- [ ] F0-09: diagnose the RenderDoc 1.44 no-present launch and capture a settled Sponza frame with verified bindings and draw order. Last evidence: none.
- [ ] RC-V01: preempt background preparation with foreground readiness, then resume. Expected: earlier progress kept, no starvation or duplicate work. Last evidence: none.
- [ ] RC-V02: saturate shadow preparation while main-scene and composition work runs. Expected: bounded shadow deferral and continued scene progress. Last evidence: none.
- [ ] RC-V03: force a real terminal preparation or native failure. Expected: reproducible identity, one terminal transition, and no retry or log storm. Last evidence: none.
- [ ] RC-V04: reuse an incompatible context or extent. Expected: a structured frame rejection with exact source and allocation provenance. Last evidence: none.

<a id="f5-01"></a><a id="f5-02"></a><a id="f5-03"></a>

### Resize Continuity

- [ ] F5-01: release continuity keeps the last authored scene generation alive, replays it under current ImGui and FPS overlays, and completes handoff only after an authored successor presents. Expected: the cross-pipeline cohort passes. Last evidence: none.
- [ ] F5-02: Advanced indirect capacity of 65,536, and a replay or clear base for every overlay and recovery presentation. Expected: held resize does not accumulate ImGui or dynamic-text history. Last evidence: none.
- [ ] F5-03: held drag and release in Default, Advanced, and Debug Opaque, including a live Advanced-to-Debug-Opaque asset replacement. Last evidence: none.

### Recording And Resident Streams

- [ ] RC-V05: measure hot-stream layouts and bytes touched against active work, including compatibility conversion passes. Expected: no unconsumed conversion path, and layout changes keep or reduce owners, files, allocations, descriptor bindings, and lifetime transitions. Last evidence: none.
- [ ] RC-V06: resident-stream allocation matrix with warm storage and pool high-water marks and measured managed-byte deltas. Expected: a named matrix artifact. Last evidence: 2026-08-17, partial; see [resident draw stream investigation](../../investigations/rendering/vulkan-resident-draw-stream-phase0-2026-08-17.md).
- [ ] RC-V07: primary and secondary pending state, command-pool synchronization, query inheritance, and dynamic-rendering and legacy render-scope inheritance across desktop, OpenXR, resize, reload, churn, and shutdown. Expected: completion-owned artifact retirement and clean validation for each entry. Last evidence: none.
- [ ] RC-V08: Release stable-static cohort of about 647 draws. Expected p95: frontend binding ≤0.15 ms, frame/view/pass publication ≤0.15 ms, unchanged material/object publication ≤0.05 ms, descriptor reuse ≤0.10 ms, artifact reuse ≤0.15 ms, total Vulkan prepare/record/submit ≤1.00 ms, excluding OS and GPU waits. Last evidence: none.
- [ ] RC-V09: Release moving-object cohort. Expected: only dirty object ranges update and total prepare/record/submit ≤1.50 ms p95. Last evidence: none.
- [ ] RC-V10: validate each retained unsafe path from the unsafe audit under lifetime, bounds, alignment, and concurrency stress, with end-to-end performance evidence. Last evidence: none.
- [ ] Resident evidence matrix: matched Release dense-Sponza baselines on the laptop and desktop (same commit, camera, resolution, settings, present mode, validation, warmup, and duration) for all five strategies where supported. Record requested and resolved strategies, CPU sampled traces for all engine threads, ThreadPool counters, context switches, core migration, QoS, and per-thread CPU time. Freeze draw, pass, material, shadow, and UI signatures and screenshots from three camera positions. Expected: the desktop and laptop difference is split into CPU work, scheduler and QoS, GPU, and presentation; every O(draw) stage has a measured count and time. Last evidence: 2026-08-17, partial.
- [ ] F3 parity: the stable-bin manifest and visible-template and draw-range equivalence for CPU-built indirect versus GPU indirect, with GPU indirect still zero-readback. Last evidence: none.

### Structure And Source Budgets

- [ ] Final hand-written Vulkan core is below 200,000 lines, keeps one top-level type per file, keeps the lifecycle spine at most 40 files and 20,000 lines, and keeps `VulkanRenderer` one non-partial facade file of at most 500 lines. Last evidence: 2026-08-06 inventory (918 files, 181,253 lines, 320 partials; fails facade budget).
- [ ] Main frame orchestration method is at most 100 logical lines, lifecycle paths use at most two owner directories below `Vulkan/`, and each file above 1,500 lines or method above 150 lines has an entry in `Tools/Reports/VulkanCoreStructuralExceptions.json`. Last evidence: none.
- [ ] Reductions did not combine unrelated types, hide behavior in generated files, move Vulkan code to a backend-neutral assembly, or add a service locator or forwarding layer. Last evidence: none.

### Data Layout And Unsafe Boundary

SIMD parity, hardware width, and promotion evidence follow the [Vulkan CPU SIMD Refactor Pass Design](../../design/rendering/vulkan-cpu-simd-refactor-pass-design.md).

- [ ] Generate a hot-data layout report for each promoted profile (type, element size, offsets, alignment, stride, capacity, managed references, producers, consumers, fields touched, bytes read, written, and copied, owning generation). Last evidence: none.
- [ ] Exercise unsafe arena reservations at zero, one, maximum, overflow, stale-generation, double-release, and use-after-settlement cases. Expected: deterministic rejection. Last evidence: none.
- [ ] Run canary or poison and randomized sequences for native scratch, mapped-frame, prepared-draw, descriptor, and readback arenas. Expected: deterministic rejection, no adjacent-slice corruption. Last evidence: none.
- [ ] Mapped host-visible memory on coherent and non-coherent types. Expected: flush and invalidate ranges expand to `nonCoherentAtomSize`, and no host write happens while a GPU read is outstanding. Last evidence: none.
- [ ] `stackalloc` is span-backed, bounded, and not in loops; pooled buffers are not used after return. Last evidence: none.
- [ ] Compare AoS, SoA, compact AoS, and AoSoA only for real hot consumers at low, medium, and high counts, including construction, transpose, tail, copy, synchronization, and settlement. Keep a safe `Span<T>` reference. Promote lower-level code only when Release results exceed noise and improve stage and full-frame p95. Record instructions, cycles, bytes, cache misses, branch misses, allocation, and wall time on one Intel and one AMD x64 machine. Last evidence: none.
- [ ] GPUScene culling and classification read only their declared streams, and no broad command build or SoA extraction remains. Record GPU bytes, dispatches, barriers, durations, visible count, and whole-frame result. Last evidence: none.
- [ ] Final indirect buffers are valid contiguous `VkDrawIndirectCommand` or `VkDrawIndexedIndirectCommand` arrays with correct offset, count, stride, and bounds. Last evidence: none.
- [ ] Frame-operation lowering keeps deterministic order while iteration touches numeric opcode and per-kind streams. Last evidence: none.
- [ ] Render packets and prepared draws use frame-owned range storage with no per-packet array allocation or `ArrayPool` rent after warmup. Measure prepared-draw header size, side-stream bytes, cache misses, commands per µs, and `CommandRecord` p50/p95/p99. Last evidence: none.
- [ ] Graph and barrier planning uses numeric IDs and flat ranges; the native boundary receives contiguous Vulkan arrays. Compare edge and barrier counts, bytes touched, planning p95, and scratch high-water marks. Last evidence: none.
- [ ] Descriptor publication scans only dirty generation streams. Record scanned, emitted, copied, and skipped counts and bytes. Last evidence: none.
- [ ] Worker scaling with 1..N workers on identical work. Expected: no false sharing, global atomic contention, or worse critical path; per-worker counters merge to the serial values. Last evidence: none.

### Advanced Rendering On Vulkan

Procedure: Vulkan mono first, then the listed profiles. Entry points are the `AdvancedRenderPipeline` partials, `AdvancedProductionCutoverContract`, `ViewSetPlan`, and the shaders named per group. OpenGL RenderDoc lacks bindless-texture support. A failed check gets a new code item in the XR and Advanced todo; the check stays open.

- [ ] ARP-V02: request an unsupported required profile. Expected: an observable failure and no silent CPU, legacy, mono, or unshaded substitution. Last evidence: none.
- [ ] ARP-V03: descriptor and image format, access, set and binding, matrix convention, record stride and version, and generation lookup at the backend boundary. Expected: a compiler and layout inventory and a clean GPU capture; the set 0/1/2/3 contract stays. Last evidence: none.

<a id="visibility-and-reconstruction"></a>

Visibility, deformation, and reconstruction (`ShadeNativeOpaque.comp` and the visibility producers):

- [ ] ARP-V04: skinned fixture through arena growth and reuse. Expected: valid current and previous deformation ranges and bounded arena storage. Last evidence: none.
- [ ] ARP-V51 and ARP-V52: capture Vulkan and OpenGL deformation-to-visibility, depth, and velocity dependencies. Expected: correct ranges, resource-specific barriers, completion-owned lifetimes, and no same-frame readback, per backend. Last evidence: none.
- [ ] ARP-V53 and ARP-V54: compare traditional, indirect, meshlet, static, and skinned visibility producers on Vulkan and OpenGL. Expected: identical logical surface and editor identity in overlapping, masked, mixed-producer, and camera-cut fixtures from two views. Last evidence: none.
- [ ] ARP-V55: late visibility recovery after disocclusion. Expected: new candidates draw after the current pyramid with no duplicate shading. Last evidence: none.
- [ ] ARP-V56 and ARP-V57: compare reconstructed attributes with the raster reference (static, skinned, normal-mapped, mirrored, masked, UV-stress) and check gradients and mip selection at boundaries, seams, tiny triangles, oblique surfaces, and LOD changes. Expected: documented tolerances and stable LOD. Last evidence: none.
- [ ] ARP-V58: measure reconstruction apart from classification and shading. Last evidence: none.
- [ ] ARP-V59 and ARP-V60: inspect reconstructed attributes in a Vulkan capture and an OpenGL capture. Last evidence: none.

Classification and clustered lighting (`ClassifyTiles.comp`, `BuildClassificationIndirect.comp`, `BuildFroxels.comp`):

- [ ] ARP-V05: shared materials and sparse or reused draw IDs. Expected: generation-correct kernel resolution and no material-row or descriptor identity in the dispatch key. Last evidence: none.
- [ ] ARP-V06: every admitted kernel ID, including pending, rare, and custom. Expected: disjoint pixel ownership and no dropped high kernel IDs. Last evidence: none.
- [ ] ARP-V07: force tile, membership, and dispatch capacity pressure. Expected: truthful counters, conservative recovery, structured required-mode failure, no same-frame readback. Last evidence: none.
- [ ] ARP-V08: compare tile dimensions with occupancy and mixed-material captures. Last evidence: none.
- [ ] ARP-V09: 1440p and 4K mono through resize and render-scale changes. Expected: correct froxel sizes and no bounds or descriptor errors. Last evidence: none.
- [ ] ARP-V10 and ARP-V11: more than 16 local lights, and exhausted froxel light-index storage. Expected: correct lists, no truncated contribution, overflow diagnostics, and GPU recovery. Last evidence: none.
- [ ] Classification fixtures: empty, background-only, one-kernel, many-material-one-kernel, many-kernel, checkerboard, tiny-triangle, masked-edge, invalid-payload, and overflow. Expected: each valid pixel assigned exactly once, and dispatch count follows visible kernel coverage. Compare tile-only and compact pixel-list classification. Last evidence: none.

Materials, shadows, and decals:

- [ ] ARP-V13: textured opaque, masked, unlit, and emissive families, including alpha-cutoff edges. Last evidence: none.
- [ ] ARP-V14: directional cascades and point and spot shadows for each filter family. Expected: machine-readable missing and stale reasons. Last evidence: none.
- [ ] ARP-V15: overlapping decals and list overflow. Last evidence: none.
- [ ] Capture OpenGL and Vulkan native-shading frames. Expected: no production dependency on a classic GBuffer, deferred light accumulation, ordinary opaque Forward+, or light combine. Last evidence: none.

<a id="motion-history-and-reset-matrix"></a>

Motion, history, and reset:

- [ ] ARP-V17: stationary, camera-only, object-only, and combined motion for rigid, skinned, and blendshape fixtures. Expected: correct velocity, reactive, and disocclusion output where history is valid. Last evidence: 2026-09-24, fails (TSR thin-edge shimmer at 0.67 scale; see [TSR jitter investigation](../../investigations/rendering/2026-09-24-advanced-vulkan-tsr-jitter.md#final-coverage-validation)).
- [ ] ARP-V18: cuts, frame gaps, new objects, topology replacement, and rejected submissions. Expected: history invalidates with no stale contribution. Last evidence: none.
- [ ] ARP-V19: resize, render scale, view count, pipeline switch, HDR and format, shader reload, and resource-generation resets. Expected: a result per cause and no cross-output history leakage. Last evidence: none.
- [ ] ARP-V20: multiple desktop viewports, empty output, and collection during delayed submission. Expected: only accepted histories commit. Last evidence: none.
- [ ] ARP-V21: OpenXR tracking loss, failed or no-layer EndFrame, and layered frames. Expected: history advances only for the exact successful frame. Last evidence: none.

Background, transparency, and composition (`ShadeBackground.comp`, `AdvancedRenderPipeline.LateAndPostCommands`):

- [ ] ARP-V25: sentinel background and compatible custom background geometry. Expected: correct clear, alpha, HDR, and capture behavior. Last evidence: 2026-09-08 (OpenGL mono/OVR and Vulkan mono and cube sky pass; Vulkan custom geometry open).
- [ ] ARP-V27: refraction and feedback snapshots. Expected: a copy only for visible consumers and no illegal attachment sampling. Last evidence: none.
- [ ] ARP-V28, ARP-V29, ARP-V39: weighted blended OIT, PPLL, and depth peeling capacity and overflow. Expected: visible parity, truthful diagnostics, bounded work, and no same-frame readback. Last evidence: none.
- [ ] ARP-V44: final composition. Expected: current output with UI and alpha composed for the output format. Last evidence: none.

Vendor upscalers and frame generation (inventory: ARP-A04):

- [ ] ARP-V61, ARP-V62, ARP-V63: native Vulkan DLSS super resolution, DLAA, and XeSS super resolution on admitted devices. Record SDK, driver, device, inputs, extents, resets, and captures. Last evidence: none.
- [ ] ARP-V64: native Vulkan DLSS frame generation. Record generated-frame presentation, ownership, pacing, and failures. Keep foreground and background evidence separate. Last evidence: 2026-09-24, partial; see [DLSS investigation](../../investigations/rendering/2026-09-24-vulkan-dlss-aa-frame-generation.md).
- [ ] ARP-V65, ARP-V66, ARP-V67: OpenGL-to-Vulkan bridge DLSS, DLAA, and XeSS. Record the same physical GPU on both APIs and the interop ownership. Last evidence: none.

Stereo, mirrors, editor, and diagnostics:

- [ ] ARP-V34: Vulkan layered and multiview path at several extents and view counts. Expected: separate eye images and history and bounded per-eye resources. Last evidence: none.
- [ ] ARP-V35: RVC two-pass profile. Expected: separate eye motion and occlusion and preserved OpenXR timing. Last evidence: none.
- [ ] ARP-V46: foveated derivatives and LOD under head motion. Last evidence: none.
- [ ] ARP-V36: mirror profile with stereo, reader pressure, and forced rejection and recovery. Last evidence: 2026-09-14 (bounded Vulkan and OpenGL mirror cohorts pass).
- [ ] ARP-V37: picking, outlines, hover, gizmos, bounds, icons, physics debug, UI, and on-top overlays. Last evidence: none.
- [ ] ARP-V38: diagnostic views and per-family GPU timing. Expected: counters match captures and RenderDoc labels are stable. Last evidence: none.
- [ ] Eye independence with asymmetric occluders, with resize, view-count change, runtime restart, swapchain recreation, and pipeline hot selection. Last evidence: none.

<a id="static-surfaces-ao-and-gi"></a>

Accepted Advanced and XR records (scope is limited to the named cohort; evidence is in the [OpenXR and Advanced investigation](../../investigations/rendering/vulkan-phase67-implementation.md) and the [mirror closeout](../../investigations/rendering/arp-mirror-placement-2026-09-14.md)):
| ID | Accepted scope | Date |
|---|---|---|
| XR-V01 | Monado strict SPS, 351 submissions, zero validation or EndFrame failures | 2026-09-06 |
| XR-V02, XR-V03, XR-V18, XR-V19 | Serial, parallel, paired-eye, and preview-copy receipts on Monado | 2026-09-14 |
| XR-V06, XR-V07, XR-V08, XR-V12, XR-V14 | Admission pressure, publication fault, retired-generation budget, STOPPING retirement, tracker allocation | 2026-09-14 |
| ARP-V01 | Shader compilation only, 26 variants | 2026-09-04 |
| ARP-V12, ARP-V22, ARP-V23, ARP-V24, ARP-V16 | Static opaque path, AO capture, AO neutral output, AO plus IBL, GI switching (Vulkan and OpenGL) | 2026-09-04 to 2026-09-08 |
| ARP-V26, ARP-V30, ARP-V31, ARP-V33, ARP-V40, ARP-V41, ARP-V32, ARP-V42, ARP-V43 | Sorted alpha, fog and atmosphere, TAA, TSR, motion blur, DoF, bloom, tone mapping, color grading (OpenGL cohorts) | 2026-09-07 to 2026-09-08 |
| ARP-V45, ARP-V47, ARP-V48, ARP-V49, ARP-V50, ARP-V68 | OpenGL SPS, portal, Vulkan probe, thumbnail, depth/visibility capture, OpenGL probe array | 2026-09-07 to 2026-09-08 |

<a id="dynamic-rendering-and-modern-backend"></a>

### Dynamic Rendering And Modern Backends

- [ ] Run both default pipelines through deferred GBuffer, forward depth reuse, transparency and OIT, compute and blit re-entry, bloom, shadows, forced diagnostics, pipeline rebuild, and shader invalidation in `DynamicRendering` and `LegacyRenderPass`. Last evidence: none.
- [ ] Exercise multiple color, depth-only, stencil-only, combined and read-only depth/stencil, mip, array layer, cube face, texture array, multisample resolve, and transient targets. Last evidence: none.
- [ ] Resize, minimize and restore, swapchain recreation, ImGui secondaries, OpenVR mirror, OpenXR sequential and parallel views, and true multiview. Expected: no stale command or resource reuse. Last evidence: none.
- [ ] Run three warm dynamic and legacy repetitions. Expected: dynamic p50/p95/p99 CPU frame time within 5% unless a documented GPU win explains it. Last evidence: none.
- [ ] Local read versus sampled attachments: compare GBuffer, depth, and lighting output on opaque, alpha-tested, stereo, and MSAA scenes, and record bandwidth, pass count, GPU p50/p95, and CPU recording cost on one tile-based and one desktop GPU. Last evidence: none.
- [ ] Descriptor heap on supporting hardware under standard and synchronization validation. Expected: heap and material rows correct in a GPU capture. Last evidence: none.
- [ ] Pipeline objects versus native shader objects across both pipelines, ImGui, compute, mesh and task, stereo, and both descriptor backends. Measure creation cost, prewarm size, misses, permutations, hot-reload latency, recording cost, and mixed-mode transitions. Last evidence: none.
- [ ] Foveation: center clarity, peripheral stability, stereo consistency, UI and text, near field, gaze and head motion, and map edges. Measure fragment work, GPU p50/p95, missed XR deadlines, map generation, and image memory for both backends. Last evidence: none.
- [ ] Transient attachments on devices with and without lazily allocated memory, including bloom, shadows, capture, depth, MSAA, resize, and memory pressure. Record committed and resident bytes and bandwidth. Last evidence: none.
- [ ] CPU-direct, indirect-count, mesh-shader, and DGC output parity in each supported configuration. Expected: zero same-frame readback, no hidden per-draw CPU loop, and no descriptor fallback. Measure submission, GPU time, generated commands, preprocess cost, memory, and pacing with rising draw and material diversity. Last evidence: none.
- [ ] Ray query and ray tracing: transforms, alpha-tested geometry, material diversity, rebuild and refit, streaming, resize, device loss, and mixed frames. Measure build, update, and trace cost, memory pressure, submission size, and TDR and XR budget behavior. Last evidence: none.
- [ ] Capability matrix over NVIDIA, AMD, and Intel GPUs and the supported XR runtimes. Run every supported combination of the backends above under standard and synchronization validation. Expected: zero engine-owned VUIDs, hazards, stale generations, rejected submissions, or device loss. Record unsupported lanes as not run. Last evidence: none.

### Occlusion

- [ ] Run open, moderate, occluder-heavy, masked, static, and moving-camera scenarios for disabled, CPU-software, CPU-query, and GPU Hi-Z occlusion. Record actual culls, work removed, CPU and GPU p50/p95/p99, and tail spikes. Last evidence: none.
- [ ] Occlusion is conservative for masked, near-plane, large-bound, and moving-camera content, and GPU Hi-Z does no current-frame readback. Last evidence: none.
- [ ] Each mode gets a disposition. Promote a mode only with positive target-scenario p95 benefit and bounded ineffective-case cost. Last evidence: none.

### Functional, Visual, And Stability Soak

- [ ] Exercise desktop, capture, probes, shadows, UI preview, OpenXR stereo, mirror, and two-to-four-view foveated output through `FreshSerial`, serial packet recording, and reuse. Compare serial, parallel, and reuse output from two camera positions. Last evidence: none.
- [ ] Run StandardValidation and SyncValidation across mixed-output, graphics-only, and graphics-plus-compute plans. Investigate every engine-owned error, stale generation, destroyed-in-use object, or device loss. Last evidence: none.
- [ ] Pin cohort cameras, animations, lights, assets, settings, seeds, warmup, and scene revisions for `Empty`, `OpaqueDense`, `MaterialDiverse`, `MaskedCoverage`, `Skeletal1`, `Skeletal8`, `Skeletal32`, `SkeletalCrowd`, `Overdraw`, `Occlusion`, `ClusteredLights`, `ShadowStress`, `Transparency`, `PostProcess`, `MixedSpecial`, `StereoAsymmetric`, and `CaptureConsumers`. Last evidence: none.
- [ ] Run long camera-motion, animation, resize, feature-toggle, shader-reload, streaming, editor-interaction, and pipeline-switch sessions. Run a two-hour mixed-output churn soak and an eight-hour Release soak. Expected: bounded memory, retirement, descriptors, command artifacts, queues, and latency, no device-wide idle, and no rerecord storm. Last evidence: none.
- [ ] Inject failure at every lifecycle boundary and after every ownership transition. Expected: first failure kept, no GPU work after loss, one terminal outcome, exactly-once settlement, and bounded shutdown or restart request. Last evidence: none.
- [ ] Empty, missing-resource, shader-pending, capacity-overflow, and capability failure paths. Expected: structured outcomes. Last evidence: none.
- [ ] On supported hardware, collect `VK_KHR_device_fault` and vendor diagnostics, and memory-budget evidence during optional-work stress. Last evidence: none.

### Performance And Tail Latency

- [ ] Equivalent Deferred and Uber prepass-on and prepass-off captures with a pass and resource ledger (geometry replay, attachments, copies, transitions, barriers, dispatches, CPU and GPU time). Last evidence: none.
- [ ] Matched Release Vulkan and OpenGL `CpuDirect` low, medium, and high cohorts plus GPU indirect instrumented and zero-readback cohorts. Report root and stage p50/p95/p99/worst, inclusive and exclusive work, waits, worker overlap, critical path, and unattributed time. Last evidence: none.
- [ ] Stable desktop command reuse after warmup. Expected: no static range rerecord from data changes, no required-pipeline deferral, no recording allocation, and reuse misses name only topology, capacity, binding, shader, or resource-generation changes. Last evidence: none.
- [ ] Full-Sponza camera path with directional lights off and on. Record visible draws, artifact builds and reuses, chains, primary decisions, preparation, manifest refresh, encoding, submission, and no-input frame advance. Last evidence: none.
- [ ] Attribute every desktop frame above 5.00 ms and every RVC zero-readback frame above 8.33 ms. Last evidence: none.
- [ ] Matched instrumentation-off, aggregate, targeted, and slow-frame-trigger captures. Expected: within the observer-overhead budget. Last evidence: none.
- [ ] Three RVC zero-readback repetitions with fresh desktop output and both eyes fresh per projection frame, for each foveation state. Last evidence: none.
- [ ] Matched original-versus-Advanced Release baselines with VSync off against the 8.33 ms budget. Expected: zero same-frame readback and zero warm managed allocation in classification and shading. Reject a GPU win that moves cost into CPU recording, synchronization, descriptors, churn, or tails. Last evidence: none.

<a id="s13i-prove-the-cumulative-fix-on-the-reported-workload"></a>

### Stall Remediation: Cumulative Acceptance

Status: NOT PASSED. Use the [stall change protocol](#stall-change-protocol). Evidence: [cumulative investigation](../../investigations/rendering/2026-10-01-cumulative-publication-validation.md), [CPU attribution](../../investigations/rendering/2026-10-01-cpu-stall-attribution.md).

- [ ] Desktop target: more than 100 fresh FPS during camera motion with one directional light and no removed features. Last evidence: 2026-10-04, 92.09-92.46 FPS on the interior route (fails).
- [ ] Pool retention: scene unload and repeated replacement. Expected: bounded retained memory and timely release. Last evidence: none.
- [ ] Each retained allocation increment: frozen control and candidate binaries, stationary and motion comparisons with matched observers, sampled bytes per present, and GC tails. Last evidence: none.
- [ ] Correlate GC suspension and present intervals with scheduling and file-I/O evidence. Explain the 1.9-second outer-dispatch gap and the 567.52 ms GC-only suspension. Last evidence: none.
- [ ] Constant-speed camera path (or accounted easing) with displayed-motion evidence and user confirmation. Last evidence: none.
- [ ] Observer overhead and retention admission on a frozen binary after camera-state and lifetime series are repaired. Keep observer-off controls and outliers. Last evidence: none.
- [ ] Matched matrices against the original baseline and the previous increment: at least three pairs and 60-second warm windows. Report dirty causes, rebuilds, allocations and GC, upload bytes, preparation calls, scans, lock wait and hold, publication latency, encoding, present intervals, and retention at p50/p95/p99/max. Last evidence: none.
- [ ] Match scene content, draw coverage, AA, resolution, and features. Inspect stationary, motion, and disocclusion sequences. Resolve the failed OpenGL image and readback check. Last evidence: none.
- [ ] Record retained diffs, child dispositions, remaining latency, and unexplained intervals. Expected: cumulative tails, retention, and stage budgets pass. Last evidence: none.

<a id="s15b-record-directional-cascade-casters-on-the-advanced-canonical-lane"></a>

### Stall Remediation: Directional Shadow Lane

Evidence: [lane record](../../investigations/rendering/2026-10-03-s15b-directional-shadow-lane.md).

- [ ] Attribute the remaining interior-motion CPU and GPU cost with detailed stage deltas and GPU history on the same route. Last evidence: none.
- [ ] Counterbalanced stationary and motion A/B with an idle host and matched shadows. Last evidence: 2026-10-04 (misses 100 FPS).
- [ ] Stationary, camera, light, and object-motion shadow parity, per-cascade coverage, masked and custom-material eligibility, and generic execution when the lane declines. Last evidence: none.

<a id="s16a-black-opengl-scene-on-the-measurement-host"></a>

### Stall Remediation: OpenGL Control

Evidence: [OpenGL record](../../investigations/rendering/2026-10-03-s16a-opengl-admission.md).

- [ ] Establish a correctly rendering OpenGL control under the same admission gates. Disclose any source delta. Last evidence: 2026-10-03, unchanged `9fee4b983` fails (0/100 interior samples).
- [ ] Matched comparisons with corrected readiness and explicit shadow dimensions. Require real geometry, current reservations, advancing receipts, and completed Vulkan frames. Last evidence: none.
- [ ] Final display tonemapping, temporal sequences, and the cross-backend matrix. Last evidence: none.
- [ ] Stereo and MSAA source reload, source replacement, driver-parallel overlap, sparse cancellation and device failure, and exposure stereo checks. Last evidence: none.

<a id="s13c-retain-logical-meshlod-registration-by-real-mutation-identity"></a><a id="s13d-update-materialdraw-auxiliary-state-only-when-its-inputs-change"></a><a id="s13e-prepare-shared-advanced-scene-state-once-per-compatible-family"></a>

### Stall Remediation: Fixture Coverage

- [ ] Registration matrix: multiple LODs, shared meshes, threshold edits, active LOD changes, streaming completion and eviction, atlas relocation, geometry replacement, and removal and re-addition. Include failed registration and retry, supersession, create and destroy cycles, and atlas-slot reuse. Expected: zero allocation, rebuild, atlas ensure, and redundant table writes on exact hits. Last evidence: none.
- [ ] Auxiliary state: instance-count changes, material override swap and removal, texture and sampler replacement, deformation, growth past capacity, and retry after failed registration. Last evidence: none.
- [ ] Family preparation: window resize and MSAA sample-count changes, and the family mutation matrix on production XR. Last evidence: none.
- [ ] Shared extraction forced growth and failure, duplicate published geometry keys, and long structural and material churn. Last evidence: none.
- [ ] Deterministic late-result rejection and the cold upload-retry recovery limit. See [readiness evidence](../../investigations/rendering/2026-10-01-warmed-pipeline-readiness.md). Last evidence: none.

### Stall Remediation: Publication Mutation Matrix

For each case, record the source command, world and view, mutation and publication identity, admitted and consumed frame, expected visibility boundary, and actual output.

- [ ] Stationary commands with new or reused publication. Expected: zero identity-only scene-content callbacks. Last evidence: none.
- [ ] Transform motion, then stop. Expected: previous and current transforms and velocity advance and settle. Last evidence: none.
- [ ] Material value, resource, and override; visibility and pass; geometry and primitive count, including an off-camera object moved into view and in-place arena-buffer replacement. Last evidence: none.
- [ ] Add, remove, re-add, shared mesh and material, and edit bursts. Expected: recycled IDs expose no stale resources. Last evidence: none.
- [ ] Mutation during a callback or after capture, and secondary views. Expected: newer pending state survives; no deadlock or unbounded retry. Last evidence: none.
- [ ] Rejected, aborted, retried, and superseded publication, and disposal. Expected: no failed identity becomes consumable; dependencies retire once. Last evidence: none.
- [ ] Pending upload, cancellation, failed successor upload, and competing transitions. Expected: transfer precedes binding. Last evidence: none.

<a id="s15-preserve-temporal-correctness-and-resolve-the-original-report"></a>

### Stall Remediation: Temporal Correctness

Use an explicit TSR fixture; the performance fixture defaults to FXAA. Evidence: [temporal investigation](../../investigations/rendering/2026-10-03-s15-temporal-checks.md).

- [ ] Recapture stationary detail, controlled motion, disocclusion, camera cut, resize, and pipeline and view switches with matched settings. View saved sequences from several positions. Last evidence: none.
- [ ] Correlate history, view, frame identity, jitter, matrices, velocity, depth, and resets with exact post-admission bindings. Last evidence: none.
- [ ] Deferred TAA and TSR consume the correct immutable pipeline snapshot. Last evidence: none.
- [ ] Resolve fine-edge TSR concerns, classify pipeline-replacement cold stalls, and get the user's ghosting and performance confirmation. Last evidence: none.

### Stall Remediation: Integrated Closeout

- [ ] Matched cold and cache-warm restart and warm still and moving runs. Report p50/p95/p99/max, presents, lost samples, preparation, recording, waits, GPU cost, and loading time. Last evidence: none.
- [ ] Repeated resize, shader and pipeline reload, mesh, index, and texture admission, multi-view ownership, failure paths, and teardown. See the [shader reload record](../../investigations/rendering/2026-10-04-shader-root-reload.md). Last evidence: 2026-10-04 (scoped root reload only).
- [ ] Play entry and exit, probe refresh, redraw and picking, attachment metadata, idle BVH diagnostics, and toolbar and camera settings on first and warm use. Last evidence: none.
- [ ] No new hot-path allocation, unbounded retention, queue starvation, unsafe disposal, silent fallback, or missing draws. Last evidence: none.

### Promotion Gates

Run on one frozen integrated revision after the applicable code items close. No scenario or threshold is waived.

Scenario matrix:

- [ ] Desktop performance scenarios: static; continuous camera motion through dense Sponza; object transform and animation updates; one material value and one texture replacement; texture streaming bursts; geometry reload and slot reuse; shader hot reload followed by warm recovery; directional shadow movement; probe maintenance; editor UI active and hidden; secondary ImGui windows; all five strategies; `Stable`, `LowLatency`, and `Uncapped` presentation; dynamic rendering and legacy render pass. Last evidence: none.
- [ ] Correctness and lifetime scenarios: resize, maximize, minimize, restore, resolution, HDR, format, and MSAA changes; offscreen, mirror, portal, probe, capture, transparent, UI, callback, query, and external outputs; pause and resume; failed acquire, submit, and present; device loss; start, stop, and shutdown; diagnostic ring wrap, full, late, and mismatch; device loss with pending diagnostics. Last evidence: none.
- [ ] OpenXR scenarios: static pose, head motion, desktop plus OpenXR, in-flight image pressure, swapchain recreation, session stop and loss recovery on Monado and one hardware runtime. Last evidence: none.

Correctness and structural gates:

- [ ] Zero VUIDs in Standard and Synchronization validation, and zero device loss, stale descriptor, use-after-free, or command-pool reuse errors. Last evidence: none.
- [ ] Camera-separated screenshots prove current output. Strategy parity keeps draw order, visibility, materials, shadows, transparency, post, UI, and strategy identity. Last evidence: none.
- [ ] One material scalar, one texture replacement, one geometry replacement, and one shader reload invalidate only exact dependents; add, remove, and re-add stays generation safe; camera and object transforms cause zero structural invalidation. Last evidence: none.
- [ ] Zero managed hot-path allocation after warmup; zero per-draw material reconstruction, descriptor validation, or command-signature rebuild. Last evidence: none.
- [ ] Warm native encoding executes zero live `VkMeshRenderer.RecordDraw` calls and zero `_recordDrawSync` acquisitions, reflection, pipeline creation, descriptor allocation, prewarm, or callbacks; zero global bind-state discovery and shared bind-state locks; dependency work scales with unique manifest entries; descriptor binds scale with command buffers or scopes. Last evidence: none.
- [ ] Warm `PrimaryPrewarm` has no visits outside classified mutation, streaming, cold-recovery, or diagnostic frames. Last evidence: none.
- [ ] Warm dense-Sponza `PrimaryCommandEncoding` p95 is at most 1.0 ms on the desktop and 1.5 ms on the laptop. Last evidence: none.
- [ ] Fresh frame data can execute through a compatible reusable artifact without native re-encoding, with a new submit serial and exact provenance for `PresentedNew`. Last evidence: none.
- [ ] Stable preparation scales with dirty ranges; sealed unchanged submission p95 is below 0.25 ms; slot-wait p95 is about 0 ms with GPU headroom; run-to-run p95 spread is at most 7.5% (target 5%). Last evidence: none.
- [ ] `CollectWaitForRender` and `RenderWaitForCollect` p95 at most 0.5 ms on trivial scenes. Last evidence: none.
- [ ] Editor UI overhead: at most 1.5 ms when active and at most 0.10 ms when cached under `EditorUiRateHz`. Last evidence: none.
- [ ] Hot-switch all strategies with prior slots in flight and retired. Expected: old artifacts preserved, canonical handles stable. Last evidence: none.
- [ ] Stable dense Sponza: zero template rebuilds, rebinning, descriptor writes, command records, allocation, and legacy holes; camera motion does only view and culling publication. Tenfold instances in unchanged bins do not cause tenfold CPU preparation. Last evidence: none.
- [ ] GPU p95 does not regress more than 5% against direct draw without an accepted quality or scalability gain. Every-N-frame spikes are absent or explained. Last evidence: none.

Promotion levels:

- [ ] Level A, 100 Hz: whole-frame p99 below 10.000 ms across desktop scenarios, no recurring unexplained spikes above 10 ms, and correctness gates pass. Last evidence: none.
- [ ] Level B, 120 Hz: whole-frame p99 below 8.333 ms (target 7.5 ms); actual present intervals meet 120 Hz; laptop CPU p50 ≤8.33 ms and p95 ≤10.0 ms; desktop CPU p50 ≤5.0 ms and p95 ≤6.0 ms; resident frame-operation preparation p50 ≤2.0 ms on both systems. Last evidence: none.
- [ ] Level C, 144 Hz: whole-frame p99 below 6.944 ms (target 6.25 ms) with measured CPU and GPU headroom. Last evidence: none.

Hardware and worker gates:

- [ ] Benchmark stable descriptor sets and dynamic offsets against descriptor indexing on NVIDIA, AMD, and an integrated GPU. Prototype descriptor-heap and DGC tiers only when they beat the portable path. Last evidence: none.
- [ ] Sweep `0`, `1`, `2`, `4`, `8`, and auto render workers for small, medium, large-dirty, stable, and moving cohorts across all strategies. Expected: auto within 5% of the best p50 and 10% of the best p95; large dirty cohorts on 8+ processors show two or more overlapping record intervals and 20% p50 improvement over inline before parallel recording is promoted; small and stable cohorts do not regress inline by more than 3% p50 or 5% p95; queue, wake, and merge cost are reported. Last evidence: none.
- [ ] Freeze the accepted revision and publish raw reports, summaries, profiler and capture evidence, screenshots, validation logs, unsupported rows, and follow-ups for each tail source. Last evidence: none.
- [ ] Vulkan promotion from opt-in to regular development use: smoke passes on one NVIDIA, one AMD, and one Intel or laptop GPU; Sync2 and legacy sync match; stress checks pass without validation errors, device loss, or unbounded allocation; no unexplained visual differences from OpenGL; pipeline misses are quiet after warmup; black-frame diagnostics are not needed for routine failures. Keep legacy allocator and sync fallbacks for one stable milestone after promotion. Last evidence: none.

## Hardware Matrix
| System | Role | Status |
|---|---|---|
| Intel Core Ultra 9 185H / RTX 4070 Laptop | Named laptop for budgets | Partial baselines (2026-08-17) |
| Ryzen 9 7950X3D / RTX 3090 | Named desktop for budgets | Partial baselines |
| AMD Vulkan driver | Descriptor and secondary policy | Not run |
| Integrated or tile-based GPU | Portable policy, local read, transient memory | Not run |
| Monado | OpenXR runtime, fault injection | Active |
| Physical OpenXR headset | Hardware XR acceptance | Partial (stereo regression open) |
| SteamVR/OpenVR | OpenVR path | Not run |

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
| ARP-V17 | Thin-edge TSR shimmer on Vulkan mono at 0.67 scale | [TSR jitter investigation](../../investigations/rendering/2026-09-24-advanced-vulkan-tsr-jitter.md) |
| Stall cumulative acceptance | Desktop motion below 100 FPS; original CPU/TSR report open | [Vulkan stall separate findings](../../todo/rendering/vulkan-stall-separate-findings-todo.md) |
| OpenXR stereo regression | Mostly black output, Sponza flicker, old-frame jitter on hardware | [Hardware record](../../investigations/rendering/2026-10-03-retained-rendering-hardware.md) |
| XR memory | 13.5 GB private versus 8 GB target | [Editor memory reduction todo](../../todo/rendering/optimization/editor-memory-reduction-todo.md) |
| OpenGL control | Black interior scene on the measurement host | [OpenGL admission record](../../investigations/rendering/2026-10-03-s16a-opengl-admission.md) |
| XR-V11, XR-V20, XR-V21 | Lifecycle recovery defects | XR-I14, XR-I16 in the [XR and Advanced todo](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md) |
| Mirror compute slots | Missing native compute slot-3 resources | XR-I22 in the [XR and Advanced todo](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md) |
