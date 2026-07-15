# Runtime Modularization Phase 4 - Remaining Rendering Move

Reference design: [runtime-modularization-plan.md](../design/runtime-modularization-plan.md)
Core prerequisite work: [runtime-modularization-phase3-todo.md](runtime-modularization-phase3-todo.md)

Created: 2026-07-14

This file contains only unfinished design Phase 4 work. Rendering backends, pipelines, render objects, models, renderable scene components, windows, viewports, cameras, and the shared rendering kernel already compile from `Runtime.Rendering` and are not repeated here.

## Goal

Complete the rendering move into `XREngine.Runtime.Rendering`, remove concrete rendering ownership from the legacy `Engine` and world host, split the transitional rendering-host facade into coherent capabilities, and make the rendering assembly obey the target one-way dependency graph.

`XRENGINE` remains a temporary forwarding/composition facade until the design Phase 6 removal. Phase 4 must not move Runtime.Core ownership upward or create a `Runtime.Core -> Runtime.Rendering` reference.

## Current Remaining Inventory

The following rendering-owned or rendering-boundary source still physically compiles from `XRENGINE`:

| Area | Current scope | Intended ownership |
|---|---:|---|
| `Scene/Components/UI/` | 83 files | Runtime.Rendering or Editor |
| Rendering-owned pawn/UI camera components | 4 files | Runtime.Rendering |
| `Functions/` | 65 files | Runtime.Rendering |
| `Rendering/Compute/` | 4 files | Runtime.Rendering after lower physics contracts land |
| Legacy render-world host | 7 files | Runtime.Core/Runtime.Rendering split or temporary facade |
| `Scene/Importers/` | 4 files | Runtime.Rendering, Runtime.ModelingBridge, or Editor |
| `Engine/Subclasses/Rendering/` | 33 files | Runtime.Rendering-owned services/types |
| Root `Engine` render/window/host partials | targeted audit required | Runtime.Rendering or application composition root |
| Rendering/backend settings and render import/serialization helpers | targeted audit required | Data, Runtime.Rendering, Runtime.ModelingBridge, or Editor |

The current `Runtime.Rendering` project also references `XREngine.Animation`, `XREngine.Audio`, `XREngine.Input`, `XREngine.Modeling`, and `XREngine.Fbx` beyond the Core/Data/Extensions dependencies in the design target. Phase 4 must remove, relocate, or explicitly redesign each of those edges rather than treating the current graph as final.

## Working Rules

- Move concrete implementation wholesale when ownership is clear; add lower contracts only at genuine lifecycle or cross-layer boundaries.
- Do not add `Runtime.Rendering -> XRENGINE` or `Runtime.Rendering -> Editor` references.
- Keep feature libraries below runtime layers. Feature-specific scene bindings belong in integration/bridge projects, not in the rendering kernel.
- Keep optional telemetry/debug capabilities allocation-free no-ops when uninstalled; required rendering capabilities must fail fast with actionable diagnostics.
- Change namespaces at move time and update serializers, reflection/AOT registration, type redirects, editor metadata, and asset compatibility in the same slice.
- Use `SetField(...)` for mutation paths on `XRBase` descendants and avoid new allocations in render/update/present hot paths.
- Build and test every coherent slice before starting the next dependency tier.

## P4.0 - Branch, Baseline, And Phase 3 Contract

- [ ] Create a dedicated branch for the Phase 4 todo list.
- [ ] Recount all rendering-owned source, generated code, packages, native assets, content files, application references, and reflection/AOT registrations still owned by `XRENGINE`.
- [ ] Record every direct project and source-level dependency from `Runtime.Rendering` to Animation, Audio, Input, Modeling, Fbx, `XRENGINE`, Editor, and application code.
- [ ] Capture the current targeted rendering-test and Editor/Server/VRClient build baseline, distinguishing unrelated pre-existing failures from Phase 4 regressions.
- [ ] Agree the Phase 3 handoff contracts for world ownership, physics/GPU dispatch data, assets/settings, transforms, scheduling, and lifecycle; do not duplicate their implementation in Rendering.

## P4.1 - Normalize The Rendering Dependency Boundary

- [ ] Classify every `Runtime.Rendering -> XREngine.Animation` use; move scene animation binding to `Runtime.AnimationIntegration` or depend on a lower value contract where rendering genuinely consumes animation output.
- [ ] Classify every `Runtime.Rendering -> XREngine.Audio` use; move audio/video scene binding to `Runtime.AudioIntegration` or expose a lower media contract without pulling audio implementation into Rendering.
- [ ] Classify every `Runtime.Rendering -> XREngine.Input` use; move device and viewport input routing to `Runtime.InputIntegration` while retaining only rendering-owned window/presentation contracts.
- [ ] Classify every `Runtime.Rendering -> XREngine.Modeling` and `XREngine.Fbx` use; move mesh conversion/import bridges to `Runtime.ModelingBridge` or document and implement a deliberate replacement bridge boundary.
- [ ] Move package, native library, and content ownership with the code that remains the final consumer; do not leave dependency cargo in `XRENGINE.csproj`.
- [ ] Add project/source-contract tests that enforce the approved Runtime.Rendering dependency set and reject new `XRENGINE`, Editor, application, or feature-implementation coupling.
- [ ] Validate `Runtime.Rendering`, all affected integration/bridge projects, and the project-reference graph.

## P4.2 - Move Runtime UI And Rendering-Owned Pawn Components

- [ ] Classify all files under `Scene/Components/UI/`: runtime UI, layout, text, video/web, Rive, interaction presentation, and function-node rendering go to `Runtime.Rendering`; editor-only UI goes to Editor.
- [ ] Define the UI/input boundary so device state and input routing remain in `Runtime.InputIntegration` while hit testing, layout, focus presentation, and rendering remain in `Runtime.Rendering`.
- [ ] Move `UICanvasComponent`, `UICanvasInputComponent`, `FlyingCameraPawn`, and `FlyingCameraPawnBaseComponent` to their final rendering/input owners without reintroducing concrete controller coupling.
- [ ] Transfer Rive, Ultralight, font/text, video, shader, native-runtime, and content dependencies to their actual owning projects.
- [ ] Replace direct legacy `Engine`, `XRWorldInstance`, pawn, or editor access with the lower world/lifecycle contracts or application composition wiring.
- [ ] Update namespaces, callers, serialized type identities, AOT factories, editor component metadata, and type redirects.
- [ ] Validate `Runtime.Rendering`, `Runtime.InputIntegration`, `XRENGINE`, Editor, UI layout/input tests, and representative Rive/web/video paths available on the machine.

## P4.3 - Move Function Graphs And Rendering Import/Serialization Helpers

- [ ] Move the remaining 65 material/shader function-graph files under `Functions/` to `Runtime.Rendering`.
- [ ] Separate reusable graph/value contracts from shader-generation implementation when doing so reduces feature or editor coupling without creating a speculative abstraction layer.
- [ ] Move rendering-dependent scene/model importers and render-asset serialization helpers from `Scene/Importers/` and `Core/` to `Runtime.Rendering`, `Runtime.ModelingBridge`, or Editor according to final ownership.
- [ ] Ensure `Runtime.ModelingBridge`, rather than `Runtime.Rendering`, owns conversions that require concrete Modeling or file-import library APIs.
- [ ] Update shader/material/model serialization, AOT registration, reflection lookups, editor factories, and type redirects for all moved types.
- [ ] Validate shader generation, material loading, model import, serialization round trips, `Runtime.Rendering`, `Runtime.ModelingBridge`, and Editor.

## P4.4 - Move The Compute And Legacy Render-World Tail

- [ ] Consume the Phase 3 lower physics data/dispatch contracts needed by soft-body and physics-chain GPU work without referencing concrete Runtime.Core physics implementations from hot rendering paths.
- [ ] Move the four files under `Rendering/Compute/` to `Runtime.Rendering` and keep CPU simulation/world ownership in `Runtime.Core`.
- [ ] Split `XRWorld`, `XRWorldInstance`, `XRScene`, `XRWorldObjectBase`, and their partials so Runtime.Core owns world/scene lifecycle while Runtime.Rendering owns render publication, visual-scene state, and render registration.
- [ ] Preserve the established `IRuntimeRenderWorld`, `IRuntimeAmbientSettings`, and `IRuntimeAudioListenerWorld` boundaries; do not create duplicate public `XRWorldInstance` or `WorldSettings` identities.
- [ ] Remove temporary render-world forwarding files after all consumers use the final Core/Rendering ownership APIs.
- [ ] Validate physics-chain/soft-body CPU-GPU coordination, world creation/destruction, render registration, Editor/Server startup, and representative OpenGL/Vulkan rendering.

## P4.5 - Split Runtime Rendering Host Capabilities

- [ ] Inventory every member and call site of `IRuntimeRenderingHostServices`; group responsibilities by required lifecycle, installation owner, thread affinity, and optionality before changing the API.
- [ ] Extract capability-focused contracts for render settings, frame timing, render-thread scheduling, diagnostics/logging, render statistics, debug drawing, profiling, asset/texture IO, renderer/window/panel factories, VR/OpenXR presentation, and backend interop.
- [ ] Separate frequently read hot-path state from cold configuration/diagnostic services so capability resolution does not add per-frame allocation, boxing, lookup, or delegate-capture overhead.
- [ ] Keep `RuntimeRenderingHostServices.Current` only as a temporary composite/installation facade while call sites migrate to focused capabilities.
- [ ] Provide explicit allocation-free no-op implementations only for optional telemetry/debug capabilities.
- [ ] Make required renderer, scheduling, presentation, asset IO, and backend capabilities fail fast with actionable diagnostics when no host implementation is installed.
- [ ] Move concrete capability implementations to `Runtime.Rendering` when they are runtime rendering behavior and to Editor/Server/VRClient composition roots when they are application policy.
- [ ] Add source-contract and behavioral tests for capability ownership, defaults, installation, replacement, teardown, and missing-required-service failures.
- [ ] Validate representative OpenGL, Vulkan, desktop-window, headless/server, and OpenXR call paths after each capability slice.

## P4.6 - Extract Rendering-Owned Engine Behavior

The legacy `Engine` implementation is a partial type, and partial declarations cannot span assemblies. Extract independently owned Runtime.Rendering types/services and retain only temporary forwarding methods in `XRENGINE` while consumers migrate.

- [ ] Extract rendering state, statistics, frame-output accounting, debug/profiling hooks, render-thread hosting, window management, viewport rebinding, and effective rendering settings into Runtime.Rendering-owned types.
- [ ] Extract or move the concrete render-object, shader, video-streaming, VR-rendering, renderer/window, and rendering-host capability implementations to `Runtime.Rendering` or the application composition root.
- [ ] Replace `Engine.Rendering.*`, `Engine.Windows`, and related static/partial call sites with explicit Runtime.Rendering services or narrow contracts.
- [ ] Preserve startup/shutdown ordering, render-thread affinity, event subscription symmetry, resource teardown, device-loss reporting, and multi-window behavior during extraction.
- [ ] Remove the rendering-owned legacy `Engine` partials and forwarding members after all production consumers migrate.
- [ ] Validate `Runtime.Rendering`, `XRENGINE`, Editor, Server, VRClient, rendering statistics/profiler tests, window lifecycle tests, and OpenXR presentation tests.

## P4.7 - Migrate Consumers And Remove Rendering Ownership From XRENGINE

- [ ] Update Editor, Server, VRClient, Bootstrap, integration projects, samples, benchmarks, tests, and tools to reference the final Runtime.Rendering APIs directly.
- [ ] Remove all rendering implementation, UI, function-graph, compute, import, settings, serialization, host-service, and engine-rendering source from `XRENGINE` after consumers migrate.
- [ ] Remove obsolete project/package/native/content references from `XRENGINE.csproj` and verify each dependency has exactly one intentional owner.
- [ ] Remove obsolete compatibility facades, duplicate defaults, type redirects, and reflection fallbacks once their migration window closes.
- [ ] Verify `Runtime.Core` has no project or source dependency on `Runtime.Rendering` and consumes only approved lower render contracts.
- [ ] Verify `Runtime.Rendering` has no dependency on `XRENGINE`, Editor, applications, or feature implementations outside the final approved graph.

## P4.8 - Final Validation And Closeout

- [ ] Build `Runtime.Core`, `Runtime.Rendering`, all integration/bridge projects, Bootstrap, Editor, Server, VRClient, UnitTests, and the full solution.
- [ ] Run targeted UI, shader/function graph, model import/serialization, world/render registration, compute/physics-chain, rendering-host capability, window, OpenGL, Vulkan, OpenXR, and project-graph tests.
- [ ] Launch and smoke-test Editor, Server, and VRClient through their canonical tasks/profiles, including at least one desktop render path and the available XR path.
- [ ] Verify no new compiler warning, forbidden dependency, duplicate public type identity, stale serialized/AOT type, hot-path allocation, silent required-capability fallback, or resource-lifetime regression remains.
- [ ] Regenerate `docs/DEPENDENCIES.md` and license outputs after final project/package/native ownership changes, then review the resulting dependency graph and licenses.
- [ ] Update the reference design and Phase 3 handoff notes with the final Runtime.Rendering boundary and identify the remaining design Phase 5 adapter cleanup.
- [ ] Merge the dedicated Phase 4 branch back into `main` after all Phase 4 validation passes.
