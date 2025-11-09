# XRENGINE Architecture

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
