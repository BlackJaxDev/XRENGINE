# Runtime Modularization Phase 3 - Physical Code Moves & XRENGINE Reduction

Reference design: [runtime-modularization-plan.md](../design/runtime-modularization-plan.md)
Previous phase: Phase 2 is complete and folded into the current modularization plan and Phase 3 cleanup.

Created: 2026-03-16

## Goal

Phase 2 established runtime service seams, project structure, and one-way dependency rules. The code contracts are in the right assemblies, but the bulk of implementation still physically compiles from `XRENGINE`. Phase 3 moves that implementation code into its target assemblies so `XRENGINE` can eventually be deleted (design doc Phase 6, Option A).

## Current State

What Phase 2 delivered:

- `Runtime.Core` owns: jobs, networking contracts, AOT metadata, startup/discovery contracts, base scene graph (`SceneNode`, `XRComponent`, `TransformBase`, `Transform`), transform host services, child-placement seam, `DefaultLayers`, `RuntimeWorldObjectBase`, state-change metadata.
- `Runtime.Rendering` owns: render-object ownership seam, shader/program/buffer runtime types, lower rendering objects (`XRTexture*`, `XRMaterial*`, `XRMesh`, `XRFrameBuffer`, etc.), material shader-parameter types, cubemap mip data, video upload backends, material render-policy types, higher host-service seams for window/viewport/pipeline/renderer lifecycle.
- `Runtime.Bootstrap` consumes only runtime-layer projects (no `XRENGINE` reference).
- Integration projects each own a first coherent bootstrap slice.
- `IBootstrapEditorBridge` narrowed to editor-only UI.
- Project graph is clean: no forbidden dependency violations.

What still lives in `XRENGINE` (~1000+ .cs files across these categories):

| Category | Location | File Count | Target Assembly |
|----------|----------|------------|-----------------|
| Engine lifecycle & threading | `Engine/` | ~69 | Runtime.Core (host-independent partials) |
| Player controllers | `Input/` | 5 | Runtime.InputIntegration |
| Rendering backends & pipelines | `Rendering/` | ~500+ | Runtime.Rendering |
| Animation components | `Scene/Components/Animation/` | ~63 | Runtime.AnimationIntegration |
| Audio components | `Scene/Components/Audio/` | ~32 | Runtime.AudioIntegration |
| Physics scene + components | `Scene/Physics/` + `Scene/Components/Physics/` | ~81 | Runtime.Core (defer dedicated assembly) |
| UI components | `Scene/Components/UI/` | ~82 | Runtime.Rendering (UI renders) |
| Mesh/model components | `Scene/Components/Mesh/` | ~10 | Runtime.Rendering |
| Light components | `Scene/Components/Lights/` | ~9 | Runtime.Rendering |
| Camera components | `Scene/Components/Camera/` | ~3 | Runtime.Rendering |
| Particle components | `Scene/Components/Particles/` | ~21 | Runtime.Rendering |
| Capture components | `Scene/Components/Capture/` | ~6 | Runtime.Rendering |
| Movement components | `Scene/Components/Movement/` | ~10 | Runtime.Core |
| Networking components | `Scene/Components/Networking/` | ~9 | Runtime.Core |
| VR components | `Scene/Components/VR/` | ~6 | Runtime.InputIntegration |
| Interaction components | `Scene/Components/Interaction/` | ~2 | Runtime.Core |
| Scripting components | `Scene/Components/Scripting/` | ~1 | Runtime.Core |
| Pawn components | `Scene/Components/Pawns/` | ~9 | Runtime.Core |
| Volume components | `Scene/Components/Volumes/` | ~5 | Runtime.Core |
| Debug components | `Scene/Components/Debug/` | ~15 | Runtime.Core or Editor |
| Editing components | `Scene/Components/Editing/` | ~5 | Editor |
| Landscape components | `Scene/Components/Landscape/` | ~15 | Runtime.Rendering |
| Spline components | `Scene/Components/Splines/` | ~2 | Runtime.Core |
| Misc components | `Scene/Components/Misc/` | ~3 | Runtime.Rendering |
| Scene transforms | `Scene/Transforms/` | ~36 | Split: VR → InputIntegration, rest → Core |
| Functions/shader graph | `Functions/` | ~82 | Runtime.Rendering |
| Game modes | `Game Modes/` | ~5 | Runtime.Core |
| Models | `Models/` | ~38 | Runtime.Rendering |
| Settings | `Settings/` | ~5 | Split: editor prefs → Editor, runtime → Core |
| Core utilities | `Core/` | ~100 | Split: editor attrs → Editor, runtime → Core/Data |

## Execution Strategy

Move code in dependency order from leaves inward. Each priority tier can be parallelized internally but must complete before the next tier starts.

Physical file moves must follow compile-time ownership, not just directory boundaries. If a target assembly would still depend on concrete types that remain in `XRENGINE`, extract or move those dependencies first rather than forcing a temporary project cycle.

Rules:
- Keep `XRENGINE` as a compatibility aggregator during migration. Forward types or re-export temporarily if needed.
- Namespace changes happen at move time — do not leave long-lived code under a legacy namespace.
- Every sub-task must pass targeted build + test before proceeding.
- Per AGENTS.md §11: flag/refactor hot-path allocations when touching render/update code.

## Sequencing Constraint Discovered During P0 Attempt

The original P0 warm-up slice is not independently buildable as written.

Why:

- `PawnComponent` in `XRENGINE` directly references `PawnController`, `LocalPlayerController`, `RemotePlayerController`, and `PlayerControllerBase`.
- `Engine.State` in `XRENGINE` owns the local/remote player registries and instantiates `LocalPlayerController` concretely.
- `LocalPlayerController` still depends on `XRViewport`, `UIInteractableComponent`, and `Engine.VRState`, which physically remain in `XRENGINE` until later slices.
- `PlayerControllerBase` still depends on `PlayerInfo`, which also physically remains in `XRENGINE`.

If the five controller files move directly to `Runtime.InputIntegration` now, `Runtime.InputIntegration` must still reference `XRENGINE`, while `XRENGINE` would also need to reference `Runtime.InputIntegration` for those controller types. That creates a forbidden project cycle.

Conclusion:

- The controller move must be implemented sequentially in three steps: shared ownership/seams first, pawn-side ownership second, concrete controller relocation third.
- The rest of Phase 3 should follow the same rule: when a planned move crosses an active concrete dependency boundary, insert a prerequisite seam or reorder the slice instead of forcing a compatibility hack that adds cycles.

## Current Todo List

### P0 — Controller Ownership Prerequisites

#### P0a — Shared controller ownership and registry seams

- [ ] Move `PlayerInfo` out of `XRENGINE` into `Runtime.Core` or another lower shared assembly.
- [ ] Extract the controller ownership abstraction used by pawns so `PawnComponent` no longer requires concrete controller implementations owned by `XRENGINE`.
- [ ] Introduce a runtime player-controller registry seam so local/remote player lists are no longer hard-owned by `Engine.State` concrete controller types.
- [ ] Keep the new seam aligned with existing runtime host-service patterns rather than inventing a one-off compatibility layer.
- [ ] Validate: Runtime.Core, XRENGINE, Bootstrap, Editor, Server, VRClient build. Existing tests pass.

#### P0b — Refactor pawn and engine-state controller references in place

The 9 Pawns files do **not** move yet — most have heavy rendering dependencies (`UICanvasComponent`, `UICanvasInputComponent`, `VRPlayerInputSet`, `FlyingCameraPawn`, `FlyingCameraPawnBaseComponent` all import `XREngine.Rendering.*`) that block a move to `Runtime.Core`. The actual pawn file move is deferred to P3b.

What P0b does: refactor `PawnComponent` and `Engine.State` *in place* (still in `XRENGINE`) so they reference only the abstract/interface types created in P0a, removing all concrete controller imports.

- [ ] Replace `PawnComponent` convenience casts (`LocalPlayerController?`, `RemotePlayerController?`, `PlayerControllerBase?`) with abstract/interface-based accessors from P0a.
- [ ] Refactor `PawnComponent.TickInput()` and possession hooks to use the abstract controller type + `LocalInputInterface` (already in `XREngine.Input`) instead of casting to `LocalPlayerController`.
- [ ] Refactor `PawnComponent.Viewport` accessor to use the runtime viewport seam or a controller-level interface method.
- [ ] Refactor `Engine.State.LocalPlayers` / `RemotePlayers` / `GetOrCreateLocalPlayer()` to use the registry seam from P0a instead of concrete controller types.
- [ ] Validate: XRENGINE, Runtime.Core, Editor, Server, VRClient build. Existing tests pass.

#### P0c — Concrete input controller move

- [ ] Move `PlayerControllerBase`, `PawnController`, `PlayerController`, `LocalPlayerController`, `RemotePlayerController` from `XRENGINE/Input/` → `Runtime.InputIntegration` after P0a and P0b are green.
- [ ] Replace remaining direct `XRENGINE` dependencies in `LocalPlayerController` (`XRViewport`, `UIInteractableComponent`, `Engine.VRState`) with moved types or narrow runtime seams as needed.
- [ ] Update all callers to reference the new assembly.
- [ ] Validate: InputIntegration, Bootstrap, Editor, Server, VRClient build. Existing tests pass.

### P1 — Subsystem Adapter Component Moves

#### P1a — Animation Components (~63 files)

- [ ] Move `Scene/Components/Animation/` (AnimStateMachineComponent, HumanoidComponent, IK solvers, MotionCapture receivers, blendtree drivers, humanoid profiles) → `Runtime.AnimationIntegration`.
- [ ] Identify and resolve any types that depend on rendering (gizmo/debug drawing) via existing runtime service seams.
- [ ] Update callers and forwarding references.
- [ ] Validate: AnimationIntegration, XRENGINE, Editor, Server build. Targeted tests pass.

#### P1b — Audio Components (~32 files)

- [ ] Move `Scene/Components/Audio/` (AudioSourceComponent, AudioListenerComponent, MicrophoneComponent, TextToSpeech, Audio2Face3D, OVRLipSync, Steam Audio scene bridges) → `Runtime.AudioIntegration`.
- [ ] Identify and resolve any rendering dependencies (audio visualization, lip sync mesh driving).
- [ ] Update callers and forwarding references.
- [ ] Validate: AudioIntegration, XRENGINE, Editor, Server build. Targeted tests pass.

#### P1c — VR Components (~6 files) + VR Transforms

- [ ] Move `Scene/Components/VR/` (VRPlayerCharacterComponent, VRHeadsetComponent, VRControllerModelComponent) → `Runtime.InputIntegration`.
- [ ] Move VR-specific transforms from `Scene/Transforms/` (VRHeadsetTransform, VRControllerTransform, etc.) → `Runtime.InputIntegration`.
- [ ] Update callers and forwarding references.
- [ ] Validate: InputIntegration, XRENGINE, Editor, VRClient build.

### P2 — Rendering Physical Moves

This is the largest body of work. Move in sub-phases.

#### P2a — Rendering Scene Components

- [ ] Move `Scene/Components/Mesh/` (~10: ModelComponent, RenderableComponent, GaussianSplatComponent) → `Runtime.Rendering`.
- [ ] Move `Scene/Components/Lights/` (~9: LightComponent, DirectionalLight, PointLight, SpotLight) → `Runtime.Rendering`.
- [ ] Move `Scene/Components/Camera/` (~3: CameraComponent, StereoCameraComponent, DesktopPlayerCameraComponent) → `Runtime.Rendering`.
- [ ] Move `Scene/Components/Particles/` (~21: ParticleEmitterComponent, particle modules) → `Runtime.Rendering`.
- [ ] Move `Scene/Components/Capture/` (~6: SceneCaptureComponent, LightProbeComponent) → `Runtime.Rendering`.
- [ ] Move `Scene/Components/Landscape/` (~15: LandscapeComponent, terrain layers/modules) → `Runtime.Rendering`.
- [ ] Move `Scene/Components/Misc/` (~3: SkyboxComponent, DeferredDecalComponent) → `Runtime.Rendering`.
- [ ] Validate: Runtime.Rendering, XRENGINE, Editor, Server, VRClient build.

#### P2b — UI Components

- [ ] Move `Scene/Components/UI/` (~82: UIButtonComponent, UITextComponent, RiveUIComponent, UIInteractableComponent, UI function nodes) → `Runtime.Rendering`.
- [ ] Resolve any editor-specific UI types — leave those in Editor or create a narrow seam.
- [ ] Validate: Runtime.Rendering, Editor build.

#### P2c — Higher Rendering Types (file relocation)

These types are already service-seam-decoupled from Phase 2 but still physically compile from `XRENGINE`:

- [ ] Relocate `XRViewport`, `XRCamera`, `XRRenderPipelineInstance`, `RenderPipeline`, `ViewportRenderCommandContainer`, `RenderCommand`, `AbstractRenderer`, `XRWindow` source files → `Runtime.Rendering`.
- [ ] Validate: Runtime.Rendering, XRENGINE, Editor build.

#### P2d — Rendering Backends & Pipelines

- [ ] Move `Rendering/API/` (~166: OpenGL/Vulkan/D3D12 backends, RenderContext, render objects) → `Runtime.Rendering`.
- [ ] Move `Rendering/Pipelines/` (~81: default render pipeline, VPRC_* commands) → `Runtime.Rendering`.
- [ ] Move remaining `Rendering/` subfolders (Camera, Commands, Compute, Generator, GI, Lightmapping, Materials, Meshlets, Occlusion, Picking, PostProcessing, Resources, Tools, UI) → `Runtime.Rendering`.
- [ ] Validate: Runtime.Rendering, XRENGINE (reduced), Editor, Server, VRClient build.

#### P2e — Functions & Models

- [ ] Move `Functions/` (~82: material/shader function graph system) → `Runtime.Rendering`.
- [ ] Move `Models/` (~38: Model, SubMesh, ModelImportOptions, material/texture definitions) → `Runtime.Rendering`.
- [ ] Validate builds.

### P3 — Runtime.Core Gameplay & Scene Moves

#### P3a — Physics

- [ ] Move `Scene/Physics/` (~57: PhysxScene, PhysxRigidBody, Jolt integration) → `Runtime.Core`.
- [ ] Move `Scene/Components/Physics/` (~24: PhysicsActorComponent, PhysicsJointComponent, PhysicsChainComponent) → `Runtime.Core`.
- [ ] Validate: Runtime.Core, XRENGINE, Editor build.

#### P3b — Gameplay Components

- [ ] Move `Scene/Components/Movement/` (~10: CharacterMovementComponent, movement modules) → `Runtime.Core`.
- [ ] Move `Scene/Components/Networking/` (~9: WebSocketClient, TcpServer, OscSender, RestApi) → `Runtime.Core`.
- [ ] Move `Scene/Components/Pawns/` — split by dependency:
  - `PawnComponent`, `CharacterPawnComponent`, `OptionalInputSetComponent`, `ExternalOptionalInputSetComponent` → `Runtime.Core` (no rendering imports).
  - `UICanvasComponent`, `UICanvasInputComponent` → `Runtime.Rendering` (heavy rendering imports).
  - `FlyingCameraPawn`, `FlyingCameraPawnBaseComponent` → `Runtime.Rendering` (rendering UI imports).
  - `VRPlayerInputSet` → `Runtime.InputIntegration` (rendering + physics + VR imports).
  - These files were already refactored in P0b to use abstract controller types.
- [ ] Move `Scene/Components/Volumes/` (~5: Trigger, SceneStreaming, Gravity, Boost) → `Runtime.Core`.
- [ ] Move `Scene/Components/Interaction/` (~2: InteractorComponent, InteractableComponent) → `Runtime.Core`.
- [ ] Move `Scene/Components/Scripting/` (~1: GameCSProjLoader) → `Runtime.Core`.
- [ ] Move `Scene/Components/Splines/` (~2: Spline2D/3D) → `Runtime.Core`.
- [ ] Validate: Runtime.Core, XRENGINE, Editor, Server build.

#### P3c — Game Modes & Non-VR Transforms

- [ ] Move `Game Modes/` (~5: GameMode, FlyingCameraGameMode, LocomotionGameMode, VRGameMode) → `Runtime.Core`.
- [ ] Move non-VR scene transforms from `Scene/Transforms/` → `Runtime.Core`.
- [ ] Validate builds.

#### P3d — Settings & Core Utilities

- [ ] Move runtime settings from `Settings/` → `Runtime.Core`. Leave editor preferences in Editor.
- [ ] Move runtime core utilities from `Core/` (interfaces, engine utilities, asset management) → `Runtime.Core` or `Data` as appropriate. Leave editor attributes in Editor.
- [ ] Validate builds.

### P4 — Engine Orchestrator Split

The `Engine` class (~69 partial files) is the hardest to move because it references everything.

#### P4a — Extract Host-Independent Engine Partials

- [ ] Move `Engine.Lifecycle.cs`, `Engine.Threading.cs`, `Engine.TickList.cs`, `Engine.Settings.cs`, `Engine.Project.cs`, `Engine.MainThreadInvokeLog.cs` → `Runtime.Core`.
- [ ] These require that rendering-aware, VR-aware, and window-aware partials remain in a higher assembly.
- [ ] Validate: Runtime.Core, XRENGINE (reduced), Editor, Server build.

#### P4b — Extract Rendering-Aware Engine Partials

- [ ] Move `Engine.Rendering*.cs` partials, `Engine.Windows.cs`, `Engine.ViewportRebind.cs` → `Runtime.Rendering`.
- [ ] Move runtime host service implementations (`Engine.RuntimeRenderingHostServices.cs`, `Engine.RuntimeRenderObjectServices.cs`, `Engine.RuntimeShaderServices.cs`, `Engine.RuntimeVideoStreamingServices.cs`) → `Runtime.Rendering`.
- [ ] Validate builds.

#### P4c — Extract Remaining Engine Partials

- [ ] Move `Engine.VRState.cs` → `Runtime.InputIntegration`.
- [ ] Move `Engine.Networking.cs` → `Runtime.Core`.
- [ ] Move `Engine.ProfilerSender.cs` → `Runtime.Core` or Profiler project.
- [ ] Move remaining host service implementations (`Engine.RuntimeSceneNodeServices.cs`, `Engine.RuntimeTransformServices.cs`, `Engine.RuntimeWorldObjectServices.cs`) → appropriate runtime assembly.
- [ ] Validate builds.

### P5 — Delete or Re-scope XRENGINE

- [ ] Verify `XRENGINE` has no remaining .cs files beyond obj/bin.
- [ ] Remove `XRENGINE.csproj` from solution.
- [ ] Update all application project references to point at runtime assemblies directly.
- [ ] Remove `XRENGINE` project reference from all .csproj files.
- [ ] Validate full solution build.
- [ ] Validate Editor, Server, VRClient startup.

### P6 — Integration Project Cleanup

After XRENGINE is removed, the integration projects no longer reference it:

- [ ] Remove `XRENGINE` project reference from `Runtime.AnimationIntegration`.
- [ ] Remove `XRENGINE` project reference from `Runtime.AudioIntegration`.
- [ ] Remove `XRENGINE` project reference from `Runtime.InputIntegration`.
- [ ] Verify no forbidden dependency violations remain.
- [ ] Final full solution build + test validation.

## Dependency Inversions Required

| Boundary | Current Problem | Resolution Strategy |
|----------|----------------|---------------------|
| `Engine` ↔ Rendering | Engine partials reference concrete rendering types | Split Engine into Core partials (lifecycle/threading) and Rendering partials (windows/viewports/render state) |
| `Engine` ↔ VR/Input | `Engine.VRState.cs` mixes VR state into lifecycle | Extract behind `IRuntimeVRHostServices` or move partial to InputIntegration |
| Player controllers ↔ Pawn / Engine state | Pawn possession and player registries currently depend on concrete controller types still owned by `XRENGINE` | Move shared controller ownership to a lower assembly, add a runtime player registry seam, then move concrete controllers |
| Scene Components ↔ Rendering | Many components import rendering types directly | Components that need rendering go to Runtime.Rendering; pure gameplay to Runtime.Core |
| Debug/Editing Components ↔ Editor | Transform tools, debug viz may need editor | Leave in Editor or use narrow bridge interface |

## Notes To Avoid Rework

- The adapter projects currently reference `XRENGINE` for engine-side component implementations. This is expected — removing that reference is P6, not P0.
- The original five-file controller move is blocked until pawn ownership and player registry dependencies are lowered first. Do not try to force it with reciprocal project references.
- Physics stays in `Runtime.Core` per the design doc. A dedicated physics assembly is deferred.
- `XRBase` and `XRObjectBase` already live in `XREngine.Data` — do not re-move.
- UI components go to `Runtime.Rendering` because they require the rendering pipeline, not because they are "rendering" in the traditional sense.
- The `Functions/` material/shader graph system goes to `Runtime.Rendering` because it's tightly coupled to shader compilation.
- `Scene/Components/Debug/` may split: pure debug drawing → Runtime.Core or Rendering, editor debug tools → Editor.
- `Scene/Components/Editing/` (TransformTool3D, etc.) → stays in Editor.
- Pre-existing rendering test failures (4 tests: ForwardDepthNormalVariant ×2, AlphaToCoverage ×1, BranchCoverage ×1) are not related to modularization.

## Validation Baseline

Phase 2 final green state (carried forward):

- Full solution build: `dotnet build XRENGINE.slnx` — green
- Core modularization tests: 62/62 pass
- Pre-existing unrelated failures: 4 rendering-behavior tests (not blockers)

Phase 3 validation per sub-task:

1. Target assembly builds
2. `XRENGINE` builds (reduced surface area each time)
3. Application builds: Editor, Server, VRClient
4. Nearest targeted tests pass
5. No new project reference cycles

## Phase 3 Exit Criteria

- [ ] `XRENGINE.csproj` is deleted from the solution (design doc Phase 6, Option A).
- [ ] All production code lives in target runtime assemblies per the design doc layout.
- [ ] No integration project references `XRENGINE`.
- [ ] The project graph exactly matches the design doc's "Allowed Dependencies" section.
- [ ] Editor, Server, VRClient, and all targeted tests build and pass.
- [ ] No new compiler warnings introduced by the migration.

## Suggested First Slice

Start with `P0a`, then `P0b`, then `P0c`.

That sequence establishes the same ownership direction the final architecture needs:

1. Lower shared controller state and registry contracts.
2. Move pawn-side ownership into `Runtime.Core`.
3. Move concrete controller implementations into `Runtime.InputIntegration`.

After `P0c` is green, continue with `P1a — Animation Components` as the first large subsystem move.
