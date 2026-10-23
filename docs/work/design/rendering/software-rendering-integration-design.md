# Software Rendering Integration Design

Status: Proposal. This document does not authorize implementation or dependency installation.
Updated: 2026-10-23
Source baseline: `e61559409b441eae3fdd8c4a8dc74297869ff7ff`

## Purpose and decision

Provide an explicit CPU rendering mode for normal XRENGINE applications. Users
must be able to run modest scenes, native UI, asset viewers, and editor sessions
on a supported computer without a hardware rendering device. Offscreen capture
and CI are additional uses. They are not the whole product.

Evaluate Mesa llvmpipe through the existing OpenGL backend first. Keep lavapipe
as a later Vulkan option, subject to the production Vulkan path and dependency
set meeting the same product checks. Reuse engine scene submission, materials,
shaders, render targets, and UI. Do not build a second rasterizer in C#.

This decision replaces the Vulkan-first order in the September 30 proposal.
The [existing software Vulkan guide][vulkan-validation] records a useful narrow
correctness lane. It does not prove windowed editor support or general Linux
support. It records a compiler override and evidence before renderer teardown.
No driver, build, runtime check, or benchmark was run for this design revision.

## Scope and non-goals

In scope: opt-in software startup, capability admission, reduced feature tiers,
windowed presentation, later display-free output, clear diagnostics, packaging
qualification, and measurements on named CPUs.

Out of scope: changing the default renderer or editor; a custom rasterizer;
automatic costly fallback; real-time VR; GPU performance parity; heavy compute,
ray tracing, vendor upscalers, CUDA interop, or full Advanced pipeline parity.
Do not silently replace GPU simulation or asset processing with CPU algorithms.
Dependency selection, installation, upgrades, and redistribution need a later
owner-approved change. This document selects an evaluation order, not binaries.

## Current integration points

All repository references below are relative to this document. Symbols describe
the inspected baseline, not interfaces already added for software mode.

| Area | Existing source and fact | Proposed integration |
|---|---|---|
| Startup policy | [GameStartupSettings][startup] has `Rendering`, frame targets, and `RunWithoutWindows`. | Resolve execution mode independently of backend and output mode. |
| Backend fallback | [Engine.Windows.cs][windows] has `CreateWindowInstance` and `AllowsRenderBackendFallback`; [RenderBackendFallbackPolicy][fallback] controls backend changes. | Preserve the execution-mode constraint across any allowed backend retry. |
| GL context | `Engine.Windows.cs` requests OpenGL 4.6. [DesktopSilkWindowBackendFactory.Create][desktop] maps the request to Silk/GLFW or SDL. | Select the approved native library before context creation; preserve context-thread ownership. |
| GL identity and features | [OpenGLRenderer.InitGL][gl-init] reads vendor, renderer, version, GLSL, and extensions. | Build an immutable device capability report; reject a mode mismatch before scene resources load. |
| Pipeline admission | [Default/Advanced invariants][pipeline] and [mesh submission contracts][mesh] govern pipeline and draw selection. | Admit a named software profile through the existing purpose-aware factory. Do not replace authored pipeline assets silently. |
| Native UI | [UserInterfaceRenderPipeline][ui-pipeline] and [UIBatchCollector][ui-batch] provide the native UI route. | Exercise the existing path on the CPU driver, including text, clip, blend, and upload semantics. |
| Vulkan | [TrySelectPhysicalDevice][vk-device] requires Vulkan 1.4. [VulkanShaderCompiler][vk-shader] uses `Vulkan1.4-SPIRV1.6`. | Add an explicit CPU-device filter only in the later lane; do not lower API or shader requirements by assertion. |
| Offscreen Vulkan | [VulkanExplicitTargetRendererHost][vk-host] and [SoftwareVulkanEnvironment][vk-env] support the existing correctness fixture. | Reuse exact output completion and native loader configuration lessons, without treating them as GL support. |
| Renderless server | [RuntimeApplicationBootstrap][bootstrap], [RuntimeRenderingBootstrap][render-bootstrap], and [Server Program][server] compose headless services. | Keep simulation/networking startup free of a graphics-device requirement. |

## Driver choice and platform limits

Mesa describes llvmpipe as an LLVM-based, multithreaded software rasterizer.
Its suitability is a hypothesis until the engine's context, shader, extension,
and output checks pass on the selected package. [Mesa llvmpipe][mesa-llvm]

Mesa identifies lavapipe as its software Vulkan frontend. A Vulkan adapter can
report an llvmpipe name; diagnostics must also identify the API and loaded ICD.
Do not confuse that name with a successful desktop GL context. [Mesa source tree][mesa-tree]

SwiftShader implements Vulkan. Its documented ANGLE route supplies OpenGL ES;
it is not a direct substitute for XRENGINE's desktop OpenGL 4.6 backend. Treat
it as a separate later Vulkan candidate. Its advertised API version must meet
the engine's actual requirements before evaluation. [SwiftShader README][swiftshader]

| Target | First qualification | Release position before evidence |
|---|---|---|
| Windows 10/11 x64 | llvmpipe, desktop GL 4.6, current Silk backend, normal application window | Primary candidate; package and GL entry-point loading remain unqualified. |
| Linux x64 with X11 | llvmpipe, normal window, complete application dependencies | Separate candidate; a portable test executable is not editor support. |
| Linux x64 with Wayland | Correct native window backend and presentation | Separate row; do not inherit X11 results. |
| Linux without a display | EGL surfaceless or another explicit offscreen context adapter | Later spike; the current desktop factory does not establish this route. |
| Windows without an interactive desktop | Explicit offscreen context and readback | Later spike; a hidden desktop window is not proof of display-free operation. |
| ARM64, macOS, browser | CPU architecture, native dependency, and graphics API audit | Deferred. No support claim from an x64 success. |

**GPU-free** means that no hardware graphics device executes rendering.
**Display-free** means that no desktop/display service is needed. A CPU driver
with a desktop window meets only the first condition. Mesa EGL has platform
and driver constraints; an EGL loader alone does not supply a usable engine
context adapter. [Mesa EGL][mesa-egl]

## Configuration and startup contract

The names in this section are proposed concepts, not implemented flags.

| Execution policy | Required behavior |
|---|---|
| Hardware | Require a qualified hardware device. A CPU renderer detected behind the OS loader is a failure, even if context creation succeeds. |
| Software | Require a qualified CPU renderer from the selected package. Do not accept a hardware GPU on failure. Show a persistent software-mode indicator. |
| Renderless | Create no graphics context or device. Run the existing headless service composition; rendering requests return an explicit unavailable result. |

Keep execution policy, backend (`OpenGL` or later `Vulkan`), output
(`Windowed` or `Offscreen`), and quality profile separate. Reject Renderless
with a graphics output request. Preserve current behavior for an unspecified
legacy setting until an explicit migration is approved. For new explicit
requests, backend fallback must satisfy both the requested execution policy and
the allowed backend policy. Failure must never quietly select software mode.

1. Resolve settings using the existing settings precedence. Freeze the requested
   mode, backend, output, required pipeline, and package identity for startup.
2. Resolve an approved local native package and verify architecture and integrity.
   Apply process-local loader selection before any graphics library is loaded.
3. Create the requested context on its owner thread. Record actual loaded-library
   identity, vendor/renderer strings, API/GLSL versions, extensions, limits, and
   output capabilities. A renderer string alone is not package provenance.
4. Classify the device using the qualified package manifest and platform probes.
   Unknown classification fails an explicit Hardware or Software request.
5. Validate required operations with small shader/FBO/draw/readback probes. Probe
   the selected pipeline and output, not merely an advertised version number.
6. Publish the admitted capabilities and reduced profile. Then load scene assets.
   Report any unsupported asset or operation with its missing capability.
7. On failure, release the partial context and resources. Return a clear error
   with the requested mode, actual driver, missing feature, and suggested action.

For a qualified Mesa Linux launch, `LIBGL_ALWAYS_SOFTWARE=true` and
`GALLIUM_DRIVER=llvmpipe` are candidate process-local controls. They do not
select or install a Windows OpenGL DLL, nor prove that Silk loaded Mesa.
Never use GL/GLSL version overrides to claim missing features. [Mesa environment variables][mesa-env]

Do not switch native drivers inside a live context. Use an explicit application
restart. Do not alter machine-wide driver settings, credentials, or search paths.
Qualify a controlled library resolver; avoid loading from arbitrary project or
working directories. Keep driver/compiler fingerprints in shader-cache keys.

## Capability tiers and frame budgets

Tier admission has two parts: correctness and measured cost. API support does
not make a compute-heavy pass suitable for interactive CPU rendering. The
following limits are initial experiment settings, not measured performance.

| Profile | Admitted work | Initial experiment budget |
|---|---|---|
| UI and 2D | Solid/border/image/text primitives, alpha blend, nested rectangular clips, offscreen UI target | 1280x720 output, 30 Hz render target; 60 Hz input/update target where host scheduling permits. |
| Basic 3D | Indexed raster draws, depth, a bounded set of lights/materials, one modest scene plus UI | 640x360 scene target, composite into 1280x720; 30 Hz target; explicit user resolution control. |
| Offscreen quality | Same qualified operations, requested output dimensions, bounded readback | No interactive FPS promise; explicit pixel/memory cap and timeout. Start at 1920x1080 maximum. |
| Advanced/compute/XR | Advanced visibility/compute pipelines, costly GI/volumetrics, mesh shaders, vendor interop, headset output | Excluded from initial profiles. Fail explicit requests; add only with separate capability and cost evidence. |

The basic profile must specify a compatible pipeline recipe through the existing
factory. Initially investigate a bounded legacy `DefaultRenderPipeline` recipe
with optional effects disabled. This is a software-profile choice; it does not
change the engine's configured desktop Advanced policy. An explicitly authored
Advanced/Required request stays required and fails if unsupported. Never rewrite
a shared camera asset to obtain a successful blank or reduced frame.

Disable optional MSAA, temporal upscaling, AO, GI, volumetrics, backdrop blur,
and dynamic shadows in the initial software preset. List every reduction in
diagnostics. Do not allocate resources for disabled passes. A material that
needs an excluded operation must fail visibly or use an explicitly selected,
qualified material variant. No magenta/empty output counted as success.

Set independent, bounded CPU budgets for the engine scheduler, shader compiler,
UI workers, and driver workers. Measure total process CPU time as well as main
thread time. Limit queued scene frames and captures; preserve event pumping when
rendering is slow. Reuse the last completed scene image only while its resource
lease is valid. Do not claim a 60 Hz UI if UI composition itself waits for a slow
scene frame. Measure input-to-present delay and expose missed budgets.

## Resource, presentation, and UI ownership

CPU drivers still implement asynchronous graphics APIs. Keep fences, barriers,
frame slots, resource generations, and teardown rules. A CPU-backed buffer is
not permission to write storage that a submitted frame still reads.

Use the existing window owner and UI renderer. Test resize, minimize/restore,
DPI, focus, clipboard, text entry, and close during compilation. The initial
native UI fixture is FPS text plus controls and a large virtual list; software
mode must not require a full native editor migration. The ImGui editor remains
the default and needs its own window/platform-window checks.

For offscreen GL, define a context-owner adapter before reuse of the normal
pipeline. Specify extent, format, completion receipt, readback ownership, timeout,
and cancellation. Do not substitute a hidden window and call it display-free.
Separate capture completion from application lifetime. Retain output generations
until all accepted readers finish, including across resize and restart.

## Dependency and distribution gate

Before any dependency change, prepare an approval packet with exact Mesa/LLVM,
loader, compiler, runtime-library, and optional validation-layer versions;
platform/CPU architecture; source URL; artifact hash; build options; license
inventory; notices; and rollback method. A version pin is a complete compatible
set, not a floating `latest` download. No binary version is approved here.

Review each shipped component under [LEGAL/README.md][legal] and
[LICENSE.md][license]. Mesa contains components with different licenses;
review the exact bundle, including LLVM and runtime dependencies. Do not infer
bundle redistribution rights from Mesa's main license. [Mesa license information][mesa-license]

After owner approval and a dependency change, use the repository dependency
report workflow and review its notices. Qualify install, update, uninstall,
offline launch, and clean-machine execution. Do not require an ad hoc compiler
symlink or modification of a shared package cache in a supported product.

## Implementation sequence

These are proposed work packages. None is complete by this document.

| Order | Concrete task and owner area | Done condition |
|---|---|---|
| 1. Admission design | Host/settings and GL owners: specify mode resolution, device classification, required probes, and package manifest. | Review can trace each requested mode to either a compatible device/output or a named failure. |
| 2. Dependency spike | Dependency/platform owners: obtain approval for one pinned Windows llvmpipe package and one separate Linux package. | Normal GL context, tiny draw/readback, and window present work on the exact set; missing capabilities are recorded. Stop if GL 4.6 requirements cannot be met. |
| 3. User-facing mode | `GameStartupSettings`, settings application, `Engine.Windows`, desktop factory, `InitGL`: add opt-in policy, loader selection, diagnostics, and profile admission. | Software mode cannot silently select hardware; hardware cannot silently select CPU; default startup stays unchanged. |
| 4. Useful first release fixture | Pipeline/UI owners: FPS overlay, control gallery, and bounded basic 3D scene in a normal window. | The functional and performance rows below pass on named release CPUs. No full editor conversion is required. |
| 5. Output and distribution | Platform owners: qualify offscreen context/readback and signed or verified package supply. | Each supported platform/output has its own lifecycle and clean-machine evidence. |
| 6. Later Vulkan lane | Vulkan owner: qualify lavapipe against current device, shader, scene, UI, and presentation contracts. | The same product checks pass, including compiler supply and teardown; no success inferred from the narrow existing fixture. |

If GL is blocked, record the exact unmet requirement and compare the cost of a
bounded GL change with qualifying Vulkan. Obtain a new decision; do not silently
reverse the evaluation order or downgrade context requirements.

## Acceptance contract

The [software rendering validation matrix](../../testing/rendering/software-rendering-validation.md)
owns the proposed runtime procedures, performance budgets, platform rows, and
measurement protocol. Release requires explicit policy selection, correct UI
and basic scenes, bounded resource lifetime, qualified packages, and a measured
interactive tier on a named minimum CPU. All checks remain planned. A narrow
offscreen success cannot qualify the windowed product.

## Risks and open decisions

- GL extension requirements may exceed the selected llvmpipe package. The first
  spike must reject this clearly before larger integration work.
- A software-compatible basic recipe may require work in optional-pass admission.
  Keep that change local to the explicit profile and preserve authored assets.
- CPU rendering, UI layout, physics, and shader compilation can compete for the
  same cores. Worker caps and end-to-end latency decide the release budget.
- Native libraries, remote desktops, DPI, and window systems vary by platform.
  Choose the minimum supported CPU and exact release rows after measurements.
- Decide whether display-free GL merits a new context adapter before promising
  it. Existing Vulkan offscreen support remains a separate useful lane.
- Owner decisions remain open for package source/version, distribution method,
  minimum CPU/RAM, and any features above the basic tier.

## References

[startup]: ../../../../XREngine.Runtime.Host/Settings/GameStartupSettings.cs
[windows]: ../../../../XREngine.Runtime.Host/RenderingHost/Engine.Windows.cs
[fallback]: ../../../../XREngine.Data/Core/Enums/RenderBackendFallbackPolicy.cs
[desktop]: ../../../../XREngine.Runtime.Platform.Desktop/Windowing/DesktopSilkWindowBackendFactory.cs
[gl-init]: ../../../../XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Bootstrap/OpenGLRenderer.Initialization.cs
[pipeline]: ../../../architecture/rendering/default-render-pipeline-notes.md
[mesh]: ../../../architecture/rendering/mesh-submission-strategies.md
[ui-pipeline]: ../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Types/UserInterfaceRenderPipeline.cs
[ui-batch]: ../../../../XREngine.Runtime.Rendering/Rendering/UI/UIBatchCollector.cs
[vk-device]: ../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.PhysicalDeviceSelection.cs
[vk-shader]: ../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderCompiler.cs
[vk-host]: ../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Presentationless/VulkanExplicitTargetRendererHost.cs
[vk-env]: ../../../../XREngine.UnitTests/SoftwareVulkan/SoftwareVulkanEnvironment.cs
[bootstrap]: ../../../../XREngine.Runtime.Bootstrap/RuntimeApplicationBootstrap.cs
[render-bootstrap]: ../../../../XREngine.Runtime.Bootstrap/RenderingHost/RuntimeRenderingBootstrap.cs
[server]: ../../../../XREngine.Server/Program.cs
[vulkan-validation]: ../../../developer-guides/testing/software-vulkan-validation.md
[legal]: ../../../../LEGAL/README.md
[license]: ../../../../LICENSE.md
[mesa-llvm]: https://docs.mesa3d.org/drivers/llvmpipe.html
[mesa-tree]: https://docs.mesa3d.org/sourcetree.html
[mesa-egl]: https://docs.mesa3d.org/egl.html
[mesa-env]: https://docs.mesa3d.org/envvars.html
[mesa-license]: https://docs.mesa3d.org/license.html
[swiftshader]: https://github.com/google/swiftshader
