# XRENGINE

Scroll down a bit for to see technical details & how to bootstrap your own build. Fastest: run `./ExecTool --bootstrap` after cloning.

## THE WHO

Hi, I'm BlackJax. I figure you'd prefer to read a fully human-written readme, so I'll be writing this from first-person. Maybe you'll find some ✨spelling, punctuation, or grammatical errors✨! I'll try to not rant. I lived through the 2020 COVID pandemic as a newly-single 20-something year-old with a studio apartment and a comfy job that let me work from home and I've been a VR streamer ever since, so I think I have a solid understanding of what people are looking for in a more adult-oriented social VR experience.

## THE WHY

This TBA game engine is my solution for a high-performance social VR platform. I think there's a lot of fun & financial potential being left on the table by current Unity-based social VR games. We need a platform that:

- doesn't crack under pressure of an instance with 2 or more people using avatars that aren't optimized manually by the uploader first, resulting in a measly 10-30 fps in VR at best even on the best PC you can build. Social VR players have made it clear that they just want to hang out and be a more true, more attractive, or more wacky version of themselves or a character in VR. They DON'T want to have to learn the nuances of game dev. More fun; less work.

- provides creative tools in and out of VR that benefit both casual and advanced creators. Some people just want to make a button without node graphs or code, others might want the power of a 3D modeling pipeline. You should be able to build anything you want in an instance with friends, like streaming your work in a screenshare call.

- takes its audience seriously. There are a lot of issues with modern VR social games not knowing what their players want or are willing to spend money on, and there's a slew of dead competition that ran out of their 2020 COVID investment-round money and shut down. I think if you give users the right freedoms, tools and improvements they ask for, they'll give back to the platform in unanticipated ways.

## THE WHAT

XREngine is a cross-platform XR engine, built on C# with .NET 10. It ships with an editor, a dedicated server, and a sample networking control plane to connect player clients to server instances. 

I chose C# over the obvious C++ because I believe C# is a perfect balance between the simplicity and maintainability of languages like Python or Java, and the raw control of C++ or Rust. It's got a great ecosystem and support, and it's fast when wielded well.

This is early-stage software. The APIs and documentation will change often and there's no backward-compatibility promise yet. The goal is to get the architecture right before v1.

## THE WHEN

This engine is being developed in conjunction with a closed-source platform running on top of it. Anyone is free to develop personal open-source software on top of this engine or as a fork, or to contact me to license this engine for private commercial use.

This is the roadmap for my own platform:
- Q1 2027: release a full-body-tracked avatar demo app for people to try out in VR
- Q3 2027: release a networked demo client and server to show off instances with custom avatars
- Q1 2028: debut the early-release alpha build of the website & closed-source VR platform
- Q3 2028: Run a Kickstarter to fund operating and expanding on the platform more seriously

## THE HOW

Silk.NET operates as the main C# backend glue for supporting most typical rendering & input.

- **Scene graph:** Traditional Object-Component model. A world contains scenes, scenes contain a hierarchy of scene nodes, and scene nodes have a transform (custom or default) and a list of components. ECS is planned as a main alternate route for avatars/players specifically.

- **Rendering:** Vulkan is the primary developed path. OpenGL 4.6 is maintained as a compatibility option. GLFW is the windowing backend. *Wishlisted: D3D12 & Metal.*

- **Physics:** PhysX is the current default with character controller support. Jolt is being actively developed as an alternate main option. *Wishlisted: Jitter 2, Box3D*

- **Audio:** OpenAL & NAudio are the two supported buffering & output options, and Steam Audio and OpenAL EFX are the two supported audio effect drivers. *Wishlisted: FMOD*

- **Video:** FFmpeg (via FFmpeg.AutoGen) is the de-facto standard for video textures, streaming, and audio extraction. yt-dlp is utilized for YouTube playback.

- **Texturing:** Mipmaps primarily load via ImageMagick/Magick.NET. Other imaging support for fonts, icons, and SVGs is provided by ImageSharp and SkiaSharp.

- **XR:** OpenXR is the recommended path, and OpenVR remains as a legacy compatibility path. Monado is used for OpenXR unit testing.

- **UI:** ImGui is currently the main interface. A native UI pipeline is under development as the intended production UI.

- **Input:** Silk.NET.Input for keyboard, mouse, and gamepad. OpenXR and OpenVR are used for VR controller input.

- **Asset import:** FBX uses the native `XREngine.Fbx` importer and glTF/GLB uses the native `XREngine.Gltf` importer by default. Other model formats load through Assimp (via AssimpNetter). *Wishlisted: .usd, .blend, .max* 

- **Animation:** Fully in-house animation support & compression.

- **Networking:** Fully in-house client/server realtime transport with entity and avatar pose replication.

## Prerequisites

- .NET 10 SDK
- Windows 10/11 with a Vulkan 1.4 or OpenGL 4.6 capable GPU
- Git (submodules are used for third-party deps)
- LunarG Vulkan SDK with `VULKAN_SDK` set, used to build the VMA native bridge
- Visual Studio 2026 or Build Tools with Desktop development with C++, used for native bridge builds

You probably also want to have a VR headset to try out a VR game engine properly.

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

For bootstrap scope, first-time setup, and what still needs manual installation afterward, see `docs/user-guide/setup/bootstrap.md`.

### Run the editor

```powershell
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj
```

Or: `./Tools/Start-Editor.bat`

To boot into the Unit Testing World, use the `Editor (Unit Testing World)` launch profile in VS Code, or run the editor with `--unit-testing`.

If you are contributing code, the best follow-up docs are `docs/developer-guides/testing/unit-testing-world.md` and `docs/architecture/getting-started-in-codebase.md`.

## Project layout

| Project | What it is |
|---------|-----------|
| `XREngine/` | Core runtime: scene graph, rendering, XR subsystems |
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
| `Build/Submodules/` | Git submodules: OpenVR.NET, CoACD, OscCore-NET9, rive-sharp, Monado |

## Running and debugging

### VS Code (recommended)

The repo includes ready-to-go `.vscode/` configs. Use **Run and Debug** (Ctrl+Shift+D) to pick a launch profile:

- **Editor (Default World)** / **Editor (Unit Testing World)**
- **Debug Client**, **Debug Server**, **Debug VRClient**

There are also no-debug tasks under **Terminal → Run Task** for the common editor, server, client, and networking scenarios.

### Visual Studio

Open `XRENGINE.slnx`, set your startup project (`XREngine.Editor`, `XREngine.Server`, etc.), and hit F5. Environment variables like `XRE_NET_MODE` and `XRE_WORLD_MODE` can be set in Project → Properties → Debug to switch between server/client/local modes.

### ExecTool

The repo root has `ExecTool.bat`, an interactive menu for all the scripts under `Tools/` — build helpers, dependency installers, report generators, and more. Run it with no arguments for the menu, or `ExecTool --bootstrap` for full first-time setup.

Texture sample downloaders are available there too. `pwsh Tools/Get-PixelFurnaceTextures.ps1` pulls Pixel-Furnace samples, and `pwsh Tools/Get-FreePbrTextures.ps1` pulls FreePBR `bl` texture ZIPs into `Build\CommonAssets\Textures\Samples\FreePBR`; review each source site's terms before redistributing downloaded assets. In the ImGui editor, open `Tools > External Texture Browser` or `View > External Texture Browser` to browse Pixel-Furnace and FreePBR preview tiles and download individual material packs into the game assets folder.

## Unit Testing World

The editor's test world is configured through the generated local file `Assets/UnitTestingWorldSettings.jsonc`. Launch with `--unit-testing` (or set `XRE_WORLD_MODE=UnitTesting`) to boot into it. The file is intentionally ignored by Git for per-workstation tuning, and has a JSON schema wired up in VS Code for autocompletion and hover docs.

For startup model imports that reference textures outside the authored folder layout, set `TextureLoadDirSearchPaths` in that file to provide recursive texture search roots.

For the full workflow, including pose/network test setups and how the JSONC file is used, see `docs/developer-guides/testing/unit-testing-world.md`.

`./ExecTool --bootstrap` creates the file on first setup. To create it manually or regenerate the schema after changing the settings type:

```powershell
pwsh Tools/Generate-UnitTestingWorldSettings.ps1
```

## Native dependencies

Most core native pieces are already wired into the build, but some optional tools and SDKs still need local setup.

For the practical setup and rebuild guide, see `docs/developer-guides/runtime/native-dependencies.md`.

## Logs

When file logging is enabled, per-session logs go to `Build/Logs/<configuration>_<tfm>/<platform>/<session>/`, including profiler traces and benchmark output.

## Documentation

The source docs live under `docs/`. See `docs/README.md` for the full index: architecture overviews, user guides, developer guides, rendering notes, and design docs.

To build the local DocFX website:

```powershell
dotnet tool restore
dotnet docfx docs/docfx/docfx.json
```

The generated site is written to `docs/docfx/_site/`, which is ignored by Git. Open `docs/docfx/_site/index.html` directly, or serve the site locally:

```powershell
dotnet docfx docs/docfx/docfx.json --serve --port 8080
```

Then open `http://localhost:8080/`.

Good starting points:

- `docs/architecture/README.md` - runtime flow, threading, project layout
- `docs/architecture/getting-started-in-codebase.md` - contributor-oriented map of where to start
- `docs/user-guide/README.md` - editor-facing concepts, settings, and workflows
- `docs/developer-guides/README.md` - code-facing guides for implemented systems and extension points
- `docs/developer-guides/testing/unit-testing-world.md` - local validation world and pose/network test setups
- `docs/user-guide/setup/bootstrap.md` - first-time setup and bootstrap scope
- `docs/developer-guides/runtime/native-dependencies.md` - native and external setup reference
- `docs/work/README.md` - active TODOs and design docs

## Contributing

No formal `CONTRIBUTING.md` yet. Open an issue or discussion first, then submit a PR.

If you are getting oriented in the codebase, start with `docs/architecture/getting-started-in-codebase.md`.

## License

Licensed under **AGPLv3**. See `LICENSE` for the full terms.

The short version: you can use, modify, and redistribute the code, but any distribution (including network use) must stay under AGPLv3 and provide source. Third-party dependencies have their own licenses. See `docs/DEPENDENCIES.md` for the full inventory.

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
