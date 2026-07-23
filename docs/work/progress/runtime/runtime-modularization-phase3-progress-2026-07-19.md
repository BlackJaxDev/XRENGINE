# Runtime Modularization Phase 3 Progress - 2026-07-19

This note records the live Phase 3 ownership and dependency inventory on branch
`codex/runtime-modularization-phase3`. It is a progress snapshot, not a replacement
for the user-maintained execution checklist in
[`runtime-modularization-phase3-todo.md`](../../todo/runtime-modularization-phase3-todo.md).
Rendering-bound work remains owned by the Phase 4 checklist.

## Current Source Ownership

Source counts below are from the live tree and exclude generated `bin` and `obj`
content.

| Project | Production `.cs` files |
|---|---:|
| `XREngine.Runtime.Core` | 379 |
| `XREngine.Runtime.AnimationIntegration` | 69 |
| `XREngine.Runtime.AudioIntegration` | 34 |
| `XREngine.Runtime.InputIntegration` | 28 |
| `XREngine.Runtime.Rendering` | 1,334 |
| `XREngine.Runtime.Bootstrap` | 19 |
| `XREngine.Editor` | 248 |
| `XRENGINE` facade | 464 |

The current worktree records 97 C# source moves into Runtime.Core, four into
Runtime.AnimationIntegration, and six into Editor. Newly added lower contracts,
host services, and facade adapters are not included in those rename counts.

### Slices now physically owned below the facade

- Transform ownership includes the base transform model and factory registry,
  `TransformNone`, rigid-body and non-rendering miscellaneous transforms, lagged
  transforms, and noise transforms in Runtime.Core. `SmoothedTransform` and
  `Spline3DTransform` are in Runtime.AnimationIntegration. `BillboardTransform`
  remains camera/rendering-bound for Phase 4.
- Runtime.Core owns lower physics contracts and authoring/service types, physics
  materials and replication policy, shared joint contracts, rigid-body value
  properties, Jitter2, Jolt, and the dependency-lowered PhysX foundation.
  `PhysxConvexMesh`, native geometry conversions, obstacle/query wrappers, and
  ref-counted mesh wrappers are now below the facade.
- Convex-hull input value types (`ConvexHullInput`, batch, source, and collection)
  are in Runtime.Core. Rendering/model extraction, the CoACD disk cache, and the
  concrete PhysX cooking orchestration remain in the facade until their rendering,
  asset, and host boundaries are separated.
- `TriggerVolumeComponent`, `BoostVolumeComponent`, `GravityVolumeComponent`, and
  `SceneStreamingVolumeComponent` are in Runtime.Core. Scene streaming uses
  `RuntimeSceneStreamingHostServices`; only `BlockingVolumeComponent` remains in
  the facade.
- Runtime.Core owns all BCL networking components, including LAN discovery, REST,
  TCP client/server, UDP, WebSocket, and webhook. OSC components are in
  Runtime.AnimationIntegration. The facade networking-component folder is empty.
- Runtime.Core owns interaction components, `TransformMovementComponent`,
  `Spline2DComponent`, `GameCSProjLoader`, `EFogMode`, `UdpSocketOptions`, the
  reusable `CustomUIComponent`, and lower prefab metadata/override helpers.
- Core utility movement now includes serializable interfaces, `EPlayModeState`,
  inspector/reflection attributes, `XRTypeRedirectRegistry`, and the focused
  delegate, expression, comparison, and remapping tools.
- Editor owns the transform editing tools and
  `ProjectionMatrixCombinerDebugComponent`. The 13 debug files left in the facade
  are render-submission/debug-visualization diagnostics reserved for Phase 4.

### Runtime host seams and dependency cleanup

- Runtime.Core exposes transform, physics, app-thread dispatch, input,
  maintenance, menu-item, scene-streaming, network-discovery, animation, runtime
  JSON, and related composition seams. The legacy `Engine` supplies adapters from
  the composition root.
- `IVRGameStartupSettings` is manifest-neutral. Runtime.Core no longer references
  `OpenVR.NET`; the concrete `VrManifest`/`IActionManifest` adaptation and casts
  live in higher layers that own OpenVR initialization.
- AudioIntegration no longer directly references `XRENGINE.csproj` and contains no
  `Engine.*` or `EngineTimer` calls. `RuntimeAudioIntegrationServices` is owned by
  `XREngine.Audio`; the engine adapter supplies its audio manager, timing, project
  path, and asset-loading behavior.
- AnimationIntegration no longer directly references `XRENGINE.csproj`; camera, debug-text, asset-loading, spline-preview, and stale PhysX dependencies now use owned lower services.

### Facade inventory still blocking R1/R2 completion

| Facade area | Remaining `.cs` files | Current classification/blocker |
|---|---:|---|
| `Scene/Physics/` | 31 | Remaining PhysX scene/actor implementation and rendering diagnostics; lower native wrappers have moved. |
| `Scene/Components/Physics/` | 20 | Concrete actors/controllers, CoACD extraction/cache orchestration, physics chain, and GPU soft-body code. |
| `Scene/Components/Movement/` | 2 | Character/player movement remains facade-owned; modules moved to Core and height scaling to InputIntegration. |
| `Scene/Components/Networking/` | 0 | Closed for the current component inventory. |
| `Scene/Components/Volumes/` | 0 | Closed; all volume components are in Runtime.Core. |
| `Scene/Components/Splines/` | 0 | Closed; preview ownership moved to AnimationIntegration. |
| `Scene/Components/Debug/` | 13 | All remaining files are Phase 4 rendering diagnostics. |
| `Scene/Transforms/` | 1 | `BillboardTransform` remains rendering/camera-bound. |
| `Scene/Prefabs/` | 3 | Source/variant and facade orchestration remain; lower metadata/override helpers have moved. |
| `Game Modes/` | 4 | Host-independent ownership moved to Runtime.Core; camera/VR composition remains in the facade. |
| `Settings/` | 12 | Runtime/data/editor/rendering settings split remains. |
| `Core/` | 118 | Residual assets/serialization, editor state, and rendering/import bridges remain mixed. |
| `Engine/` | 87 | Host adapters increased this count while removing lower-layer dependencies; final orchestrator decomposition remains an R2 gate. |

`Scene/Components/Interaction/`, `Scene/Components/Scripting/`,
`Scene/Components/Editing/`, and `Scene/Components/Networking/` are empty in the
facade.

## Project Graph

Runtime.Core follows the target project-reference rule exactly:

```text
XREngine.Runtime.Core
    -> XREngine.Data
    -> XREngine.Extensions
```

The live integration graph is:

```text
Runtime.InputIntegration
    -> Data, Extensions, Input, Runtime.Core, Runtime.Rendering

Runtime.AnimationIntegration
    -> OscCore, Animation, Data, Runtime.Core, Runtime.Rendering

Runtime.AudioIntegration
    -> Audio, Data, Runtime.AnimationIntegration, Runtime.Core, Runtime.Rendering

Runtime.Bootstrap
    -> Animation, Audio, Data, Extensions, Input
    -> all four runtime integration/core/rendering projects
    -> XRENGINE (transitional facade-owned composition)
```

AudioIntegration and AnimationIntegration both have their direct forbidden facade edges closed.

Seven projects now directly reference `XRENGINE.csproj`:

- `XREngine.Runtime.Bootstrap`
- `XREngine.Editor`
- `XREngine.UnitTests`
- `XREngine.Benchmarks`
- `Samples/MonkeyBallVR`
- `XREngine.Server`
- `XREngine.VRClient`

Editor, Server, and VRClient still have direct facade project references.
Aggregator deletion still requires the remaining application and
test/benchmark/sample consumers to migrate to direct runtime references.

## Package, Content, And Native Ownership

Runtime.Core directly owns `BitsKit`, `DotnetNoise`, `JoltPhysicsSharp`,
`MagicPhysX`, `Jitter2`, `MemoryPack`, `Newtonsoft.Json`, and `YamlDotNet`, along
with the Runtime.Core-owned MagicPhysX native output. Its former direct
`OpenVR.NET` assembly reference has been removed.

Ownership remains transitional. The facade retains packages and native/content
copying needed by concrete physics, rendering, audio, input, and import consumers.
Package declarations should leave the facade only after their last consumers and
output-copy responsibilities move. Dependency/license regeneration remains a
finalization gate after that transfer stabilizes.

## Validation Evidence

Evidence is under
`Build/_AgentValidation/20260719-runtime-modularization-phase3/`.

| Artifact | Result |
|---|---|
| `logs/baseline.binlog` | Full-solution baseline captured: 0 errors and 44 pre-existing warnings. |
| `logs/integrated-full-build.binlog` | Integrated full-build evidence captured after the first broad move set. |
| `logs/phase3-final-full-build.binlog` | Earlier full-build evidence after the initial dependency and ownership changes. |
| `logs/phase3-wrapup-full-build-rerun.binlog` | Current full solution build passed with 0 errors and 42 known Magick.NET warnings. |
| `logs/phase3-bootstrap-reference-fix.binlog` | Direct Bootstrap and full solution builds pass after declaring its transitional facade dependency. |
| `reports/phase3-wrapup-gameplay.trx` | Pawn, movement, and blocking-volume tests passed, 7/7. |
| `reports/baseline-animation-audio.trx` | 326 executed: 324 passed, 2 failed; exposed animation detach/reset and PCM16 normalization defects. |
| `reports/validation-debt-fixes.trx` | Exact debt fixes passed, 2/2. |
| `reports/phase3-transform-boundary.trx` | Initial transform boundary run: 14/15 passed. |
| `reports/phase3-transform-boundary-rerun.trx` | Transform boundary rerun passed, 15/15. |
| `reports/phase3-transform-legacy-aqn.trx` | Legacy transform assembly-qualified-name compatibility passed, 1/1. |
| `reports/phase3-jolt-boundary.trx` | Initial Jolt boundary run: 62/63 passed. |
| `reports/phase3-jolt-boundary-rerun.trx` | Jolt boundary rerun passed, 63/63. |
| `reports/phase3-runtime-dispatch-streaming.trx` | Runtime dispatch and scene-streaming tests passed, 4/4. |
| `reports/phase3-integrated-targeted.trx` | Initial integrated targeted run: 89/90 passed. |
| `reports/phase3-integrated-targeted-rerun.trx` | Integrated targeted rerun passed, 90/90. |

Additional narrow validation during the move included clean Runtime.Core,
Runtime.AudioIntegration, and facade builds after OpenVR/audio decoupling. The TRX
reruns close the targeted regressions they cover; they do not replace startup
smoke tests or final validation after the remaining AnimationIntegration and
aggregator edges are removed.

## Source-Path Hygiene

- Active AOT factory generation scans the facade, Runtime.Core,
  Runtime.InputIntegration, and Runtime.Rendering roots dynamically, so moved
  volume and lower component paths are discovered without a hand-maintained file
  list.
- Current guides and source-contract tests touched by completed moves were updated
  with their new paths where ownership was stable.
- Generated audit reports under `docs/work/audit/` retain historical source paths.
  They should be regenerated after the move set is final rather than hand-edited.
  Historical completed work notes remain unchanged unless they serve as current
  navigation.

## Next Completion Gates

1. Migrate the remaining application, test, benchmark, and sample facade references.
2. Finish concrete PhysX/physics-component separation while leaving GPU and
   rendering diagnostics to Phase 4.
3. Complete the remaining blocking volume, prefab, world/settings, core utility,
   game-mode, and pawn/VR input ownership work.
4. Migrate UnitTests, Benchmarks, and MonkeyBallVR to direct runtime references;
   transfer remaining package/native/content ownership and remove the aggregator.
5. Regenerate dependency/license and audit outputs, then run the complete R4 build,
   test, and startup-smoke matrix.

## Closeout - 2026-07-22

Phase 3 is complete. The earlier "Next Completion Gates" list mixed the
Runtime.Core carve-out with design Phase 4 rendering work and design Phase 6 facade
removal. The completed tracker now preserves those later gates as an explicit
handoff instead of treating them as blockers for the lower runtime kernel.

The final Phase 3 slice moved 30 PhysX source files from the facade into
`XREngine.Runtime.Core/Scene/Physics/Physx/`. This includes the native scene,
backend service, controllers, actors, shapes, materials, geometry adapter, cooker,
and joints. Their ownership namespace is now `XREngine.Scene.Physics.Physx`.
`InstancedDebugVisualizer.cs` is the only file left under the facade's
`Scene/Physics/Physx/` path because it directly owns renderer buffers, materials,
jobs, and editor visualization policy.

The move removed direct dependencies on the legacy `Engine`, concrete
`XRWorldInstance`, editor preferences, and renderer-buffer population from the
simulation backend. PhysX now consumes `RuntimePhysicsServices`,
`RuntimeThreadServices`, `IRuntimePhysicsWorldContext`, and
`IRuntimePhysicsStepListener`. The engine composition adapter supplies accurate
shutdown, thread, fixed-delta, and elapsed-tick state.

Current source counts after the move are 452 C# files in Runtime.Core, 1,381 in
Runtime.Rendering, 28 in Runtime.InputIntegration, 19 in Runtime.Bootstrap, and 610
in the transitional facade. Runtime.Core still has exactly two project references:
Data and Extensions. Bootstrap is the only direct project reference to the facade;
that compatibility edge is intentionally a Phase 6 removal gate.

Validation:

- Runtime.Core build: passed, 0 warnings and 0 errors.
- Transitional XRENGINE facade build: passed, 0 warnings and 0 errors.
- Targeted physics and boundary matrix: 73/73 passed. The rerun TRX is at
  `Build/_AgentValidation/20260719-vulkan-dlss/runtime-modularization-phase3-closeout/phase3-physics-rerun.trx`.
- Full 25-project solution build: passed, 0 warnings and 0 errors.
- Isolated Editor session `phase3-closeout-0722`: started, returned an MCP `ping`
  with `ok: true`, and stopped cleanly through the named session manager. Startup
  logs contain only unrelated optional-SDK/runtime warnings (missing Steam Audio,
  disabled/unavailable NVIDIA features, and expected deferred Vulkan startup
  frames); no Phase 3 physics or dependency failure was present.
