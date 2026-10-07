# Window And Render Thread Validation

## Scope

This document owns runtime checks for window creation, renderer startup and fallback, render-settings ownership, interactive resize, and render-thread ownership.

Architecture: [Window Creation And Renderer Initialization](../../../architecture/rendering/window-creation-and-renderer-init.md), [Rendering Code Map](../../../architecture/rendering/code-map.md).

Code todos: [Vulkan Core Frame Loop Master TODO](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md), [Vulkan Stall Separate Findings TODO](../../todo/rendering/vulkan-stall-separate-findings-todo.md).

## Setup

Use the smallest run mode that covers the feature.

Tasks from `.vscode/tasks.json`:
| Task | Purpose |
|---|---|
| `Build-Editor` | Build the editor before a runtime validation run. |
| `Start-Editor-NoDebug` | Start the editor with the default world. |
| `Start-Editor-RendererDevelopment-NoDebug` | Start the editor with renderer-development mode. |
| `Start-Editor-UnitTesting-OpenXR-Monado-NoDebug` | Start a Monado OpenXR validation lane. |
| `Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug` | Start a SteamVR OpenXR validation lane. |

Launch profiles from `.vscode/launch.json`:
| Profile | Purpose |
|---|---|
| `Editor (Default World)` | Run the editor with the default world. |
| `Editor (Renderer Development)` | Run the editor with `--renderer-development`. |
| `Editor (Unit Testing World)` | Run the Unit Testing World with `XRE_WORLD_MODE=UnitTesting`. |
| `Editor (Unit Testing World, Validation Layers)` | Run the Unit Testing World with `XRE_VULKAN_VALIDATION=1` and `XRE_GL_DEBUG=1`. |

Settings and environment variables:
| Setting or variable | Purpose |
|---|---|
| `XRE_WINDOW_PUMP_HOST=sdl-prototype` | Select the split window-pump prototype. |
| `XRE_INTERACTIVE_RESIZE_STRATEGY` | Select the interactive resize strategy. |
| `Rendering.RenderBackend` | Select OpenGL or Vulkan in settings. |
| `RenderBackendFallbackPolicy.RequireRequested` | Require the requested backend to start. |

For split-pump checks, run the collapsed mode and `XRE_WINDOW_PUMP_HOST=sdl-prototype` unless the check names one mode.

## Checks

### Render Settings API

Architecture: [Settings Ownership](../../../architecture/rendering/window-creation-and-renderer-init.md#render-settings-ownership).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Settings tests | Run `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter Settings`. | Settings tests pass. Report unrelated failures separately. | Open | none |
| OpenGL startup | Start the editor with OpenGL. | Normal startup. | Open | none |
| Vulkan dynamic startup | Start the editor with Vulkan dynamic rendering. | Normal startup. | Open | none |
| Vulkan legacy render-pass startup | Start the editor with Vulkan legacy render-pass target mode. | Normal startup. | Open | none |
| Required backend failure | Make Vulkan initialization fail, or run where Vulkan is unavailable, with `RenderBackendFallbackPolicy.RequireRequested`. | Startup fails visibly with the requested backend, policy, and exception summary. | Open | none |
| Permitted backend fallback | Repeat with a fallback policy that permits fallback. | The log records the fallback reason. | Open | none |
| Settings UI | Open the profiler, diagnostics, and settings panels. | Grouped settings (`OpenGL`, `Vulkan`, and editor diagnostics groups) show clearly with their source. | Open | none |

### Window Ownership And Render Thread

Architecture: [Window Ownership And Render Thread](../../../architecture/rendering/window-creation-and-renderer-init.md#window-ownership-and-render-thread).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Default and Unit Testing worlds | Start the editor in the default world and with `--unit-testing`. | Rendering and input work normally. | Open | none |
| OpenGL window lifetime | Start the editor with OpenGL. | Render, input, resize, minimize, restore, and close work. | Open | none |
| Vulkan window lifetime | Start the editor with Vulkan. | Render, input, resize, minimize, restore, and close work. | Open | none |
| Overlay modes | In split mode, exercise ImGui overlay submission, scene-panel mode, full-window mode, profiler overlays, and render diagnostics. | Each mode renders on the correct owner. | Open | none |
| Native drag responsiveness | Drag a native resize border continuously. | The native rectangle stays responsive. | Open | none |
| Vulkan live resize | Drag a Vulkan window border. | Presents refresh lagging output content, or skips ticks with clear diagnostics. | Open | none |
| Release after drag | Release the mouse after a drag. | The exact-size full internal generation catches up. | Open | none |
| Preference changes | Change VSync and HDR preferences. | The change applies without errors. | Open | none |
| Screenshot or readback | Run a screenshot or readback flow. | The image is correct. | Open | none |
| Multiple startup windows | Open multiple startup windows. | Each window renders, resizes, minimizes, restores, and closes cleanly. | Open | none |
| OpenXR owner startup | Start OpenXR. | Startup uses the correct render owner and desktop mirror path. | Open | none |
| OpenVR swap paths | Start OpenVR. | Stereo and mirror swap paths keep their behavior. | Open | none |
| VR-disabled desktop baseline | Start desktop rendering with VR disabled. | Desktop rendering stays the baseline path. | Open | none |
| Vulkan resize regression tests | Run `VulkanP1ValidationTests`. | The swapchain-resize regression tests pass. | Open | none |

### Interactive Resize

Architecture: [Interactive Resize](../../../architecture/rendering/window-creation-and-renderer-init.md#interactive-resize).

For each strategy, record whether frames update during the drag, the final size after mouse-up, and any validation or device-loss messages. Select a strategy with `XRE_INTERACTIVE_RESIZE_STRATEGY`.
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| OpenGL strategy sweep | Run Windows + OpenGL with `Default`, `GlfwRefreshCallback`, `GlfwResizeCallbackRender`, `SdlBackend`, and `Win32ModalLoopTimer`. | Each strategy starts and reports its behavior. | Open | none |
| Vulkan strategy sweep | Run Windows + Vulkan with `Default`, `GlfwRefreshCallback`, `GlfwResizeCallbackRender`, `SdlBackend`, and `Win32ModalLoopTimer`. | Each strategy starts and reports its behavior. | Open | none |
| Default strategy | Select `Default`. | Resize behavior has no callbacks or hooks, and OpenGL and Vulkan initialize and render normally. | Open | none |
| GLFW refresh callback | Select `GlfwRefreshCallback`. | OpenGL renders a refreshed frame when the callback fires. Vulkan does not recreate the swapchain from the native callback. Cleanup survives window close and Vulkan-to-OpenGL fallback. | Open | none |
| GLFW resize callback render | Select `GlfwResizeCallbackRender`. | When callbacks arrive during a drag, the window updates before mouse-up. When they do not, diagnostics show it and rendering stays stable. | Open | none |
| SDL backend | Select `SdlBackend`. | OpenGL context creation, input, clipboard, cursor capture, DPI, and transparent framebuffer work. Vulkan surface creation works or fails with a clear message. No GLFW-specific path runs. Editor scene panel and full-window rendering survive resize. | Open | none |
| Win32 modal loop timer | Select `Win32ModalLoopTimer`. | No hook runs on non-Windows platforms. The hook is restored on normal close and on exception cleanup. OpenGL makes its context current before rendering. Vulkan queues only frame-boundary-safe resize work. | Open | none |
| Engine borderless resize | Select `EngineBorderlessResize`. | Native title-bar mode stays available and unaffected. | Open | none |
| Editor presentation modes | Resize with editor full-window presentation and editor scene-panel presentation. | Both modes survive resize. | Open | none |
| Runtime startup window | Resize a game or runtime startup window. | The window updates and closes correctly. | Open | none |
| Vulkan-to-OpenGL fallback | Force failed Vulkan window creation, then allow fallback. | OpenGL fallback starts and reports the reason. | Open | none |
| Multiple windows | Resize with multiple windows open. | Each window keeps correct owner state. | Open | none |
| Close during drag | Close the window while dragging or just after a drag. | No crash and no stale window-procedure hook. | Open | none |
| High-DPI resize and monitor crossing | Resize on a high-DPI monitor and move the window across monitors. | Scale, final size, and rendering remain correct. | Open | none |

### Modular Runtime Resize Soak

Architecture: [Window Creation And Renderer Initialization](../../../architecture/rendering/window-creation-and-renderer-init.md).

- [ ] Run the extended performance and resize soak on the modular runtime. Procedure: use the modular runtime with the relevant backend and resize scenario. Expected: stable rendering, no unbounded waits, and no leaked window or render-thread resources. Last evidence: none.

## Hardware Matrix
| System | Role | Status |
|---|---|---|
| Windows 10 or 11 desktop | Primary window and resize validation | Required |
| High-DPI or multi-monitor setup | DPI and monitor-crossing checks | Required for that feature |
| OpenXR runtime | OpenXR owner-startup check | Required for XR row |
| OpenVR runtime | OpenVR stereo and mirror swap check | Required for OpenVR row |

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
