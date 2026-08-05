# Rendering Backend Hot Reload Closeout

Date: 2026-07-25

Result: Complete for the documented Windows/CoreCLR desktop workflow.

> **2026-07-27 regression addendum:** The historical validation below remains
> accurate for its recorded sessions, but current structural Vulkan
> `Build and Reload Renderer` is not considered safe after NVIDIA Streamline
> initializes. A candidate generation terminated the editor with native
> fast-fail `0xc0000409` while Streamline was being reinitialized. Vulkan shader
> reload and same-generation renderer restart were subsequently live-validated
> and remain the supported same-process paths. Structural Vulkan C# changes
> require a full editor restart until Streamline runtime ownership is moved to a
> stable process-lifetime service or the operation is rejected before teardown.
> See the
> [2026-07-27 investigation](../../investigations/rendering/archive/vulkan-uber-pipeline-stall-black-recovery-2026-07-27.md).

## Inventory and ownership audit

The work was performed directly in the active Runtime Modularization Phase 4
working tree; no competing branch was created. `rg --files`, project evaluation,
source-contract tests, dependency generation, and live unload root tracing were
used to inventory source, package, native, content, reflection, callback,
serializer, AOT/static-registration, and test ownership.

| Owner | Contents |
|---|---|
| Stable rendering host | logical render assets/resources; windows/viewports; pipelines; replacement transaction; module ABI/catalog; generation validation; process-lifetime ImGui, clipboard, Streamline, and Vulkan-debug callback entry points |
| OpenGL leaf | GL renderer/wrappers; programs/shaders; upload/readback/query/mesh/material/FBO; shared contexts/compiler workers; ImGui renderer/platform adapter; Ultralight shader resources; OpenGL/WGL packages |
| Vulkan leaf | Vulkan renderer/wrappers; instance/device/swapchain; commands/descriptors/pipelines/render graph; allocator/VMA; ImGui; OpenXR binding; Shaderc; Streamline/DLSS/XeSS adapters; Vulkan packages/native payloads |
| Editor | backend-only build/stage/watch service; collectible loader; manifest; UI/preferences; MCP actions |

The stable-reference audit is enforced by
`RendererBackendModuleSourceContractTests`: no concrete constructor/type test,
leaf project reference, cross-backend reference, or executable policy edge may
enter the stable kernel. The callback/static audit found and removed the final
collectible roots: GL debug delegates, dynamically marshalled GL delegates,
Vulkan/vendor callbacks, ImGui platform delegates, native API finalizers, and
wrapper-cache ordering.

Creation now follows catalog registration -> stable factory ->
`XRWindow.InitializeRenderer`. Replacement follows coordinator quiesce -> GPU
drain -> wrapper retirement -> backend cleanup -> module prepare/unload ->
candidate register/create -> physical resource rehydration -> first valid frame.
Render/window affinity is asserted through the render-thread invocation helper.

Multi-window replacement collects every window using the backend into one
transaction. Detached ImGui viewports retain the stable context/docking state
and recreate platform/renderer resources. OpenGL shared contexts and workers
are module-owned and joined. XR, Streamline/DLSS/XeSS, video/external texture,
capture/markers, profiler, and device-loss entry points are either explicitly
leased/unregistered or report a named reload boundary.

## Functional validation

Hardware/software: Windows x64, .NET SDK 10.0.301, OpenGL 4.6 and Vulkan desktop
drivers available on the validation machine, editor Unit Testing World.

OpenGL live session `hotreload-final-opengl-20260725`:

- generations 43 and 44 built, staged, activated, rendered, and unloaded
  consecutively in one editor process;
- final state `Idle`, successful reloads 2, failed reloads 0, rollbacks 0,
  unload leaks 0;
- generation 44 teardown 32.8743 ms, unload preparation 0.1982 ms, candidate
  initialization 407.7979 ms, backend build/stage 10.542 s;
- the post-reload capture visibly contains the Unit Testing World, editor UI,
  hierarchy/selection, transform gizmo, and active frame statistics.

Vulkan live session `hotreload-unload15-vulkan-20260725`:

- generations 29 and 30 built, staged, activated, rendered, and unloaded
  consecutively in one editor process;
- final state `Idle`, successful reloads 2, failed reloads 0, rollbacks 0,
  unload leaks 0;
- generation 30 teardown 356.4539 ms, unload preparation 2.5924 ms, candidate
  initialization 1228.6374 ms;
- visual captures exercised the Unit Testing World's default pipeline,
  directional shadows, UI, post processing, compute/GPU submission, and
  framebuffer capture path.

The same stable transaction is used for same-DLL restart and device-loss.
Logical world, selection, inspector, camera, viewport, settings, and unsaved
state remained owned by the unchanged editor process across both live runs.
Resize/minimize restoration, scene/play transitions, asset changes, prewarm,
viewport/platform-window creation, HDR/AA invalidation, and inactive-XR
boundaries are covered by the common window/resource lifecycle and focused
source/unit contracts. Active XR hardware was not present; deterministic
boundary coverage verifies rejection and explicit OpenXR stop/reload/restart
routing without silently disabling XR.

Shader coverage includes vertex, fragment, geometry, tessellation control,
tessellation evaluation, compute, task, and mesh source identities. Dependency
tests cover top-level/transitive normalized paths, in-place text/type changes,
rename/create/delete notification, monotonic revisions, and selective
invalidation. Backend candidate publication preserves last-good and rejects
obsolete revisions; interface changes use existing broad/targeted material,
descriptor, pipeline, transform-feedback, and command invalidation.

## Stress, failure, and budgets

`RendererBackendCollectibleModuleTests` performs 100 consecutive load/unload
cycles of a real staged leaf generation. Every ALC becomes unreachable, retained
managed growth remains below 32 MiB, and generation/build directories are
bounded and reclaimed. Live OpenGL and Vulkan each additionally proved two
full renderer/device generations, including native resource and callback
teardown.

Deterministic failure hooks cover shader compile/program link, backend build,
shadow copy, ABI/module/dependency validation, GPU drain, worker shutdown,
outstanding callback, resource leak, candidate initialization, first frame,
rollback, device loss, delayed obsolete completion, and unload leak. Candidate
failures retain or restore last-good; unload leaks block unsafe continuation and
are categorized in the status snapshot. Alternating and lifecycle-transition
races collapse behind monotonic generations and the single transaction gate.

Budgets:

- steady-state orchestration cost/allocation after `Idle`: zero per frame;
- render/message-pump blocking: no build, load, GC, or worker wait on the
  application loop; only bounded render-thread teardown/creation handoffs;
- visible placeholder: transaction duration only;
- desktop teardown target: under 500 ms (met by both backends);
- candidate initialization target: under 2 s (met by both backends);
- build plus visible replacement target: under 15 s on the validation machine
  (met by both backends);
- retained generations: 3 by default, configurable 2-16;
- 100-cycle managed growth: below 32 MiB (met).

## Automated and build evidence

- 28 focused shader/module/source-contract tests passed, including every
  supported shader stage and the
  100-cycle collectible stress test.
- Runtime.Core, stable Runtime.Rendering, OpenGL, Vulkan, Bootstrap, Editor,
  Server, VRClient, UnitTests, integrations, and the solution build in the final
  matrix.
- MCP documentation was regenerated with
  `Tools/Reports/generate_mcp_docs.ps1`.
- Dependencies and license inventory were regenerated with
  `Tools/Reports/Generate-Dependencies.ps1`; leaf ownership appears in
  `docs/DEPENDENCIES.md`.

The repository's existing Magick.NET security-advisory warning remains
unrelated to hot reload. The MCP documentation generator also reports its
pre-existing Magick.NET 14.13.1/14.14.0 reference-version conflict. No new
compiler warning, graphics validation error, forbidden dependency, unload leak,
hot-path allocation, or fallback was introduced by this work.

The unfiltered repository test sweep is not green for unrelated active-worktree
reasons. Representative failures are engine-default persistence
(`EnableFrameLogging`), networking/MemoryPack contracts, default-instance asset
serialization, tests constructing rendering scenes without installing
`RuntimeRenderingHostServices`, and Vulkan shader compilation tests using the
same missing-host setup. These were recorded separately from the passing
hot-reload gate and were not hidden or weakened to close this work.

## Known unsupported changes

Stable-host/ABI, target framework/architecture, dependency upgrades, driver
state, and incompatible already-loaded native DLL replacement remain process
boundaries. GPU-only state with no CPU/cooked reconstruction source resets or
blocks reload. Active OpenVR remains a visible stop-first boundary; OpenXR uses
the explicit session restart workflow. Production NativeAOT uses static module
registration rather than collectible loading.
