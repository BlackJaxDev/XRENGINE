# XRENGINE Architecture

[← Docs index](../README.md)

High-level notes on how the engine stages work, render, and synchronize data across threads.

## Runtime Flow
- `Engine.Run` wires up windowing, XR bootstrapping, timers, and shared services before launching the game loop.
- The outer loop blocks on the render thread while Update, Collect Visible, Fixed Update, and background workers run on dedicated tasks.
- Shutdown occurs when every window signals close; subsystems tear down in reverse order.

## Thread Model
- **Update**: variable-rate gameplay, input, and scene logic.
- **Collect Visible**: builds the next frame’s render commands, then hands buffers to the render thread.
- **Render**: swaps buffers, submits GPU work, and gates the main thread.
- **Fixed Update**: advances deterministic systems such as physics.
- **Job Worker**: drains enumerator-based jobs and async tasks.

## Worlds and Scenes
- `XRWorldInstance` hosts active scenes, physics contexts, and timer hooks for a running world.
- Tick groups (`PrePhysics`, `Normal`, `PostPhysics`, etc.) provide deterministic ordering each frame.
- Scenes can be enabled or disabled per world so multiple viewports reuse the same world state.

## Data Synchronization
- Transforms mutated on the Update thread are depth-sorted, solved, and double-buffered before the render phase.
- Renderable state is swapped alongside command lists, minimizing contention between the gameplay and render threads.

## Rendering Pipeline
- Every `XRWindow` owns a render pipeline instance; the default pipeline orchestrates deferred passes, forward lighting, transparency, UI, and post-processing.
- Render commands live in `ViewportRenderCommandContainer` chains so pipeline stages can branch between onscreen and offscreen targets.

## Background Jobs
- `JobManager` runs continuously on the job worker thread, progressing queued jobs while other phases execute.
- Main-thread work can be scheduled and is executed during `RenderFrame` just before GPU submission.

## Project Layout
- Each game lives inside a project root that contains only `*.xrproj` plus the standard folders listed below; tooling emits warnings when extra files sit beside the descriptor.
- `Assets/`: gameplay code, content, and any authorable data the editor should watch.
- `Intermediate/`: generated artifacts (solutions, project files, build outputs) managed by the C# project builder; editors dynamically load DLLs from here.
- `Build/`: the latest cooked builds, organized per configuration and platform for distribution.
- `Packages/`: third-party or externally sourced content mirrored into the project.
- `Config/`: persistent project + engine settings, including `engine_settings.asset` and per-user overrides.

## Further Reading
- [Audio Architecture](audio-architecture.md)
- [Modeling XRMesh Editing Architecture](modeling-xrmesh-editing.md)
- [Job System](../api/job-system.md)
- [Networking Overview](networking-overview.md)
- [Editor Undo System](undo-system.md)
