# Runtime Modularization Phase 3 - Remaining Work

Reference design: [runtime-modularization-plan.md](../design/runtime-modularization-plan.md)
Rendering follow-on: [runtime-modularization-phase4-todo.md](runtime-modularization-phase4-todo.md)

Updated: 2026-07-19

This file contains only unfinished Runtime.Core and non-rendering prerequisite work. Completed Phase 3 history remains available in Git. Remaining rendering work has moved to the Phase 4 todo and is intentionally not duplicated here.

## Goal

Finish the Runtime.Core carve-out and the non-rendering prerequisites needed for later adapter and aggregator removal without introducing project cycles or forbidden dependencies.

## Current Remaining Inventory

The following source still physically compiles from `XRENGINE` and drives this checklist:

| Area | Current `.cs` files | Intended ownership |
|---|---:|---|
| `Scene/Physics/` | 31 | Runtime.Core |
| `Scene/Components/Physics/` | 20 | Runtime.Core |
| Other gameplay components | 24 | Runtime.Core, Runtime.InputIntegration, or Editor; rendering-owned files are tracked in Phase 4 |
| `Scene/Transforms/` | 1 | Primarily Runtime.Core |
| `Scene/Prefabs/` | 3 | Runtime.Core |
| `Game Modes/` | 4 | Runtime.Core or an integration/bootstrap layer |
| `Settings/` | 12 | Data, Runtime.Core, or Editor; rendering settings are tracked in Phase 4 |
| `Core/` | 118 | Data, Runtime.Core, or Editor; rendering/import bridges are tracked in Phase 4 |
| `Engine/` | 87 | Runtime.Core, Runtime.InputIntegration, or Profiler; rendering behavior is tracked in Phase 4 |

Recount and reclassify this inventory at the start of each workstream. Physical moves must follow compile-time ownership rather than directory names.

## Working Rules

- Keep dependencies one-way. Add a narrow lower contract or reorder a slice instead of adding a reverse project reference.
- Keep `XRENGINE` only as a temporary forwarding/composition facade while applications migrate.
- Change namespaces when ownership changes; update type redirects, reflection/AOT registration, serializers, and asset metadata in the same slice.
- Use `SetField(...)` for mutation paths on `XRBase` descendants.
- Treat allocations introduced in render, update, fixed-update, visibility, and present paths as defects unless justified by profiling.
- Validate the target assembly, `XRENGINE`, affected applications, and the nearest tests after every coherent slice.

## R0 - Prepare The Remaining Migration

- [x] Create a dedicated branch for the remaining Phase 3 migration work.
- [x] Re-run the physical ownership and project-reference inventory; record every production `.cs`, package/content dependency, native asset, generated source, and application reference still owned by `XRENGINE`.
- [x] Run and record the targeted animation-integration tests that remain as validation debt from the completed animation component move.
- [x] Run and record the targeted audio-integration tests that remain as validation debt from the completed audio component move.

## R1 - Carve Out The Remaining Runtime Core

### R1a - Physics

- [x] Move shared physics contracts plus the complete Jolt and Jitter implementations to `Runtime.Core`.
- [ ] Move the remaining scene-coupled PhysX implementation to `Runtime.Core`; Phase 4 retains rendering-only diagnostics.
- [x] Move backend-neutral physics authoring, rigid-body values, joint contracts, convex-hull inputs, and lower dispatch contracts to `Runtime.Core`.
- [x] Move `CharacterControllerComponent` and its contact-state contract to `Runtime.Core` behind lower world/thread dispatch services.
- [x] Move `StaticRigidBodyComponent` and its backend-neutral settings/authoring contracts to `Runtime.Core`.
- [ ] Move the remaining concrete `Scene/Components/Physics/` ownership to `Runtime.Core`; Phase 4 retains GPU/rendering-only components.
- [x] Transfer dependency-ready Jolt/Jitter/MagicPhysX package and native ownership to `Runtime.Core`.
- [ ] Remove residual physics package/configuration ownership from `XRENGINE.csproj` after its final concrete consumers move.
- [ ] Validate `Runtime.Core`, `Runtime.Rendering`, `XRENGINE`, Editor, and targeted physics tests.

### R1b - Gameplay Components

- [x] Move dependency-independent transform movement and movement-policy modules to `Runtime.Core`.
- [x] Move the VR/model height-scale components to `Runtime.InputIntegration` behind `RuntimeVrStateServices`.
- [ ] Move the remaining character/player movement components after their rendering and concrete-physics dependencies are lowered.
- [x] Move `Scene/Components/Networking/` to `Runtime.Core` (OSC integration is owned by AnimationIntegration).
- [x] Move all `Scene/Components/Volumes/` ownership, including `BlockingVolumeComponent`, to `Runtime.Core`.
- [x] Move `Scene/Components/Interaction/` to `Runtime.Core`.
- [x] Move `Scene/Components/Scripting/` to `Runtime.Core`.
- [x] Move runtime spline ownership to `Runtime.Core` and animation preview ownership to AnimationIntegration.
- [x] Move `PawnComponent` to `Runtime.Core` behind a narrow input/camera/viewport/UI host contract.
- [ ] Move `CharacterPawnComponent` after its remaining rendering and concrete-physics dependencies are lowered.
- [x] Move `OptionalInputSetComponent` and `ExternalOptionalInputSetComponent` to `Runtime.InputIntegration`.
- [ ] Move `VRPlayerInputSet` to `Runtime.InputIntegration` after its rendering and physics dependencies are available through owned references or narrow contracts.
- [x] Split `Scene/Components/Debug/`: runtime-only helpers are in `Runtime.Core`, editor-only tools are in Editor, and the remaining rendering diagnostics are assigned to Phase 4.
- [x] Move `Scene/Components/Editing/` to Editor, except any reusable runtime/rendering primitive that first needs extraction into a lower assembly.
- [x] Validate `Runtime.Core`, `Runtime.Rendering`, `Runtime.InputIntegration`, `XRENGINE`, Editor, Server, and the nearest gameplay tests.

### R1c - World, Transforms, Game Modes, And Prefabs

- [x] Move the non-VR transforms remaining under `Scene/Transforms/` to `Runtime.Core`; expose a narrow lower contract for any rendering-only transform behavior tracked by Phase 4.
- [ ] Move the scene/world ownership skeleton and `WorldSettings` to `Runtime.Core` or value-only settings contracts to Data without introducing a `Runtime.Core -> Runtime.Rendering` reference.
- [x] Move scene-prefab override metadata, node mapping, traversal, and hierarchy helpers to `Runtime.Core`.
- [ ] Move the remaining prefab source/variant, serialization, cloning, asset-save, and world-attachment ownership out of the facade.
- [x] Classify the six game-mode files: host-independent `GameMode`, `CustomGameMode`, and registry ownership is in `Runtime.Core`; flying-camera, locomotion, and VR composition modes remain in the facade composition layer.
- [ ] Validate world creation, prefab loading, transform behavior, and Editor/Server/VRClient startup paths.

### R1d - Settings And Core Utilities

- [ ] Split `Settings/`: value-only shared settings go to Data, runtime lifecycle settings to `Runtime.Core`, and editor preferences/secrets to Editor; Phase 4 owns rendering/backend settings.
- [x] Move dependency-ready runtime reflection metadata, type redirects, serialization contracts, lifecycle enums, and generic tools to `Runtime.Core`.
- [ ] Finish splitting `Core/` runtime time, asset/runtime orchestration, serialization, and editor state; Phase 4 owns rendering/model-import bridges.
- [x] Move the residual top-level `UdpSocketOptions` networking type to `Runtime.Core`.
- [ ] Retire or relocate the facade-owned physics global using after its remaining consumers move.
- [x] Move MagicPhysX native output ownership and dependency-ready runtime packages out of `XRENGINE.csproj`.
- [ ] Migrate the remaining package/content/native dependencies as their final facade consumers move.
- [x] Validate `Runtime.Core` against the design rule that it references only Data and Extensions.

## R2 - Decompose The Engine Orchestrator

The `Engine` implementation is a partial type today. Partial declarations cannot span assemblies, so this work must extract separately owned runtime services/types and leave, at most, a thin forwarding/composition facade until `XRENGINE` is removed.

### R2a - Host-Independent Runtime Engine

- [x] Extract lower timing, app-thread dispatch, transform, physics, input, maintenance, animation, audio, network-discovery, and scene-streaming host-service seams.
- [ ] Finish extracting lifecycle, tick-list, settings application, project/startup state, logging, jobs, and shutdown coordination into Runtime.Core-owned types.
- [x] Extract host-independent LAN discovery settings/state and networking configuration orchestration behind Runtime.Core contracts.
- [ ] Finish extracting the remaining host-independent engine state and networking orchestration.
- [x] Migrate completed transform, physics-backend, BCL-networking, audio, and animation-host callers to lower runtime APIs.
- [ ] Migrate the remaining production callers away from the legacy partial `Engine` identity.
- [ ] Validate `Runtime.Core`, `XRENGINE`, Editor, Server, and unit tests.

### R2b - Input, VR, Profiling, And Lower Runtime Services

- [ ] Move VR state/input behavior to `Runtime.InputIntegration` while keeping rendering presentation behind Runtime.Rendering contracts.
- [ ] Move profiler transport/capture behavior to `Runtime.Core` or `XREngine.Profiler`, with application wiring at the composition root.
- [ ] Move scene-node, transform, world-object, and player-controller host implementations to their owning runtime assemblies or application composition roots.
- [ ] Remove the remaining production reliance on the legacy `Engine` facade.

## R3 - Migrate Consumers And Remove The Aggregator

- [ ] Ensure every production file still under `XRENGINE` has moved or is an explicitly temporary forwarding facade; remove the facades after their consumers migrate.
- [ ] Remove direct facade project references from Editor, Server, and VRClient in favor of runtime/bootstrap references.
- [ ] Migrate UnitTests, Benchmarks, MonkeyBallVR, and remaining tools to direct runtime references.
- [x] Remove `Runtime.AudioIntegration -> XRENGINE` and migrate all AudioIntegration `Engine.*`/`EngineTimer` calls to its lower host service.
- [x] Remove `Runtime.AnimationIntegration -> XRENGINE` after lowering camera, debug-text, asset-loading, spline-preview, and stale PhysX dependencies.
- [ ] Remove all remaining project references to `XRENGINE.csproj` and verify the graph against the design's allowed/forbidden dependency rules.
- [ ] Remove `XRENGINE.csproj` from the solution and delete the project after its package/content/native ownership has been transferred.
- [ ] Regenerate `docs/DEPENDENCIES.md` and license outputs if package or project ownership changes require it.

## R4 - Final Validation And Closeout

- [x] Build `Runtime.Core`, `Runtime.Rendering`, all integration projects, Bootstrap, Editor, Server, VRClient, UnitTests, and the full solution.
- [ ] Run the targeted core, rendering, physics, animation, audio, input/VR, modeling, bootstrap, and project-graph tests.
- [ ] Launch and smoke-test Editor, Server, and VRClient through their canonical tasks/profiles.
- [ ] Verify no forbidden dependency, duplicate public type identity, stale type redirect, reflection/AOT registration failure, or new compiler warning remains.
- [ ] Update the reference design and durable docs so they describe the final assembly graph and no longer name Phase 3 as the active execution-status document.
- [ ] Merge the dedicated Phase 3 branch back into `main` after all validation passes.
