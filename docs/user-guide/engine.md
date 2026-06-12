# Engine

[Back to user guide](README.md)

This page covers the surface-level engine concepts you configure or touch while running XRENGINE. For the code-facing API reference, see [Engine API](../developer-guides/runtime/engine-api.md).

## Startup

The engine starts from `GameStartupSettings` plus a `GameState`. Startup settings describe windows, timing, default user settings, render/audio/physics preferences, VR options, networking mode, and world targets.

Use the editor launch profiles and VS Code tasks as the source of truth for local runs. For first-time setup, see [Bootstrap And First-Time Setup](setup/bootstrap.md).

## Settings

Settings are layered:

- global defaults for the engine installation,
- project defaults under the active project config,
- user settings for local preferences.

Rendering settings include renderer selection, frame timing, VSync, transform debug drawing, mesh bounds, and other runtime diagnostics.

## Runtime Concepts

- `Engine.Time` owns frame timing and fixed update cadence.
- `Engine.Rendering` owns renderer settings and render state.
- `Engine.Audio` owns audio playback and device integration.
- `Engine.State` tracks editor/play state and local players.
- `Engine.Jobs` schedules asynchronous work.

## Deeper Docs

- [Engine API](../developer-guides/runtime/engine-api.md)
- [Architecture Overview](../architecture/README.md)
- [Job System](job-system.md)
