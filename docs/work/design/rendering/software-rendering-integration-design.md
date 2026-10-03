# Software Rendering Integration Design

**Project:** XRENGINE  
**Status:** Proposal; implementation is not authorized by this document  
**Date:** September 30, 2026  
**Reviewed baseline:** `engine-validation` at `de9ee0d4018668c9b961be82a7e138266c4a0feb`

## Decision and purpose

Make CPU software rendering an explicit execution mode of the existing graphics backends. Start with the production Vulkan renderer on Mesa lavapipe. Reuse scene submission, shaders, materials, compute dispatch, synchronization, resource lifetime, and UI rendering. Do not introduce a separate rasterizer, scene database, or material implementation.

The first product goal is repeatable development and correctness validation on computers without an exposed hardware GPU. The next goal is a usable editor for small scenes, asset inspection, screenshots, and remote debugging. Real-time VR and performance equivalence with a GPU are not goals. Frame rates must be measured on named CPUs and scenes.

Keep three independent concepts visible:

- **Headless logic:** no graphics device; evaluates simulation, animation, calibration, and asset processing.
- **Offscreen graphics:** a real Vulkan/OpenGL device renders without a window, using either hardware or a software driver.
- **Windowed graphics:** the same rendering backend presents to a desktop window. A CPU driver can draw a normal editor interface, subject to API, platform, and performance constraints.

## Verified starting point

The committed [software Vulkan validation suite](../../../../XREngine.UnitTests/SoftwareVulkan/Program.cs) already runs the production explicit-target host. Its qualified execution passed presentationless service lifecycle checks, repeated clear/readback and frame-slot reuse, fullscreen shader image checks, compute buffer output, and standard plus synchronization validation. It reported zero validation warnings/errors before renderer teardown. This does not establish teardown validation, a complete scene, the editor UI, presentation, or VR behavior.

That execution used Mesa 25.0.7 llvmpipe and an explicit Debian shaderc 2025.2 override. The bundled native compiler failed the engine's Vulkan 1.4 / SPIR-V 1.6 target. No managed package upgrade was made. Preserve this distinction: overriding the compiler proves an execution configuration, not qualification of the default package set. See [reproduction and limitations](../../../developer-guides/testing/software-vulkan-validation.md).

The Vulkan physical-device policy requires Vulkan 1.4. The Vulkan project and editor are Windows-targeted; a successful narrow portable validation build does not prove every editor dependency is portable. The normal editor launch, ImGui multi-window behavior, native import/physics/audio integrations, and packaging still need explicit qualification.

## Scope and exclusions

### In scope

- Explicit CPU adapter/driver selection for offscreen and windowed runs.
- A reproducible, versioned driver and shader-compiler dependency contract.
- Capability-based feature selection with clear diagnostics.
- A bounded software quality preset and responsive editor scheduling.
- Production-path scene, shader, compute, UI, presentation, and lifetime tests.
- Windows and Linux qualification as separate release targets.

### Out of scope

- A custom C# CPU rasterizer or duplicated software shader implementations.
- Silent substitution when the user requested hardware acceleration.
- Rewriting GPU skinning, culling, or animation merely because their compute shaders execute on a CPU driver.
- Implicit OpenXR fallback, headset frame-rate guarantees, or bypassing runtime-selected adapter requirements.
- Automatic installation of arbitrary ICDs, downloading native libraries at every launch, or changing system-wide driver settings.
- Treating software results as representative GPU performance or proof of vendor-specific extension behavior.

## Configuration and startup contract

Extend existing renderer/backend configuration rather than building a parallel launch system. The following names express proposed semantics; they are not implemented flags:

| Setting | Proposed meaning |
|---|---|
| Adapter preference | Hardware, software, or an explicit device identifier; existing behavior remains default |
| Software driver manifest | Optional explicit validated ICD path, selected before loader initialization |
| Software quality profile | Conservative resolution, effects, frame cadence, and worker budget |
| Validation policy | Required for CI, optional for interactive use, with an explicit skipped result if unavailable |
| Compiler provenance | Report selected native compiler/version and shader target |

Startup order:

1. Resolve requested backend, execution mode, output mode, and platform.
2. Resolve trusted driver/compiler dependencies and validate path/version compatibility.
3. Apply process-local native loader configuration before any Vulkan instance or shader compiler is created. The existing test helper demonstrates native environment propagation; do not rely only on managed environment mutation.
4. Enumerate adapters, then require the selected adapter to match the requested software/hardware policy. Record adapter type, name, vendor/device IDs, driver/API version, enabled extensions/features, and queue/presentation capabilities.
5. Evaluate required capabilities for the requested render path and output surface. Reject an unavailable requirement with a specific error; apply only documented optional reductions.
6. Create the existing renderer and normal application composition. Use the existing presentationless composition only for offscreen execution.
7. Display a persistent software-rendering indicator and selected quality profile in the editor diagnostics.

An explicit software request must never silently select a hardware GPU. An explicit hardware or XR request must never silently select a CPU adapter. Avoid environment-wide ICD changes that interfere with other programs. Backend changes requiring a different native driver should use an explicit renderer/process restart with ownership-safe teardown.

## Renderer reuse and capability policy

The entry points to extend are the existing Vulkan device-selection policy, frame-loop composition, desktop window backend, rendering settings, and editor diagnostics. Keep the current resource authority and frame-completion mechanisms; software drivers still execute Vulkan asynchronously and require correct synchronization.

Retain Vulkan 1.4 initially, because it is the current renderer and shader contract. Lowering the API version is a distinct compatibility project: it would require negotiating the shader target, extension/core promotion paths, feature usage, and cache keys together. A version string alone is insufficient.

Classify requirements explicitly:

| Category | Policy |
|---|---|
| Required device/API, resource, synchronization and output features | Fail startup if absent |
| Optional acceleration or vendor integrations | Disable through existing capability paths and report the reason |
| Expensive but supported passes | Quality-profile choices; never silently alter correctness tests |
| Unsupported material/shader features | Fail the affected asset visibly or use an already-defined compatible variant; no blank successful frame |
| OpenXR adapter/runtime requirements | Preserve explicit runtime selection; software mode is not an XR fallback |

Use Vulkan first because its real offscreen execution is already demonstrated. OpenGL llvmpipe is a possible second lane, but must satisfy XRENGINE's own context and extension requirements. Godot launching on OpenGL 4.5 does not prove XRENGINE's OpenGL 4.6 path works. Existing OpenGL tests that classify software-driver compute results as inconclusive must retain that status until independently justified and revised.

## Shader compiler and dependency packaging

Fix the native shaderc supply mismatch deliberately. Evaluate a compatible packaged version or an explicit supported external compiler location, obtain approval for dependency upgrades, and qualify both Windows and Linux binaries. A newer compiler override must not remain an undocumented developer-machine assumption.

Fingerprint the native compiler, target environment, relevant options, backend and driver-sensitive inputs in shader-cache policy. Invalidate artifacts when their contract changes. Test cache miss, cache hit, compiler failure, unsupported target, and corrupted/obsolete artifact behavior.

Record loader, ICD, compiler, LLVM/Mesa and validation-layer versions in the setup manifest and diagnostics. Prefer official vendor/distribution sources; verify expected artifact integrity and architecture. Review redistributability and notices for each shipped binary under the repository's legal/dependency process. Do not infer the license of a complete binary bundle from one upstream project's name.

Portable builds should not require Windows-only PDB copies or unavailable native SDKs when using a supported managed/portable path. Audit each native integration independently; disabling graphics acceleration does not make physics, importers, UI or audio dependencies portable. Keep optional native integrations explicit rather than swallowing load exceptions.

## Interactive editor and scheduling

Exercise the real editor/runtime startup through the existing desktop window backend. Validate surface creation, swapchain format/present support, resize, minimize/restore, DPI changes, input, ImGui docked and platform windows, scene loading and clean exit. No screenshot-only harness substitutes for this lane.

Provide conservative defaults, initially proposed as a small viewport, reduced optional post-processing/shadows, bounded frame cadence, and existing scene complexity controls. Tune concrete values from measurements. Separate UI responsiveness from scene redraw cadence where the current scheduler supports it; reuse the last completed scene frame safely when no redraw is needed.

CPU rendering competes with simulation, shader compilation and editor tasks. Coordinate the existing job scheduler and driver worker budget to avoid oversubscription; measure rather than assuming maximum thread counts are fastest. Avoid unbounded queues, busy loops, blocking readbacks, or replacing asynchronous work with synchronous waits.

Use completed-frame leases/fences for screenshots and previews. Test delayed consumers, rejected submissions, resize with outstanding readers, renderer restart and shutdown. Capture validation diagnostics through teardown in the expanded suite.

## Validation and acceptance

Keep independent results for headless logic, software offscreen, software windowed/editor, real-GPU rendering, and hardware XR. A skipped lane is never a pass.

Required evidence for the software integration:

- Default installed dependency set launches reproducibly without the ad hoc compiler override used in the initial experiment.
- Explicit CPU selection succeeds; absent/wrong ICD, wrong architecture, unsupported API and missing required features fail with useful diagnostics and nonzero status.
- Existing clear, image, compute and native-validation checks continue to pass; validation captures creation, submission and teardown.
- Representative static and skinned scenes render with materials, animation, lighting and depth. Add temporal effects only when their history has a deterministic acceptance rule.
- Image comparisons use defined tolerances and deterministic inputs; report differing pixels and reference configuration rather than require unqualified bit equality across drivers.
- Windowed output and ImGui work through resize, focus changes and repeated startup/shutdown, without device/lifetime warnings or hidden dropped operations.
- Shader-cache tests cover compiler/driver changes and cache invalidation.
- Record CPU model/core count, RAM, OS, dependency versions, scene, output dimensions, cold/warm shader times, frame-time distribution, peak memory, allocations and UI response latency.
- A hardware-GPU comparison lane protects existing behavior; software mode remains opt-in.

Do not accept the full editor on the basis of the five existing offscreen checks. A simple scene showing pixels is the first integration milestone, not complete rendering parity.

## Delivery sequence and effort

Estimates below are cumulative planning ranges for one experienced engineer, not elapsed-time promises. They assume reuse of the committed validation work and no major unexpected platform blockers.

| Deliverable | Rough cumulative effort | Exit evidence |
|---|---|---|
| Developer software mode | 2–4 working days | Explicit selection, qualified dependencies, repeatable offscreen scene checks and clear failures |
| Usable small-scene editor mode | 1–2 working weeks | Normal startup/presentation/UI, conservative profile, scene loading and lifecycle results |
| Supported Windows and Linux distribution | 2–4 working weeks | Both native dependency sets packaged, licensing reviewed, clean-machine reproducibility and automated matrix |

Begin with a dependency/platform spike: default compiler qualification, CPU adapter selection and one normal presented editor viewport. Re-estimate after that result. Dependency packaging and scene tests can progress in parallel once configuration contracts are stable; serialize renderer lifetime/device-selection changes with other Vulkan work.

The requested deliverable is this design on `engine-validation`. A future implementation may use a separate `software-rendering` branch from the appropriate reviewed commit. This proposal does not create that branch or claim any unimplemented option exists.

## Sources and related implementation

- [Committed validation baseline](https://github.com/BlackJaxDev/XRENGINE/commit/de9ee0d4018668c9b961be82a7e138266c4a0feb)
- [Software Vulkan executable](../../../../XREngine.UnitTests/SoftwareVulkan/Program.cs)
- [Native loader environment helper](../../../../XREngine.UnitTests/SoftwareVulkan/SoftwareVulkanEnvironment.cs)
- [Vulkan device selection](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.PhysicalDeviceSelection.cs)
- [Shader target configuration](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderCompiler.cs)
- [Desktop window backend](../../../../XREngine.Runtime.Platform.Desktop/Windowing/DesktopSilkWindowBackendFactory.cs)
- [Rendering invariants](../../../architecture/rendering/default-render-pipeline-notes.md) and [mesh submission contracts](../../../architecture/rendering/mesh-submission-strategies.md)
- [Mesa llvmpipe documentation](https://docs.mesa3d.org/drivers/llvmpipe.html) and [Mesa driver source layout including lavapipe](https://docs.mesa3d.org/sourcetree.html)

