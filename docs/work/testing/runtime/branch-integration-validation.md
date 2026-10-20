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
| OpenGL runtime | Run the baseline scene on the combined revision; inspect frame progress, captures, and logs. | No desktop regression against the baseline. | Passed | 2026-10-09, OpenGL with the Default pipeline: the avatar renders in all views and in play mode, the three chains have valid handles in play mode, and there is no timer fault. A single-script run logs 14 `Shader Texture Binding` fallback lines for the whole session, the same as `master`. The higher earlier count came from two capture scripts that ran at the same time. |
| Vulkan runtime | Repeat with the requested Vulkan path and validation enabled. | No new steady-state validation failures or implicit CPU fallback. | Passed | 2026-10-09, Vulkan with the Advanced pipeline and `XRE_VULKAN_VALIDATION=1`: the avatar renders in edit and play mode, play entry has no timer fault, the session logs contain no VUID message and no `[ERROR]` line, and the overlay reports 0 validation callbacks and `cpu fallback 000`. |
| Focused regressions | Run existing physics, scheduler, frame-view, GPU publication, deformation, and index-preparation tests. Compare with detached worktrees of both inputs. | No failure that is new in the merge. | Passed | 2026-10-09, working tree after `e9eb73bac`: 39 of 1066 fail. None is new in the merge. `MissingOrNullSourceClearsIdentityAndRecovers` passes after the fixture update. `DuplicatePoolRetirementIsSuppressedBeforeTicketCapture` reads a source file that is absent on `master`, physics, browser, and the merge. |
| Browser compile and publish | Use the portable compile and browser publish gates on the merged revision. | Shared provider boundaries compile; existing browser limitations remain explicit. | Partly passed | 2026-10-09: `Tools/Test-PortableBrowserCompile.ps1` found a merge break: `WebGpuMeshRenderer` did not implement the new `IApiMeshRenderer.RenderIndexedIndirect`. It now fails explicitly (UR06.10 in the unified-runtime TODO). After the fix, 17 of the 20 portable projects compile for `browser-wasm` with zero warnings and zero errors. `XREngine.Browser`, `XREngine.Browser.Standalone`, and `XREngine.Runtime.Physics.Jolt` need the prepared Jolt browser supply, and Emscripten is not installed on this machine. Run the CI lane or prepare Jolt to finish this check. The browser publish and smoke gates were not run. |
| Desktop host lifetime | Exercise start, world load/unload, worker failure, and shutdown through the existing runtime paths. | No deadlock, stale publication, or disposal race. | Passed except worker failure | 2026-10-09, working tree after `e9eb73bac`, OpenGL with the Default pipeline: play-mode entry, play-mode exit (20 s; the chains register again with valid handles), deletion of the avatar subtree (no chains remain, rendering continues), and session shutdown. No exception and no timer fault in the session logs. Scheduler and world-lifecycle unit tests pass. No test or runtime path injects a worker fault yet; that needs a new unit test, which requires user clearance. |

## Limits

The WebGPU documents at `ff0eabf95` leave desktop OpenGL, Vulkan, and frame pacing checks open. They also record browser UI frame-progress and shadow preparation failures. Those records do not prove desktop safety. A successful compile alone does not satisfy this validation plan.
