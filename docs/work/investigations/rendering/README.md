# Rendering Investigations

Only actionable, evidence-driven rendering defects live directly in this
directory. Completed, superseded, or implementation-handoff records are kept
under [archive](archive/README.md); their internal status and remaining-work
sections describe the historical point-in-time state, not the current backlog.

## Current focus

- [Advanced pipeline (Vulkan) directional shadows missing](advanced-vulkan-dirlight-shadows-2026-10-02.md)
  records the cascade slope-bias unit mismatch that made every receiver lit, the
  back-facing and footprint bias corrections, and the still-intermittent
  CastsShadows re-enable failure.
- [Production component-profile admission and output](2026-10-01-component-profile-production.md)
  records construction publication, cold shader admission, exact output evidence,
  and GPU topology validation for the presentationless production fixture.

- [Remaining GPUScene and Vulkan storage synchronization](2026-10-01-remaining-synchronization-gates.md) records separate gate distributions, real transform churn, concurrency limits and the measured synchronization deferral.

- [Warmed Advanced pipeline readiness allocations](2026-10-01-warmed-pipeline-readiness.md) records allocation removal, the measured CPU gate, live invalidation coverage and existing regression-test limits.

- [Advanced operation metadata scan attribution](2026-10-01-advanced-operation-metadata.md) records the measured structural-scan deferral and conditions for reopening.

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
