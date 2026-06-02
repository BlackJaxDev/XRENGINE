# AGENTS.md

Concise operating guide for human and AI contributors in this repository.

## Product Posture

XRENGINE is a Windows-first C# XR engine and editor. It has not shipped v1, so there is no backward-compatibility obligation yet.

- Prefer clean v1 architecture over preserving legacy patterns.
- Breaking API changes, renames, and layout restructures are acceptable when they make the codebase better.
- Optimize for clear, maintainable, high-performance engine code.

## Default Working Agreement

- Favor root-cause fixes and coherent subsystem cleanup over tiny patches when the task justifies it.
- Run targeted tests or the narrowest useful build/run validation.
- Update docs when user-facing behavior, launch flags, env vars, tasks, setup, or workflows change.
- Fix easy unrelated validation issues when nearby; report larger unrelated failures.
- Do not hide explicitly requested GPU/accelerated paths behind silent CPU fallbacks; make failures visible with diagnostics unless fallback behavior is explicitly requested.
- When asked to commit, use simple imperative commit messages.
- If this file conflicts with an explicit user request, follow the user request and note the deviation.

## Platform And Constraints

- Primary platform: Windows 10/11.
- SDK/tooling: .NET 10 SDK.
- Rendering: OpenGL 4.6 is primary; Vulkan and DX12 are WIP.
- XR: OpenXR and SteamVR/OpenVR paths exist; OpenVR is currently the tested path.
- Submodules live under `Build/Submodules`.

## Repository Map

- `XREngine/` - core runtime, scene graph, rendering, XR systems.
- `XREngine.Editor/` - desktop editor and unit-testing world bootstrap.
- `XREngine.Server/` - dedicated server executable.
- `XREngine.VRClient/` - standalone VR client executable.
- `XREngine.UnitTests/` - engine/editor tests.
- `Assets/UnitTestingWorldSettings.jsonc` - generated local unit-testing world settings.
- `Build/Logs/` - per-run logs and profiler traces when file logging is enabled.
- `.vscode/tasks.json` / `.vscode/launch.json` - canonical local run/debug orchestration.
- `ExecTool.bat` - interactive launcher for scripts under `Tools/`.
- `docs/` - architecture, API guides, rendering notes, backlog/design docs.
- `docs/architecture/rendering/default-render-pipeline-notes.md` - DefaultRenderPipeline invariants and known issues.
- `docs/architecture/rendering/mesh-submission-strategies.md` - CPU/GPU mesh submission strategy contract and resolver rules.
- `docs/features/mcp-server.md` - MCP server documentation.

## Editor UI

The editor has two UI paths:

- ImGui editor: current day-to-day interface; use this by default.
- Native UI editor: intended production UI, but still unstable and actively changing.

Default to ImGui unless the task explicitly targets native UI.

## Starting A Task

1. Read the request fully.
2. Identify the subsystem, run mode, and nearest tests.
3. Check existing docs, tasks, and launch configs before changing workflows.
4. Decide whether a broader subsystem cleanup is warranted.
5. Stop for approval before risky operations.

## Build, Run, Debug

Build:

```powershell
dotnet restore
dotnet build XRENGINE.slnx
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
```

Editor CLI:

```powershell
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -- --unit-testing
```

`XRE_WORLD_MODE=UnitTesting` also boots the Unit Testing World.

Preferred VS Code tasks:

- Build: `Build-Editor`, `Build-Server`, `Build-VRClient`.
- Run: `Start-Editor-NoDebug`, `Start-Server-NoDebug`, `Start-Client-NoDebug`, `Start-Client2-NoDebug`, `Start-2Clients-NoDebug`, `Start-DedicatedServer-NoDebug`.
- Pose sync: `Start-PoseServer-NoDebug`, `Start-PoseSourceClient-NoDebug`, `Start-PoseReceiverClient-NoDebug`, `Start-LocalPoseSync-NoDebug`.
- Settings generation: `Generate-UnitTestingWorldSettings`.

Use `.vscode/launch.json` as the source of truth for debug profiles, including default/unit-testing editor, client/server, VRClient, and profiler configurations.

## ExecTool

`ExecTool.bat` provides a numbered menu for scripts under `Tools/`.

```powershell
ExecTool
ExecTool 16
ExecTool --bootstrap
ExecTool --list
ExecTool --help
```

Tool categories include Setup, Build, Editor, Repo, Docs, Reports, and Deps. `ExecTool --bootstrap` initializes submodules, downloads dependencies, builds submodules, generates Unit Testing World settings, builds DocFX, then launches DocFX and the editor.

## Logs And Unit Testing World

- Logs and profiler traces are written under `Build/Logs/<configuration>_<tfm>/<platform>/<session>/`.
- Common profiler logs: `profiler-main-thread-invokes.log`, `profiler-fps-drops.log`, `profiler-render-stalls.log`.
- Math Intersections benchmarks also write `math-intersections-benchmarks.log`.
- Root settings: `Assets/UnitTestingWorldSettings.jsonc`.
- Server mirror: `XREngine.Server/Assets/UnitTestingWorldSettings.jsonc`.
- The root JSONC is ignored by Git and generated by bootstrap/settings tasks.
- JSONC schema: `.vscode/schemas/unit-testing-world-settings.schema.json`.
- Regenerate settings/schema with `Tools/Generate-UnitTestingWorldSettings.ps1` or `Generate-UnitTestingWorldSettings` after settings type changes.
- Pose/networking tests use `XRE_UNIT_TEST_WORLD_KIND=NetworkingPose` plus role env vars: `server`, `sender`, `receiver`.

## Testing Policy

1. Run the most targeted tests for the changed subsystem.
2. If no test exists, run a narrow build or run-path validation.
3. Fix easy unrelated failures only when low-risk; otherwise report them.

Useful tasks include `Test-SurfelGi` and `Test-VulkanPhase3-Regression`.

New tests belong in `XREngine.UnitTests/`, should follow nearby naming patterns, and should be deterministic.

## Architecture And Code Rules

- Follow existing style in touched files.
- Prefer explicit, readable code over cleverness.
- Keep diffs coherent and avoid drive-by formatting.
- Add comments only for non-obvious reasoning.
- Since v1 has not shipped, improve APIs when it produces a cleaner design.
- Avoid speculative rewrites, unrelated cross-repo churn, and silent behavior changes without docs.

### XRBase Mutation

For any type deriving from `XRBase`, do not assign backing fields directly from property setters or similar mutation paths. Use `SetField(...)` so change notification and invalidation remain correct. When touching existing direct assignments, prefer converting them.

### Warnings

- New code must not introduce compiler warnings.
- Fix low-risk warnings in touched files.
- Use `#pragma warning disable` only as a last resort.

### Hot-Path Allocations

Heap allocations in per-frame hot paths are bugs unless profiling proves otherwise.

Hot paths include render submission, swap/present, visible collection, fixed update, and per-frame update.

Flag or refactor `new`, LINQ, captured closures, boxing, string concatenation, and `foreach` over non-struct enumerators in these paths. Prefer `Span<T>`, `stackalloc`, pooling, preallocated collections, `ref struct`, cached delegates, and appropriate unsafe code. If an allocation is unavoidable, add a short reason.

## Risk And Dependencies

Ask for approval before:

- Submodule bumps.
- Dependency upgrades/replacements.
- Data format or storage migrations.
- Large build/release/deployment script changes.
- Changes likely to break editor/server/client launch flows.

Dependency license rule: every NuGet package or submodule must permit open-source and commercial use. GPL/AGPL are acceptable only for engine-owned code or isolated linking models; LGPL must remain dynamically linked or otherwise isolated.

After adding, upgrading, or replacing a dependency:

```powershell
pwsh Tools/Generate-Dependencies.ps1
```

Then review and include updated `docs/DEPENDENCIES.md` and `docs/licenses/`. Do not merge unknown or incompatible licenses; escalate for owner review.

Repository-managed native/tooling dependencies include CoACD scripts, MagicPhysX interop binaries, Rive native DLL submodule flow, optional `yt-dlp`, optional nvCOMP/CUDA binaries, and optional NVIDIA SDK binaries under `ThirdParty/NVIDIA/SDK/win-x64/`. Do not silently change these supply paths.

## Docs And Work Items

Update docs with behavior/workflow changes. Likely touchpoints are `README.md`, `docs/README.md`, and relevant docs under `docs/`.

When generating a todo from a design doc:

- First task: create a dedicated branch for the todo list.
- Final task: merge that branch back into `main` after completion and validation.

## PR Expectations

When preparing a PR, include:

- What changed.
- Why.
- Validation performed.
- Risks and follow-ups.

## MCP Server

The editor embeds an HTTP JSON-RPC 2.0 MCP server for scene, component, transform, asset, and editor-state operations. Full docs: `docs/features/mcp-server.md`.

Enable it at runtime in ImGui Global Editor Preferences under MCP Server, or launch with:

```powershell
XREngine.Editor.exe --mcp
XREngine.Editor.exe --mcp --mcp-port 8080
```

CLI flags persist to preferences. Default port is `5467`.

VS Code/Copilot workspace config:

```json
{
  "servers": {
    "xrengine": {
      "type": "http",
      "url": "http://localhost:5467/mcp/"
    }
  }
}
```

Key settings: `McpServerEnabled`, `McpServerPort`, `McpServerRequireAuth`, `McpServerReadOnly`, `McpServerAllowedTools`, `McpServerDeniedTools`, and `McpServerRateLimitEnabled`.

After adding or renaming MCP tools, regenerate docs:

```powershell
pwsh Tools/Reports/generate_mcp_docs.ps1
```
