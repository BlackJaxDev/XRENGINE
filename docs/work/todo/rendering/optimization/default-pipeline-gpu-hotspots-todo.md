# Default Pipeline GPU Hotspots TODO

Last Updated: 2026-07-28
Owner: Rendering
Status: Technical Child Of Workstream 06; VR-Only Acceptance Remains Cross-Cutting
Execution: Desktop subset uses the active workstream 06 branch/worktree; any
later VR-only branch requires separate approval.

Parent tracker:

- [Forward+ render-graph code changes](../vulkan-core-hardening-and-device-loss-todo.md#6-simplify-the-forward-render-graph)

Evidence source:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-gpu-pipeline-defaultrenderpipeline-32-2026-07-01-12-48-23-916-ba9dd90f.log`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-gpu-pipeline-defaultrenderpipeline-28-2026-07-01-12-48-21-719-86905924.log`

Related local docs:

- [Engine Rendering Optimization Roadmap](engine-rendering-optimization-roadmap.md)
- [Deferred Plus Render Path TODO](deferred-plus-render-path-todo.md)
- [VR Rendering Performance Contract TODO](vr-rendering-performance-contract-todo.md)
- [Default Render Pipeline Notes](../../../../architecture/rendering/default-render-pipeline-notes.md)

## Goal

Reduce the real shader and quality-profile GPU cost of the minimal default
pipeline after workstream 06 establishes which passes, producers, consumers,
copies, and resolves are actually required. The current GPU hotspots are not
the cause of 200 ms frames, but they are still too expensive for VR budgets.

## Ownership Boundary

Workstream 06 exclusively owns current Default-pipeline graph topology:
prepass and velocity geometry replays, attachment lifetimes and aliases,
full-resolution copies/resolves, transitions/barriers, and removal of hidden
producer or consumer passes.

This child tracker retains:

- AO shader resolution, sample-count, denoise, and quality tuning;
- exposure algorithm and policy tuning after required execution is known;
- LightCombine/clustered-light shader cost and GBuffer format analysis;
- bloom, TSR, motion-blur, and post-process quality profiles;
- desktop, editor, and VR quality defaults plus pass-level quality metadata.

Do not independently redesign graph topology here. Record topology findings in
workstream 06, then measure shader and quality changes against its accepted
minimal graph. The desktop subset executes as part of workstream 06 and cannot
be used as a later parallel performance track. VR-only completion remains
governed by the independent VR performance contract.

## Issue

The July 1 dedicated GPU pipeline dumps show that GPU work is much smaller than
render-thread CPU time, but the VR/stereo pipeline still averages about 20 ms
of GPU timestamped work. That misses common XR frame budgets even before CPU
overhead.

Worst `DefaultRenderPipeline#32` GPU nodes by average active frame:

- GTAO resolve/blur to `AmbientOcclusionBlurFBO`: about 5.1 ms avg,
  8.1 ms max.
- Auto exposure compute `VulkanAutoExposure2DArray`: about 3.9 ms avg,
  7.5 ms max.
- `VPRC_LightCombinePass`: about 3.8 ms avg, 6.4 ms max.
- `OpaqueDeferred`: about 2.0 ms avg, 5.6 ms max.
- `MaskedForward` leaf material: about 1.1 ms avg, 2.0 ms max.
- TSR upscale, light-combine blit, post-process output, GTAO resolve to
  G-buffer, bloom copy, and motion vectors each contribute smaller but steady
  costs.

## Why This Matters

Once render-thread recording and OpenXR fence waits are fixed, these GPU passes
will become the next visible blockers. VR cannot afford several full-resolution
screen-space passes plus expensive deferred lighting unless those passes are
quality-scaled, shared correctly across views, or moved to more efficient
implementations.

## Fix Direction

- Treat GPU optimization as a second-stage effort after CPU/sync attribution is
  clean and workstream 06's pass/resource ledger is accepted. Do not tune
  shaders based on a run where the render thread is still spending hundreds of
  milliseconds.
- Add quality profiles for desktop, editor, and VR:
  AO mode/resolution, auto exposure policy, bloom, TSR, motion blur, and
  light-combine quality.
- Make VR defaults conservative. Expensive full-resolution screen-space effects
  should be opt-in or dynamically quality-scaled for XR.
- Optimize GTAO:
  half/quarter resolution where acceptable, tighter denoise radius, lower slice
  or step count in VR, and view-array-aware sampling. Redundant resolve removal
  belongs to workstream 06.
- Optimize or bypass auto exposure:
  do not run the compute pass when the active VR exposure policy skips or
  cannot consume the result; support stereo-array input correctly when used.
- Optimize light combine:
  validate G-buffer formats, light count, BRDF inputs, MSAA resolve strategy,
  and whether tiled/clustered lighting is doing redundant work.
- Add pass-level output resolution, sample count, view count, and quality
  settings to GPU profiler dumps.

## Landed Ahead Of Branch (2026-07-01)

Two hotspot mitigations landed directly on `main` during the desktop Vulkan
perf triage (run `xrengine_2026-07-01_14-44-35_pid37936`):

- GTAO gen defaults reduced from 5 slices x 10 steps (~100 taps/px) to
  3 slices x 4 steps (24 taps/px, XeGTAO High-equivalent) in
  `GroundTruthAmbientOcclusionSettings` and the `GTAOGen`/`GTAOGenStereo`
  shader defaults. Half-res modes and VR profiles remain open items below.
- Vulkan auto exposure compute shaders rewritten from a single-invocation
  256-tap serial loop (with insertion sort) to a 256-invocation workgroup
  using shared-memory parallel reduction and a bitonic sort for the
  percentile path. Policy-based skips (Phase 1) remain open.

Also relevant to baseline cleanliness: Vulkan validation layers are now
opt-in in all configurations (`XRE_VULKAN_VALIDATION=1`), so future captures
on DEBUG builds no longer include validation-layer CPU cost by default.

## Phase 0 - Clean GPU Baseline

- [ ] Execute the required desktop subset inside workstream 06; do not create a
  competing desktop performance branch.
- [ ] Confirm workstream 06 has an accepted pass/resource ledger and record the
  exact graph revision used by this child tracker.
- [ ] Re-capture GPU pipeline dumps after disabling profiler UI and diagnostic
  logging overhead.
- [ ] Capture desktop mono, OpenXR stereo, and mirror-off VR modes separately.
- [ ] Record active settings for AO, exposure, light combine, MSAA, TSR, bloom,
  motion vectors, and stereo mode.
- [ ] Confirm whether GPU times are per-eye, stereo-array, or whole-frame
  timings.

Acceptance criteria:

- [ ] GPU hotspot rankings are based on clean captures where CPU recording is
  not the dominant unexplained cost.
- [ ] Each hotspot row includes enough settings to reproduce it.

## Phase 1 - Quality Scaling And Correct Skips

- [ ] Add a VR performance quality profile for AO, exposure, bloom, TSR, and
  post-processing.
- [ ] Ensure auto exposure does not run when the active policy skips the result.
- [ ] Verify workstream 06 removes disabled effects' producers, resolves, and
  copies; retain only quality-policy and shader-side skip logic here.
- [ ] Add diagnostics for effect enabled/disabled state and skip reason.

Acceptance criteria:

- [ ] Turning off or reducing an effect removes its GPU pass cost.
- [ ] The active quality profile is visible in GPU dumps and logs.

## Phase 2 - GTAO And AO Resolve

- [ ] Validate GTAO resolution divisor and denoise settings in VR.
- [ ] Add half-resolution or quarter-resolution GTAO modes with explicit
  upsample/resolve policy.
- [ ] Feed redundant AO intermediate-copy or aliasing findings into workstream
  06; benchmark AO kernels only after its graph decision is applied.
- [ ] Add a debug view comparing AO quality/perf modes.

Acceptance criteria:

- [ ] GTAO no longer consumes 5-8 ms in the standard VR performance profile.
- [ ] AO quality regressions are visible through debug views or screenshots.

## Phase 3 - Lighting And Post

- [ ] Profile `VPRC_LightCombinePass` by light count, tile/cluster count,
  G-buffer format, and MSAA state.
- [ ] Validate whether light combine runs once per stereo frame or redundantly
  per eye.
- [ ] Reduce full-resolution post passes in VR where the effect can run at
  lower resolution or be disabled.
- [ ] Verify masked foliage and alpha-tested materials use appropriate depth,
  overdraw, and shader variants.

Acceptance criteria:

- [ ] Standard VR GPU pipeline time is below the selected headset budget in a
  clean run, or the remaining over-budget passes are explicitly listed with
  quality tradeoffs.
