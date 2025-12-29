# XRENGINE

XRENGINE is an experimental C# engine for virtual, augmented, and mixed reality. The codebase is under active development; expect breaking changes while rendering, physics, and editor workflows evolve.

## Status at a Glance
- Early stage project with frequent refactors across the runtime, editor, and asset pipeline.
- Windows 10/11 is the current target platform.
- OpenGL 4.6 is the supported renderer; Vulkan and DirectX 12 backends are prototypes.
- OpenXR and SteamVR integrations exist but still require manual setup for most devices.
- PhysX powers today’s physics path; Jolt and Jitter backends are being brought online.

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

## Quick Start
```powershell
git clone --recurse-submodules https://github.com/BlackJaxDev/XRENGINE.git
cd XRENGINE
dotnet restore
dotnet build XRENGINE.sln
dotnet run --project XREngine.Editor
```

Running the editor launches the Unit Testing World, a collection of scenes that exercise rendering, animation, physics, audio, and XR workflows. Use this environment to verify changes and explore current functionality.

## Native Dependencies
- `CoACD` – `dotnet build` automatically invokes `Tools/Dependencies/Build-CoACD.ps1` to pull the upstream source and produce `lib_coacd.*` under `XRENGINE/runtimes/<rid>/native`. Use `/p:ForceCoACDBuild=true` or run the script manually when you need to rebuild or retarget a new release. The legacy wheel extractor (`Tools/Dependencies/Get-CoACD.ps1`) remains available if you prefer the vendor-provided binaries.

## Development Notes
- The engine employs a multi-threaded update loop (update, fixed update, job worker, collect visible, render) to separate gameplay, physics, and rendering work.
- Asset importers handle common model formats (FBX, OBJ) and feed into the editor-driven content pipeline.
- Compute-based experimentation exists for animation and physics chains, but these paths are not production ready yet.

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
- Issues: https://github.com/BlackJaxDev/XRENGINE/issues
- Discussions: https://github.com/BlackJaxDev/XRENGINE/discussions