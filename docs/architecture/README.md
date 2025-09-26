# XREngine Architecture Overview

This document summarizes how the runtime systems in **XREngine** fit together, from the initial render loop down to the way worlds and transforms are synchronized. It is intended as a companion to the root README and focuses on the concrete code paths you will encounter while extending the engine.

## Coordinate Spaces

XREngine uses a right-handed world space where the +X axis points right, +Y is up, and looking forward points toward **-Z**. Convenience vectors such as `Globals.Forward` and `Globals.Up` expose these canonical directions for use throughout the codebase.【F:XREngine.Data/Globals.cs†L10-L17】

## Engine Lifecycle

`Engine.Run` is the high-level entry point. It initializes engine subsystems (window creation, VR bootstrap, timer configuration, and networking), then starts the multithreaded game loop before blocking the main thread to feed the graphics API. When all windows are closed the engine cleans up shared assets.【F:XRENGINE/Engine/Engine.cs†L130-L195】【F:XRENGINE/Engine/Engine.cs†L439-L488】

During initialization the engine wires timer events into task queues that shuttle work between worker threads and the render thread. Helper methods such as `InvokeOnMainThread` ensure graphics-facing code runs on the render thread while still allowing background jobs to enqueue work safely.【F:XRENGINE/Engine/Engine.cs†L36-L111】

## Multithreaded Timer & Frame Phases

`EngineTimer.RunGameLoop` spins up the core execution threads: an Update thread, a Collect Visible thread, a Fixed Update thread, and a job-processing worker. The Collect Visible and render threads coordinate via synchronization primitives so that buffer swaps happen deterministically between frames.【F:XRENGINE/Core/Time/EngineTimer.cs†L88-L193】

The timer exposes phase-specific events:

- **UpdateFrame**: variable-rate gameplay logic.
- **CollectVisible**: render command generation that runs ahead of the renderer.
- **SwapBuffers / RenderFrame**: render-thread callbacks for buffer management and GPU submission.
- **FixedUpdate**: fixed-rate tasks such as physics.【F:XRENGINE/Core/Time/EngineTimer.cs†L26-L74】

`BlockForRendering` is called on the render thread. It waits for Collect Visible to finish preparing a frame, swaps the double buffers, and executes `RenderFrame` handlers until the engine reports no active windows.【F:XRENGINE/Core/Time/EngineTimer.cs†L116-L193】【F:XRENGINE/Engine/Engine.cs†L453-L457】

## Worlds, Scenes, and Tick Groups

An `XRWorldInstance` represents the live state of a world that can be rendered into one or more windows. When play begins it initializes physics, recalculates transforms, and registers its callbacks with the engine timer so that world logic participates in every frame.【F:XRENGINE/Rendering/XRWorldInstance.cs†L55-L147】

World logic is partitioned into tick groups (`Normal`, `Late`, and pre/during/post physics bands). These groups execute in deterministic order each Update phase, while `FixedUpdate` advances the physics scene and runs the associated ticks.【F:XRENGINE/Rendering/XRWorldInstance.cs†L74-L444】

Scenes are attached to a world instance through `LoadScene`. Enabling a scene promotes its root nodes into the world’s root collection; disabling it removes them, which lets multiple viewports share the same world data without duplication.【F:XRENGINE/Rendering/XRWorldInstance.cs†L265-L358】

## Transform Synchronization Between Threads

Gameplay code mutates transforms on the Update thread. Dirty transforms are queued per hierarchy depth so their world matrices can be recalculated in deterministic order after Update logic completes. The recalculated matrices are swapped into a render-side queue, and the render thread applies them during the Swap Buffers phase before issuing draw calls. This double-buffering keeps transform updates thread-safe while minimizing contention.【F:XRENGINE/Rendering/XRWorldInstance.cs†L193-L263】

## Rendering Pipeline Overview

Each `XRWindow` owns a render pipeline instance. The default pipeline defines the order of render passes and their sorters (e.g., near-to-far for opaque geometry, far-to-near for transparent surfaces).【F:XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs†L14-L53】

Pipeline execution is described by a command chain built from `ViewportRenderCommandContainer`. It branches depending on whether the target is an on-screen viewport or an off-screen framebuffer, then executes a sequence of SSAO, deferred lighting, forward passes, transparency, UI composition, and post-processing commands while caching the necessary G-buffer textures and FBOs.【F:XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs†L94-L200】

Because the pipeline holds references to the active viewport via `Engine.Rendering.State`, each command can access viewport dimensions and shared render targets when (re)allocating textures.【F:XRENGINE/Rendering/Pipelines/RenderPipeline.cs†L13-L78】

## Background Jobs and Main-Thread Tasks

The `JobManager` runs as part of the timer’s dedicated job-processing loop. Jobs encapsulate enumerator-based workloads and are progressed continuously while the game loop is running. Separate queues allow asynchronous tasks to enqueue work for the render thread, and `Time.Timer.RenderFrame` drains this queue right before issuing GPU commands.【F:XRENGINE/Engine/Engine.State.cs†L12-L41】【F:XRENGINE/Engine/Engine.cs†L36-L180】【F:XRENGINE/Core/Time/EngineTimer.cs†L153-L157】

---

With these moving pieces in mind, you can navigate the engine by locating the relevant timer phase, finding the world or pipeline callback that participates in that phase, and then following how data is synchronized into the render thread.
