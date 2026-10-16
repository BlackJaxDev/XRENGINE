# Rendering Investigations

Only actionable, evidence-driven rendering defects live directly in this
directory. Completed, superseded, or implementation-handoff records are kept
under [archive](archive/README.md); their internal status and remaining-work
sections describe the historical point-in-time state, not the current backlog.

## Current focus

- [Unity import shader compiler pressure](2026-10-15-unity-import-shader-compiler-pressure.md)
  records delayed OpenGL compile stalls, compiler allocation failures, timeout
  retries, the correlated Codex resource failure, and bounded Fishnet CPU
  compiler measurements after static lighting-path removal and shared local-light
  evaluation, including the dynamic lighting variant. The later single-material
  runtime retry still reached the 2 GiB stop limit during driver linking. The
  later CPU-only checks reduced repeated Vogel paths and four-slot depth-filter
  copies. Stage measurements attribute the dynamic 1.79 GiB peak to native
  optimization; pass isolation identifies redundancy elimination. Shared local
  lighting, point metadata indexing, and shared receivers reduced the CPU peak
  to 1.14 GiB after contact-loop and atlas-filter sharing. The dynamic compile
  passed with the unchanged 3 GiB RAM
  reserve, 2 GiB child-private limit, and 120-second limit. Driver-link memory
  after these changes remains unverified. A full Aryia package attempt with the
  authorized 4 GiB launch check stopped after 8.5 seconds at the unchanged
  3 GiB available-RAM threshold. Editor-private memory peaked at 1.33 GiB.
  Package preparation now completes in a CPU process with 54% fewer managed
  allocated bytes. HDR decoding removes temporary float arrays and releases
  owned output on disposal; the controlled image check reduced peak process
  memory by about 128 MiB with identical pixels. The next full-package runtime
  attempt still reached the 3 GiB RAM stop threshold during startup, with a
  0.92 GiB sampled editor-private peak. Complete avatar import, rendering, and
  the reported lag remain unverified.
  A later CPU probe measured 109 MB of cold source-optimizer allocation for one
  Advanced shader. Span-based line and identifier scans passed exact source
  and interface comparisons. Advanced cold optimization allocation fell to
  51 MB. A guarded startup reached all seven companion sources before the RAM
  reserve stopped it. Host sampling found about 370 MiB of audio-process growth;
  legacy listeners now defer device creation until native audio demand. The
  native audio checks and editor startup passed. Package roots now convert
  one at a time, and dependency graphs scan each reached file once. A guarded
  retry imported seven hidden prefab scenes and one visible demo scene before
  reaching the 2 GiB editor-private stop. Directional-shadow arrays now omit
  unused mip levels; runtime logs confirm one level at full resolution. Import
  still reaches the private-memory guard. The latest run observed a 2.54 GiB
  peak during shutdown, so the scratch monitor now requests direct termination
  before session bookkeeping. Full Aryia import remains unverified.
  CPU isolation now uses a verified 2 GiB Windows job cap before child startup.
  It separates the main scene's large CPU import cost from shadow storage.
  Streamed geometry conversion passed exact populated-data comparisons with
  40–50% fewer managed allocations. Stale imported deformation payloads and
  repeated texture validation have further fixes. Their builds passed. The CPU
  retry confirmed release of 116.9 MiB of stale deformation data, then stopped
  at the unchanged 3 GiB RAM reserve during main-scene texture validation.
  Sampled process private memory peaked at 1.81 GiB. Full import remains open.
  Full-decode texture validation now omits unused RGBA export. A focused 4096²
  texture check reduced the sampled process peak from 383 MiB to 203 MiB and
  passed damaged-pixel rejection checks. The full CPU retry passed texture
  validation, then hit the 2 GiB cap during repeated avatar-model import.
  Native import now releases consumed packed chunks, builds weight dictionaries
  per active worker, and skips premature deformation preparation for Unity.
  Packed source tangents stay implicit while zero. Exact CPU mesh comparisons
  passed. The full CPU import then completed all 26 roots in 88.01 seconds,
  with all four avatar hierarchies, mesh buffers, and bones independent. Peak
  private memory was 1.970 GiB and minimum available RAM was 3.018 GiB. Limits
  stayed unchanged. The margin is small; GPU rendering remains unverified.
  The next change reuses one parsed FBX source revision per root import while
  keeping runtime instances separate. Builds passed, but its full CPU retry
  stopped at the 3 GiB RAM reserve before the independence audit. A final
  dictionary-capacity reduction and cache-contract probe still need validation;
  available RAM fell below the unchanged 4 GiB launch requirement.
- [Math selection AOT exception and frozen view](2026-10-13-math-selection-aot-and-blank-view.md)
  records the gizmo asset fixes, Vulkan review, and live checks of a view that
  showed one new frame per resize.
- [Jax2031 Unity and Advanced Vulkan parity](2026-10-08-jax2031-unity-advanced-parity.md)
  records the repaired Assimp root import, remaining visible deformation,
  native FBX normalization guard, Pro 9.3.66 material evidence, native Uber
  contract gaps, and separate Release shader readiness failure.
- [OpenXR stereo flicker and render-target duplication](2026-10-04-openxr-stereo-flicker-and-target-duplication.md)
  records the stale descriptor sets behind the alternating HUD, the stereo
  pipeline target set held three times by the resource planner, the
  VR View measurement method, and the remaining startup transition black.
- [Editor memory retention across Play round trips](2026-10-04-editor-memory-retention.md)
  records the retention chains that kept every Play copy alive, the fixed-cost
  reductions, desktop/OpenXR measurements, and the remaining OpenXR mapped
  memory, shader-variant duplication and per-frame allocation work.
- [Disk-backed shader root reload](2026-10-04-shader-root-reload.md)
  investigates stale top-level source after file notifications and preservation
  of unsaved edits and generated shader sources.
- [OpenGL admission and measurement](2026-10-03-s16a-opengl-admission.md)
  records upload, timing readback and source-reload fixes, shadow fixture
  attribution, and the validated visible-geometry measurement gate.
- [Directional shadow canonical lane](2026-10-03-s15b-directional-shadow-lane.md)
  records motion-frame improvements, cold-start readiness and submission-retry
  corrections, and the remaining masked-shadow parity gate.
- [Advanced pipeline (Vulkan) directional shadows — Resolved](advanced-vulkan-dirlight-shadows-2026-10-02.md)
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

- [Browser Attachment-Clear Scope Unwind](browser-scissored-clear-unwind-2026-10-04.md):
  pending command preparation leaked viewport crops and framebuffer scopes;
  bounded restoration is implemented and exact-source browser validation remains.
- [Browser Canonical Shader Staging](browser-canonical-shader-staging-2026-10-04.md):
  canonical source closure, portable provenance and manifest-capacity corrections
  for the complete browser shader inventory; fresh Windows publication remains
  the integration acceptance check.
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
