# Source-Backed C# Script Components

Last Updated: 2026-04-29
Status: Design proposal

## Overview

XRENGINE should support user-written C# components as source assets under the project `Assets/` tree before those scripts have successfully compiled. Authors should be able to create, inspect, attach, rename, and serialize script components while the source is still in a source-only state. When compilation succeeds, the editor can materialize those source-backed script bindings into real compiled `XRComponent` instances through the existing dynamic DLL pipeline.

The key design rule is that scene and prefab persistence must never depend on an assembly-qualified type from a hot-loaded game DLL. Scenes should serialize a stable engine-owned proxy component that points at the script asset and declared component type. The compiled component is a transient runtime/editor materialization of that proxy.

This keeps the current compiled-script workflow useful while removing its biggest serialization risk: a scene that directly stores a dynamic user component can become brittle when the DLL is missing, stale, unloaded, or incompatible.

## Current Pipeline

The repository already has the main compile and hot-load pieces:

- [`XREngine.Editor/CodeManager.cs`](../../../XREngine.Editor/CodeManager.cs) treats `.cs` files under `Engine.Assets.GameAssetsPath` as game code.
- `CodeManager.RemakeSolutionAsDLL()` generates a game `.csproj` from the assets tree, compiles it as a DLL, and asks the loader to hot-load it.
- [`XRENGINE/Scene/Components/Scripting/GameCSProjLoader.cs`](../../../XRENGINE/Scene/Components/Scripting/GameCSProjLoader.cs) loads the DLL into a collectible `AssemblyLoadContext`, discovers exported `XRComponent` subclasses, and exposes them through `LoadedAssemblies`.
- [`XREngine.Runtime.Core/Scene/SceneNode.Components.cs`](../../../XREngine.Runtime.Core/Scene/SceneNode.Components.cs) can add components from a `Type`, which is how dynamically discovered component types can be attached once compiled.
- [`XREngine.Runtime.Core/Scene/SceneNode.cs`](../../../XREngine.Runtime.Core/Scene/SceneNode.cs) currently serializes the actual component list through `ComponentsSerialized`.
- [`XREngine.Editor/Mcp/Actions/EditorMcpActions.Scripting.cs`](../../../XREngine.Editor/Mcp/Actions/EditorMcpActions.Scripting.cs) already exposes script CRUD, compile, compile status, and compile diagnostics over MCP.
- [`XREngine.Editor/Mcp/Actions/EditorMcpActions.Components.cs`](../../../XREngine.Editor/Mcp/Actions/EditorMcpActions.Components.cs) can add compiled component types to nodes once `McpToolRegistry` can resolve them.

The missing layer is a durable script binding model that exists independently of successful compilation.

## C# Execution Boundary

Normal C# component code cannot truly execute without being compiled to IL. Roslyn scripting also compiles under the hood. Source-only script components are therefore authoring-time placeholders, not interpreted runtime behavior.

Supported modes should be explicit:

| Mode | Meaning |
|---|---|
| Source-only | The script asset and component binding exist, serialize, and appear in inspectors, but user code does not run. |
| Compiled dynamic | The script compiles into the generated game DLL and is materialized into a live `XRComponent` in editor/dev CoreCLR processes. |
| Published CoreCLR | The game ships with compiled script assemblies or compiled game projects. Dynamic loading may remain available if the build profile allows it. |
| NativeAOT final | Runtime script compilation and dynamic managed assembly loading are unavailable. Scripts must be precompiled and statically registered. |

## Goals

- Let users create and attach `.cs` assets before the code compiles.
- Keep source-backed script components serializable in scenes and prefabs even when the compiled DLL is missing.
- Preserve the current `CodeManager` and `GameCSProjLoader` pipeline for compile-on-demand and hot reload.
- Snapshot editable state before unloading compiled assemblies and restore it after rematerialization.
- Make unresolved, compile-error, and stale-type states visible in the editor instead of failing deserialization.
- Keep NativeAOT restrictions isolated to published runtime builds.

## Non-Goals

- Do not build a C# interpreter.
- Do not require the editor to become NativeAOT-compatible.
- Do not serialize dynamic assembly-qualified user component types as the durable scene format.
- Do not preserve legacy dynamic-script scene serialization pre-v1 if a cleaner source-backed format is available.
- Do not expand this into a package/dependency upgrade or external scripting language change.

## Target Architecture

### Stable Serialized Proxy

Add an engine-owned component, tentatively named `CSharpScriptComponent`, that derives from `XRComponent` and is safe to serialize without the user DLL being loaded.

It stores only stable authoring data:

- script asset ID, when metadata exists
- project-relative script path as a fallback and human-readable repair handle
- declared component type name, preferably full name plus simple-name fallback
- selected runtime policy, such as source-only, materialize when compiled, or play-mode-only
- last known compile/materialization status
- serialized member state bag for fields/properties that belong to the compiled component

The proxy itself is the component that scenes and prefabs save. It can remain attached when source is invalid, when the DLL is unloaded, or when the component type has been renamed.

### Script Asset Identity

The script file remains a normal `.cs` asset under `Engine.Assets.GameAssetsPath`. A single file may declare zero, one, or many `XRComponent` subclasses, so the binding must include both the source asset and the declared component type.

Resolution should prefer stable identity in this order:

1. Asset GUID from metadata, if available.
2. Project-relative path under `Assets/`.
3. Declared full type name.
4. Declared simple type name as a repair fallback.

If the source file moves but the asset ID remains the same, the binding should update the stored display path. If the type is renamed, the proxy should remain unresolved until the user selects the new type or an automated rename map can repair it.

### Materialization Service

Add a coordinator, tentatively named `ScriptComponentMaterializer`, responsible for turning proxies into live compiled components and back again.

Responsibilities:

- Listen to `GameCSProjLoader.OnAssemblyLoaded` and `OnAssemblyUnloaded`.
- Traverse loaded worlds/scenes for `CSharpScriptComponent` proxies.
- Resolve each proxy against `GameCSProjLoader.GetAssemblyData("GAME")` or a future per-project assembly ID.
- Create the compiled component with the same `SceneNode` and normal `XRComponent` construction path.
- Apply the proxy's serialized member state to the compiled component.
- Preserve component ordering and lifecycle semantics.
- Track proxy-to-live and live-to-proxy mappings while the compiled assembly is loaded.
- Snapshot live state back into the proxy before unload, recompile, node destruction, scene save, or play-mode exit.

Materialization should be an editor/dev feature by default. Runtime hosts can opt into it only on CoreCLR profiles that intentionally support dynamic managed plugins.

### Component List Strategy

The compiled component should participate in normal component behavior while it is live. It needs access to `SceneNode`, transforms, world state, activation, begin/end play, sibling lookup, tick registration, and engine component services.

There are two viable implementation strategies:

| Strategy | Shape | Pros | Cons |
|---|---|---|---|
| Temporary swap | Replace the proxy in the node's component list with the live compiled component, while the materializer keeps the proxy mapping. | Live component works exactly like any other `XRComponent`. Existing tick, activation, sibling lookup, and MCP component tools mostly keep working. | Serialization must normalize live script components back to proxies. Recompile/unload must snapshot and swap reliably. |
| Proxy host | Keep `CSharpScriptComponent` in the component list and let it own a transient live instance that is not listed as a normal component. | Serialization is simpler because the proxy never leaves the list. | Harder to make arbitrary `XRComponent` subclasses behave normally unless lifecycle and scene-node internals are carefully delegated. |

The recommended v1 path is temporary swap plus a serialization guard. It best preserves the existing engine model: live components are real components while they execute, and proxies are the durable storage model.

To make this safe, `SceneNode.ComponentsSerialized` should not blindly return dynamic live script instances. It should ask the materializer for a serialization view that substitutes each live compiled script component with its proxy after first snapshotting current state. The cooked-binary component path should use the same normalization rule.

### Member State Bag

The proxy needs to preserve state even when the user type cannot be loaded. Store state as a portable member bag rather than as an instance of the compiled component type.

Recommended shape:

```csharp
public sealed class ScriptComponentMemberState
{
    public Dictionary<string, ScriptSerializedValue> Members { get; set; } = [];
    public string? SourceAssemblyId { get; set; }
    public string? SourceTypeName { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}
```

The exact `ScriptSerializedValue` representation should reuse existing asset serialization primitives where possible, but it must not require loading the compiled user type to parse the proxy. It needs to handle common inspector values first:

- primitives, strings, enums by name
- `Vector2`, `Vector3`, `Vector4`, quaternions, colors, matrices where supported
- asset references by stable asset ID/path
- scene object references by stable scene/prefab IDs where available
- arrays/lists/dictionaries only after the scalar path is solid

Unknown members should be retained. If a field disappears after a code change, the state remains in the proxy as dormant data and can be restored if the member returns. If a member exists but type conversion fails, the inspector should show a repair warning and keep the stored value.

### Resolution Lifecycle

#### Source-only attach

1. User creates or selects a `.cs` asset.
2. Editor scans the source or uses latest compile metadata to list candidate `XRComponent` declarations.
3. User attaches a `CSharpScriptComponent` proxy to a node.
4. The proxy serializes normally and reports `SourceOnly` or `Uncompiled` status.
5. No user code runs.

#### Compile succeeds

1. `CodeManager.RemakeSolutionAsDLL()` regenerates the game project from the assets tree.
2. `CodeManager.CompileSolution()` builds the DLL and calls `GameCSProjLoader.LoadFromPath("GAME", ...)`.
3. `GameCSProjLoader` discovers exported `XRComponent` subclasses and raises `OnAssemblyLoaded`.
4. `ScriptComponentMaterializer` resolves proxies by asset/type metadata.
5. Each resolved proxy snapshots any pending source-side member state, creates the compiled component, applies state, then swaps the live component into the node.
6. If the node has begun play or is active in hierarchy, normal component activation/begin-play rules are applied exactly once.

#### Compile fails

1. The existing DLL, if any, may remain loaded only if the editor chooses to preserve last-known-good behavior.
2. Proxies tied to the failed source show compile diagnostics.
3. If last-known-good is disabled or the assembly unloads, live components snapshot back into proxies and source-only proxies remain attached.
4. Scene save still writes proxies, not failed live types.

#### Recompile or unload

1. Materializer receives pre-unload intent from `CodeManager` or `GameCSProjLoader.Unload`.
2. Each live compiled script component snapshots current serializable member state into its proxy.
3. The live component receives deactivation/end-play as appropriate and is removed from the component list.
4. The proxy is restored at the same component index.
5. The collectible assembly context unloads.
6. After the new DLL loads, proxies are rematerialized if policy allows.

#### Scene or prefab save

1. Save code asks `SceneNode.ComponentsSerialized` for component data.
2. The getter normalizes live compiled script components to their proxies.
3. Any live state is captured before serialization.
4. The saved YAML/cooked binary contains only engine-known proxy types plus member bags.

## Editor And Inspector Behavior

The editor should treat source-backed script components as first-class components:

- Component add menus show both compiled engine components and source script components.
- Source-only proxies show script path, declared type, compile state, and diagnostics.
- When compiled, the inspector shows the live component's reflected editable members while clearly preserving the proxy binding.
- When unresolved, the inspector shows the portable member bag and repair controls instead of dropping data.
- Rename/move workflows update asset references where possible.
- Deleting a script asset should not delete component bindings automatically; it should mark them missing so the user can restore or retarget.

Compiled script execution should be governed by a project/editor setting. A conservative default is:

- Edit mode: materialize only when explicitly requested or when script preview execution is enabled.
- Play mode: materialize compiled scripts automatically before entering play.
- Source-only mode: never materialize or execute, even if a compiled DLL is available.

## MCP Integration

Existing MCP scripting tools remain useful and should be extended rather than replaced.

Current useful tools:

- `list_game_scripts`
- `read_game_script`
- `write_game_script`
- `delete_game_script`
- `rename_game_script`
- `compile_game_scripts`
- `get_compile_status`
- `get_compile_errors`

Recommended additions:

- `list_script_components`: list candidate `XRComponent` declarations from source and latest compiled metadata.
- `attach_script_component`: attach a `CSharpScriptComponent` proxy to a node by script path/asset ID and declared type name.
- `get_script_component_status`: report source path, declared type, compile status, materialized status, and diagnostics.
- `retarget_script_component`: change a proxy to a different script asset or declared type while preserving compatible member state.
- `materialize_script_component`: explicitly materialize one proxy when a compatible compiled type is loaded.
- `dematerialize_script_component`: snapshot and return one live script binding to source-only proxy state.

`add_component_to_node` can continue to add compiled component types by name. It should not be the only path for source-backed scripts because unresolved source-only scripts do not have a `Type` yet.

## NativeAOT And Published Builds

`GameCSProjLoader` already blocks runtime managed assembly loading when `XRRuntimeEnvironment.IsAotRuntimeBuild` is true. This design should preserve that boundary.

Runtime support matrix:

| Build profile | Script proxy serialization | Dynamic DLL materialization | Required shipping path |
|---|---:|---:|---|
| Editor/dev CoreCLR | Yes | Yes | Existing `CodeManager` plus materializer. |
| Published CoreCLR game | Yes | Optional by build setting | Ship compiled game DLL or include dynamic plugin host intentionally. |
| NativeAOT final game | Yes | No | Precompile scripts into the launcher/game assembly and register script bindings statically. |

For NativeAOT final builds, the proxy remains a useful authoring and cooked-asset format, but resolution must use generated/static metadata rather than `AssemblyLoadContext` or broad assembly scanning.

## Serialization And Migration

### New scenes and prefabs

New authored content should serialize source-backed script bindings as `CSharpScriptComponent` proxies from the start.

### Existing dynamic component scenes

If pre-v1 content already contains dynamically loaded component types directly in YAML, provide a one-time migration command:

1. Load the scene while the old game DLL is available.
2. Locate components whose assemblies come from `GameCSProjLoader`.
3. Create matching `CSharpScriptComponent` proxies with script asset/type bindings.
4. Snapshot member state into the proxy bag.
5. Replace the dynamic component with the proxy.
6. Save the scene in the source-backed format.

If the old DLL is unavailable, the migration cannot recover arbitrary user component state unless the serialized data can be parsed without loading that type. In that case, keep the raw serialization payload for manual repair if possible.

## Implementation Plan

### Phase 1 - Durable proxy and source-only attachment

- Add `CSharpScriptComponent` with stable script reference, declared type, status, policy, and member state bag.
- Add editor component menu support for creating source-backed bindings.
- Add MCP `attach_script_component` and `get_script_component_status`.
- Ensure scenes and prefabs save/load source-only proxies without requiring a compiled DLL.

Acceptance criteria:

- A new `.cs` file can be created under `Assets/`, attached to a node as a source-backed component, saved, reloaded, and inspected without compiling.
- Missing or invalid script paths do not break scene deserialization.

### Phase 2 - Dynamic materialization

- Add `ScriptComponentMaterializer` and assembly load/unload hooks.
- Resolve proxies against `GameCSProjLoader` component metadata.
- Swap resolved proxies into live compiled components while preserving component order.
- Add explicit materialize/dematerialize editor and MCP actions.

Acceptance criteria:

- Compiling a valid script can materialize the proxy into a live `XRComponent`.
- Unloading or recompiling snapshots state and restores the proxy before the assembly unloads.

### Phase 3 - State capture and inspector parity

- Implement portable member bag capture/apply for common inspector value types.
- Show live reflected members when compiled and portable stored members when unresolved.
- Preserve unknown or temporarily invalid members across code edits.
- Add diagnostics for conversion failures.

Acceptance criteria:

- Editing a serialized property on a compiled script, recompiling, and rematerializing preserves the value.
- Renaming/removing a member does not drop its stored data silently.

### Phase 4 - Serialization normalization and migration

- Normalize `SceneNode.ComponentsSerialized` so saves always emit proxies for live script bindings.
- Apply the same rule to cooked-binary serialization paths.
- Add a migration command for scenes that directly serialized dynamic user components.
- Add targeted tests around save/load while compiled scripts are materialized.

Acceptance criteria:

- Saving a scene while compiled scripts are live produces no direct user-DLL component types in the scene file.
- Reloading that scene with no game DLL still succeeds and shows source-backed proxies.

### Phase 5 - Published build integration

- Define project/build settings for dynamic script loading in published CoreCLR builds.
- Generate static script registration metadata for NativeAOT final builds.
- Ensure `CSharpScriptComponent` resolution uses static metadata in NativeAOT profiles.
- Document supported shipping profiles.

Acceptance criteria:

- CoreCLR published builds either load the compiled game DLL intentionally or reject dynamic loading with a clear message.
- NativeAOT final builds do not call `GameCSProjLoader` and can resolve precompiled script bindings through generated registration data.

## Test Plan

Targeted tests should live near scene/component serialization and editor script pipeline coverage.

Recommended coverage:

- Source-only proxy save/load with no `.csproj` or DLL present.
- Missing script asset load keeps proxy and member bag intact.
- Compile valid script, materialize, activate, begin play, dematerialize, and unload.
- Compile failure preserves last proxy state and reports diagnostics.
- Recompile after changing a member default preserves serialized override state.
- Scene save while materialized writes proxy data only.
- Prefab instantiate and override extraction treat script proxies as durable components.
- MCP attach/status/materialize/dematerialize tools return stable IDs and useful diagnostics.
- NativeAOT runtime boundary rejects dynamic materialization and uses static registration once available.

## Risks And Mitigations

| Risk | Mitigation |
|---|---|
| Live dynamic components leak references and prevent collectible context unload. | Snapshot and detach before unload, clear event subscriptions, then assert the context weak reference can be collected in tests. |
| Scene saves accidentally persist dynamic component types. | Route `ComponentsSerialized` and cooked-binary serialization through a materializer normalization step. |
| Member bags lose data when user code changes. | Retain unknown members and failed conversions instead of dropping them. |
| Source parsing becomes too complex. | Prefer compiled metadata when available; for source-only candidate listing, use Roslyn syntax discovery limited to class declarations inheriting `XRComponent`. |
| Executing user code in edit mode surprises users. | Gate materialization with explicit editor/project policy and default source-only behavior outside play mode unless enabled. |
| NativeAOT paths accidentally call dynamic loading. | Keep dynamic materialization behind runtime capability checks and generated/static registration for AOT. |

## Open Questions

- Should edit mode materialize compiled scripts by default, or should execution be play-mode-only unless preview execution is enabled?
- What is the canonical stable scene object reference format for member bags that point to other nodes/components?
- Should one `.cs` asset map to a generated script asset record per declared component type, or should the proxy always store file plus declared type directly?
- Should last-known-good compiled behavior continue after a failed compile, or should failed compile always dematerialize to source-only state?
- Which existing asset metadata system should own script GUIDs if a `.cs` file has no metadata yet?

## Recommended Direction

Implement source-backed script components as stable serialized proxies plus dynamic materialization. Do not attempt to interpret arbitrary C# source. Keep the current DLL compile/hot-load path as the execution mechanism for editor/dev CoreCLR workflows, and add a separate static registration path for NativeAOT final builds.

The most important invariant is simple: authored scenes and prefabs save script bindings, not hot-loaded user component types.