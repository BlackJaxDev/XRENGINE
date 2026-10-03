# Browser WebGPU canvas host

**Date:** 2026-09-28. **Base:** `85c04f19099fd65b9699b61698d667fc2747dfd0`.
**State:** Implemented source; build, browser, GPU and desktop validation deferred
at the user's explicit request. This is not a rendering or mobile acceptance
result. No tests were added or run.

The subsequent [mesh and packet bridge](browser-mesh-packet-bridge.md) replaces
the scalar triangle submission described below. This document records the earlier
canvas implementation; its validation limitations still apply.

## Code delivered

- Portable `BrowserCanvasRenderTarget`, `IRuntimeSurfaceHost`, and immutable
  surface/input snapshots in the existing rendering assembly. The target requests
  `BrowserCanvas`/`BrowserCanvasPresentation`, with no native-window dependency.
- Instance-owned canvas/device/scene sessions with asynchronous adapter, device,
  shader and pipeline startup. Superseded startup work cannot publish into a newer
  session. Generated imports/exports route by non-reused scene identifiers.
- A continuous `requestAnimationFrame` entry point with bounded fixed simulation
  and variable-cadence rendering. Real scene/component lifecycle updates feed
  the triangle's transform; one or two cached view projections target the canvas.
- CSS resize, DPR cap/device limit, orientation, zero-size/detachment suspension,
  reattachment, visibility clock reset, focus, pointer capture and directional
  input. Session observers/listeners are removed on stop. Restart and BFCache
  restoration create fresh sessions without overlapping loops.
- A WebGPU-only WGSL triangle executor with asynchronous pipeline creation,
  compilation diagnostics, fresh current-canvas texture acquisition per frame,
  per-view aligned uniform slices, explicit submission and resource disposal.
  Device loss/uncaptured errors stop the session and report the reason. There is
  no CPU/WebGL fallback or synchronous GPU wait.

## Boundaries and follow-up

The executor is a diagnostic browser-app leaf, not an implementation of the
full desktop-shaped renderer API. No ordinary engine mesh, material, cooked
asset, visibility pipeline, render-buffer swap, or generic frame packet is
silently substituted by the triangle. The portable view record supplies a
rectangle and view-projection matrix; it does not claim to be the complete
`XRViewport` implementation.

The scalar bridge sends one transform per view, plus frame begin/end. It is
bounded for this diagnostic scene, but is not the planned draw-count-independent
packet ABI. Implement the versioned packet/upload arenas and resource handle
generations before expanding to arbitrary draws. API-specific WebGPU work should
then move into the dedicated renderer leaf module.

Shader/pipeline preparation allocates only during startup. Frame submission
reuses matrix storage and descriptors; WebGPU encoder/pass/view/command-buffer
objects are unavoidable API products. Existing shared transform/runtime paths
have not been profiled here. No allocation-free or performance budget claim is
made. No temporal render history is allocated by this executor.

Full input mapping, keyboard text/IME, touch gestures, full engine cameras,
textures/materials/depth targets, device feature negotiation, automatic device
recovery, and mobile hardware remain follow-up work. The diagnostic input path
supports pointer movement and arrow keys only.

## Deferred validation

Per user instruction, this change skips the normal AGENTS.md build/live-render
validation workflow. The previously passing scene-only fixture does not prove
that these new generated interop signatures, shaders, GPU calls, multi-view
behavior, or lifecycle races work. Next validation should cover a Release publish,
actual WebGPU presentation, resize/zero-size/DPR transitions, cancel during each
startup await, stop/restart/device loss, input release, page hide/restore, and a
desktop build of the expanded shared contracts.

Build and usage instructions: [browser README](../../../../XREngine.Browser/README.md).

API references used during implementation:

- [WebGPU canvas configuration](https://developer.mozilla.org/en-US/docs/Web/API/GPUCanvasContext/configure)
- [Current canvas texture lifetime](https://developer.mozilla.org/en-US/docs/Web/API/GPUCanvasContext/getCurrentTexture)
- [Asynchronous pipeline creation](https://developer.mozilla.org/en-US/docs/Web/API/GPUDevice/createRenderPipelineAsync)
