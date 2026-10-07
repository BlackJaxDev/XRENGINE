# Desktop And Browser Reference Harness

Status: browser reference, editor-published player, minimal OpenGL/Vulkan visual and native callback smokes pass locally. Broader test/native integration acceptance remains unqualified.

Updated: 2026-09-30. Tracks the active [unified runtime checklist](../../todo/platform/unified-desktop-browser-runtime-todo.md) and [native integration acceptance](../../testing/platform/platform-validation.md).

## Findings

- All six build-gate commands pass with zero warnings after the player-shell, renderer diagnostics boundary and Vulkan cleanup changes. The SDK is pinned to 10.0.401 and workload set 10.0.401.1, supplying browser runtime pack 10.0.12 and Emscripten 3.1.56.
- The browser reference harness reaches WebGPU ready and renders the textured, masked, transparent, shadowed fixture in both split and single-camera views. Both exported canvas PNGs were viewed. Gesture audio activation reports ready. This establishes a local smoke, not performance or physical-device qualification.
- The editor's actual `--build-project <project.xrproj> --build-platform BrowserWebGPU` command publishes a real engine-serialized minimal world: an active camera and indexed unlit quad with UV set zero. The resulting page runs the cooked scene through the existing reference exporter/host. It does not yet run the engine's full world lifecycle or game assemblies.
- The published entrypoint loads `player.js`, requires the cooked startup-world descriptor, hides the fixture overlay, and has no demo or diagnostic controls. Stale compressed HTML/descriptors and the harness entry script are removed from staged output before activation. Browser startup has no captured console errors; audio activation removes the remaining audio control.
- The original Vulkan/Sponza/Advanced session starts and serves MCP, but viewport readback times out. Minimal OpenGL and Vulkan sessions using the default pipeline now render and return captures from two confirmed camera transforms. All four PNGs were viewed. These establish minimal desktop smokes, not Sponza/Advanced or rendering-parity qualification.

## Desktop startup and event pumping

The minimal OpenGL session first failed while looking up the optional `glMultiDrawMeshTasksIndirectCountEXT` symbol. The desktop GL context now returns address zero for unavailable symbols; OpenGL queries optional mesh-task functions only after admitting the extension and indirect-count prerequisites. The cached capability result also avoids repeatedly parsing the GL version during submission. Unsupported direct mesh-task dispatch reports a named error.

The next live run exposed a separate event-pump stall. Presentation windows selected Silk's event-driven mode, whose GLFW implementation waits for input inside `DoEvents`. The engine calls that pump on its render owner, so an idle window also stopped rendering and queued readback. A profiler sample recorded 154,290.828 milliseconds in `XRWindow.WindowPumpHost.DoEvents`; an earlier capture request timed out before its render job ran.

Engine-owned presentation windows now poll events because the engine already owns frame pacing. Fresh OpenGL and Vulkan sessions continue rendering while input is idle and complete viewport captures. The minimal world uses a player camera rather than a flying-camera pawn: `set_editor_camera_view` correctly reports no pawn, so the successful smokes move `Editor View` with `set_node_world_transform`. The confirmed views are position `(0, 4, 15)`, pitch `-12`, yaw `0`, and position `(12, 6, 8)`, pitch `-16`, yaw `56`.

Two RenderDoc captures attempted before the polling fix timed out without producing an RDC. The RenderDoc doctor passed; no replay session was opened. The subsequent viewed MCP captures establish continued frame submission without relying on those failed attempts.

Profiler requests and speed-profile capture now obtain backend diagnostics through the shared capability rather than casting to a concrete Vulkan renderer. Speed-profile values retain their frame-authority lookup and allocation-free value snapshots. Cold MCP results contain primitive JSON trees, preserving the response schema while avoiding process-lifetime serializer caches retaining types from a collectible renderer generation. The existing concrete-renderer boundary fixture passes all three cases.

## Shader package failure and repair

The initial browser startup rejected the hash-addressed shader descriptor. Git stored LF bytes, while Windows checkout conversion supplied CRLF bytes; the descriptor and WGSL therefore no longer matched their content-addressed names. The repository now requires LF for `XREngine.Runtime.Rendering.WebGPU/Assets/shaders/**`. The three checked-in package files were normalized without changing their logical contents.

A reload on the original origin still consumed an immutable cached payload from before the fix. A fresh localhost origin loaded the corrected package and rendered successfully. Republishing from the corrected checkout also succeeds. Do not change expected hashes to accommodate newline conversion; preserve the cooked bytes.

## Unit-test execution

The full baseline run executed 4,022 of 4,071 discovered cases: 3,737 passed, 285 failed, and 49 were not executed before the test host aborted. The abort was an unhandled `NullReferenceException` in `VkObject<T>.OnRetiring` on the finalizer thread.

Constructor-bypassing fixtures create Vulkan wrappers with no data object or binding. Existing swapchain tests that create such wrappers had completed before the abort; the shader-compilation test active at the time is not sufficient evidence of causality. Ordinary construction failure before assigning data can expose the same cleanup state. Retirement now removes the wrapper only if data exists, retaining unlink ordering and idempotence. A disposable driver invoked the existing swapchain test, then forced collection and waited for finalizers: exit 0. No native object was generated by that reproduction.

The targeted swapchain, shader-compilation and dependency-boundary run passes 57 of 58 cases. The remaining assertion rejects Bootstrap's existing ModelingIntegration generator input, which is a separate pending owner decision. Server/VRClient model-pipeline references and Bootstrap's model-pipeline generator input were approved and are represented in the boundary checks.

Failure triage identifies 148 source-text contracts, 16 old or ambiguous path lookups, four dependency-graph assertions, one CUDA-required failure, and 120 other behavioral/invariant cases. Representative behavioral clusters include humanoid mapping, imported animation, render-resource lifetimes, OpenXR timing, prefab serialization, material bindings, bindless resources and shadow allocation. Do not relabel these as mechanical move fallout or add CPU fallback for CUDA. Larger unrelated failures remain integration work.

The repeat full run finishes normally in 17 minutes 20 seconds: 4,804 passed, 669 failed and seven skipped in the runner summary. Of the 285 baseline-failing names, 273 still fail, nine pass and three are absent. No previously passing baseline name now fails. Another 396 failing names are represented after the abort repair; 1,451 more cases execute. This completes execution and triage, not unit-suite acceptance.

The complete-run classifier finds 431 direct source-text failures, 31 missing named source paths, ten missing source markers, 50 further source/contract locator or assertion failures, 145 behavior/runtime/fixture failures and two dependency/owner checks. Important independent clusters include 24 humanoid cases, 16 command-chain/packet cases, five material-row layout mismatches (`36` layout words versus `32` uploaded words), and six device-context fixtures passing a null Vulkan API. The CUDA-required failure remains a named environment requirement. The retired hot-command shader contract must be reconciled with its deletion rather than restored by redirecting it to an unrelated kernel.

The TRX totals differ from the runner only in nonexecuted accounting: the TRX reports 5,530 total and 57 not executed, while the runner reports 5,480 total and seven skipped. Both agree on passed/failed counts. Do not use their total or skip counters as an acceptance gate until that discrepancy is resolved.

The null-safe cleanup does not settle the broader finalizer ownership policy: a generated shader's unlink path can still reach native destruction from the finalizer thread without proving device lifetime. Review that policy separately before claiming lifetime qualification.

## Native callbacks

A disposable application script subscribed to the existing `DearImGuiComponent.Draw` in a fresh isolated Vulkan editor. Its native ImGui context contained the expected process-stable platform, renderer and clipboard callback addresses. Normal ImGui platform-window updates created an undocked viewport with nonzero platform/native/renderer handles; the actual HWND dimensions grew when resized. Removing the viewport hid the HWND and completed membership in the desktop backend's abandoned-resource collection after GPU retirement. The surviving hidden HWND is the documented [quarantine policy](../../../architecture/rendering/default-render-pipeline-notes.md), not a failed native destroy check. Diagnostic native disposal was not enabled.

The first actual clipboard read exposed a null-pointer failure. ImGui.NET 1.91.6.1's [clipboard binding](https://github.com/ImGuiNET/ImGui.NET/blob/8e26803be78b344fd68834817905405b3cdffb94/src/ImGui.NET/Generated/ImGui.gen.cs#L12557-L12560) passes its result to an [unchecked UTF-8 pointer scan](https://github.com/ImGuiNET/ImGui.NET/blob/8e26803be78b344fd68834817905405b3cdffb94/src/ImGui.NET/Util.cs#L11-L19); local package metadata and IL match that commit. The desktop callback now normalizes missing text to empty and returns a process-lifetime NUL byte after provider failure, initialized before callback addresses are published. A fresh live session confirms actual native reads return safely. Native get/set round-trips also pass with temporary null, sentinel and throwing clipboard services, restored in `finally`. The OS clipboard is read only: its contents are neither recorded nor replaced, and native OS clipboard write ownership is not qualified by this smoke.

A verbose message submitted through the native Vulkan debug-utils extension reaches both the installed callback's cumulative diagnostics and `log_vulkan.log`. This is an injected callback smoke, not a manufactured driver validation failure. Standard/synchronization validation and the debug messenger are active; cumulative validation reports zero errors and two independent warnings (loader device enumeration and an unused vertex attribute).

The existing `get_render_capabilities` query initializes the installed Streamline runtime's capability check. Actual `[Streamline:Info]` native messages reach the registered logging callback; no DLSS/FG rendering path is enabled by the smoke.

RenderBench's normal Engine host composition triggers the desktop platform registration. A separate disposable process first confirms installed entry points, deliberately clears them, then invokes the production Vulkan presentationless startup path with validation enabled. It exits with the named missing native-callback-entrypoint error from `VulkanDeviceContext.PrepareDebugMessengerCreateInfo`. This confirms the independent-host guard without changing the live editor. The ordinary RenderBench smoke reaches capture but fails its zero-allocation validity gate at 20,416 bytes; the full-suite presentationless fixture separately stops at a missing execution-scheduler service. Those remain broader acceptance failures.

MCP's `MainThread` dispatch means application-thread dispatch. The temporary script queues its native operations onto the render owner and returns a task; those wrappers are invoked through MCP Direct dispatch. Its callbacks are detached, queued requests completed and assembly unloaded before stopping each isolated editor session.

## Reproduction and evidence

Disposable evidence is under `Build/_AgentValidation/20260930-105523-unified-browser-runtime/`; required findings are recorded above so cleanup does not remove the conclusions.

| Evidence | Relative location within the run |
| --- | --- |
| Six current build logs | `logs/current-gate-*.log` |
| Shader cooker build | `logs/shader-cooker-build.log` |
| Full baseline results | `reports/unified-runtime-baseline.trx`, `logs/full-tests.log` |
| Complete repeat and triage | `reports/unified-runtime-after-harness-fixes.trx`, `reports/full-suite-after-harness-fixes-triage.md`, `logs/full-tests-after-harness-fixes.log` |
| Targeted results | `reports/targeted-runtime-boundaries.trx` |
| Forced-finalizer reproduction | `reports/vulkan-finalizer-driver.md`, `logs/vulkan-finalizer-driver.log` |
| Browser split/single view PNGs | `mcp-captures/browser-reference-a.png`, `mcp-captures/browser-reference-b.png` |
| Browser renderer counters | `reports/harness-counters.json` |
| Editor-authored project publish | `reports/browser-editor-publish-fixture.md`, `logs/browser-fixture-publish.log` |
| Core index normalization | `reports/core-index-normalization.json` |
| Vulkan editor camera/readback failures | `reports/editor-camera-a.json`, `reports/editor-camera-b.json`, `reports/editor-capture-a.json`, `reports/editor-capture-b.json` |
| Confirmed OpenGL and Vulkan camera/captures | `reports/opengl-poll-node-*.json`, `reports/vulkan-poll-node-*.json`, corresponding `mcp-captures/Screenshot_*.png` |
| Renderer diagnostics boundary | `logs/profile-capture-json-boundary-build.log`, `logs/profile-capture-json-boundary-test.log`, `reports/vulkan-poll-profiler.json`, `reports/vulkan-poll-owners.json` |
| Native callback smoke and validation | `reports/vulkan-clipboard-probe-status.json`, `reports/vulkan-clipboard-post-profiler.json`, `reports/vulkan-callback-post-profiler.json` |
| Independent host guard | `reports/native-callback-guard/README.md`, `logs/native-callback-guard-driver.log` |
| Clipboard desktop rebuilds | `logs/clipboard-*-build.log`, `logs/vulkan-clipboard-start.log` |

The isolated Vulkan editor session was `unified-browser-baseline`, under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260930-105753-unified-browser-baseline/`. Its logs are nested under `logs/XREngine.Editor_debug/windows_x64/`. The session was stopped through the session manager.

Browser automation could not capture a whole-tab screenshot. A disposable same-origin wrapper exposes a normal capture button; its application code exports the WebGPU canvas at an animation-frame boundary. The saved PNGs contain actual canvas pixels and were visually inspected, but omit the surrounding HTML controls.

The recorded counter snapshot has 117 submitted frames, 1,638 scene draws, 936 shadow draws, 702 UI draws and zero rejected packets. The last recorded managed frame allocated zero bytes; warmup allocation was 13,016 bytes. These counters are instrumentation, not a sustained allocation/performance measurement. Automatic strategy selected CPU-direct rendering; no forced GPU request was substituted.

## Open

- Complete the remaining stale path/source-contract repairs without weakening assertions; resolve the separate ModelingIntegration factory-input decision.
- Broader desktop rendering, physical VR, native OS clipboard write ownership and application publish-layout qualification remain outside the minimal callback/visual smokes.
- Resolve the independently classified source, behavior and fixture failures before claiming broader native integration acceptance.
- Investigate the publish step's settings serialization: saving the scratch project expands its portable startup-world reference into an inline world mapping. The reference was restored after the successful publish so the fixture remains reproducible.
- Linux checkout/CI execution and physical-device/browser performance qualification remain untested.
