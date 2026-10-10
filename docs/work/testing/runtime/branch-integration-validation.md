# Branch Integration Validation

Code work: [Branch integration TODO](../../todo/runtime/branch-integration-todo.md)
State: [Integration handoff](../../progress/runtime/branch-integration-handoff.md)
Workflow: [Isolated editor validation](../../../developer-guides/ai/agent-editor-workflows.md)

## Setup

Use the ImGui editor and named isolated MCP sessions. Use a separate unit-world settings file through `XRE_UNIT_TEST_WORLD_SETTINGS_PATH`. Preserve normal editor settings. Validate the physics-only revision before the combined browser/runtime revision. Use the same scene and settings for comparisons.

Baseline fixture: copy the generated unit-world settings, then set `PhysicsChain: true`, `ProceduralSky: true`, `CameraAntiAliasingModeOverride: "None"`, and `Rendering.RenderPipeline: "DefaultRenderPipeline"`. The scene imports the `jax2031` avatar with three hardcoded chains (both ears and one hair chain). Run a Vulkan copy with the Advanced pipeline for the Vulkan rows. Capture three fixed views: front `(0, 1.5, 2.2)`, back-right `(1.8, 1.8, -1.2)`, and wide `(-4, 3, 5)`. Then enter play mode and capture the wide view.

Reasons for this fixture:

- The default world picks a random environment map at each launch. Use the procedural sky so that captures can be compared.
- The Advanced pipeline in desktop mode does not show this avatar on `master` either. OpenGL with the Advanced pipeline stays black or sky-only until all texture uploads finish. Do not use it for A/B checks.
- In this Debug build, play-mode entry takes about eight minutes, because the snapshot serializes the avatar meshes. The MCP `enter_play_mode` call times out after 120 seconds. Poll `get_engine_state` until `playModeState` is `Play`.

## Checks

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Physics Editor build | `dotnet build XREngine.Editor/XREngine.Editor.csproj -m:1 -v:minimal` | No warnings or errors. | Passed | 2026-10-09, `ce54e3c9b` (recreated physics merge): zero warnings, zero errors. |
| Physics desktop baseline | Start an isolated session, inspect scene and frame progress, capture and view multiple camera positions, then inspect logs. | Assets, animation, physics chains, and rendering work. | Passed, matches `master` | 2026-10-09, `ce54e3c9b` and `master` `b89e427c4`, OpenGL with the Default pipeline. Both revisions render the avatar in all three views and enter play mode. The three chains have valid runtime handles in play mode. Both revisions show a static T-pose in edit and play mode, so animation playback is not visible in either. Vulkan with the Advanced pipeline renders the avatar and environment on `ce54e3c9b`. Startup logs show only transient frame-package mismatches. |
| Combined desktop builds | Build Editor, Server, VRClient, and unit tests. | No new errors or warnings. | Passed | 2026-10-09, resolved merge in the working tree on `ce54e3c9b`: zero warnings, zero errors for all four. |
| OpenGL runtime | Run the baseline scene on the combined revision; inspect frame progress, captures, and logs. | No desktop regression against the baseline. | Passed with one observation | 2026-10-09, OpenGL with the Default pipeline: the avatar renders in all views and in play mode, the three chains have valid handles in play mode, and there is no timer fault. Observation: the same seven materials log `Shader Texture Binding` fallbacks on all revisions, but the repeat count is 2 on `master`, 3 on physics, and 8 on the combined revision. That session also ran two capture scripts at the same time. Investigate whether browser shader-cache changes relink programs more often. |
| Vulkan runtime | Repeat with the requested Vulkan path and validation enabled. | No new steady-state validation failures or implicit CPU fallback. | Passed | 2026-10-09, Vulkan with the Advanced pipeline and `XRE_VULKAN_VALIDATION=1`: the avatar renders in edit and play mode, play entry has no timer fault, the session logs contain no VUID message and no `[ERROR]` line, and the overlay reports 0 validation callbacks and `cpu fallback 000`. |
| Focused regressions | Run existing physics, scheduler, frame-view, GPU publication, deformation, and index-preparation tests. Compare with detached worktrees of both inputs. | No failure that is new in the merge. | One new failure | 2026-10-09: merged 40 of 1029 fail; physics input 36 of 1011; browser input 22 of 941. The only new failure is `MissingOrNullSourceClearsIdentityAndRecovers` (two cases): its fixture casts a private lookup to an old tuple type. The fix is a test change and needs user clearance. The other 38 failures also fail on an input revision. |
| Browser compile and publish | Use the portable compile and browser publish gates on the merged revision. | Shared provider boundaries compile; existing browser limitations remain explicit. | Open | None. |
| Desktop host lifetime | Exercise start, world load/unload, worker failure, and shutdown through the existing runtime paths. | No deadlock, stale publication, or disposal race. | Open | Play-mode entry and exit of the editor world work on the combined revision. A focused concurrency review found disposal issues; the confirmed ones are fixed, and the others are open code items. No world unload or worker-failure run yet. |

## Limits

The WebGPU documents at `ff0eabf95` leave desktop OpenGL, Vulkan, and frame pacing checks open. They also record browser UI frame-progress and shadow preparation failures. Those records do not prove desktop safety. A successful compile alone does not satisfy this validation plan.
