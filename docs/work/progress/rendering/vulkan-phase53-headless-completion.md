# Phase 5.3 headless integration

Phases 5.2 and 5.3 are closed as of 2026-08-31. This note records the streaming,
material, and pipeline integration. Validation uses the production presentationless Vulkan
host, not an editor window, desktop automation, component mock, or CPU rendering
fallback. No tests were added or modified during feature validation.

## Implemented contracts

- Texture preparation remains worker-owned. A ticket carries one bounded staging
  chunk at a time; shared native batches retain destination and staging ownership
  through their actual fence. Foreground staging has a protected reservation.
- Completion/publication work is metered per render-frame boundary. A required
  manifest exhausting its budget receives retryable admission; unrelated jobs
  retain their global ownership and cannot be published through that manifest.
- Transfer GPU time comes from four worker-prepared timestamp pairs, leased to
  actual native batches through fence proof and command cleanup. Non-waiting
  result reads and unavailable counts are separate from CPU fence-wait time.
- Immutable material publications share unchanged CPU pages and scalar-equivalent
  descriptor closures. Dedicated, geometrically sized native banks belong to an
  exact arena, frame slot and reset epoch. Allocation runs on a bounded worker;
  completed-slot replay writes only changed row runs. Descriptor identity uses
  native backing/generation/range/ABI and closure identity, not scalar revision.
- Authoring snapshots, accepted frames and recorded commands retain their own
  material ownership. Cached executable commands release it at reset/abandon or
  destruction, not merely GPU completion. Final descriptor-closure release is
  deferred outside command-tracker locks. OpenXR authoring copies own detached
  snapshots; sealed preparation supplies the actual eye-slot material authority.
- Graphics/compute preparation preserves Missing/Pending/Failed. Native worker
  results are generation-checked; steady-state misses retry rather than compile
  inline or omit a dispatch. Persistent cache identity includes device, driver,
  engine build, target mode and shader artifacts.

## Defects caught during headless integration

1. Empty/disjoint foreground manifests reset the background upload gather flag,
   starving ready chunks. Only the corresponding ordinary gather resets it now.
2. A first-chunk staging-capacity yield discarded the pending image owner after
   its handles had moved out of preparation. The next attempt produced a zero
   image barrier and a native access violation with five foreground tickets.
   Yield now transfers the exact pending owner into the job and resumes staging;
   recording also validates destination and staging generations.
3. Cancellation cleanup assumed a submitted batch was complete. Cleanup now
   requires successful fence proof, is one-shot, and retains/quarantines unknown
   completion instead of treating it as unsubmitted work.
4. The initial material command guard compared independent global recording and
   per-buffer lifetime counters. Begin now captures the lifetime epoch explicitly.
5. First-use backing, closure and cached-command identities were incomplete.
   Retained CPU/native material readback checks now expose those failures instead
   of accepting a descriptor-ready texture as a completed upload ticket.
6. Required-upload budget exhaustion escaped the headless host as a private
   readiness exception. It now becomes the public retryable admission result
   before acquisition/submission, preserving cleanup and fresh-plan retries.
7. Material-bank shadow comparisons included newly exposed capacity bytes.
   Comparisons are now restricted to the previously initialized range; new rows
   are always written, including when their expected contents are zero.

## Recorded integration evidence

Evidence root: `Build/_AgentValidation/20260830-124809-phase52-bounded-rendering/`.
Ignored reports are disposable; these results are the durable summary.

- `reports/phase53-streaming-final`: four fresh children (both depth modes,
  two repeats), 836 completed production receipts. Every child verifies all
  34 mip hashes / 112,197,628 bytes, 55 chunks coalesced into 52 submissions,
  three final publications and 52 GPU timing samples with zero unavailable.
  Queued and submitted-but-unobserved cancellations publish nothing and settle
  through seven ordinary retirement boundaries. Standard and synchronization
  validation are enabled with zero errors.
- `reports/phase53-materials-final`: four fresh children, 44 completed receipts.
  A bound 4096² required-visible texture crosses the staging reservation through
  31 chunks and ten typed fresh-plan retries per child. Initial, scalar and
  texture/sampler publications match their actual native rows. Each mutation
  changes exactly one 64-byte row; scalar mutation preserves descriptor closure
  identity while texture/sampler replacement changes it. After all slot banks
  are warmed, page writes remain 6 → 6, descriptor writes 5 → 5 and closure
  acquisitions 3 → 3. Standard/synchronization validation report zero errors.
- `reports/phase53-pipelines-final`: eight fresh cold/warm children across
  both depth modes and two repeats; 96 completed production receipts, standard
  and synchronization validation enabled, zero validation errors. Warm runs load
  451,659 native cache bytes. Every steady interval has zero pipeline creates, queued or
  pending jobs, foreground waits, and render-thread shader compiles.
- `reports/phase53-regression-masked-moving-canonical`: 288 frames at 1279x719,
  both depth modes and two repeats. Each cohort demonstrates four hidden-object
  culls, zero false occlusion and zero missing visible objects. The separate
  640x360 control remains conservative and demonstrates no culls; it is not the
  dimension-equivalent acceptance fixture.
- `reports/phase53-native-buffer-regression-normal` and `...-reversed`: 4096x4096
  exact-generation growth/retirement controls pass through real in-flight
  ownership and ordinary retirement with native validation enabled.
- `reports/phase53-clear-regression`: the original deterministic-clear recipe
  retains zero capture-thread and fixture-worker allocations after 30 warmup,
  five stability and three capture frames.
- Isolated RenderBench and editor builds pass with zero warnings/errors.

The final streaming/material children each record two Vulkan loader warnings;
these are distinct from native validation errors. The earlier single-child
upload submissions and failed foreground ownership runs are historical evidence,
not the final acceptance results.

Headless checks do not establish live OpenXR runtime, swapchain/resize, desktop
visual acceptance, cross-vendor performance, or in-flight material-slot
reclamation. Readbacks are cold correctness diagnostics, never production
rendering inputs or zero-readback performance evidence. Remaining general
retirement/swapchain work belongs to Phase 5.4.

Run guides:

- [Texture streaming](../../../developer-guides/rendering/renderbench-phase53-streaming.md)
- [Material publication](../../../developer-guides/rendering/renderbench-phase53-materials.md)
- [Pipeline readiness](../../../developer-guides/rendering/renderbench-phase53-pipelines.md)
