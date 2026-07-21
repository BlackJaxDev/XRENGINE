# Physics Chain Phase 8 Activity And Sleep Progress — 2026-07-20

## Implemented

- Added a backend-neutral normalized activity-error contract. The CPU solver
  samples Verlet particle velocity (per-step displacement), post-solve segment
  constraint error, discrete root acceleration, collider shape/pose changes,
  external force, and explicit recent visibility/use signals.
- Added independent enter thresholds, a configurable wake multiplier, minimum
  quiet-frame duration, and deterministic sleep hysteresis.
- Added documented wake inputs for root teleport/acceleration, collider shape
  and pose changes, force/event input, manual gameplay requests,
  visibility/use, relevance, and authored template/parameter changes.
- Collapsed legacy interpolation history, direct CPU palette history, and the
  backend-neutral world output record when entering sleep. GPU-native palette
  history still requires the dispatcher active-work integration and therefore
  its TODO remains open.
- Added one-frame-delayed aggregate active/sleep/entered-sleep/wake counters
  and a fixed-capacity selection of at most 16 detailed instance snapshots.
  Collection uses preallocated arrays and stack storage; it does not allocate
  in the world late-tick path.
- Added independent simulation and collision controls plus independent palette,
  conservative-bounds, and compatibility transform-mirror publication controls.
  CPU collision projection is skipped without removing collider inputs, and
  held outputs retain their last published values while other outputs advance.
- Added allocation-free affine interpolation between previous/current CPU
  palettes. Presentation reads clamp interpolation alpha and do not advance the
  simulation frame, output generation, or physical accumulator.
- Lower-rate transitions now preserve normalized cadence progress when rates
  change, and stable slot/generation hashing phase-staggers initial cadence.
- Added explicit offscreen modes: continue simulating, staged 30/15/7.5 Hz
  decay followed by sleep, and immediate sleep. Automatic-by-importance maps
  high importance to simulation, medium importance to decay, and low importance
  to immediate sleep; recent interaction temporarily restores strict quality.
- Requested/effective tier, decision reason, residence frames, and last
  transition frame remain available through generation-safe world diagnostics.
- Visibility restoration now wakes a sleeping chain and starts its recent-use
  grace period; the existing root, collider, force/event, explicit, relevance,
  template, parameter, and excessive-error wake reasons remain observable.
- Added generation-safe renderer observations carrying distance, normalized
  projected size, and visibility. A deterministic resolver maps those inputs
  and authored importance to the visible cadence; recent interaction and
  offscreen policy are then applied before the existing measured CPU/GPU budget
  controller. Fixed-tier chains continue to bypass automatic decisions.

## Validation

- `PhysicsChainActivity.Tests.csproj`: 5 passed, warnings treated as errors.
- `PhysicsChainWorldActivity.Tests.csproj`: 1 passed, warnings treated as
  errors, compiling the production diagnostics partial against a minimal world
  harness.
- `PhysicsChainCpuOutput3.Tests.csproj`: 23 passed, including held
  current/previous palette coherence.
- `PhysicsChainCpuQualityOutput.Tests.csproj`: 3 passed with warnings treated as
  errors, covering independent output cadences, collision disable, and
  interpolation reads that do not advance simulation/output generations.
- `PhysicsChainQualityPolicy2.Tests.csproj`: 13 passed with warnings treated as
  errors, covering independent controls, deterministic phase/progress,
  offscreen policy/decay, and bounded affine interpolation.
- `PhysicsChainAutomaticQuality.Tests.csproj`: 7 passed with warnings treated as
  errors, covering distance, projected size, importance, and invalid input.
- The targeted repository test command is presently blocked before reaching
  the engine tests by unrelated Vulkan rendering compile failures:
  inaccessible `MarkResultEpochSubmitted`, missing
  `RecordVulkanSwapchainRetirement`, missing `NvidiaDlssManager`, and an
  unassigned `failureReason`.
- Building `XRENGINE.csproj` with project-reference builds disabled reaches
  unrelated stale rendering-output contract errors in
  `Engine.Rendering.SecondaryContext.cs`; no Phase 8 compile diagnostic was
  emitted.

## Remaining Phase 8 Work

- GPU activity evaluation and sleeping-work compaction without CPU readback.
- GPU-native current/previous palette hold integration and end-to-end visual
  validation once the dispatcher active-work path is complete.
- Phase-level acceptance benchmarks and frame-budget evidence.
