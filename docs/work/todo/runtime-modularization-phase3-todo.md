# Runtime Modularization Phase 3 - Remaining Work

Reference design: [runtime-modularization-plan.md](../design/runtime-modularization-plan.md)
Rendering follow-on: [runtime-modularization-phase4-todo.md](runtime-modularization-phase4-todo.md)

Updated: 2026-07-14

This file contains only unfinished Runtime.Core and non-rendering prerequisite work. Completed Phase 3 history remains available in Git. Remaining rendering work has moved to the Phase 4 todo and is intentionally not duplicated here.

## Goal

Finish the Runtime.Core carve-out and the non-rendering prerequisites needed for later adapter and aggregator removal without introducing project cycles or forbidden dependencies.

## Current Remaining Inventory

The following source still physically compiles from `XRENGINE` and drives this checklist:

| Area | Current `.cs` files | Intended ownership |
|---|---:|---|
| `Scene/Physics/` | 68 | Runtime.Core |
| `Scene/Components/Physics/` | 28 | Runtime.Core |
| Other gameplay components | 38 | Runtime.Core, Runtime.InputIntegration, or Editor; rendering-owned files are tracked in Phase 4 |
| `Scene/Transforms/` | 18 | Primarily Runtime.Core |
| `Scene/Prefabs/` | 4 | Runtime.Core |
| `Game Modes/` | 6 | Runtime.Core or an integration/bootstrap layer |
| `Settings/` | 12 | Data, Runtime.Core, or Editor; rendering settings are tracked in Phase 4 |
| `Core/` | 147 | Data, Runtime.Core, or Editor; rendering/import bridges are tracked in Phase 4 |
| `Engine/` | 76 | Runtime.Core, Runtime.InputIntegration, or Profiler; rendering behavior is tracked in Phase 4 |

Recount and reclassify this inventory at the start of each workstream. Physical moves must follow compile-time ownership rather than directory names.

## Working Rules

- Keep dependencies one-way. Add a narrow lower contract or reorder a slice instead of adding a reverse project reference.
- Keep `XRENGINE` only as a temporary forwarding/composition facade while applications migrate.
- Change namespaces when ownership changes; update type redirects, reflection/AOT registration, serializers, and asset metadata in the same slice.
- Use `SetField(...)` for mutation paths on `XRBase` descendants.
- Treat allocations introduced in render, update, fixed-update, visibility, and present paths as defects unless justified by profiling.
- Validate the target assembly, `XRENGINE`, affected applications, and the nearest tests after every coherent slice.

## R0 - Prepare The Remaining Migration

- [ ] Create a dedicated branch for the remaining Phase 3 migration work.
- [ ] Re-run the physical ownership and project-reference inventory; record every production `.cs`, package/content dependency, native asset, generated source, and application reference still owned by `XRENGINE`.
- [ ] Run and record the targeted animation-integration tests that remain as validation debt from the completed animation component move.
- [ ] Run and record the targeted audio-integration tests that remain as validation debt from the completed audio component move.

## R1 - Carve Out The Remaining Runtime Core

### R1a - Physics

- [ ] Move `Scene/Physics/` to `Runtime.Core`, including shared contracts and the Jolt, PhysX, and Jitter implementations.
- [ ] Move `Scene/Components/Physics/` to `Runtime.Core`; expose the lower physics data/dispatch contracts needed by the Phase 4 GPU compute move.
- [ ] Move the required physics package references, native assets, runtime configuration, and bootstrap registration out of `XRENGINE.csproj`.
- [ ] Validate `Runtime.Core`, `Runtime.Rendering`, `XRENGINE`, Editor, and targeted physics tests.

### R1b - Gameplay Components

- [ ] Move `Scene/Components/Movement/` to `Runtime.Core`, preserving the existing lower VR/movement contracts.
- [ ] Move `Scene/Components/Networking/`, `Scene/Components/Volumes/`, `Scene/Components/Interaction/`, `Scene/Components/Scripting/`, and `Scene/Components/Splines/` to `Runtime.Core`.
- [ ] Move `PawnComponent`, `CharacterPawnComponent`, `OptionalInputSetComponent`, and `ExternalOptionalInputSetComponent` to `Runtime.Core`.
- [ ] Move `VRPlayerInputSet` to `Runtime.InputIntegration` after its rendering and physics dependencies are available through owned references or narrow contracts.
- [ ] Split `Scene/Components/Debug/`: move runtime-only helpers to `Runtime.Core` and editor visualization/tools to Editor; Phase 4 owns rendering diagnostics.
- [ ] Move `Scene/Components/Editing/` to Editor, except any reusable runtime/rendering primitive that first needs extraction into a lower assembly.
- [ ] Validate `Runtime.Core`, `Runtime.Rendering`, `Runtime.InputIntegration`, `XRENGINE`, Editor, Server, and the nearest gameplay tests.

### R1c - World, Transforms, Game Modes, And Prefabs

- [ ] Move the non-VR transforms remaining under `Scene/Transforms/` to `Runtime.Core`; expose a narrow lower contract for any rendering-only transform behavior tracked by Phase 4.
- [ ] Move the scene/world ownership skeleton and `WorldSettings` to `Runtime.Core` or value-only settings contracts to Data without introducing a `Runtime.Core -> Runtime.Rendering` reference.
- [ ] Move scene prefab ownership and override utilities to `Runtime.Core`.
- [ ] Classify the six game-mode files: host-independent orchestration goes to `Runtime.Core`; camera, VR, or composition-specific modes go to the appropriate integration or bootstrap layer.
- [ ] Validate world creation, prefab loading, transform behavior, and Editor/Server/VRClient startup paths.

### R1d - Settings And Core Utilities

- [ ] Split `Settings/`: value-only shared settings go to Data, runtime lifecycle settings to `Runtime.Core`, and editor preferences/secrets to Editor; Phase 4 owns rendering/backend settings.
- [ ] Split `Core/`: runtime utilities, time, asset/runtime orchestration, and serialization ownership go to `Runtime.Core` or Data, and editor attributes/state go to Editor; Phase 4 owns rendering/model-import bridges.
- [ ] Move the residual top-level networking option and shared global using to their owning lower assemblies.
- [ ] Migrate package/content/native dependencies from `XRENGINE.csproj` as their final consumers move.
- [ ] Validate `Runtime.Core` against the design rule that it references only Data and Extensions.

## R2 - Decompose The Engine Orchestrator

The `Engine` implementation is a partial type today. Partial declarations cannot span assemblies, so this work must extract separately owned runtime services/types and leave, at most, a thin forwarding/composition facade until `XRENGINE` is removed.

### R2a - Host-Independent Runtime Engine

- [ ] Extract lifecycle, threading, timing, tick-list, settings application, project/startup state, main-thread dispatch/logging, jobs, and shutdown coordination into Runtime.Core-owned types.
- [ ] Extract host-independent engine state and networking orchestration into `Runtime.Core` without concrete rendering, input, audio, animation, or modeling dependencies.
- [ ] Update callers to consume the Runtime.Core API instead of relying on the legacy partial `Engine` identity.
- [ ] Validate `Runtime.Core`, `XRENGINE`, Editor, Server, and unit tests.

### R2b - Input, VR, Profiling, And Lower Runtime Services

- [ ] Move VR state/input behavior to `Runtime.InputIntegration` while keeping rendering presentation behind Runtime.Rendering contracts.
- [ ] Move profiler transport/capture behavior to `Runtime.Core` or `XREngine.Profiler`, with application wiring at the composition root.
- [ ] Move scene-node, transform, world-object, and player-controller host implementations to their owning runtime assemblies or application composition roots.
- [ ] Remove the remaining production reliance on the legacy `Engine` facade.

## R3 - Migrate Consumers And Remove The Aggregator

- [ ] Ensure every production file still under `XRENGINE` has moved or is an explicitly temporary forwarding facade; remove the facades after their consumers migrate.
- [ ] Update Editor, Server, VRClient, samples, benchmarks, tests, and tools to reference their required runtime assemblies directly.
- [ ] Remove `Runtime.AnimationIntegration -> XRENGINE` and `Runtime.AudioIntegration -> XRENGINE` after their residual dependencies move.
- [ ] Remove all remaining project references to `XRENGINE.csproj` and verify the graph against the design's allowed/forbidden dependency rules.
- [ ] Remove `XRENGINE.csproj` from the solution and delete the project after its package/content/native ownership has been transferred.
- [ ] Regenerate `docs/DEPENDENCIES.md` and license outputs if package or project ownership changes require it.

## R4 - Final Validation And Closeout

- [ ] Build `Runtime.Core`, `Runtime.Rendering`, all integration projects, Bootstrap, Editor, Server, VRClient, UnitTests, and the full solution.
- [ ] Run the targeted core, rendering, physics, animation, audio, input/VR, modeling, bootstrap, and project-graph tests.
- [ ] Launch and smoke-test Editor, Server, and VRClient through their canonical tasks/profiles.
- [ ] Verify no forbidden dependency, duplicate public type identity, stale type redirect, reflection/AOT registration failure, or new compiler warning remains.
- [ ] Update the reference design and durable docs so they describe the final assembly graph and no longer name Phase 3 as the active execution-status document.
- [ ] Merge the dedicated Phase 3 branch back into `main` after all validation passes.
