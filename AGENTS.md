# AGENTS.md

Operating guide for human and AI contributors working in this repository.

## 1) Purpose and Current Product Stage

XRENGINE is a **Windows-first C# XR engine + editor** and is **not yet shipped**.

Implications:
- **No backward-compatibility obligation exists until v1 ships.** Breaking API changes, renames, and layout restructures are acceptable when they produce a cleaner v1.
- The goal is to ship the best possible default codebase as version 1. Prefer getting it right now over preserving legacy patterns.
- Favor clear, maintainable, high-performance architecture over quick hacks.

## 2) Default Working Agreement (Owner Preferences)

These are repository-default expectations unless a task explicitly says otherwise:

- **Change style:** Prefer clean architecture improvements, not only micro-patches.
- **Scope:** Allow broader subsystem sweeps when they materially improve correctness/maintainability.
- **Testing bar:** Run targeted tests closest to your changes.
- **Risk posture:** Balanced risk controls.
- **Risky operations:** Propose first and wait for approval before submodule bumps, dependency upgrades, schema/data migrations, or other high-blast-radius changes.
- **Docs policy:** Always update docs when user-facing behavior, launch flags, environment variables, tasks, or workflows change.
- **Unrelated issues during validation:** Opportunistically fix easy ones; report anything larger.
- **Commit style (when committing is requested):** simple imperative commit messages.

## 3) Environment and Platform Constraints

- Primary platform: **Windows 10/11**.
- Runtime/tooling: **.NET 10 SDK**.
- Rendering baseline: OpenGL 4.6 path is primary; Vulkan/DX12 remain WIP.
- XR paths exist for OpenXR and SteamVR/OpenVR; OpenVR is currently tested path.
- Repo uses git submodules under `Build/Submodules`.

## 4) Repository Map (High Value Areas)

- `XREngine/` - core runtime, scene graph, rendering, XR systems.
- `XREngine.Editor/` - desktop editor and unit-testing world bootstrap.
- `XREngine.Server/` - dedicated server executable.
- `XREngine.VRClient/` - standalone VR client executable.
- `XREngine.UnitTests/` - test project for engine/editor subsystems.
- `Assets/UnitTestingWorldSettings.json` - startup world toggles used by unit-testing world flows.
- `.vscode/tasks.json` and `.vscode/launch.json` - source of truth for local run/debug task orchestration.
- `docs/` - architecture, API guides, rendering notes, backlog/design docs.

### Editor UI Paths

The editor ships with **two UI pipelines**:

- **ImGui editor** — The current day-to-day interface. Fast to iterate on, fully functional, and the recommended path for development and testing right now.
- **Native UI editor** — The intended production-quality editor UI, built on the native scene-node pipeline with multithreaded layouting. It is under rigorous active development and **not yet dependable** for regular use. Expect missing features, layout instability, and breaking changes.

When working on editor tooling or features, default to the ImGui path unless the task explicitly targets the native UI pipeline.

## 5) First 10 Minutes in a Task

1. Read the user request fully.
2. Identify target subsystem(s) and nearest tests.
3. Check existing docs and task/launch configs before changing workflows.
4. Decide whether the request permits a subsystem sweep.
5. If risky operation is involved, stop and request approval with a concrete plan.

## 6) Build, Run, and Debug Workflows

### Build

- Full solution:
  - `dotnet restore`
  - `dotnet build XRENGINE.sln`
- Editor only:
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`

### Primary run modes

- Editor CLI:
  - `dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj`
- Common VS Code tasks (preferred for local orchestration):
  - `Build-Editor`, `Build-Server`, `Build-VRClient`
  - `Start-Editor-NoDebug`, `Start-Server-NoDebug`
  - `Start-Client-NoDebug`, `Start-Client2-NoDebug`, `Start-2Clients-NoDebug`
  - `Start-DedicatedServer-NoDebug`
  - `Start-P2PPeer-NoDebug`, `Start-P2PPeer2-NoDebug`
  - Pose sync tasks:
    - `Start-PoseServer-NoDebug`
    - `Start-PoseSourceClient-NoDebug`
    - `Start-PoseReceiverClient-NoDebug`
    - `Start-LocalPoseSync-NoDebug`

### Debug profiles

Use `.vscode/launch.json` profiles as canonical local debug setup, including:
- `Editor (Default World)`
- `Editor (Unit Testing World)`
- `Debug Client (Server & other client run separately)`
- `Debug Server (Clients runs separately)` / `Debug Server (Server only)`
- `Debug P2P Client (Peer client separate, no server)`
- `Debug VRClient (Editor runs separately)`
- Profiler configurations

## 7) Testing and Validation Policy

Default validation sequence:
1. Run the most targeted tests for changed subsystem.
2. If no tests exist, perform narrow build + run-path validation that exercises the changed flow.
3. If unrelated failures appear:
   - Fix easy nearby issues opportunistically.
   - Report larger unrelated failures without blocking handoff.

Useful predefined tasks include:
- `Test-SurfelGi`
- `Test-VulkanPhase3-Regression`

When adding new tests:
- Place tests in `XREngine.UnitTests/` using nearby naming patterns.
- Keep tests deterministic and focused on behavior, not implementation details.

## 8) Architecture and Refactor Guidance

Because this product is pre-ship and no backward compatibility is owed, architecture can and should improve as we touch code.

Preferred approach:
- Solve root causes, not only symptoms.
- Permit broader subsystem cleanup when directly related to the task's correctness or maintainability.
- Breaking changes to internal/public APIs are fine if they produce a cleaner v1.
- Actively reduce per-frame allocations when refactoring hot paths (see §11 Hot-Path Allocation Discipline).
- Avoid speculative rewrites with weak payoff.

Avoid:
- Unbounded cross-repo churn unrelated to task goals.
- New abstractions without immediate use.
- Silent behavior changes without docs updates.

## 9) Risk Management Rules

### Must request approval first

- Upgrading or replacing major dependencies/submodules.
- Data format/storage migrations.
- Large changes to build/release/deployment scripts.
- Changes that could break existing editor/server/client launch flows.

### License compliance (mandatory)

Every NuGet package or submodule pulled into the repository **must** have a license that permits both open-source and commercial use (e.g., MIT, Apache-2.0, BSD, Zlib, Unlicense). Copyleft licenses (GPL, AGPL) are acceptable only for the engine's own code or when the linking model keeps them isolated (e.g., LGPL via dynamic linking).

When any dependency is added, upgraded, or replaced:
1. Run the dependency-inventory generator to refresh the license audit:
   ```powershell
   pwsh Tools/Generate-Dependencies.ps1
   ```
2. Review the updated `docs/DEPENDENCIES.md` and `docs/licenses/` output for any `(unknown)` or newly flagged license.
3. If a license is incompatible or unknown, do **not** merge — escalate for owner review.
4. Include the refreshed `docs/DEPENDENCIES.md` and any new `docs/licenses/` files in the same commit.

### Can proceed without extra approval

- Localized code fixes/refactors in scoped subsystem.
- Targeted tests and small test additions.
- Docs updates that reflect behavior/task/flag changes.

## 10) Docs and Communication Requirements

Update docs in the same change when modifying user-visible behavior, including:
- CLI flags and env vars (e.g., networking/pose/test-world variables).
- Debug or task orchestration in `.vscode/tasks.json` or `.vscode/launch.json`.
- New prerequisites, native dependency handling, or setup steps.

Likely doc touchpoints:
- `README.md` for top-level workflows.
- `docs/README.md` index links.
- Relevant architecture/API/work docs under `docs/`.

## 11) Coding Conventions for Agents

- Follow existing style and naming in touched files.
- Prefer explicit, readable code over cleverness.
- Keep diffs coherent (group related edits, avoid drive-by formatting).
- Add comments only when they clarify non-obvious reasoning.
- No legacy API preservation is required pre-ship; improve APIs freely when it yields a better v1.

### Warnings Policy

- Keep compiler warnings to a minimum. New code must not introduce warnings.
- When touching a file that already has warnings, fix them if the fix is low-risk.
- Treat `#pragma warning disable` as a last resort; prefer resolving the root cause.

### Hot-Path Allocation Discipline

This is a game engine. **Heap allocations in per-frame hot paths are treated as bugs** unless proven harmless by profiling.

Hot paths include (but are not limited to):
- **Render** – draw call submission, command buffer recording, material/shader bind.
- **Swap** – buffer swap, frame presentation.
- **Collect Visible** – frustum culling, octree/BVH traversal, visibility determination.
- **Fixed Update** – physics step, fixed-rate simulation ticks.
- **Update** – per-frame game logic, transform propagation, animation tick.

When reviewing or writing code in these paths:
- **Flag / refactor** any `new` object allocations, LINQ queries, `foreach` over non-struct enumerators, closures/lambdas that capture, `string` concatenation, or boxing.
- **Prefer:** `Span<T>`, `stackalloc`, object pooling / rent-return patterns, pre-allocated lists/arrays, `ref struct`, `unsafe` pointer arithmetic where appropriate, and cached delegates.
- **Suggest** allocation-reducing improvements proactively when touching nearby code, even if not explicitly requested.
- When an allocation is unavoidable (e.g., first-time init, rare path), add a brief comment explaining why it's acceptable.

## 12) PR / Commit Expectations

When asked to prepare commits:
- Use simple imperative subject lines.
  - Good: `Refactor asset import fallback for deterministic extension selection`
  - Good: `Update networking pose task docs for client role flags`

Suggested PR summary structure:
- What changed
- Why
- Validation performed (targeted tests/build)
- Risks and follow-ups

## 13) Native Dependencies and External Tooling Notes

Be aware of repository-managed native/tooling dependencies:
- CoACD build/fetch scripts under `Tools/Dependencies/`.
- MagicPhysX native interop binary expectations.
- Rive native DLL workflow via submodules/build scripts.
- Optional YouTube URL extraction via `yt-dlp` (`Install-YtDlp` task).
- Optional nvCOMP/CUDA native binaries via `Tools/Dependencies/Get-NvComp.ps1` (`Install-NvComp` task).
- Optional NVIDIA SDK binaries under `ThirdParty/NVIDIA/SDK/win-x64/`.

Do not silently change these supply paths; treat as risky operations requiring proposal/approval when impact is non-trivial.

After any change to NuGet packages or submodules, run:
```powershell
pwsh Tools/Generate-Dependencies.ps1
```
and commit the refreshed `docs/DEPENDENCIES.md` + `docs/licenses/` output. See §9 *License compliance* for the full policy.

## 14) Ready-to-Use Task Template for Agents

Before coding:
- Confirm subsystem and expected run mode (Editor/Server/VRClient/P2P/Pose).
- Confirm if broader subsystem sweep is desired (default: yes when useful).

During coding:
- Keep a small, explicit checklist.
- Make coherent edits with root-cause intent.

Before handoff:
- Run targeted tests/build for touched area.
- Include any opportunistic unrelated fixes you made (if small).
- Call out any larger unrelated failures.
- Update docs for any user-facing workflow/flag/task changes.

---

If this file conflicts with an explicit task request, follow the explicit request and note the deviation in your summary.