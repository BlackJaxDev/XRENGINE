# AGENTS.md

## Project And Working Agreement

XRENGINE is a pre-v1, Windows-first C# XR engine/editor: Windows 10/11, .NET 10, OpenGL 4.6 primary; Vulkan/DX12 are WIP. OpenXR and SteamVR/OpenVR exist; OpenVR is the tested path. Default to the ImGui editor unless explicitly targeting the unstable native UI.

- Favor clean, maintainable, high-performance v1 architecture and root-cause fixes. Breaking APIs, renames, and restructuring are acceptable; avoid speculative rewrites, unrelated churn, and drive-by formatting.
- Identify the subsystem, run mode, nearby tests, and relevant docs/tasks/launch profiles before editing. Follow existing style and validate through the narrowest useful build, test, or runtime path.
- Fix easy, low-risk nearby validation failures; report larger unrelated failures.
- Never silently substitute CPU fallback for a requested GPU/accelerated path; expose failures diagnostically unless fallback was requested.
- Update docs for behavior, flags, environment variables, tasks, setup, or workflow changes.
- Explicit user requests override this file; note deviations. Use simple imperative commit messages when asked to commit. PRs explain what changed, why, validation, risks, and follow-ups.
- Never put local machine paths or user profile names in code, comments, docstrings, tracked docs, or their links. Use repository-relative paths or placeholders such as `<repo-root>`, `<user-profile>`, `<desktop>`, `<downloads>`, or `%LOCALAPPDATA%`.

## Read On Demand

These linked guides remain required instructions for their respective tasks; read them before acting:

- **Broker/API workers or hierarchical swarms:** [broker policy](docs/developer-guides/ai/agent-broker-policy.md). Setup/trust/restarts: [broker user guide](docs/user-guide/ai/local-agent-broker.md); implementation/security: [broker developer guide](docs/developer-guides/ai/local-agent-broker.md).
- **Editor/MCP/RenderDoc work or launch/settings/tooling changes:** relevant sections of [editor and tooling workflows](docs/developer-guides/ai/agent-editor-workflows.md). MCP: [setup](docs/user-guide/ai/mcp-server.md) and [protocol/tools](docs/developer-guides/ai/mcp-server.md).
- **Rendering changes:** [DefaultRenderPipeline invariants](docs/architecture/rendering/default-render-pipeline-notes.md) and [mesh submission contracts](docs/architecture/rendering/mesh-submission-strategies.md).
- **Licensing, dependencies, or contributions:** `LICENSE.md`, `LEGAL/README.md`, and relevant `LEGAL/` guidance.

## Model Routing And Delegation

Optimize cost per validated result using ambiguity, risk, reasoning difficulty, and validation strength, not file counts or workload percentages.

- **GPT-6 Sol:** default coordinator/implementer for ordinary exploration, design, debugging, review, refactoring, integration, and test iteration.
- **GPT-6 Luna:** bounded, reversible, low-ambiguity work with deterministic checks: inventories, mechanical edits, boilerplate, docs, straightforward test scaffolding, validation execution, and log classification. Exclude unresolved architecture, novel algorithms, ambiguous root causes, security, and subtle concurrency/lifetime/unsafe-code/rendering/GPU/performance work.
- **GPT-6 Astra:** only the difficult/high-risk slice: cross-subsystem architecture, unresolved root causes, complex concurrency/GPU failures, sophisticated algorithms, security/data-loss risk, or consequential final review. Return routine work to Sol/Luna afterward.
- GPT-5.6 tiers are deprecated; support exact legacy runs only when explicitly requested. Never recommend or silently fall back to them.
- For repeated workloads, compare one lower reasoning effort on representative tasks; keep it only with equivalent validation. High/max effort requires measured benefit, not task size.
- Handoff evidence contains only objective, success criteria, constraints, relevant files/symbols, diff, commands/results, failed hypotheses, unresolved questions, and next decision.

Routing commands preserve completed work and the exact tier: **"Escalate to Astra"** → `gpt-6-astra`; **"Escalate to Sol"** → `gpt-6-sol`; **"De-escalate to Luna"** → `gpt-6-luna`. Stop at a coherent boundary without waiting for arbitrary failures. For Luna, provide a bounded checklist with exact files, constraints, acceptance criteria, validation commands, and known risks. Use a supported native switch/handoff; otherwise state that the runtime cannot switch itself and return the packet for the user. Never simulate a switch with a new task, branch, worktree, commit, push, or external write. Resume recorded work without repeating it unless evidence is stale or a concrete reason warrants it. The user controls the current task's native model.

Native defaults: Sol/medium coordinator, Luna/low spawned agents. Proactively delegate independent bounded work when it improves speed or reduces noisy context; trivial commands stay local. Use `.codex/agents/` roles:

- `luna_explorer`: Luna, read-only searches, inventories, extraction, classification, deterministic doc drafts, log summaries.
- `terra_worker`: Sol (legacy role name), ordinary implementation/refactoring/integration and authorized filesystem work with clear ownership and acceptance criteria.
- `sol_architect`: Astra (legacy role name), read-only max-effort escalation for difficult/high-risk reasoning and final review.

Parallelize independent reads; serialize overlapping edits, moves, and removals.

For every substantive task, assess whether a bounded broker reasoning/editor-evidence slice adds value; invoke it automatically when useful under the linked policy. Broker spend is pre-authorized within that policy; do not ask per run. Keep Codex as coordinator and use native agents for broad searches, implementation, filesystem work, and validation. Broker workers are separately billed API requests, never native model switches. All five broker tools must be callable; do not simulate missing tools. Preserve exact model selection, narrow data/tool/budget limits, credential privacy, explicit mutation/destructive authority, terminal-state polling, and local validation as detailed in the policy.

## Code Rules

- Prefer explicit, readable code, guard clauses, and early returns. Use `=>` for single clear expressions; omit single-statement braces only when unambiguous.
- One enum/interface/class/record/struct per matching filename. Suggest focused partial files for large classes and helpers for large methods; split when in scope and clearer without hiding coupling.
- Put genuinely reusable helpers in cohesive shared types/projects, preserving dependency direction. Avoid miscellaneous utility collections and premature moves.
- Add practical XML summaries; comments explain intent, invariants, assumptions, and non-obvious reasoning.
- In `XRBase` descendants, use `SetField(...)` instead of direct backing-field assignment in setters/mutation paths; convert touched direct assignments where practical.
- Introduce no compiler warnings; fix low-risk warnings in touched files. `#pragma warning disable` is a last resort.
- Per-frame heap allocations are bugs unless profiling proves otherwise. Hot paths include render submission, swap/present, visible collection, fixed update, and frame update. Flag/refactor `new`, LINQ, captured closures, boxing, string concatenation, and non-struct enumerators; prefer spans, stack allocation, pooling, preallocated storage, `ref struct`, cached delegates, and appropriate unsafe code. Explain unavoidable allocations.
- Never allocate arrays or call `.ToArray()` merely to count, inspect, or iterate hot-path data, including hidden materialization in convenience APIs; use counts, indexing, spans, or reusable storage.

### Phase And Todo References

Keep implementation-plan phases and todo-document references/status out of code, comments, XML summaries, type/member names, diagnostics, change summaries, and other lasting artifacts. Describe behavior, responsibility, invariants, and limitations directly.

Temporary references are allowed **only when the associated todo document is active and open, and necessary to track current implementation/debugging state**. Keep them minimal, tied to that document, and preferably in the document itself. Closing a finished document requires removing obsolete references, renaming affected identifiers/usages, and rewriting useful explanations in generally applicable terms; verify this cleanup at closeout. Domain terms such as runtime lifecycle phases remain valid.

## Validation

- During feature-regression debugging or an integration awaiting feature validation, **do not add or modify tests**. First validate the feature through its live/runtime path; test work then requires explicit user clearance.
- Avoid unnecessary tests during implementation or todo-only documentation work. This sequencing does not waive final validation or tests needed to reproduce/diagnose an active defect.
- Run the most targeted applicable tests; if none exist, use a narrow build/run check. New tests belong in `XREngine.UnitTests/`, follow nearby naming, and must be deterministic.
- Useful tasks: `Test-SurfelGi`, `Test-VulkanPhase3-Regression`.

## Repository And Commands

- Runtime: `XREngine/`; editor/unit-world bootstrap: `XREngine.Editor/`; executables: `XREngine.Server/`, `XREngine.VRClient/`; tests: `XREngine.UnitTests/`; submodules: `Build/Submodules/`.
- Canonical tasks/debug profiles: `.vscode/tasks.json`, `.vscode/launch.json`. Build tasks: `Build-Editor`, `Build-Server`, `Build-VRClient`. Script menu: `ExecTool.bat`.
- Build: `dotnet restore`, then `dotnet build XRENGINE.slnx` or `dotnet build XREngine.Editor/XREngine.Editor.csproj`.
- Run: `dotnet run --project XREngine.Editor/XREngine.Editor.csproj`; append `-- --unit-testing` or set `XRE_WORLD_MODE=UnitTesting`.
- Unit-world settings: generated/ignored `Assets/UnitTestingWorldSettings.jsonc`; server mirror under `XREngine.Server/Assets/`. After settings type changes, run `Tools/Generate-UnitTestingWorldSettings.ps1` (or matching task) to regenerate settings/schema.
- Logs: `Build/Logs/<configuration>_<tfm>/<platform>/<session>/`. Detailed launch, pose-test, profiler, bootstrap, and MCP commands are in the linked workflows.

## Editor And GPU Investigation

- Agent MCP work always uses a named isolated session via `Tools/Manage-McpEditorSession.ps1 Start|Stop -Name <name>`; never stop processes by name or an editor you did not start. Sessions build/run under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/<timestamp>-<name>/`, avoiding normal build locks.
- For observable editor/rendering/scene/transform issues, follow the linked live-editor loop: configure the scene, start the isolated session, position the camera, capture and **view** PNGs from multiple positions, stop your session, inspect its logs, change one variable, and repeat. Use `Start -NoBuild` only when existing session binaries contain the change.
- Track problems, findings, proposed/attempted fixes, validation, and user-reported success/failure in `docs/work/investigations/<subsystem>/`. Record ruled-out causes and next steps to avoid repeating work.
- For inconclusive Vulkan/OpenGL screenshots/logs, use RenderDoc per the workflow guide/skill. Inspect exported suspicious textures/targets visually, close capture sessions, and record findings.
- After adding/renaming MCP tools, run `pwsh Tools/Reports/generate_mcp_docs.ps1`.

## Scratch Output And Durable Docs

All AI-generated artifacts not intended for commit belong under `Build/_AgentValidation/`; required build/test/docs behavior must never depend on ignored evidence.

- Keep at most five immediate directories: reserved `00000000-000000-shared` (reproducible tooling, renderer hot-reload generations, at most five MCP sessions) plus four task runs named `yyyyMMdd-HHmmss-short-task-name`.
- Before creating a run, execute `pwsh Tools/Limit-AgentValidation.ps1 -ReserveTaskRun`; delete the oldest inactive run before exceeding the limit. Omit the flag for routine cleanup. Use one run root per task/investigation.
- Organize into `mcp-captures/`, `mcp-output/`, `logs/`, `temp-build/`, `renderdoc/`, `reports/`, and `scratch/`. Pass the capture folder explicitly as MCP `output_dir`.
- Never create scratch at the repository root, `McpCaptures/`, `Screenshots/`, `_verify_temp/`, or `Build/TempValidation/`. Relocate useful forced outputs and safely delete disposable originals.
- Engine logs may remain in `Build/Logs/`; copy relevant evidence to the run's `logs/` or record the exact session path in a durable work doc. Evidence remains disposable even when linked; copy required findings into tracked docs.

Use `docs/work/<purpose>/<subsystem>/`: `investigations` for debugging, `progress` for active status/validation/closeout, `design` for proposals, `todo` for checklists/backlog, and `testing` for validation plans/reproduction/matrices. Do not create top-level subsystem buckets such as `docs/work/rendering/`. Update relevant `README.md`, `docs/README.md`, and feature docs when behavior/workflows change.

## Risk, Dependencies, And Licensing

Ask before submodule bumps, dependency upgrades/replacements, data/storage migrations, large build/release/deployment-script changes, or changes likely to break launch flows.

- Dependencies must permit both the XRENGINE Community Source License and commercial distribution. Do not assume GPL/AGPL compatibility: require an isolated process/aggregation boundary, a separate compatible license, or owner review. LGPL must remain dynamically linked or isolated. Escalate unknown/incompatible licenses; never merge them.
- After dependency additions/upgrades/replacements, run `pwsh Tools/Reports/Generate-Dependencies.ps1`; review/include `docs/DEPENDENCIES.md` and `docs/licenses/`.
- Do not silently change managed supply paths for CoACD, MagicPhysX, Rive native submodules, optional `yt-dlp`, nvCOMP/CUDA, or NVIDIA SDK binaries under `ThirdParty/NVIDIA/SDK/win-x64/`.
- `LICENSE.md` controls: this is a custom **source-available** license, not OSI-approved or MIT/LGPL/GPL/AGPL/"open source." Use `LEGAL/README.md` to classify engine/application code.
- Transaction-free applications may keep Independent Application Code closed, but distributed/externally deployed Engine Modifications must be public.
- Any Monetization requires a signed Commercial License; private Engine Modifications separately require a signed Private Engine Modification License, even without monetization. Neither grants the other. No public order form: contact `blackjax0@gmail.com`.
- Pre-v1 Indie/Enterprise financial terms, eligibility, fees, royalties, reporting, and duration are privately negotiated; publish no numeric commercial terms. Licensing scope, pricing, thresholds, notices, contributor grants, and commercial terms require owner review; no drive-by changes.
- External contributions require accepted `LEGAL/CONTRIBUTING.md` and an Owner-retained acceptance record before merge. Disclose material AI assistance and verify grant rights. Individual acceptance uses the submitting GitHub account/PR checkbox; never require public legal names; additional verification stays private.
- Historical GPLv3/AGPLv3 releases retain their terms. The Community Source License is additionally offered for covered code since 12 June 2023, with no retroactive obligations/liability before 27 July 2026.
- `BlackJax` (primary), `BlackJaxVR`, `BlackJaxDev`, `Jax`, and `BlackJax96` are one maintainer, representing the company in the license/agreements.
