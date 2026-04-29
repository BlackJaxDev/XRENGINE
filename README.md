# XRENGINE

A Windows-first C# XR engine and editor, built on .NET 10. Ships a desktop editor, a dedicated server, and a separate legacy OpenVR companion app for VR/desktop switching without restarting the main game.

This is early-stage software — APIs change often and there's no backward-compatibility promise yet. The goal is to get the architecture right before v1.

If you want the fastest way in: build the editor, launch the Unit Testing World, and use that as your main validation loop.

## Main subsystems

- **Rendering:** OpenGL 4.6 via Silk.NET is the primary path. WIP Vulkan and DX12 backends exist but only Vulkan development is ongoing.
- **Physics:** PhysX (via MagicPhysX) is the current default with character controller support. Jolt is planned as the future default but isn't fully integrated currently. Jitter 2 is on the roadmap for lightweight use cases, such as VTubing.
- **Audio:** OpenAL (Soft) via Silk.NET, with NAudio and LAME for codec support. Video audio goes through FFmpeg.
- **Scene graph:** Traditional scene node tree with an attached component model. Nodes form a parent-child transform hierarchy, and components derive from `XRBase` for change tracking.
- **Animation:** Skeletal animation with blend support, humanoid IK (VRIK for VR), and Assimp-based clip import.
- **Asset import:** FBX uses the native `XREngine.Fbx` importer and glTF/GLB uses the native `XREngine.Gltf` importer by default. Other model formats still load through Assimp (via AssimpNetter).
- **Networking:** Client/server realtime transport with entity replication and pose sync for multiplayer testing.
- **XR:** OpenXR and SteamVR/OpenVR paths. OpenVR works, and OpenXR is still in the debugging phase.
- **Editor UI:** ImGui is the day-to-day interface. A native UI pipeline is under development as the intended production UI.
- **Input:** Silk.NET.Input for keyboard, mouse, and gamepad. GLFW for windowing.
- **Video/media:** FFmpeg (via FFmpeg.AutoGen) for video textures, streaming, and audio extraction.

## Prerequisites

- .NET 10 SDK
- Windows 10/11 with an OpenGL 4.6 capable GPU
- Git (submodules are used for third-party deps)
- Optional: SteamVR or an OpenXR headset for XR testing

## Quick start

### Clone

```powershell
git clone --recurse-submodules https://github.com/BlackJaxDev/XRENGINE.git
cd XRENGINE
```

If you already cloned without `--recurse-submodules`:

```powershell
git submodule sync --recursive
git submodule update --init --recursive
```

Or use the convenience script: `./Tools/Initialize-Submodules.bat`

### Build

```powershell
dotnet restore
dotnet build XRENGINE.slnx
```

If you want the broadest one-command repo setup instead, run `ExecTool --bootstrap`.

For bootstrap scope, first-time setup, and what still needs manual installation afterward, see `docs/features/bootstrap.md`.

### Run the editor

```powershell
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj
```

Or: `./Tools/Start-Editor.bat`

To boot into the Unit Testing World, use the `Editor (Unit Testing World)` launch profile in VS Code, or run the editor with `--unit-testing`.

If you are contributing code, the best follow-up docs are `docs/features/unit-testing-world.md` and `docs/architecture/getting-started-in-codebase.md`.

## Project layout

| Project | What it is |
|---------|-----------|
| `XREngine/` | Core runtime — scene graph, rendering, XR subsystems |
| `XREngine.Editor/` | Desktop editor |
| `XREngine.Server/` | Dedicated server |
| `XREngine.VRClient/` | Legacy OpenVR companion app. Keeps the SteamVR/OpenVR connection isolated from the main engine process, forwards player input to the main app through a pipe, and displays per-eye frames streamed back from the engine. |
| `XREngine.Animation/`, `XREngine.Audio/`, `XREngine.Input/`, `XREngine.Modeling/`, `XREngine.Extensions/` | Supporting modules |
| `XREngine.Data/` | Shared data types and serialization primitives |
| `XREngine.Fbx/`, `XREngine.Gltf/` | Native FBX and glTF importers |
| `XREngine.Runtime.*/` | Runtime integration layers (Core, Bootstrap, Rendering, AnimationIntegration, AudioIntegration, InputIntegration, ModelingBridge) |
| `XREngine.Profiler/`, `XREngine.Profiler.UI/` | Standalone profiler app and UI |
| `XREngine.Benchmarks/` | Performance benchmarks |
| `XREngine.UnitTests/` | Automated tests |
| `Submodules/` | Git submodule: MagicPhysX |
| `Build/Submodules/` | Git submodules: OpenVR.NET, CoACD, OscCore-NET9, rive-sharp |

## Running and debugging

### VS Code (recommended)

The repo includes ready-to-go `.vscode/` configs. Use **Run and Debug** (Ctrl+Shift+D) to pick a launch profile:

- **Editor (Default World)** / **Editor (Unit Testing World)**
- **Debug Client**, **Debug Server**, **Debug VRClient**

There are also no-debug tasks under **Terminal → Run Task** for the common editor, server, client, and networking scenarios.

To export the code-defined default render pipeline as an `.xrs` script, run the `Export-DefaultRenderPipelineScript` task. The editor executable also supports `--export-default-render-pipeline-script --render-pipeline-script-output <path>` as a one-shot CLI command.

`Debug VRClient` is mainly for the legacy OpenVR path. OpenVR cannot be cleanly shut down and restarted inside the same running app, so `XREngine.VRClient` exists to hold that SteamVR connection in a separate process. The main engine app can then stay alive and switch between desktop and VR gameplay without forcing a full restart. OpenXR does not have that limitation, so this extra process is specifically for legacy OpenVR support.

### Visual Studio

Open `XRENGINE.slnx`, set your startup project (`XREngine.Editor`, `XREngine.Server`, etc.), and hit F5. Environment variables like `XRE_NET_MODE` and `XRE_WORLD_MODE` can be set in Project → Properties → Debug to switch between server/client/local modes.

If you are working on the legacy OpenVR path, use `XREngine.VRClient` as the companion process rather than treating it like a second copy of the main game. It collects VR-side player input and sends it to the main engine app over a pipe, while the engine streams back the left/right eye renders for presentation through SteamVR/OpenVR.

### Networking quick test

```powershell
./Tools/Start-NetworkTest.bat              # dedicated server + two clients
./Tools/Start-NetworkTest.bat mismatch     # one good client + one rejected world-hash mismatch
./Tools/Start-NetworkTest.bat pose         # server + pose source + pose receiver
```

### ExecTool

The repo root has `ExecTool.bat`, an interactive menu for all the scripts under `Tools/` — build helpers, dependency installers, report generators, and more. Run it with no arguments for the menu, or `ExecTool --bootstrap` for full first-time setup.

## Unit Testing World

The editor's test world is configured through `Assets/UnitTestingWorldSettings.jsonc`. Launch with `--unit-testing` (or set `XRE_WORLD_MODE=UnitTesting`) to boot into it. The file has a JSON schema wired up in VS Code for autocompletion and hover docs.

For startup model imports that reference textures outside the authored folder layout, set `TextureLoadDirSearchPaths` in that file to provide recursive texture search roots.

For the full workflow, including pose/network test setups and how the JSONC file is used, see `docs/features/unit-testing-world.md`.

To regenerate the schema after changing the settings type:

```powershell
pwsh Tools/Generate-UnitTestingWorldSettings.ps1
```

## Native dependencies

Most core native pieces are already wired into the build, but some optional tools and SDKs still need local setup.

For the practical setup and rebuild guide, see `docs/features/native-dependencies.md`.

## Logs

When file logging is enabled, per-session logs go to `Build/Logs/<configuration>_<tfm>/<platform>/<session>/`, including profiler traces and benchmark output.

## Documentation

See `docs/README.md` for the full index — architecture overviews, API guides, rendering notes, and design docs. Key starting points:

- `docs/architecture/README.md` — runtime flow, threading, project layout
- `docs/architecture/getting-started-in-codebase.md` — contributor-oriented map of where to start
- `docs/api/` — system-level API guides (scene, components, animation, physics, rendering, VR)
- `docs/features/bootstrap.md` — first-time setup and bootstrap scope
- `docs/features/native-dependencies.md` — native and external setup reference
- `docs/work/README.md` — active TODOs and design workstreams

## Contributing

No formal `CONTRIBUTING.md` yet. Open an issue or discussion first, then submit a PR. Active areas: rendering backends, XR tooling, editor UX, and automated testing.

If you are getting oriented in the codebase, start with `docs/architecture/getting-started-in-codebase.md`.

## License

Licensed under **AGPLv3**. See `LICENSE` for the full terms.

The short version: you can use, modify, and redistribute the code, but any distribution (including network use) must stay under AGPLv3 and provide source. Third-party dependencies have their own licenses — see `docs/DEPENDENCIES.md` for the full inventory.

- Issues: https://github.com/BlackJaxDev/XRENGINE/issues
- Discussions: https://github.com/BlackJaxDev/XRENGINE/discussions

## Star History

<a href="https://www.star-history.com/#BlackJaxDev/XRENGINE&type=date&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=BlackJaxDev/XRENGINE&type=date&theme=dark&legend=top-left" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=BlackJaxDev/XRENGINE&type=date&legend=top-left" />
   <img alt="Star History Chart" src="https://api.star-history.com/svg?repos=BlackJaxDev/XRENGINE&type=date&legend=top-left" />
 </picture>
</a>
