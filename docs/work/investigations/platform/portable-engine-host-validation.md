# Portable Engine Host Validation

This record captures runtime evidence for the portable engine host extraction described in the [unified desktop/browser runtime todo](../../todo/platform/unified-desktop-browser-runtime-todo.md). It records selected host behavior; it does not establish completion of all acceptance criteria.

The final gate passed with zero warnings and zero errors for each command: solution build (2m37.67s), WebGPU build (4.2s), Release compile of the 15-project browser graph, clean Browser publish to `Build/_AgentValidation/20260930-105523-unified-browser-runtime/temp-build/qualified-browser-publish`, and Development `DebugMonkeyBall` build (48.87s). These results establish the listed compile/publish outcomes only; runtime qualification remains incomplete. The solution build log is `logs/portable-host-solution-final-build.log` and the clean Browser output is in the same task run.

## Editor host runs

The extracted Editor host started in OpenGL and Vulkan sessions, entered Play, and returned to Edit. Across the observed runs, frame-package rejects and stale reuse were both zero. Four scheduler operation allocated-byte counters were zero. Vulkan reported zero standard validation errors and zero synchronization errors, with two warnings.

Evidence is in the named MCP sessions under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/`: `20260930-144313-unified-host-opengl` and `20260930-150512-unified-host-vulkan`. Play/Edit evidence and renderer state snapshots are in `Build/_AgentValidation/20260930-105523-unified-browser-runtime/reports/portable-host/`, including `opengl-enter-play.json`, `opengl-exit-play.json`, `vulkan-immediate-enter-play.json`, `vulkan-immediate-exit-play.json`, and the Vulkan render-state snapshots.

The Server disposable production-entry probe reached a live playing world: one world was present, update count advanced from 52 to 686 over roughly seven seconds, fixed-update timestamps advanced, presentation count remained zero, and the process exited successfully. The main Server session also ran the ten-ball world with UDP port 5000 configured. Probe evidence: `Build/_AgentValidation/20260930-105523-unified-browser-runtime/reports/portable-host/server-runtime.txt`; session output is in the corresponding `scratch/server-host-session/` directory.

The quiet production VRClient reflection-driver run reached a playing world and produced 30 ready samples over 30.565 seconds: 2,745 updates (89.808 Hz), 6,764 `PresentFrameId` events (221.298 Hz), and advancing fixed-update timestamps. `PresentFrameId` counts completed render dispatches, not physical swaps, so its rate is not physical FPS. The saved `targetFPS` override is enabled at 0, the update override is disabled, and VSync is off; these settings explain the 90 Hz update target with unrestricted render dispatch. `Engine.ShutDown` was requested by the monitor, but the entry did not return, so the owned process (PID 31728) was stopped after shutdown stalled.

The shutdown stall is an existing lifecycle limitation: the last native-window close stops the timer before queued owner-thread `Dispose` removes the last window; `WaitToRender` skips processing main-thread tasks, leaving the outer loop unable to finish. Do not treat this run as a clean VRClient shutdown.

The first reflection-driver attempt (PID 46544) failed to find the Silk GLFW platform because the scratch driver had a different runtime/dependency context and native search path. The retry used the production runtimeconfig/deps and native directories in `PATH`; this changed only the probe environment, not production code. In unit-testing mode, existing `ApplyUserSettingsSessionValues` and `ApplyGameSettingsSessionValues` apply session values so the selected OpenGL renderer survives sandbox settings. Probe evidence is in `Build/_AgentValidation/20260930-105523-unified-browser-runtime/reports/portable-host/vrclient-runtime-steady-2.txt` and the associated scratch session records.

The intended direct launch is:

```powershell
dotnet XREngine.VRClient/bin/Debug/net10.0-windows7.0/XREngine.VRClient.dll --unit-testing
```

## Rendering diagnostics and timing

Three recurring rendering diagnostics also appear in the pre-extraction reference sessions, so their presence is inherited rather than first introduced by the host move: `SurfaceEmissionTexture` sampler diagnostics, unsupported `Lines` primitive topology, and slow shadow-atlas allocation solves. The pre-extraction sessions have logical names containing `unified-browser`, but they are desktop Editor runs, not browser renders. Counts in the exact pre/post sessions were:

| Diagnostic | Reference Editor OpenGL | Reference Editor Vulkan | Host OpenGL | Host Vulkan |
|---|---:|---:|---:|---:|
| `SurfaceEmissionTexture` sampler diagnostic | 41 | 0 | 13 | 0 |
| Unsupported `Lines` topology | 1 | 1 | 3 | 3 |
| Slow shadow allocation solve | 17 | 5 | 28 | 4 |

All four sessions were configured for 90 Hz. The profiler FPS-drop event logs report `CurrentMs` median / p95 / maximum in milliseconds as follows:

| Session | Median | p95 | Maximum |
|---|---:|---:|---:|
| Reference Editor OpenGL, pre-extraction | 48.782 | 94.150 | 2531.145 |
| Reference Editor Vulkan, pre-extraction | 42.927 | 76.788 | 3358.674 |
| Host OpenGL | 37.186 | 50.483 | 276.030 |
| Host Vulkan | 37.256 | 64.817 | 1377.721 |

These are values from logged FPS-drop events, not measurements of steady per-frame render time or sustained FPS. The session logs are under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/`: `20260930-121450-unified-browser-polling` and `20260930-124419-unified-browser-vulkan-polling` are reference desktop Editor runs; `20260930-144313-unified-host-opengl` and `20260930-150512-unified-host-vulkan` are extracted-host Editor runs.

No PhysX-while-simulating warning/error was found in those four sessions. The matching physics-log text was limited to ordinary `DisableSimulation=False` actor flag entries.

## Camera restore validation

Fresh OpenGL (PID 24220) and Vulkan (PID 23492) builds passed the live Before→Play→Edit→After camera check. In both, pose A is `(0, 4, 15)` and pose B is `(12, 6, 8)`; canonical transform, render transform, active viewport, and main-pawn camera agree at each stage, and strict identity lookups succeeded. The ImGui editor UI remained visible in the before/after captures.

Evidence: `Build/_AgentValidation/20260930-105523-unified-browser-runtime/mcp-captures/Screenshot_20260930_160319_974_5af9d55e5f464a119b5a977657f10901.png`, `Screenshot_20260930_160333_523_6c47cb78e96d40b8a03b724eb249ddbf.png` (OpenGL), and `Screenshot_20260930_161345_316_b5a0b94421844128856ceeb0a297852a.png`, `Screenshot_20260930_161412_894_45d37c4e6f8d4020b45098921b6044fa.png` (Vulkan). Calls are recorded in `reports/portable-host/retained-ui-*`.

The earlier repair attempt resolved camera state but detached the retained `TestUINode` UI. The final `RepairWorld` resolves camera, pawn, UI, and owning-pawn edges across actual scenes and retained runtime roots. Astra's source review found no blockers. There was no live `ReferenceEquals` UI probe and no pre-move binary identity run; the evidence is strict identity lookup plus visible before/after UI captures. The camera restore issue is verified fixed in these fresh Editor runs; broader runtime qualification and the final gate remain pending.

## Reproduction path

Use the named isolated editor sessions above for OpenGL and Vulkan, capture the renderer state before and after entering/exiting Play, and compare the corresponding session logs and snapshots. For camera restore validation, compare canonical transform, render transform, active viewport, and main-pawn camera at pose A `(0, 4, 15)` and pose B `(12, 6, 8)`; also verify retained UI in the before/after captures. Do not infer steady frame timing from the FPS-drop event summaries.
