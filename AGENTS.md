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
- `Build/_AgentValidation/` - ignored per-task root for AI/LLM scratch outputs, MCP captures, temp validation builds, logs, reports, and generated diagnostics.
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

For agent-driven MCP work, always use a named isolated editor session:

```powershell
pwsh Tools/Manage-McpEditorSession.ps1 Start -Name <unique-agent-session>
pwsh Tools/Invoke-Mcp.ps1 -Session <unique-agent-session> -Method ping
pwsh Tools/Manage-McpEditorSession.ps1 Stop -Name <unique-agent-session>
```

The session build and running editor live under `Build/_AgentValidation/mcp-sessions/<name>/`, so they do not lock `Build/Editor` or normal solution-build outputs. Never stop editor processes by name or shut down an editor you did not start. Use the session manager, which validates PID ownership and only stops the named session.

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

## Agent Output And Scratch Data

All AI/LLM-generated outputs that are not intended to be committed must live under `Build/_AgentValidation/`. The folder is ignored by Git and should be treated as disposable local evidence, not durable project documentation.

Keep `Build/_AgentValidation/` bounded. Before creating a new run root, delete old agent validation run subfolders as needed so there are no more than 10 immediate run subfolders under `Build/_AgentValidation/` at once. Preserve only active investigation output or evidence explicitly referenced by a durable `docs/work/` note; otherwise delete stale run folders instead of archiving them elsewhere in the repo.

Use one run root per task or investigation:

```powershell
$RunRoot = "Build\_AgentValidation\$(Get-Date -Format yyyyMMdd-HHmmss)-short-task-name"
New-Item -ItemType Directory -Force $RunRoot | Out-Null
```

Organize the run root with clear subfolders:

- `mcp-captures/` - MCP screenshots and viewport captures; pass this as `output_dir` to MCP capture tools.
- `mcp-output/` - JSON-RPC responses, scene dumps, editor-state snapshots, and other MCP-generated text or JSON.
- `logs/` - captured build output, command transcripts, copied or filtered engine logs, profiler extracts, and crash diagnostics.
- `temp-build/` - temporary validation projects, redirected build outputs, disposable binaries, and generated repro assets.
- `renderdoc/` - `.rdc` captures plus exported render targets, textures, pass lists, binding dumps, and inspection screenshots.
- `reports/` - generated validation reports, benchmark outputs, analysis tables, CSV/JSON summaries, and diff artifacts.
- `scratch/` - throwaway scripts, one-off tools, downloaded inspection inputs, and intermediate files.

Do not create new AI scratch folders at the repo root, `McpCaptures/`, `Screenshots/`, `_verify_temp/`, or `Build/TempValidation/`; those are legacy or tool-default locations. If a tool forces output elsewhere, move or copy the useful result into the current `Build/_AgentValidation/<run>/` folder and delete the disposable original when safe.

Engine-owned logs may still be emitted to `Build/Logs/` by the runtime. For investigations, copy the relevant logs or filtered excerpts into the current run's `logs/` folder, or record the exact `Build/Logs/...` session path in the durable `docs/work/` note.

Long-lived findings, decisions, and reproduction steps belong in tracked docs such as `docs/work/`. Ignored agent output should support that write-up, but the repository must not depend on ignored files for required build, test, or docs behavior.

## Iterating On The Editor

"Iterate on the editor" (a.k.a. "iterate on the editor") means driving a tight, evidence-based debug loop against a live editor process using the MCP server and the per-run logs, instead of guessing from source alone. Use it for rendering, scene, transform, and other visually observable issues. Each iteration is one full pass through the loop below.

When iterating on the editor to debug one or more issues, create or update an investigation doc under `docs/work/investigations/<subsystem>/` for the issue, for example `docs/work/investigations/rendering/` for rendering/editor-viewport work. Use it as the durable progress tracker until the issue is solved. It should record the problem statement, issues found, suggested solutions, attempted solutions, validation evidence, and whether the user reported each attempted solution worked or did not work.

The loop:

1. Choose a unique session name for the investigation and start an isolated editor build with the Unit Testing World and MCP enabled:

   ```powershell
   pwsh Tools/Manage-McpEditorSession.ps1 Start -Name <unique-agent-session>
   ```

   The command builds into the session's own .NET artifacts root, selects an unused MCP port, starts the editor asynchronously, and waits for MCP readiness. Confirm the relevant scene content is configured in `Assets/UnitTestingWorldSettings.jsonc` (for example `RenderAPI`, lights, and the model to import) before launching.
2. Position the view with MCP: `set_editor_camera_view` (or `focus_node_in_view` after locating the subject with `find_nodes_by_name`/`select_node_by_name`). Use `duration: 0` for an immediate cut.
3. Capture the result with MCP `capture_viewport_screenshot` and actually view the saved PNG. Pass `output_dir` under the current run root, for example `Build/_AgentValidation/<run>/mcp-captures/`; do not rely on the default `McpCaptures/` location. Re-capture from more than one camera position — an artifact that does not change with the camera is sampling stale/uninitialized data rather than rendering the scene.
4. Determine what the issue looks like and whether it still exists by inspecting the image(s), not by trusting tool return values.
5. Stop only the named editor session:

   ```powershell
   pwsh Tools/Manage-McpEditorSession.ps1 Stop -Name <unique-agent-session>
   ```

6. Review that session's logs under `Build/_AgentValidation/mcp-sessions/<name>/logs/` — for rendering work this is primarily `log_vulkan.log`, `log_opengl.log`, and `log_rendering.log`. Distinguish steady-state messages from shutdown-only teardown noise (for example `VUID-vkDestroyDevice-device-05137` is teardown, not a render bug). Group/filter validation errors, warnings, and the render-pass (`BeginRendering FBO=...`) sequence.
7. Form or refine a hypothesis, change exactly one variable (a setting, a toggle, or a targeted code fix), and repeat from step 1 until the issue is understood or resolved. Use `Start -NoBuild` only when the stopped session's existing binaries already contain the source change being tested.

Notes:
- Record durable findings (symptoms, ruled-out causes, render-pass order, next isolation step) so later iterations build on earlier ones instead of repeating them.

### RenderDoc GPU Captures

For Vulkan or OpenGL rendering issues, use RenderDoc when MCP screenshots and logs do not identify the failing pass/resource. Prefer it for shadow maps, post-process inputs, motion vectors, G-buffer contents, descriptor binding mistakes, layout hazards, and "the frame looks wrong but logs are inconclusive" cases.

1. Verify capture tooling first:

   ```powershell
   rdc doctor
   ```

   If `rdc` is unavailable but RenderDoc is installed, use `C:\Program Files\RenderDoc\renderdoccmd.exe` directly. Make sure the Vulkan RenderDoc layer is registered before Vulkan captures.
2. Build and launch from the repo root so assets and generated settings resolve correctly. Capture the editor with the same Unit Testing World/MCP flags used by the editor iteration loop:

   ```powershell
   $RunRoot = "Build\_AgentValidation\$(Get-Date -Format yyyyMMdd-HHmmss)-renderdoc"
   New-Item -ItemType Directory -Force "$RunRoot\renderdoc" | Out-Null
   rdc capture -o "$RunRoot\renderdoc\xrengine-vulkan.rdc" -- dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467
   ```

   RenderDoc fallback:

   ```powershell
   & "C:\Program Files\RenderDoc\renderdoccmd.exe" capture -w -d . -c "$RunRoot\renderdoc\xrengine-vulkan.rdc" dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467
   ```

3. Inspect the capture in an open-work-close session:

   ```powershell
   rdc open "$RunRoot\renderdoc\xrengine-vulkan.rdc"
   rdc info --json
   rdc passes
   rdc draws --limit 40
   rdc bindings <EID> --json
   rdc rt <EID> -o "$RunRoot\renderdoc\analysis-pass.png"
   rdc close
   ```

4. Always export suspicious render targets/textures to PNG and visually inspect them. For this engine, useful first checks are directional shadow atlas/cascade depth, Velocity, AmbientOcclusionTexture, LightingAccumTexture, BloomBlurTexture mips, TsrOutputTexture, and the final post-process output.
5. Keep capture output under the current run root's `renderdoc/` folder, close RenderDoc/`rdc` sessions when done, and record durable findings alongside the MCP/log observations.

## Testing Policy

Do not create or run unnecessary test methods while a feature is still being implemented or when only writing a new todo document. First make the feature work correctly through the narrowest relevant build or run-path validation; then add and run the appropriate tests after the implementation is functionally sound. This sequencing does not waive final validation or tests needed to reproduce and diagnose an active defect.

1. Run the most targeted tests for the changed subsystem.
2. If no test exists, run a narrow build or run-path validation.
3. Fix easy unrelated failures only when low-risk; otherwise report them.

Useful tasks include `Test-SurfelGi` and `Test-VulkanPhase3-Regression`.

New tests belong in `XREngine.UnitTests/`, should follow nearby naming patterns, and should be deterministic.

## Architecture And Code Rules

- Follow existing style in touched files.
- Prefer explicit, readable code over cleverness.
- Keep diffs coherent and avoid drive-by formatting.
- Write XML documentation summaries for types and members where practical, and comment code to explain intent, invariants, assumptions, and non-obvious reasoning. Avoid comments that merely restate the code.
- Since v1 has not shipped, improve APIs when it produces a cleaner design.
- Avoid speculative rewrites, unrelated cross-repo churn, and silent behavior changes without docs.

### C# Style

- Use expression-bodied members (`=>`) for methods and other members whose implementation is a single clear expression.
- Prefer early returns and guard clauses over nested `if` blocks.
- Omit braces around a single-line nested statement when doing so remains unambiguous; keep braces where they prevent ambiguity or improve readability.
- Keep each enum, interface, class, record, and struct in its own file rather than grouping multiple type declarations into a monolithic file. Name the file after the declared type.
- When working in a particularly large monolithic class file, suggest splitting the class into focused partial-class files organized by responsibility. Make the split when it is in scope and improves navigation without obscuring ownership or coupling.
- When working with a particularly large method, suggest splitting it into focused helper methods whose names clearly describe each operation or phase. Make the split when it is in scope and improves readability, while keeping tightly coupled control flow together.
- Keep broadly reusable helper methods that are not specific to the class or subsystem being changed in an appropriate generic/shared project and a cohesively named type. Preserve project dependency direction, and do not create miscellaneous utility dumping grounds or move helpers before their general-purpose responsibility is clear.

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

Work docs are organized by document purpose first, subsystem second:

- `docs/work/investigations/<subsystem>/` - bug, regression, crash, performance, visual artifact, and evidence-driven debug notes.
- `docs/work/progress/<subsystem>/` - implementation phase ledgers, status updates, validation manifests, and closeout/progress notes for active work.
- `docs/work/design/<subsystem>/` - design proposals and architecture plans.
- `docs/work/todo/<subsystem>/` - execution checklists and backlog items.
- `docs/work/testing/<subsystem>/` - validation plans, reproducible test notes, and hardware/software test matrices.

Do not create top-level subsystem buckets such as `docs/work/rendering/` for investigation or progress notes. For rendering debug loops, use `docs/work/investigations/rendering/`; for rendering implementation status, use `docs/work/progress/rendering/`.

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
