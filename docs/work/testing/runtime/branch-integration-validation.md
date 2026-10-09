# Branch Integration Validation

Code work: [Branch integration TODO](../../todo/runtime/branch-integration-todo.md)
State: [Integration handoff](../../progress/runtime/branch-integration-handoff.md)
Workflow: [Isolated editor validation](../../../developer-guides/ai/agent-editor-workflows.md)

## Setup

Use the ImGui editor and named isolated MCP sessions. Use a separate unit-world settings file through `XRE_UNIT_TEST_WORLD_SETTINGS_PATH`. Preserve normal editor settings. Validate the physics-only revision before the combined browser/runtime revision. Use the same scene and settings for comparisons.

## Checks

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Physics Editor build | `dotnet build XREngine.Editor/XREngine.Editor.csproj --no-restore -m:1 -v:minimal` | No warnings or errors. | Passed | 2026-10-09, `0011f22c5`: zero warnings, zero errors. |
| Physics desktop baseline | Start an isolated session, inspect scene and frame progress, capture and view multiple camera positions, then inspect logs. | Assets, animation, physics chains, and rendering work. | Open | `integration-physics` was stopped during preparation at the user's request. No runtime pass. |
| Combined desktop builds | Build Editor, Server, and VRClient through canonical tasks. | No new errors or warnings. | Open | None. |
| OpenGL runtime | Run the baseline scene on the combined revision; inspect frame progress, captures, and logs. | No desktop regression against the baseline. | Open | None. |
| Vulkan runtime | Repeat with the requested Vulkan path and validation enabled. | No new steady-state validation failures or implicit CPU fallback. | Open | None. |
| Focused regressions | Run existing physics, scheduler, frame-view, GPU publication, deformation, and index-preparation tests. | Relevant tests pass on the final source revision. | Open | No tests run or changed during integration. |
| Browser compile and publish | Use the portable compile and browser publish gates on the merged revision. | Shared provider boundaries compile; existing browser limitations remain explicit. | Open | None. |
| Desktop host lifetime | Exercise start, world load/unload, worker failure, and shutdown through the existing runtime paths. | No deadlock, stale publication, or disposal race. | Open | Review identified cross-world locking and deferred-transfer risks; implementation remains open. |

## Limits

The WebGPU documents at `ff0eabf95` leave desktop OpenGL, Vulkan, and frame pacing checks open. They also record browser UI frame-progress and shadow preparation failures. Those records do not prove desktop safety. A successful compile alone does not satisfy this validation plan.
