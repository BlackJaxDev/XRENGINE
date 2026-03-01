# MCP Server Expansion TODO

Last Updated: 2026-03-01
Current Status: 112 tools shipped (75 original + 11 Phase 1 + 13 Phase 2 + 13 Phase 3)
Primary Objective: Expose enough engine surface through MCP that an AI agent can inspect types, manage game assets, author/compile/hot-reload C# scripts, and perform advanced scene authoring — all without leaving the conversation.

## Realtime Visual Context (Shipped)

- [x] OpenAI Realtime WebSocket now supports a built-in screenshot function tool (`request_view_screenshot`) during active response generation.
- [x] The model can request the current view or target a specific camera via `camera_node_id` or `camera_name`.
- [x] Screenshot results are returned immediately as both structured function output and inline image input, enabling the assistant to continue reasoning with fresh visual context in the same realtime exchange.

## Phased Roadmap

Tools are grouped into phases by priority. Each phase unlocks a qualitatively new capability for AI-driven workflows.

---

## Phase 1 — .NET Type System Navigation (HIGH PRIORITY)

**Why first:** Every downstream capability (code generation, smart property editing, asset authoring) depends on the AI being able to understand the engine's type system. These tools turn the running AppDomain into a queryable knowledge base.

### P1.1 Core Type Inspection

- [x] **`get_type_info`** — Get full type metadata serialized as JSON.
  - Params: `type_name` (short or full), `include_members` (bool, default false).
  - Returns: name, namespace, assembly, base type, interfaces, generic parameters, attributes, visibility, abstract/sealed/static/enum/struct flags.
  - Resolves any type in any loaded assembly, not just `XRComponent` subtypes.

- [x] **`get_type_members`** — Get public properties, fields, methods, events, and constructors from any type.
  - Params: `type_name`, `include_properties` (bool), `include_fields` (bool), `include_methods` (bool), `include_events` (bool), `include_constructors` (bool), `include_non_public` (bool), `include_static` (bool), `include_inherited` (bool).
  - Returns arrays of member descriptors with names, types, signatures, settability, descriptions, and custom attributes.

- [x] **`get_method_info`** — Get detailed signature for a specific method.
  - Params: `type_name`, `method_name`, `parameter_types` (optional, for overload resolution).
  - Returns: full parameter list with types/defaults, return type, generic constraints, attributes.

### P1.2 Inheritance & Relationship Queries

- [x] **`get_derived_types`** — Find all types that derive from a given type.
  - Params: `type_name`, `direct_only` (bool, default false), `include_abstract` (bool, default true).
  - Scans all loaded assemblies. Returns array of type summaries.

- [x] **`get_parent_types`** — Walk the inheritance chain upward.
  - Params: `type_name`, `include_interfaces` (bool, default true).
  - Returns ordered base-type chain plus all implemented interfaces.

- [x] **`get_type_hierarchy_tree`** — Returns a full inheritance tree rooted at a type as nested JSON.
  - Params: `type_name`, `max_depth` (int), `direction` ("up"/"down"/"both").
  - Great for understanding the `SceneNode` / `XRComponent` / `TransformBase` type families.

### P1.3 Discovery & Search

- [x] **`search_types`** — Fuzzy/regex search across all loaded types.
  - Params: `pattern` (string), `match_mode` ("contains"/"regex"/"exact"), `base_type` (optional filter), `assembly_filter` (optional).
  - Returns matching type summaries.

- [x] **`list_assemblies`** — List all loaded assemblies.
  - Returns: name, version, location, whether game/engine/system.

- [x] **`get_assembly_types`** — List all types in a specific assembly.
  - Params: `assembly_name`, `include_internal` (bool), `namespace_filter` (optional).

- [x] **`list_enums`** — List enum types with their values.
  - Params: `namespace_filter`, `assembly_filter`.

- [x] **`get_enum_values`** — Get all named values for an enum type.
  - Params: `type_name`.
  - Returns array of { name, underlyingValue } entries.

---

## Phase 2 — Code / Script Management (HIGH PRIORITY)

**Why second:** With type knowledge from Phase 1, the AI can now write correct C# code. These tools close the authoring loop: write → compile → hot-reload → verify — entirely via MCP.

### P2.1 Script CRUD

- [x] **`list_game_scripts`** — List all `.cs` files in the game scripts directory.
  - Params: `path` (relative subfolder, optional), `recursive` (bool, default true).

- [x] **`read_game_script`** — Read the contents of a `.cs` file.
  - Params: `path` (relative to game assets root).

- [x] **`write_game_script`** — Write/create a `.cs` file. Triggers CodeManager invalidation.
  - Params: `path`, `content`, `compile_now` (bool, default false).

- [x] **`delete_game_script`** — Delete a `.cs` script file.
  - Params: `path`.

- [x] **`rename_game_script`** — Rename/move a script file.
  - Params: `old_path`, `new_path`.

### P2.2 Compilation & Hot-Reload

- [x] **`compile_game_scripts`** — Trigger `CodeManager.RemakeSolutionAsDLL(compileNow: true)`.
  - Params: `config` ("Debug"/"Release"), `platform` ("Any CPU"/"x64").
  - Regenerates project files, compiles, and hot-reloads the DLL.

- [x] **`get_compile_status`** — Get current compilation state.
  - Returns: scripts dirty flag, last compile succeeded flag, binary path.

- [x] **`get_compile_errors`** — Returns structured compile errors/warnings from last build.
  - Returns array of { file, line, column, code, message, severity }.

- [x] **`set_compile_on_change`** — Toggle `CodeManager.CompileOnChange`.
  - Params: `enabled` (bool).

- [x] **`get_game_project_info`** — Get project name, solution path, binary path, target framework, state.

### P2.3 Loaded Plugin Inspection

- [x] **`get_loaded_game_types`** — List all types currently loaded from the game DLL plugin.
  - Wraps `GameCSProjLoader.LoadedAssemblies`. Returns components, menu items, etc.

### P2.4 Code Scaffolding

- [x] **`scaffold_component`** — Generate a new `XRComponent` subclass from a template.
  - Params: `class_name`, `namespace`, `properties` (array of {name, type, default}), `dest_path`.
  - Boilerplate includes: backing fields with `SetField`, `OnComponentActivated`, `RegisterCallbacks`, `Description` attribute.

- [x] **`scaffold_game_mode`** — Generate a new game mode class from template.
  - Params: `class_name`, `namespace`, `dest_path`.

---

## Phase 3 — Game Project Asset Management (HIGH PRIORITY)

**Why third:** AI needs to read, create, and organize asset files on disk to be a useful project co-author. These tools operate on `Engine.Assets.GameAssetsPath`.

### P3.1 File-System Operations

- [x] **`list_game_assets`** — List files/folders in the game project's assets directory.
  - Params: `path` (relative), `recursive` (bool), `filter` (glob, e.g. `"*.asset"`), `include_metadata` (bool — size, modified date).

- [x] **`read_game_asset`** — Read raw text contents of an asset file (`.asset`, `.json`, `.xml`, etc.).
  - Params: `path` (relative).

- [x] **`write_game_asset`** — Write/overwrite a text asset file.
  - Params: `path`, `content`, `create_dirs` (bool).

- [x] **`delete_game_asset`** — Delete a file or empty directory from game assets.
  - Params: `path`, `recursive` (bool, for directories).

- [x] **`rename_game_asset`** — Rename/move a file within game assets.
  - Params: `old_path`, `new_path`.

- [x] **`copy_game_asset`** — Copy a file within game assets.
  - Params: `source_path`, `dest_path`, `overwrite` (bool).

- [x] **`get_game_asset_tree`** — Get the full directory tree as nested JSON.
  - Params: `max_depth`, `include_file_info` (bool).

### P3.2 Engine-Aware Asset Operations

- [x] **`create_asset`** — Create a new typed engine asset (material, mesh, animation) and save to game assets.
  - Params: `asset_type`, `name`, `dest_folder`, `properties` (JSON object).

- [x] **`import_third_party_asset`** — Import a third-party file (GLTF, FBX, OBJ, PNG, WAV) into game assets, triggering the engine's import pipeline.
  - Params: `source_path` (absolute), `dest_folder` (relative).

- [x] **`reload_asset`** — Force-reload an asset from disk after external edits.
  - Params: `asset_id` or `asset_path`.

- [x] **`get_asset_dependencies`** — List all assets referenced by a given asset.
  - Params: `asset_id` or `asset_path`.

- [x] **`get_asset_references`** — Reverse lookup: find all assets/scene nodes that reference a given asset.
  - Params: `asset_id` or `asset_path`.

- [x] **`cook_asset`** — Trigger cooking/packaging of an asset for runtime use.
  - Params: `asset_path`, `output_dir`.

---

## Phase 4 — Live Instance Inspection (MEDIUM PRIORITY)

**Why fourth:** Phase 1-3 cover design-time. Phase 4 extends into runtime debugging and live manipulation of object instances.

### P4.1 Generic Object Reflection

- [ ] **`get_object_properties`** — Read all property values from any `XRBase`-derived instance by GUID.
  - Params: `object_id`, `include_non_public` (bool), `max_depth` (for nested objects).

- [ ] **`set_object_property`** — Set a property on any `XRBase` instance by GUID (uses `SetField` pipeline).
  - Params: `object_id`, `property_name`, `value`.

- [ ] **`invoke_method`** — Invoke a method on an `XRBase` instance or a static method on any type.
  - Params: `object_id` (null for static), `type_name` (for static), `method_name`, `arguments` (JSON array).

- [ ] **`evaluate_expression`** — Evaluate a simple property-chain expression on a scene object (e.g., `node.Transform.WorldMatrix.Translation.X`).
  - Params: `node_id`, `expression`.

### P4.2 Event & Change Tracking

- [ ] **`get_component_events`** — List events on a component and their current subscriber count.
  - Params: `node_id`, `component_id`.

- [ ] **`watch_property`** — Subscribe to change notifications for a property, queryable via polling.
  - Params: `object_id`, `property_name`.

---

## Phase 5 — Project Configuration & Settings (MEDIUM PRIORITY)

**Why fifth:** Settings tools are straightforward wiring but unlock the AI's ability to configure the project without manual UI navigation.

- [ ] **`get_game_settings`** — Read the current `GameStartupSettings`.
- [ ] **`set_game_setting`** — Modify a game startup setting.
  - Params: `property_name`, `value`.
- [ ] **`get_editor_preferences`** — Read all editor preferences.
- [ ] **`set_editor_preference`** — Modify an editor preference.
  - Params: `property_name`, `value`.
- [ ] **`get_engine_settings`** — Read engine configuration.
- [ ] **`list_game_configs`** — List config files in the game project's Config/ directory.
- [ ] **`read_game_config`** — Read a config file.
  - Params: `path`.
- [ ] **`write_game_config`** — Write a config file.
  - Params: `path`, `content`.

---

## Phase 6 — Advanced Scene Authoring Workflows (LOWER PRIORITY)

**Why last:** These are convenience/batch tools that build on all previous phases. Nice-to-have once the foundation is solid.

- [ ] **`instantiate_prefab`** — Instantiate a prefab into the active scene.
  - Params: `prefab_id` or `prefab_path`, `parent_id`, `name`, `position`, `rotation`, `scale`.

- [ ] **`create_prefab_from_node`** — Save an existing scene node hierarchy as a new prefab asset.
  - Params: `node_id`, `output_path`, `name`.

- [ ] **`batch_create_nodes`** — Create multiple scene nodes in a single call (reduces round trips).
  - Params: `nodes` (array of {name, parent_id, components, transform}).

- [ ] **`batch_set_properties`** — Set properties across multiple components/nodes in one call.
  - Params: `operations` (array of {node_id, component_type, property_name, value}).

- [ ] **`clone_scene`** — Deep-clone a scene for experimentation.
  - Params: `source_scene_name`, `new_scene_name`.

---

## Implementation Notes

### Pattern to Follow

All new tools follow the existing pattern in `XREngine.Editor/Mcp/Actions/`:

1. Add a new partial file `EditorMcpActions.<Category>.cs` (or extend an existing one).
2. Decorate methods with `[XRMcp]`, `[McpName("tool_name")]`, `[Description("...")]`.
3. Accept `McpToolContext context` as first parameter, `CancellationToken token` where async I/O is needed.
4. Return `Task<McpToolResponse>`.
5. The `McpToolRegistry` discovers tools automatically via reflection — no manual registration needed.

### Suggested New Partial Files

| File | Covers |
|------|--------|
| `EditorMcpActions.TypeSystem.cs` | Phase 1 — all type-system navigation tools |
| `EditorMcpActions.Scripting.cs` | Phase 2 — code management and scaffolding |
| `EditorMcpActions.GameAssets.cs` | Phase 3 — game project file-system operations |
| `EditorMcpActions.LiveInspection.cs` | Phase 4 — runtime object reflection |
| `EditorMcpActions.Settings.cs` | Phase 5 — game/editor/engine settings |

Phase 6 tools extend existing `EditorMcpActions.Scene.cs` and `EditorMcpActions.Workflow.cs`.

### Security Considerations

- File-system tools (Phases 2-3) must sandbox paths to `Engine.Assets.GameAssetsPath` — reject path traversal attempts (`..`, absolute paths outside the project).
- `invoke_method` (Phase 4) should be gated behind `McpServerReadOnly` and possibly an explicit opt-in preference.
- Code compilation tools should respect `McpServerReadOnly` — don't allow writes or compiles in read-only mode.

### Permission System (Implemented)

A 4-tier permission gate is now integrated into the MCP server pipeline. Every tool call is classified by risk and checked against a user-configurable auto-approve policy before execution.

**Risk levels** (`McpPermissionLevel` enum in `XREngine.Data/Core/Objects/`):
| Level | Value | Meaning |
|-------|-------|---------|
| `ReadOnly` | 0 | Queries, inspections, screenshots — no side effects |
| `Mutate` | 1 | Creates/modifies in-memory state (add node, set property) |
| `Destructive` | 2 | Writes to disk, deletes objects, replaces the active world |
| `Arbitrary` | 3 | Executes arbitrary methods/expressions (e.g., `invoke_method`) |

**Auto-approve policy** (`McpPermissionPolicy` enum, configurable in Global Editor Preferences → MCP Server):
| Policy | Effect |
|--------|--------|
| `AlwaysAsk` | Every tool call prompts |
| `AllowReadOnly` | ReadOnly auto-approved; Mutate+ prompts *(default)* |
| `AllowMutate` | ReadOnly & Mutate auto-approved; Destructive+ prompts |
| `AllowDestructive` | Only Arbitrary prompts |
| `AllowAll` | No prompts (advanced/dangerous) |

**How it works:**
1. `McpToolRegistry.ResolvePermissionLevel()` reads `[McpPermission]` attributes on tool methods, falling back to a heuristic based on tool name prefixes (`get_`/`list_` → ReadOnly, `delete_` → Destructive, else → Mutate).
2. `McpServerHost.HandleToolCallAsync()` calls `McpPermissionManager.RequestPermissionAsync()` before invoking the tool handler.
3. If the tool's level exceeds the policy threshold, a `TaskCompletionSource<bool>` is enqueued.
4. `McpPermissionPromptUI` (ImGui modal) renders once per frame, dequeues pending requests, and shows the user: risk badge, tool name, description, arguments, and Allow/Deny buttons with an optional "Remember for this tool" checkbox.
5. Remembered decisions are stored in a `ConcurrentDictionary` for the session.

**Tagging new tools:** When adding tools from this roadmap, apply `[McpPermission(McpPermissionLevel.X)]` to the method:
```csharp
[McpPermission(McpPermissionLevel.Destructive, Reason = "Deletes files from disk")]
[XRMcp, McpName("delete_game_asset"), Description("Delete a file from game assets")]
public static async Task<McpToolResponse> DeleteGameAsset(McpToolContext ctx, ...)
```

If omitted, the registry heuristic assigns a level automatically based on the tool name prefix.

### After Implementation

After adding or renaming MCP tools, regenerate the docs:

```powershell
pwsh Tools/Reports/generate_mcp_docs.ps1
```

---

## Tool Count Summary

| Phase | New Tools | Running Total (with existing 75) |
|-------|-----------|----------------------------------|
| Phase 1 — Type System | 11 | 86 |
| Phase 2 — Scripting | 13 | 99 |
| Phase 3 — Game Assets | 13 | 112 |
| Phase 4 — Live Inspection | 6 | 118 |
| Phase 5 — Settings | 8 | 126 |
| Phase 6 — Scene Authoring | 5 | 131 |
| **Total new** | **56** | **131** |
