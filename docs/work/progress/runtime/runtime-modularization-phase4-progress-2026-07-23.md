# Runtime Modularization Phase 4 Progress

Started: 2026-07-23  
Branch: `codex/runtime-modularization-phase4`  
Integration base: `3a4e695e` (`rendering-vulkan-core-hardening`)

## Scope

This ledger tracks implementation of P4.0 through P4.8c from
[runtime-modularization-phase4-todo.md](../../todo/runtime-modularization-phase4-todo.md).
The working tree already contained line-ending-only modifications to the Phase 4
and Vulkan frame-loop TODOs, a modified `OscCore-NET9` submodule worktree, and
untracked repository-managed dependency directories. Those pre-existing changes
are outside this implementation and must be preserved.

## P4.0 Baseline

The inventory below was measured from the integration-base tree before source
migration:

| Ownership area | C# files |
|---|---:|
| `XRENGINE/Scene/Components/UI/` | 83 |
| `XRENGINE/Functions/` | 65 |
| `XRENGINE/Rendering/Compute/` | 39 |
| `XRENGINE/Scene/Importers/` | 4 |
| `XRENGINE/Engine/Subclasses/Rendering/` | 33 |
| Runtime.Rendering OpenGL backend | 107 |
| Runtime.Rendering Vulkan backend | 364 |
| Root `Engine` render/window/viewport/shader/video/VR partial candidates | 44 |

The Phase 4 TODO's four-file compute estimate is stale. The current compute
folder contains 39 files because lower contracts and both concrete GPU backends
now live below that root.

`XREngine.Runtime.Rendering.csproj` directly referenced Animation, Audio, Data,
Extensions, Fbx, Input, Modeling, and Runtime.Core at baseline. Source-level
namespace/text references in Runtime.Rendering were found in 3 Animation files,
4 Audio files, 21 Input files, 1 Modeling file, and 2 Fbx files. Text matches for
XRENGINE and Editor names were also present, but require classification because
several are documentation strings or namespace-compatible type names rather
than project references.

Concrete backend references outside Runtime.Rendering at baseline:

| Consumer | OpenGL-related files | Vulkan-related files |
|---|---:|---:|
| Editor | 29 | 22 |
| Runtime.Bootstrap | 2 | 2 |
| XRENGINE facade | 15 | 15 |
| UnitTests | 43 | 66 |
| Server | 0 | 0 |
| VRClient | 0 | 0 |

Runtime.Rendering owned 77 package references, two generated C# files, one
runtime-native file under `runtimes/`, and four shader/content/native files
matched by the baseline content audit. Registration and serialization markers
occurred in 101 files across Runtime.Rendering, XRENGINE, and Editor; each moved
slice must narrow and update that set rather than assuming physical movement is
sufficient.

The accepted Phase 3 ownership contract remains:

- Runtime.Core owns world/scene lifecycle, transforms, scheduling, CPU physics,
  and lower runtime context contracts.
- Runtime.Rendering owns visual publication, render registration, rendering
  settings, GPU dispatch consumption, and presentation.
- Animation, Audio, Input, and Modeling/Fbx implementation stays in feature or
  integration/bridge assemblies.
- `IRuntimeRenderWorld`, `IRuntimeAmbientSettings`, and
  `IRuntimeAudioListenerWorld` remain the cross-layer identities; no duplicate
  `XRWorldInstance` or `WorldSettings` type is introduced.
- Runtime.Core must not reference Runtime.Rendering.

## Baseline Validation

- `dotnet build XRENGINE.slnx --no-restore -m:1 --nologo`: passed with 42
  warnings and zero errors. The warnings were pre-existing NuGet vulnerability
  notices for Magick.NET 14.13.1 and Magick.NET 14.13/14.14 assembly-version
  conflicts in application/benchmark outputs.
- `RuntimeRenderingHostServicesTests`: 13 passed.
- `RuntimeModularizationPhase3RenderingTests`: 6 failed because their
  source-contract paths still point at pre-Phase-3 locations such as
  `XRENGINE/Rendering/Commands/RenderCommandCollection.cs`,
  `XRENGINE/Rendering/Pipelines/XRRenderPipelineInstance.cs`,
  `XRENGINE/Rendering/VisualScene.cs`, and the removed
  `Runtime/RuntimeEngineFacade.cs`. These are recorded pre-existing stale test
  failures and must be repaired as part of the Phase 4 source-contract update.

## Implementation Ledger

| Slice | Status | Evidence |
|---|---|---|
| P4.0 branch, inventory, baseline, Phase 3 contract | Complete | This document and baseline commands above |
| P4.1 dependency normalization | Complete | Runtime.Rendering now references only Data, Extensions, and Runtime.Core; graph/source tests enforce the boundary |
| P4.2 UI and pawn move | Complete | Runtime UI moved to Rendering; device/controller bindings moved to InputIntegration; redirects and serializers updated |
| P4.3 function graphs and importers | Complete | Function graphs moved to Rendering; concrete import conversion moved to ModelingBridge; round-trip tests pass |
| P4.4 compute and render-world tail | Complete | Core owns canonical world lifecycle, Rendering owns publication/GPU coordination, and the facade only composes them |
| P4.5 focused host capabilities | Complete | Focused cached capabilities, fail-fast/no-op policy, backend module catalog, and concrete-type allowlists are enforced |
| P4.6 rendering-owned Engine behavior | Complete | Runtime.Rendering owns render state, statistics, settings, windows, VR state, debug behavior, and render-thread hosting; legacy rendering partials are removed |
| P4.7 consumer and ownership cleanup | Complete | Production consumers use RuntimeEngine/focused contracts, concrete adapters install from Runtime.Bootstrap, and obsolete XRENGINE dependencies/facades are removed |

## Completed Ownership And Capability Boundaries

- `XREngine.Runtime.Rendering` has exactly three project dependencies:
  `XREngine.Data`, `XREngine.Extensions`, and `XREngine.Runtime.Core`.
- Runtime UI, font, web/video, Rive, function-graph, render serialization,
  render import, GPU-compute, and visual-world implementation now compile from
  their final P4.1-P4.4 owners. Device/controller bindings compile from
  `Runtime.InputIntegration`; Modeling/Fbx conversion compiles from
  `Runtime.ModelingBridge`.
- Runtime.Core owns the canonical `XRWorld`, `XRScene`, `WorldSettings`,
  `RootNodeCollection`, and world lifecycle types. The legacy
  `XRWorldInstance` facade composes Core lifecycle with Rendering publication
  state without duplicating those public identities.
- The transitional rendering host is split into cached focused capabilities for
  settings, timing, scheduling, diagnostics, statistics, debug drawing,
  profiling, assets, factories, presentation, and backend interop. Optional
  telemetry/debug services use allocation-free no-ops; required services fail
  with actionable diagnostics.
- A stable backend module catalog now owns backend IDs, metadata, capabilities,
  factories, lifecycle, reload limitations, registration leases, and static
  built-in registration. Concrete backend references are restricted by tested
  allowlists to backend integration files that remain scheduled for P4.8 leaf
  extraction.

## P4.0-P4.5 Acceptance Evidence

- Full clean restore/build: `dotnet build XRENGINE.slnx -m:1 --nologo -v:q`
  passed with zero warnings and zero errors.
- Runtime.Rendering, XRENGINE, Editor, Server, VRClient, and UnitTests targeted
  builds passed. A clean isolated restore exposed and fixed the required
  `SharpFont.Dependencies` conflict pins in XRENGINE, InputIntegration, and
  ModelingBridge.
- Phase 3/4 ownership, serialization, world/compute, host-capability,
  backend-catalog, concrete-boundary, UI, and import suites passed: 67 tests.
- Physics-chain, GPU-dispatch, selective-readback, Vulkan parity, and
  atmospheric-render coordination suites passed: 33 tests.
- OpenXR timing, stereo isolation, retry-policy, and SteamVR parity contract
  suites passed: 70 tests after updating moved paths and focused-capability
  expectations.
- Isolated named Editor sessions started under both Vulkan and OpenGL, answered
  MCP `ping`, and stopped through `Manage-McpEditorSession.ps1` without
  process-wide termination.
- The headless Server executable initialized and remained healthy for the
  bounded eight-second smoke window; only its exact launched process was then
  stopped.
- The dependency inventory generator was run after package moves. Its output
  was reviewed but not retained because it also incorporated unrelated
  pre-existing untracked dependency checkouts and rewrote license snapshots;
  no dependency versions or supply paths were changed by this phase.

## P4.6-P4.7 Ownership Closeout

- `RuntimeEngine` now owns the active window registry, viewport enumeration,
  render/window thread identity, render state, frame-output accounting,
  statistics, debug drawing, effective rendering settings, and process-wide VR
  state. Render-frame begin/complete entry points keep the timer-to-statistics
  lifecycle explicit.
- Concrete render-object, shader, video-streaming, VR-rendering, renderer/window,
  and composite rendering-host adapters now compile from
  `XREngine.Runtime.Bootstrap/RenderingHost/`. Editor, Server, and VRClient
  install them explicitly before engine startup; unconfigured startup fails
  with an actionable diagnostic.
- All production `Engine.Rendering.*`, `Engine.Windows`, and `Engine.VRState`
  consumers migrated to `RuntimeEngine` or focused capabilities. The public
  `Engine.VRState` facade and its legacy JSON input type were removed.
- Rendering settings DTOs, effective snapshots, backend settings, debug shape
  support, and all legacy `Engine.Rendering` implementation partials now
  compile from Runtime.Rendering. The remaining
  `EngineRenderingSettingsApplication`, `XRWorldInstance`, `Engine.Windows`,
  `EngineVrLifecycle`, and native window-pump files are application composition:
  they apply runtime settings and coordinate world, physics, audio, play-mode,
  native-window, and VR process lifecycles without owning the lower rendering
  implementation.
- The OpenXR render-world pre-collect path now requires and invokes
  `IRuntimeRenderWorld.GlobalPreCollectVisible()`; the aggregate world
  implements the hook, preventing the former default no-op from silently
  skipping visibility preparation.
- `XRENGINE.csproj` no longer carries obsolete rendering packages, OscCore,
  dead moved-source items, or user-specific NAudio.Lame content paths.
  Runtime.Core still references only Data and Extensions; Runtime.Rendering
  still references only Data, Extensions, and Runtime.Core. The pre-existing
  Runtime.Rendering friend access granted to the downstream XRENGINE
  composition facade remains so its established internal integration surface
  is not converted into dozens of public APIs; Runtime.Rendering consumes no
  XRENGINE types or project reference.

## P4.6-P4.7 Validation

- Runtime.Rendering, XRENGINE, Runtime.Bootstrap, Editor, Server, VRClient, and
  UnitTests targeted builds passed with zero warnings and zero errors after the
  ownership move.
- Phase 4 dependency, serialization, render-state, resolver, settings, window
  ownership, rendering statistics/profiler, and OpenXR presentation source and
  behavior suites passed: 202 tests, zero failures, zero skips.
- Source audits found no production calls to `Engine.Rendering`,
  `Engine.Windows`, `Engine.VRState`, or the removed concrete rendering-host
  classes, and no stale source-contract paths for the moved Phase 4 files.
- `git diff --check` passed; the only output was the repository's existing CRLF
  conversion warnings.

## P4.8a Mechanical Leaf-Assembly Extraction

- `XREngine.Runtime.Rendering.OpenGL` now owns `OpenGLRenderer`, GL API
  wrappers and resources, shader/program queues, shared-context workers, ImGui
  and platform-window integration, OpenGL OpenXR, GPU UI/video integration,
  texture-streaming providers, and the Ultralight embedded shaders.
- `XREngine.Runtime.Rendering.Vulkan` now owns `VulkanRenderer`, Vulkan
  device/swapchain/frame/command/resource/descriptor/pipeline/render-graph
  implementation, ImGui, Vulkan OpenXR, VMA, Shaderc, Streamline/DLSS, XeSS,
  video, texture streaming, and NVIDIA SDK/VMA native-copy ownership.
- The stable Runtime.Rendering kernel retains logical resources, render
  pipelines, windows/viewports, `AbstractRenderer`, renderer lifecycle and
  catalogs, settings and statistics, and backend-neutral contracts. It has no
  project or package dependency on either leaf and no direct Silk.NET OpenGL
  or Vulkan package reference.
- Backend-specific OpenXR session, swapchain-image, mirror, preview, and
  strict-SPS state lives in the leaves behind `IXrGraphicsBinding`; the kernel
  exposes only a narrow backend-neutral host context. Texture streaming,
  physics-chain compute, render capture, UI framebuffer interop, and vendor
  upscaling similarly cross the boundary through focused capabilities or
  registered providers.
- Runtime.Bootstrap statically registers both leaf modules without reflection,
  preserving the production/native-AOT composition path. XRENGINE, Editor,
  UnitTests, Benchmarks, Server, and VRClient receive the required leaf
  references through their intended composition graph.
- Both `XRENGINE.slnx` and the legacy `XRENGINE.sln` contain the kernel and two
  leaf projects. The Editor dependency manifest contains both leaf DLLs.

## P4.8a Validation

- Solution restore completed after the package ownership move.
- Runtime.Rendering, OpenGL leaf, Vulkan leaf, XRENGINE, Editor, UnitTests,
  and the full solution built with zero warnings and zero errors. Vulkan
  validation reused the repository-managed native bridge through
  `XREngineUseExistingNativeBridges=true`.
- Phase 4 dependency/catalog/concrete-boundary, OpenGL and Vulkan source
  contracts, OpenXR timing and strict-SPS, RVC, imported texture streaming,
  mesh parity, Vulkan P1, and physics-chain parity suites passed: 233 passed,
  zero failed, one intentional CI-workflow skip.
- The dependency inventory generator was run after package ownership changed.
  Its output was reviewed but not retained because the generator also
  incorporated unrelated pre-existing dependency checkouts and rewrote the
  repository-wide license snapshots; direct package ownership is enforced by
  the P4.8a project/source-contract tests.
- Source audits found no stale active backend implementation paths; the old
  kernel OpenGL/Vulkan implementation trees are absent and both leaf trees are
  present. `git diff --check` passed with only the repository's existing CRLF
  conversion warnings.

## P4.8b Vulkan Desktop Frame-Loop Decomposition

- `WindowRenderCallback` is now an 89-line coordinator in the Vulkan leaf. A
  stack-only attempt captures immutable frame/slot identity and carries typed
  acquire/upload ownership through preflight, slot preparation, acquire, image
  preparation, recording, submission, presentation, recovery, and finalization
  partials.
- Generic renderer frame-op/state APIs and render-object factory dispatch no
  longer live in the frame-loop owner.
- Desktop activity is atomically published for coherent OpenXR/device-loss
  observations. Desktop in-flight slots and OpenXR eye frame-data slots retain
  separate domains.
- Desktop attempt entry/exit and OpenXR's complete retirement check-and-drain
  interval share `_desktopFrameRetirementGate`, providing cross-thread
  exclusion between slot classification and retired-resource destruction.
- Acquire/present result policy is callable without a Vulkan device.
  `SuboptimalKhr` is treated as acquired work, surface loss fails visibly
  instead of entering a swapchain-only retry, and device loss prohibits new
  recovery queue work.
- Successful submit publishes timeline/acquire/upload ownership before
  fallible auxiliary work. Recovery and normal presentation share one tracked
  presentation primitive, and collect release remains before present.
- The final source map and automated/runtime evidence are recorded in
  [Vulkan Desktop Frame Loop Decomposition Progress](../rendering/vulkan-desktop-frame-loop-decomposition-progress-2026-07-24.md).

P4.8b implementation and focused automated validation are present, but its
runtime, visual, resize, validation-layer, OpenXR/OpenVR, supported-hardware
Streamline, and performance gates remain explicitly unvalidated. P4.8c remains
the owner of final consumer migration, collectible registration/retention
rules, packaging orchestration, and static-load validation.

## P4.8c Consumer Migration, Registration, Packaging, And Validation

P4.8c completed on 2026-07-29.

### Stable Consumer And Lifetime Boundary

- Editor diagnostics, previews, and backend panels now consume stable copied
  values and capability DTOs instead of retaining OpenGL wrappers. The obsolete
  GL-object editor registry and renderer type-forward were removed.
- Vendor upscaling is routed through `IRuntimeVendorUpscaleService` and
  `VendorUpscaleRuntime`; application settings no longer reference Vulkan
  DLSS/XeSS implementation types.
- Texture-streaming and web-renderer backend registration are lease-counted.
  `RuntimeRenderingHostServices` owns and disposes the installed module
  registrations, so replacement or teardown removes non-backend static roots.
- The OpenGL and Vulkan entry points use the same
  `IRendererBackendModuleEntry`/factory lifecycle for static and collectible
  registration. Backend-specific factories, exceptions, delegates, workers,
  `Type` objects, and handles remain inside their leaf generation.

### Static, Collectible, And AOT Composition

- `XRENGINE` and Editor no longer reference either renderer leaf. Editor also
  no longer owns the 22 Silk.NET OpenGL, WGL, Shaderc, Vulkan, extension, and
  Vulkan-loader packages that moved to the leaves.
- Bootstrap is the static composition root. The
  `XREngineRendererBackends=All|OpenGL|Vulkan` build property conditionally
  compiles module registration and project references without reflection.
- Generated/AOT launch projects resolve the Bootstrap project source and pass
  the selected backend property, so the linker sees explicit static factory
  registration. A single-backend request fails visibly if source composition
  is unavailable rather than silently packaging both leaves.
- Evaluated restore graphs for Editor, Server, and VRClient showed only the
  selected renderer leaf in OpenGL-only and Vulkan-only modes. Clean Bootstrap
  outputs independently confirmed that each single-backend mode contains the
  kernel plus only its selected leaf.
- Backend implementation tests and benchmarks intentionally retain direct leaf
  references where they validate leaf internals. Consumer-facing catalog,
  host, application, and published-launcher tests select by stable backend ID
  and module metadata.

### Validation Evidence

- XRENGINE, both renderer leaves, Editor, and UnitTests built successfully with
  zero errors.
- The P4.8c catalog, host, dependency-boundary, concrete-type-boundary,
  source-contract, and published-launcher selection group passed 69 tests with
  zero failures or skips.
- The collectible module fixture passed 9 tests, including 100 complete module
  registration/unregistration/unload cycles. Collectible backend builds disable
  shared compilation and MSBuild node reuse so compiler/build-server processes
  cannot retain staged assemblies.
- Static validation preceded collectible validation. The same named isolated
  editor build rendered Vulkan and OpenGL through the stable factory contract;
  two camera positions per backend produced changing screenshots. Vulkan logs
  contained no validation, device-loss, fatal, exception, or leak diagnostics,
  and OpenGL reported `NoError`.
- Complete wrapper, callback, worker, resource, and native-handle teardown is
  also covered by the completed
  [renderer hot-reload validation](../../todo/COMPLETED/rendering-backend-hot-reload-todo.md).
- `Tools/Reports/Generate-Dependencies.ps1 -NoPromptForUnknownLicenses` was run
  after the package moves. Network-restricted metadata lookups produced
  repository-wide offline license downgrades, so that unsafe generated license
  rewrite was reviewed and discarded. The known-license snapshots were
  preserved and `docs/DEPENDENCIES.md` was reconciled to the verified project
  ownership changes.

P4.8c is complete. Phase 4 remains open for the explicitly outstanding P4.8b
runtime/visual/hardware acceptance gates and P4.9 final validation.

## Phase 4 Production-Code Closeout

The remaining P4.8b production seams completed on 2026-07-29:

- renderer-local deterministic phase fault injection now covers acquire, image
  preparation, scene/overlay recording, submit, post-submit auxiliary work,
  present, and post-present auxiliary work without per-frame delegates or
  retained external callbacks;
- consecutive non-interactive acquire unavailability is owned by the
  allocation-free `VulkanDesktopAcquireAvailabilityTracker`, with successful
  acquire reset and bounded swapchain recovery;
- healthy queue-submit rejection settles all acquired/upload ownership before
  propagating a visible result-specific failure; and
- callback-tick observation uses an explicitly named timestamp distinct from
  GPU submission and presentation readiness.

All Phase 4 production code and required architecture/handoff documentation are
complete. Per the code-completion scope, no new test, runtime, hardware, or
performance execution is claimed for this final pass. P4.9 now contains only
deferred validation, integration review, and branch-promotion work.
