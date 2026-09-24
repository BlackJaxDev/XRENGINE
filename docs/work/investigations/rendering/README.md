# Rendering Investigations

Only actionable, evidence-driven rendering defects live directly in this
directory. Completed, superseded, or implementation-handoff records are kept
under [archive](archive/README.md); their internal status and remaining-work
sections describe the historical point-in-time state, not the current backlog.

## Current focus

- [Vulkan Desktop Camera Motion, Stale Frames, And CPU Scaling](vulkan-camera-motion-black-flicker-2026-08-10.md)
  is the canonical desktop camera/input/cadence triage guide. Its stale-frame
  correctness fixes are live-validated; remaining prepared-producer CPU work is
  owned by the linked optimization and testing trackers.
- [Directional Light Vulkan Stability](directional-light-inspector-shadow-2026-08-03.md)
  is the canonical active investigation.

## Other open investigations

- [Vulkan DLSS, Engine AA, And Frame Generation](2026-09-24-vulkan-dlss-aa-frame-generation.md):
  Default/Advanced AA selection, TSR with frame generation, and native DLSS
  dispatch/presentation revalidation.
- [Advanced Vulkan TSR Jitter And Aliasing](2026-09-24-advanced-vulkan-tsr-jitter.md):
  resolve-grid alignment, Vulkan Y conversion, depth ordering, and deferred
  jitter snapshot lifetime are fixed; coverage tracking, flicker retention, and
  unsharpened history have live mono validation with remaining quality limits.
- [Editor Hidden Scene And Camera Input](editor-hidden-scene-input-2026-07-08.md):
  live OpenXR/editor input and preview validation remains.
- [OpenGL GPU Pipeline Timestamp Readiness](opengl-gpu-pipeline-timestamp-readiness-2026-07-28.md):
  query readiness/publication remains an open instrumentation defect.
- [Vulkan Startup Black Screen And Close Lockout](vulkan-startup-black-and-close-lockout-2026-07-30.md):
  implementation exists, but the isolated runtime acceptance pass remains.

Missing feature implementation belongs in `docs/work/todo/`; acceptance and
hardware matrices belong in `docs/work/testing/`; implementation ledgers belong
in `docs/work/progress/`. Do not reopen an archived investigation when one of
those canonical owners already carries the remaining work.
