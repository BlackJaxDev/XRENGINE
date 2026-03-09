# Phase 1 Execution Checklist - Runtime Bootstrap Extraction

## Scope

This document turns phase 1 of the runtime modularization plan into an implementation checklist.

Phase 1 goal:

- remove the `XREngine.Server -> XREngine.Editor` project dependency
- introduce a new `XREngine.Runtime.Bootstrap` project
- move shared startup, unit-testing-world settings, and runtime-safe world composition into the new bootstrap project
- leave editor-only UI and editor-shell behavior in `XREngine.Editor`

This phase does not split `XRENGINE` yet. It is the first dependency-cleanup slice only.

## Target Outcome

At the end of phase 1:

- `XREngine.Server.csproj` references `XREngine.Runtime.Bootstrap.csproj` instead of `XREngine.Editor.csproj`
- `XREngine.Server/Program.cs` contains no `using XREngine.Editor;`
- unit-testing settings and world factories are loaded from runtime bootstrap types
- editor UI creation still works, but only through editor-owned extension points or editor wrappers
- the solution still launches Editor and Server successfully

## New Project To Add

Add:

- `XREngine.Runtime.Bootstrap/XREngine.Runtime.Bootstrap.csproj`

Recommended initial project references:

- `..\XREngine.Animation\XREngine.Animation.csproj`
- `..\XREngine.Audio\XREngine.Audio.csproj`
- `..\XREngine.Data\XREngine.Data.csproj`
- `..\XREngine.Extensions\XREngine.Extensions.csproj`
- `..\XREngine.Input\XREngine.Input.csproj`
- `..\XRENGINE\XREngine.csproj`

Do not reference:

- `XREngine.Editor`

Rationale:

- phase 1 is only about extracting shared bootstrap/world composition from editor-owned code.
- the new project must be consumable by both Editor and Server.

## Concrete Source Inventory

### Editor Startup Touchpoints To Replace

Current editor entrypoint code to replace or route through bootstrap APIs:

- `XREngine.Editor/Program.cs`
  - `LoadUnitTestingSettings`
  - `ApplyUnitTestingWorldKindOverride`
  - `ApplyAudioSettingsFromToggles`
  - `CreateDefaultEmptyWorld`
  - `GetTargetWorld`
  - `CreateEditorStartupSettings`
  - direct reads of `EditorUnitTests.Toggles`

### Server Startup Touchpoints To Replace

Current server code using editor-owned APIs:

- `XREngine.Server/Program.cs`
  - `LoadUnitTestingSettings`
  - `CreateWorld`
  - all `EditorUnitTests.*` usages
  - `using XREngine.Editor;`

### Unit-Testing World Files In Scope

Files under `XREngine.Editor/Unit Tests` relevant to this extraction:

#### Move now with little or no semantic change

- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Toggles.cs`
- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Lighting.cs`
- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Audio.cs`
- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Physics.cs`
- `XREngine.Editor/Unit Tests/Networking/UnitTestingWorld.Networking.cs`

Notes:

- these files are currently namespaced under `XREngine.Editor`, but they are not fundamentally editor-shell code
- `UnitTestingWorld.Networking.cs` is configuration logic, not editor UI

#### Split before moving

- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.cs`
- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Pawns.cs`
- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Models.cs`

Reasons:

- `UnitTestingWorld.cs` currently contains runtime-safe world creation plus `Undo.TrackWorld(...)` and `UserInterface.EnableTransformToolForNode(...)`
- `UnitTestingWorld.Pawns.cs` currently mixes runtime-safe pawn creation with editor UI composition and `EditorFlyingCameraPawnComponent`
- `UnitTestingWorld.Models.cs` is mostly runtime-safe, but currently calls editor-owned transform tool helpers

#### Leave in editor during phase 1

- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.UserInterface.cs`
- `XREngine.Editor/Unit Tests/Math/UnitTestingWorld.Math.cs`
- `XREngine.Editor/Unit Tests/Mesh Editing/UnitTestingWorld.MeshEditing.cs`
- `XREngine.Editor/Unit Tests/Physx/UnitTestingWorld.PhysxTesting.cs`
- `XREngine.Editor/Unit Tests/Uber Shader/UnitTestingWorld.UberShader.cs`

Reasons:

- `UnitTestingWorld.UserInterface.cs` is explicitly editor UI code
- the specialized world files still call `Undo.TrackWorld(...)` and are not required to eliminate the server/editor dependency in phase 1
- mesh editing world creation is editor-tooling oriented and can move later

## Recommended Bootstrap API Surface

Create the following types in `XREngine.Runtime.Bootstrap`.

### Settings and loader

- `UnitTestingWorldSettings`
  - moved from `EditorUnitTests.Settings`
- `UnitTestingWorldSettingsStore`
  - `Load(bool writeBackAfterRead)`
  - `ApplyWorldKindOverride(UnitTestingWorldSettings settings)`
  - `ApplyAudioOverrides(UnitTestingWorldSettings settings)`

### Global current settings holder

- `RuntimeBootstrapState`
  - `public static UnitTestingWorldSettings Settings { get; set; }`

This is the temporary replacement for `EditorUnitTests.Toggles`.

### World factory

- `BootstrapWorldFactory`
  - `CreateSelectedWorld(bool setUI, bool isServer)`
  - `CreateUnitTestWorld(bool setUI, bool isServer)`
  - `CreateDefaultEmptyWorld(bool setUI, bool isServer)` or `CreateDefaultGameplayWorld(...)`
  - `CreateServerDefaultWorld()`

### World builders

- `BootstrapLightingBuilder`
- `BootstrapAudioBuilder`
- `BootstrapPhysicsBuilder`
- `BootstrapPawnFactory`
- `BootstrapModelBuilder`

These names are intentionally runtime-neutral and should replace `EditorUnitTests.Lighting`, `EditorUnitTests.Audio`, `EditorUnitTests.Physics`, `EditorUnitTests.Pawns`, and `EditorUnitTests.Models` over time.

### Editor extension hooks

To avoid editor references inside bootstrap, add optional hooks rather than direct editor calls.

Recommended shape:

```csharp
public static class BootstrapEditorHooks
{
    public static Action<SceneNode, CameraComponent?, PawnComponent?>? CreateEditorUi;
    public static Action<SceneNode>? EnableTransformToolForNode;
    public static Action<XRWorld>? TrackWorld;
}
```

Rules:

- `XREngine.Runtime.Bootstrap` may invoke these hooks if present
- `XREngine.Editor` registers them during startup
- `XREngine.Server` does not register them

This keeps phase 1 small and avoids inventing a full plug-in architecture too early.

## Exact File Actions

### Step 1 - Add project skeleton

Create:

- `XREngine.Runtime.Bootstrap/XREngine.Runtime.Bootstrap.csproj`

Add to solution:

- `XRENGINE.sln`

Update project references:

- `XREngine.Editor/XREngine.Editor.csproj` -> add reference to `..\XREngine.Runtime.Bootstrap\XREngine.Runtime.Bootstrap.csproj`
- `XREngine.Server/XREngine.Server.csproj` -> add reference to `..\XREngine.Runtime.Bootstrap\XREngine.Runtime.Bootstrap.csproj`
- `XREngine.Server/XREngine.Server.csproj` -> remove reference to `..\XREngine.Editor\XREngine.Editor.csproj`

### Step 2 - Move settings types and settings loader

Source to move or copy first:

- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Toggles.cs`

New bootstrap files to create:

- `XREngine.Runtime.Bootstrap/UnitTestingWorld/UnitTestingWorldSettings.cs`
- `XREngine.Runtime.Bootstrap/UnitTestingWorld/UnitTestingWorldSettingsStore.cs`
- `XREngine.Runtime.Bootstrap/RuntimeBootstrapState.cs`

Editor-side replacements:

- `EditorUnitTests.Toggles` becomes a temporary passthrough to `RuntimeBootstrapState.Settings`, or all phase-1 call sites are updated directly

Server-side replacements:

- replace local `LoadUnitTestingSettings` with `UnitTestingWorldSettingsStore.Load(...)`

### Step 3 - Move runtime-safe builders first

Move as-is or near-as-is:

- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Lighting.cs`
- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Audio.cs`
- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Physics.cs`
- `XREngine.Editor/Unit Tests/Networking/UnitTestingWorld.Networking.cs`

Rename into bootstrap-oriented files:

- `BootstrapLightingBuilder.cs`
- `BootstrapAudioBuilder.cs`
- `BootstrapPhysicsBuilder.cs`
- `BootstrapNetworkingWorldProfiles.cs`

Required edits during move:

- change namespace from `XREngine.Editor` to `XREngine.Runtime.Bootstrap`
- replace `Toggles` reads with `RuntimeBootstrapState.Settings`

### Step 4 - Split UnitTestingWorld.cs

Current file:

- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.cs`

Extract the runtime-safe part into bootstrap:

- render settings application
- default world assembly
- world kind dispatch
- mirror/spline/decal helpers

Leave editor-specific calls out of the moved file:

- `Undo.TrackWorld(world)`
- `UserInterface.EnableTransformToolForNode(targetNode)`

New bootstrap files:

- `XREngine.Runtime.Bootstrap/UnitTestingWorld/BootstrapWorldFactory.cs`
- `XREngine.Runtime.Bootstrap/UnitTestingWorld/BootstrapRenderSettings.cs`
- `XREngine.Runtime.Bootstrap/UnitTestingWorld/BootstrapSceneDecorators.cs`

Editor follow-up:

- if editor still wants undo tracking for worlds created through bootstrap, call `BootstrapEditorHooks.TrackWorld?.Invoke(world)` instead of direct `Undo.TrackWorld(world)`

### Step 5 - Split UnitTestingWorld.Pawns.cs

Current file mixes these categories:

- runtime-safe pawn and camera construction
- editor UI creation via `UserInterface.CreateEditorUI(...)`
- editor pawn selection via `EditorFlyingCameraPawnComponent`

Extraction rule:

- keep runtime-safe camera/pawn construction in bootstrap
- isolate editor-specific UI composition and editor-specific pawn types behind bootstrap hooks or editor wrappers

Create:

- `XREngine.Runtime.Bootstrap/WorldBuilders/BootstrapPawnFactory.cs`

Likely required split points:

- any call to `UserInterface.CreateEditorUI(...)`
- any call to `UserInterface.ShowMenu(...)`
- any direct dependency on `EditorFlyingCameraPawnComponent`

Recommended temporary strategy:

- keep the bootstrap pawn factory capable of producing a generic desktop pawn for server/runtime use
- let `XREngine.Editor` wrap or augment it when it needs editor-specific pawn behavior

### Step 6 - Split UnitTestingWorld.Models.cs

Current file is mostly runtime-safe, but phase 1 should isolate editor helpers instead of moving the whole file blindly.

Split out the runtime-safe portion to:

- `XREngine.Runtime.Bootstrap/WorldBuilders/BootstrapModelBuilder.cs`

Replace direct editor calls:

- `UserInterface.EnableTransformToolForNode(...)`

with:

- `BootstrapEditorHooks.EnableTransformToolForNode?.Invoke(...)`

### Step 7 - Keep UnitTestingWorld.UserInterface.cs in editor

Do not move in phase 1:

- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.UserInterface.cs`

Instead, add a new editor-owned registration file, for example:

- `XREngine.Editor/Bootstrap/BootstrapEditorHookRegistration.cs`

Responsibilities:

- register `BootstrapEditorHooks.CreateEditorUi`
- register `BootstrapEditorHooks.EnableTransformToolForNode`
- register `BootstrapEditorHooks.TrackWorld`

### Step 8 - Update editor startup to use bootstrap types

Update:

- `XREngine.Editor/Program.cs`

Concrete changes:

- replace `EditorUnitTests.Toggles = LoadUnitTestingSettings(false);`
  with bootstrap store load into `RuntimeBootstrapState.Settings`
- replace `ApplyUnitTestingWorldKindOverride(...)` with bootstrap store override API
- replace `ApplyAudioSettingsFromToggles(...)` with bootstrap store audio override API
- replace `EditorUnitTests.CreateSelectedWorld(...)` with `BootstrapWorldFactory.CreateSelectedWorld(...)`
- replace `CreateDefaultEmptyWorld()` implementation with bootstrap world factory or move the implementation wholly into bootstrap
- update `CreateEditorStartupSettings()` to read from `RuntimeBootstrapState.Settings`

Temporary compatibility option:

- keep a thin `EditorUnitTests` shim exposing `Toggles => RuntimeBootstrapState.Settings`
- this is acceptable for one phase if it avoids a massive editor rename sweep

### Step 9 - Update server startup to use bootstrap types

Update:

- `XREngine.Server/Program.cs`

Concrete changes:

- remove `using XREngine.Editor;`
- remove local `LoadUnitTestingSettings(...)`
- replace all `EditorUnitTests` references with bootstrap types
- move `CreateWorld()` to call `BootstrapWorldFactory.CreateSelectedWorld(false, true)` or a dedicated server world entrypoint

Recommended explicit server API:

- `BootstrapWorldFactory.CreateServerDefaultWorld()`

That avoids server code having to know editor-era unit-test naming.

## First Commit Sequence

Use small, coherent commits.

### Commit 1 - Add bootstrap project skeleton

Suggested message:

- `Add runtime bootstrap project skeleton`

Contents:

- new `XREngine.Runtime.Bootstrap.csproj`
- solution entry
- project references in Editor and Server
- no source movement yet

Expected state after commit:

- solution builds
- Server may still reference Editor until the next commit if done in two steps

### Commit 2 - Move unit-testing settings and loader

Suggested message:

- `Move unit testing settings into runtime bootstrap`

Contents:

- `UnitTestingWorldSettings`
- `UnitTestingWorldSettingsStore`
- `RuntimeBootstrapState`
- editor/server startup updated to load settings from bootstrap

Expected state after commit:

- both Editor and Server load the same settings type from bootstrap

### Commit 3 - Move lighting, audio, physics, and networking profile helpers

Suggested message:

- `Extract shared bootstrap world builders`

Contents:

- moved runtime-safe builder files
- namespace updates
- settings reads redirected to `RuntimeBootstrapState.Settings`

Expected state after commit:

- most shared world-building helpers now live outside editor

### Commit 4 - Add bootstrap hooks and split editor-only UI dependencies

Suggested message:

- `Add editor hook registration for bootstrap worlds`

Contents:

- `BootstrapEditorHooks`
- editor registration code
- split `UnitTestingWorld.cs`, `Pawns.cs`, `Models.cs` at the obvious editor hook boundaries

Expected state after commit:

- bootstrap can create runtime worlds without referencing editor UI classes

### Commit 5 - Remove server dependency on editor

Suggested message:

- `Remove server dependency on editor bootstrap code`

Contents:

- server csproj reference cleanup
- `Program.cs` updates in server
- final removal of `using XREngine.Editor;` from server entrypoint

Expected state after commit:

- `XREngine.Server` no longer references `XREngine.Editor`

### Commit 6 - Optional cleanup shim for editor

Suggested message:

- `Add temporary editor bootstrap compatibility shim`

Contents:

- only if needed
- thin `EditorUnitTests` forwarding API for remaining editor call sites

Expected state after commit:

- editor can continue migrating without forcing an all-at-once rename

## Build And Validation Checklist

After commit 1:

- build `XREngine.Runtime.Bootstrap`
- build `XREngine.Editor`
- build `XREngine.Server`

After commit 2:

- run Editor and confirm `Assets/UnitTestingWorldSettings.json` still loads
- run Server and confirm settings deserialize without editor assembly present in the dependency chain

After commit 3:

- launch Editor unit-testing world
- launch Server default world
- verify light probes, skybox, and basic world composition still appear

After commit 4:

- verify editor UI still appears when requested in editor startup
- verify server startup does not attempt to instantiate editor UI hooks

After commit 5:

- build `XREngine.Server`
- inspect project references to confirm no `XREngine.Editor` reference remains
- run server startup path and verify the world loads

## Explicit Acceptance Criteria

Phase 1 is done when all of the following are true:

- `XREngine.Server/XREngine.Server.csproj` has no `ProjectReference` to `XREngine.Editor/XREngine.Editor.csproj`
- `XREngine.Server/Program.cs` has no `using XREngine.Editor;`
- settings loading and world composition used by server are resolved from `XREngine.Runtime.Bootstrap`
- editor-only UI files remain in `XREngine.Editor`
- Editor and Server both build cleanly

## Deferred To Phase 2+

Do not expand phase 1 to include these unless required to keep the build green:

- moving all specialized world files (`Math`, `Mesh Editing`, `Physx`, `Uber Shader`)
- splitting the main `XRENGINE` assembly
- rendering/core separation
- animation/audio/input/modeling adapter projects
- removing all editor metadata strings from runtime types

Keeping phase 1 narrow is the main control against a stalled refactor.