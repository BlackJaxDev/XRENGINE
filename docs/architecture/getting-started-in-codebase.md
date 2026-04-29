# Getting Started In The Codebase

This guide is for contributors who want to get productive quickly without reading the whole repository first.

## Start here

If you are new to XRENGINE, this is the fastest path:

1. Build the solution.
2. Run the editor.
3. Launch the Unit Testing World.
4. Pick one subsystem and trace it from entry point to runtime behavior.

For most feature work, the editor and the Unit Testing World are the shortest path to understanding how a system behaves.

If you want the broadest built-in setup path first, run:

```powershell
ExecTool --bootstrap
```

That bootstrap flow:

1. initializes git submodules
2. downloads the repo's standard dependency set
3. builds submodules
4. generates the local Unit Testing World JSONC settings files and schema
5. builds the DocFX site
6. launches the docs server
7. launches the editor

It is the closest thing the repo currently has to a one-command setup.

It does not automatically provision every optional dependency or proprietary SDK. If you need optional features such as NVIDIA SDK integrations or some Audio2Face-related prerequisites, check `docs/features/bootstrap.md` and the dependency/setup docs after bootstrap finishes.

## High-value projects

These are the projects most people touch first:

| Project | Why you would open it |
|--------|------------------------|
| `XREngine/` | Core engine runtime, scene graph, rendering, XR systems |
| `XREngine.Editor/` | Editor startup, tooling, unit-testing world bootstrap |
| `XREngine.Server/` | Dedicated server executable |
| `XREngine.VRClient/` | Legacy OpenVR companion process. It owns the SteamVR/OpenVR connection, sends VR input to the main engine app through a pipe, and presents per-eye frames streamed back from the engine. |
| `XREngine.UnitTests/` | Automated tests for engine/editor subsystems |

Other projects such as `XREngine.Animation`, `XREngine.Audio`, `XREngine.Input`, and `XREngine.Modeling` are subsystem-specific modules that support the main runtime.

## Where to begin by task type

### Rendering work

Start with:

- `XREngine/`
- `docs/api/rendering.md`
- `docs/architecture/rendering/frame-lifecycle-and-dispatch-paths.md`

Typical things to trace:

- scene collection and visibility
- render passes and pipeline selection
- OpenGL path vs experimental backends

### Scene graph or component work

Start with:

- `docs/api/scene.md`
- `docs/api/components.md`
- `docs/api/transforms.md`

The engine uses a scene node hierarchy with components attached to nodes. If you are changing behavior propagation, transforms, or state updates, start there before touching rendering or editor code.

### Physics work

Start with:

- `docs/api/physics.md`
- the unit-testing world settings and physics-related toggles

PhysX is the current default path, so changes there should be validated in the editor test world first.

### XR / VR work

Start with:

- `docs/api/vr-development.md`
- editor/unit-test-world XR toggles
- `XREngine.VRClient/` if the change is client-specific

If you are touching runtime selection or controller/pose behavior, validate the exact runtime path you changed.

`XREngine.VRClient` is not just a generic VR executable. It exists for the legacy OpenVR path, where SteamVR/OpenVR initialization cannot be cleanly torn down and restarted inside the same running app. The separate process keeps that OpenVR lifetime isolated so the main engine app can switch between desktop and VR gameplay without restarting. OpenXR does not have that shutdown/startup limitation, so this extra process is mainly about legacy OpenVR support.

### Editor work

Start with:

- `XREngine.Editor/`
- `docs/architecture/undo-system.md`

The day-to-day editor UI is the ImGui path. There is also a native UI pipeline under active development, but it is not the default path for most tasks.

### Networking work

Start with:

- `docs/architecture/networking-overview.md`
- the VS Code tasks for server/client and pose sync
- the Unit Testing World networking pose setup

## Practical workflow

### 1. Reproduce the behavior in the editor first

For many changes, the fastest loop is:

- run the editor
- use the Unit Testing World
- narrow the scene to the subsystem you care about
- make the smallest code change that proves your understanding

### 2. Find the nearest existing doc

Before making broad changes, check whether the subsystem already has an API or architecture doc. The docs are uneven, but they usually contain enough naming and entry points to shorten the search.

### 3. Use the nearest test or launch path

Good default validation order:

1. targeted unit tests if they exist
2. narrow build of the touched project
3. run the exact editor/server/client flow that exercises the change

### 4. Keep the working directory in mind

Some startup paths depend on the process working directory being the repo root, especially flows that load the generated local `Assets/UnitTestingWorldSettings.jsonc`.

## Useful docs to bookmark

- `docs/README.md`
- `docs/api/engine.md`
- `docs/api/scene.md`
- `docs/api/components.md`
- `docs/api/rendering.md`
- `docs/api/physics.md`
- `docs/api/vr-development.md`
- `docs/features/bootstrap.md`
- `docs/features/unit-testing-world.md`

## Useful local workflows

- `ExecTool --bootstrap`
- `Editor (Unit Testing World)` launch profile in VS Code
- `Start-Editor-NoDebug`
- `Start-Server-NoDebug`
- `Start-Client-NoDebug`
- `Start-Client2-NoDebug`
- `Start-LocalPoseSync-NoDebug`

`ExecTool.bat` is also useful when you need to discover setup scripts, reports, or one-off maintenance tools without hunting through `Tools/` manually.

## What not to do first

- do not start with a broad rewrite before you can run the affected path locally
- do not assume the experimental backend is the one to validate unless your task is explicitly about it
- do not treat the native UI editor path as the default unless the task is specifically targeting it

## Related documentation

- [Documentation Index](../README.md)
- [Architecture Overview](README.md)
- [Unit Testing World](../features/unit-testing-world.md)
