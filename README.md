# XRENGINE

XRENGINE is a Windows-first C# XR engine + editor.

It includes a desktop editor, a dedicated server, and a standalone VR client. This is a dev-focused codebase (expect refactors and occasional breaking changes).

## Status at a Glance
- **Maturity**: early-stage; APIs and workflows change.
- **Platform**: Windows 10/11, .NET 10.
- **Projects**: `XREngine.Editor`, `XREngine.Server`, `XREngine.VRClient`.
- **Rendering**: OpenGL 4.6 is the main path; Vulkan/DX12 are WIP.
- **XR**: OpenXR and SteamVR/OpenVR paths exist - only OpenVR is tested.
- **Physics**: 
PhysX is the current default and supports a character controller;
Jolt is the planned default but only semi-implemented & untested;
Jitter 2 work is planned for lightweight usage such as for VTubing.

## Solution Layout
- `XREngine` – core runtime, scene graph, rendering backends, and XR subsystems.
- `XREngine.Editor` – desktop editor that boots into the unit testing world used to validate features.
- `XREngine.Animation`, `XREngine.Audio`, `XREngine.Extensions` – supporting modules for animation, audio, and common utilities.
- `XREngine.Data`, `XREngine.Input`, `XREngine.Modeling` – data structures, input handling, and 3D modeling utilities.
- `XREngine.Server`, `XREngine.VRClient` – networking server and standalone VR client.
- `XREngine.UnitTests` – automated tests for engine subsystems.
- `Build/Submodules` – third-party dependencies (OpenVR.NET, MagicPhysX, CoACD, Flyleaf, OscCore, rive-sharp).

## Prerequisites
- .NET 10 SDK
- Windows 10/11 with a GPU capable of OpenGL 4.6
- Optional: OpenXR-compatible headset or SteamVR setup for XR testing
- Optional (required for YouTube URL playback): `yt-dlp` available on PATH or copied as `yt-dlp.exe` beside the app executable

## Quick Start
### 1) Clone (with submodules)

This repo relies on Git submodules under `Build/Submodules`.

Recommended (one command):

```powershell
git clone --recurse-submodules https://github.com/BlackJaxDev/XRENGINE.git
cd XRENGINE
```

If you already cloned without submodules:

```powershell
cd XRENGINE
git submodule sync --recursive
git submodule update --init --recursive
```

Windows convenience script (does the same and prints status):

```powershell
./init_submodules.bat
```

### 2) Build

Solution build:

```powershell
dotnet restore
dotnet build XRENGINE.sln
```

Or build the Editor only:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
```

### 3) Run the Editor (CLI)

```powershell
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj
```

Shortcut script:

```powershell
./run_editor.bat
```

Running the editor launches the Unit Testing World, a collection of scenes that exercise rendering, animation, physics, audio, and XR workflows. Use this environment to verify changes and explore current functionality.

## Unit Testing World Settings (JSON)

The Unit Testing World is configured by a JSON file and loaded on startup.

- **How it’s selected**: launch the Editor with `--unit-testing` (or set `XRE_WORLD_MODE=UnitTesting`).
- **Where the JSON is loaded from**: the Editor looks for `Assets/UnitTestingWorldSettings.json` relative to the process **working directory** (`Environment.CurrentDirectory`). In the provided VS Code launch configs, the working directory is set to the workspace root, so the file used is `Assets/UnitTestingWorldSettings.json`.
- **What happens on load**: the JSON is deserialized into `UnitTestingWorld.Toggles` (type `UnitTestingWorld.Settings`). If the file doesn’t exist yet, a default one is written out.
- **How it affects the world**: `UnitTestingWorld.CreateSelectedWorld(...)` switches on `Toggles.WorldKind` to choose which unit-test world factory to run, and the other toggle values control what gets added (models to import, lighting, physics, UI overlays, etc.).
- **It also influences engine startup**: the Editor loads these toggles early so render/update settings (render API, tick rates, pipeline choices, etc.) can be applied consistently.

## Launch Options (VS Code)

This repo includes a ready-to-go VS Code setup in `.vscode/`.

### Debug (F5)

Use **Run and Debug** (Ctrl+Shift+D), then pick one of these launch configurations:

- **Editor (Default World)**
- **Editor (Unit Testing World)** (passes `--unit-testing` and sets `XRE_WORLD_MODE=UnitTesting`)
- **Debug Client (Server & other client run separately)** (sets `XRE_NET_MODE=Client`, `XRE_UDP_CLIENT_RECEIVE_PORT=5001`)
- **Debug Server (Clients runs separately)** (dedicated server exe; also launches 2 editor clients separately before attach)
- **Debug Server (Server only)**
- **Debug P2P Client (Peer client separate, no server)** (sets `XRE_NET_MODE=P2PClient`)
- **Debug VRClient (Editor runs separately)** (sets `XRE_VRCLIENT_GAMENAME=XREngine.Editor`)

### No-Debug launchers (Tasks)

Use **Terminal → Run Task…** and run any of these tasks (they build first where needed):

- `start-editor-no-debug`
- `start-server-no-debug` (Editor-as-server, `XRE_NET_MODE=Server`)
- `start-client-no-debug` (Editor-as-client, port 5001)
- `start-client2-no-debug` (Editor-as-client, port 5002)
- `start-2-clients-no-debug`
- `start-dedicated-server-no-debug` (runs `XREngine.Server.exe`)
- `start-p2p-peer-no-debug` (P2P peer 1, port 5001)
- `start-p2p-peer2-no-debug` (P2P peer 2, port 5002)

There are also “prep” tasks used by the debug configurations:

- `prep-debug-server-with-2-clients`, `prep-debug-server-only`
- `prep-debug-client-with-server-and-client`
- `prep-debug-p2p-client-with-peer`
- `prep-debug-vrclient-with-editor`

## Launch Options (Visual Studio)

Open `XRENGINE.sln`.

### Run / Debug the Editor

- Set startup project to `XREngine.Editor`.
- Use **Debug → Start Debugging** (F5) or **Start Without Debugging** (Ctrl+F5).

To emulate the same modes as the VS Code profiles, set environment variables in:

- Project → Properties → Debug → **Environment variables**

Common environment variables:

- `XRE_NET_MODE=Server|Client|P2PClient`
- `XRE_UDP_CLIENT_RECEIVE_PORT=5001` (or `5002` for a second client)
- `XRE_WINDOW_TITLE=...`
- `XRE_WORLD_MODE=UnitTesting`

If you want the “Unit Testing World” behavior, also pass `--unit-testing` under Project → Properties → Debug → **Command line arguments**.

### Run / Debug the Dedicated Server

- Set startup project to `XREngine.Server`.

### Run multiple instances (Server + Clients)

Two common approaches:

- **Multiple startup projects**: Solution → Properties → Startup Project → **Multiple startup projects**, set `XREngine.Server` and one (or more) `XREngine.Editor` entries to **Start**.
- **Start New Instance**: right-click `XREngine.Editor` (or `XREngine.Server`) and choose **Debug → Start new instance**, then vary env vars per instance (e.g., ports 5001/5002) via separate Debug Profiles (VS 2022+) or by editing the project Debug settings before launching each instance.

## Network test helper

For a quick two-instance Editor networking test (server + client), there is a helper script:

```powershell
./run_network_test.bat
```

## Native Dependencies
- `CoACD` – `dotnet build` invokes `Tools/Dependencies/Build-CoACD.ps1` (when needed) to fetch/build CoACD and produce `lib_coacd.*` under `XRENGINE/runtimes/<rid>/native`. Use `/p:ForceCoACDBuild=true` to force a rebuild, or run the script manually. (Requires `git` + `cmake`; on Windows it uses the VS 2022 generator.) The legacy wheel extractor (`Tools/Dependencies/Get-CoACD.ps1`) remains available if you prefer vendor-provided binaries.

- `MagicPhysX` – physics interop uses `libmagicphysx.dll` under `XRENGINE/runtimes/win-x64/native` (copied to output on build). If you change/update the MagicPhysX submodule or native bits, rebuild the related submodule/native output.

- `Rive` (UI) – the managed Rive wrapper is sourced from `Build/Submodules/rive-sharp`, and the engine loads `rive.dll` from `XRENGINE/runtimes/win-x64/native`. If you need to rebuild it (or other submodule binaries), use:

	```powershell
	./build_submodules.bat Debug x64
	# or: ./build_submodules.bat Release x64
	```

	This requires Visual Studio (or Build Tools) with the **Desktop development with C++** workload. The script will fetch `premake5` automatically if missing.

- Video/streaming codecs – the repo ships FFmpeg-family native DLLs (`avcodec`, `avformat`, etc.) under `XRENGINE/runtimes/win-x64/native` and copies them to output as needed (used by the Flyleaf integration).

- YouTube URL extraction (`yt-dlp`) – YouTube links are resolved to a direct playable URL through `yt-dlp` before FFmpeg open. Install `yt-dlp` and keep it on PATH, or place `yt-dlp.exe` next to the Editor/Client/Server executable.

	To install/update `yt-dlp` into the repo-standard dependency location, run:

	```powershell
	./Tools/Dependencies/Get-YtDlp.ps1
	```

	This downloads `yt-dlp.exe` to `Build/Dependencies/YoutubeDL/yt-dlp.exe` and the build copies it into app output folders automatically for executable projects.

- NVIDIA features (DLSS / Reflex / Streamline) – **NVIDIA proprietary SDK binaries are not redistributed in this repo.** To enable these features locally, obtain the relevant NVIDIA SDK(s) from NVIDIA and drop the required DLLs into `ThirdParty/NVIDIA/SDK/win-x64/`. The build copies them into the output directory when present. See `ThirdParty/NVIDIA/SDK/README.md`.

- `RestirGI.Native.dll` – the optional ReSTIR GI bridge is built from the native project under `Build/RestirGI/` and copied into the managed output as `RestirGI.Native.dll` when present. If it’s missing, build `Build/RestirGI/RestirGINative.sln` for your configuration/platform and then rebuild the C# projects so the copy step can pick it up.

## Documentation
Start with the docs index at `docs/README.md` for a structured map of architecture notes, API guides, and rendering deep dives. Highlights:
- `docs/architecture/README.md` – runtime flow, threading, project layout.
- `docs/api/*.md` – system-level API guides (scene, components, transforms, animation, physics, rendering, VR, engine API).
- Rendering notes live alongside other docs (for example `docs/architecture/CoACD.md`, `docs/work/design/gpu-render-pass-pipeline.md`, and `docs/features/light-volumes.md`).
- `docs/work/README.md` – working docs (TODOs, checklists, Vulkan and indirect rendering design workstreams).

## Contributing
There is no dedicated `CONTRIBUTING.md` in this repo yet. If you want to help out, open an issue or discussion first, then submit a PR. Active areas of contribution include rendering backends, XR tooling, editor UX, and automated testing.

## License and Support
- License: `LICENSE`

### Dependency inventory

For a best-effort list of referenced NuGet packages, submodules, and native/managed DLLs (with best-effort owner attribution), see `docs/DEPENDENCIES.md`.

### License summary (non-authoritative)

This project is licensed under the **GNU Affero General Public License v3.0 (AGPLv3)**. This is only a convenience summary; the full terms are in `LICENSE`.

- You can use, modify, and redistribute the code, but **redistribution must remain under AGPLv3** (copyleft).
- If you **distribute binaries/object code**, you must also provide the **Corresponding Source** under AGPLv3 (including the build/install/run scripts needed to produce and run the binaries).
- If you **modify the program** and users **interact with it over a network**, your modified version must **prominently offer those users access to the Corresponding Source** of that modified version (AGPL network-use requirement).
- When conveying copies, you must **keep license/copyright notices intact**, provide a copy of the license, and mark modified versions.
- The software is provided **“as is” with no warranty**, and there is a **limitation of liability** to the extent permitted by law.

Note: this repo includes third-party submodules/dependencies that may have their own license terms.
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
