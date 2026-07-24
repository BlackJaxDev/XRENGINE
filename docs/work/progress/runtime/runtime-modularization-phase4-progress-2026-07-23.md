# Runtime Modularization Phase 4 Progress

Started: 2026-07-23  
Branch: `codex/runtime-modularization-phase4`  
Integration base: `3a4e695e` (`rendering-vulkan-core-hardening`)

## Scope

This ledger tracks implementation of P4.0 through P4.5 from
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

P4.8 remains the owner of the physical OpenGL/Vulkan leaf-DLL extraction and
the embedded Vulkan desktop frame-loop decomposition. P4.0-P4.5 establish the
stable contracts and concrete-reference allowlists required for that work but
do not claim the leaf assemblies already exist.
