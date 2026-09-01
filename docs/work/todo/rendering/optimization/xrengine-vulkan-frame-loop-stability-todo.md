# XRENGINE Vulkan Frame Loop Stability and High-Refresh Optimization TODO

**Last updated:** 2026-08-31
**Owner:** Rendering / Vulkan / Frame Scheduling  
**Status:** Proposed - ready for implementation  
**Primary target:** Stable desktop rendering above 100 Hz, with a 120 Hz promotion gate and a 144 Hz stretch gate  
**Secondary target:** Non-blocking OpenXR coexistence without transferring VR compositor stalls to the desktop render loop  
**Scope:** XRENGINE Vulkan frame loop, presentation, synchronization, resident rendering, resource publication, and OpenXR lifecycle.

---

## 1. Executive summary

XRENGINE can already exceed 100 Hz in clean Vulkan runs, but it does not yet hold the required deadline consistently across p95/p99 frames, repeated runs, mutation events, editor activity, and mixed desktop/OpenXR workloads.

The current problem is primarily **tail latency and architectural consolidation**, not a single slow Vulkan call or a universally GPU-bound renderer. The clean path is fast enough to demonstrate that the backend is viable, but it leaves too little headroom at 120 Hz and is vulnerable to frame-slot contention, broad resident-cache invalidation, remaining compatibility data paths, submission-time bookkeeping, background-worker interference, descriptor/resource publication bursts, and synchronous OpenXR completion waits.

The renderer should not be rewritten. The required direction is to finish the resident/data-oriented architecture already in progress so that an unchanged frame is genuinely unchanged:

- deliberate presentation and pacing policy instead of accidental burst pacing;
- exact attribution of every frame-slot, acquire, queue, and present wait;
- a sealed resident-submission fast path;
- reverse-dependency manifests and local invalidation;
- one canonical scene/material/geometry publication path;
- zero per-draw reconstruction, descriptor validation, or managed allocation on stable frames;
- one process-wide execution topology with bounded worker ownership;
- bounded publication, retirement, compilation, streaming, shadow, and probe work;
- asynchronous OpenXR submission tracking and swapchain retirement, subject to runtime-specific lifecycle validation.

This TODO is ordered to establish evidence before optimization, remove pacing illusions before changing renderer architecture, and keep correctness validation separate from performance promotion.

---

## 2. Current baseline and safety constraints

### 2.1 Measured baseline

- Clean XRENGINE Release evidence reported render p50 in the 6.959-7.716 ms range and render p95 in the 8.241-9.175 ms range.
- Vulkan-frame p50 was approximately 5.511-5.995 ms and p95 approximately 6.410-7.071 ms in the cited clean runs.
- One otherwise comparable run reported frame-slot wait p50/p95 of 4.791/7.728 ms and render p50 of 13.577 ms; an immediate rerun returned slot wait to approximately 0.019/0.029 ms and render p50 to 7.716 ms.
- XRENGINE currently prefers Mailbox presentation and falls back to FIFO; each mode must be characterized separately because they place pacing waits at different points in the frame loop.
- XRENGINE's compact production zero-readback path no longer depends on the old full-bucket CPU scan.
- The resident template path has substantially reduced stable native-template lookup work, but broad invalidation and incomplete canonical material/geometry publication remain identified work.
- XRENGINE's tracked submission gateway performs image-state, ownership, lifetime, and publication work around native submission.
- The OpenXR Vulkan path has an existing tracked synchronous eye-submit fence-wait problem with historical 70-100 ms drop evidence.

### 2.2 Measurement hypotheses

- FIFO and Mailbox can produce materially different pacing behavior; each mode must be measured independently before attributing instability to CPU or GPU rendering cost.
- The tracked submission gateway is a plausible tail-latency contributor, but it must be instrumented before being declared a dominant bottleneck.
- Intermittent frame-slot waits may arise from GPU overrun, swapchain pressure, compositor/driver behavior, worker interference, queue contention, or another producer delaying timeline progress. The current evidence does not isolate one universal cause.

### 2.3 OpenXR design constraints

The OpenXR workstream uses a timeline-semaphore or fence-ring submission tracker, immediate return after eye submission, deferred resource recycling, and tombstoned swapchain retirement. These are implementation targets, but several details must remain validation gates rather than assumptions:

- Do not assume every OpenXR runtime permits `xrReleaseSwapchainImage` before application GPU completion.
- Do not assume a runtime automatically imports or observes an application timeline semaphore unless the graphics binding and runtime contract explicitly support that behavior.
- A fence ring may be required when a timeline semaphore cannot serve as the completion authority.
- Old OpenXR swapchain resources must be safe with respect to both application GPU completion and runtime ownership before destruction.
- Recommended view dimensions and session-state events are inputs to a recreation policy, not proof that a specific runtime supports zero-gap concurrent old/new swapchain replacement.

The OpenXR phases below therefore require runtime-specific verification on Monado and at least one hardware runtime before promotion.

---

## 3. Frame-budget problem statement

| Refresh target | Hard frame deadline | Recommended engineering target |
|---|---:|---:|
| 100 Hz | 10.000 ms | 8.5-9.0 ms |
| 120 Hz | 8.333 ms | 7.1-7.5 ms |
| 144 Hz | 6.944 ms | 5.9-6.25 ms |
| 165 Hz | 6.061 ms | Characterization only until 144 Hz is stable |
| 200 Hz | 5.000 ms | Long-term desktop target |

At 120 Hz, an 8.241 ms p95 leaves approximately 0.092 ms before the deadline. That is not sufficient engineering margin for OS scheduling, driver work, descriptor publication, a resident-template miss, streaming, a queue delay, or a minor GPU fluctuation.

The primary success metric is therefore **deadline reliability**, not average FPS. All workstreams must report p50, p95, p99, maximum, run-to-run spread, and the exact count and cause of missed deadlines.

---

## 4. Goals

- [ ] Sustain the 100 Hz hard deadline through p99 in all required desktop scenarios.
- [ ] Pass the 120 Hz promotion gate with p99 below 8.333 ms and an engineering target of 7.1-7.5 ms.
- [ ] Reach the 144 Hz stretch gate with p99 below 6.944 ms and an engineering target of 5.9-6.25 ms.
- [ ] Make a stable frame perform no per-draw material reconstruction, descriptor compatibility validation, command-signature reconstruction, or managed hot-path allocation.
- [ ] Make stable preparation and publication scale with dirty owners/ranges rather than visible draw count.
- [ ] Make every wait longer than 0.1 ms attributable to a specific queue, timeline, image, slot, limiter, runtime, lock, or background work owner.
- [ ] Keep normal desktop rendering free of current-frame readbacks, mappings, host fence waits, and `vkDeviceWaitIdle`.
- [ ] Preserve exact rendering semantics; selected zero-readback strategies must not silently fall back to CPU enumeration or diagnostic readback paths.
- [ ] Keep validation, RenderDoc, screenshots, readbacks, verbose tracing, and profiler dump generation outside performance totals.
- [ ] Remove the recurring synchronous OpenXR eye-submit wait from the render hot path while preserving runtime and Vulkan lifetime correctness.

---

## 5. Non-goals and rejected first responses

- [ ] Do not add more command-recording threads before proving command recording is the active bottleneck.
- [ ] Do not permanently increase frames in flight merely to hide frame-slot waits; test it only as a latency/performance A/B.
- [ ] Do not reintroduce CPU count readbacks, full bucket scans, active-list mapping, or current-frame diagnostic waits.
- [ ] Do not re-enable the historically harmful software-occlusion or Hi-Z paths without a new measured implementation and crossover proof.
- [ ] Do not blame Vulkan or C# as a category; the clean Vulkan-frame evidence already proves the backend can be fast.
- [ ] Do not micro-optimize `vkQueuePresentKHR` before separating native present time from pacing, acquire, queue admission, and compositor behavior.
- [ ] Do not compare performance captures collected under different present modes, refresh rates, feature stacks, or power states and treat the difference as renderer efficiency.
- [ ] Do not create another scene database, cache hierarchy, or identity allocator. Finish and consolidate the canonical resident database.
- [ ] Do not let a performance fallback silently alter visibility, material, transparency, shadow, or submission semantics.
- [ ] Do not make OpenXR release/destruction assumptions without runtime-specific lifecycle evidence.

---

## 6. Workstream sequence and dependencies

| Phase | Workstream | Depends on | Promotion effect |
|---|---|---|---|
| 0 | Freeze baseline and measurement contract | None | Establishes trustworthy evidence |
| 1 | Presentation profiles and deliberate pacing | Phase 0 | Removes FIFO/Mailbox comparison ambiguity |
| 2 | Wait attribution and frame-slot diagnosis | Phases 0-1 | Identifies the direct source of cadence stalls |
| 3 | Sealed submission fast path | Phase 2 instrumentation | Reduces normal-frame submission CPU variance |
| 4 | Reverse dependencies and granular invalidation | Phase 0 mutation baselines | Removes broad cold-frame bursts |
| 5 | Complete canonical resident publication | Phases 3-4 foundations | Removes compatibility and draw-centric preparation |
| 6 | Scheduler, allocation, and thread topology closure | Phase 0 topology baseline | Removes host-side variance and oversubscription |
| 7 | Render graph and GPU pass stabilization | Phase 0 GPU attribution | Reduces GPU/bandwidth and barrier tails |
| 8 | Materials, descriptors, uploads, and pipelines | Phases 4-5 publication model | Bounds mutation and streaming tails |
| 9 | Resource retirement and swapchain lifecycle | Phases 2-3 completion authorities | Prevents destruction/recreation spikes |
| 10 | OpenXR asynchronous submission and retirement | Phases 2-3 lifetime model | Removes VR-controlled render-thread stalls |
| 11 | Validation matrix and promotion | All required phases | Final acceptance |

Phases 3, 4, 6, 7, and 8 may proceed in parallel after Phase 0 instrumentation is frozen, but promotion must use one frozen integrated revision.

---

# Phase 0 - Freeze the baseline and measurement contract

## Objective

Create a matched, repeatable, low-noise baseline that can distinguish presentation policy, CPU preparation, native Vulkan work, GPU work, and environmental interference.

## Implementation checklist

- [ ] Freeze the exact XRENGINE revision, dependency manifests, build configuration, driver, OS build, GPU, CPU, memory, monitor, refresh rate, power plan, window mode, resolution, render scale, scene, camera, submission strategy, and feature stack.
- [ ] Add the selected present mode, swapchain image count, frame-slot count, frame-generation state, validation state, render-target mode, and active OpenXR runtime to every benchmark manifest.
- [ ] Use a dedicated `ReleaseBenchmark` or equivalent build with validation, verbose Vulkan logging, profiler UI graphs, screenshots, readbacks, RenderDoc injection, and frame capture disabled.
- [ ] Warm shader compilation, pipeline caches, material tables, resident templates, imports, streaming, and swapchain resources before the measured interval.
- [ ] Use at least three 60-second repetitions for every promotion comparison.
- [ ] Reject a comparison when run-to-run p95 range exceeds 7.5% until the environmental cause is identified.
- [ ] Record p50, p95, p99, maximum, standard deviation, missed-deadline count, and consecutive missed-deadline streak for whole frame, render frame, Vulkan frame, GPU frame, and every major CPU stage.
- [ ] Record actual presentation intervals rather than inferring display cadence from CPU FPS.
- [ ] Add a frame-interval histogram and periodicity analysis to expose every-N-frame spikes.
- [ ] Record managed allocation bytes, native allocation calls, command-buffer allocation/reset counts, descriptor writes, resident hits/misses, broad invalidations, queue submissions, readback bytes, mappings, and forced waits.
- [ ] Capture matched CPU-direct, GPU indirect zero-readback, and GPU meshlet zero-readback baselines.
- [ ] Capture static-camera and continuously moving-camera baselines.
- [ ] Keep OpenXR baselines separate from desktop-only promotion captures.
- [ ] Run the complete desktop benchmark matrix with the XRENGINE `Stable` FIFO profile.
- [ ] Run the same benchmark matrix with the XRENGINE `LowLatency` Mailbox profile where supported.
- [ ] Compare only runs with matched scene, camera, resolution, render scale, feature stack, refresh rate, and power state.

## Required baseline report

- [ ] Whole-frame p50/p95/p99/max.
- [ ] Render p50/p95/p99/max.
- [ ] Vulkan-frame p50/p95/p99/max.
- [ ] GPU p50/p95/p99/max.
- [ ] Frame-slot, acquired-image, acquire, queue-admission, native-submit, present-call, and present-interval distributions.
- [ ] Resident hit/miss/replacement/invalidation counts by reason.
- [ ] Submission gateway substage costs.
- [ ] Worker-domain counts, wakeups, queue ages, and CPU migrations where available.
- [ ] Streaming, descriptor publication, retirement, pipeline compilation, shadow, probe, and secondary-window activity during every outlier frame.

## Exit criteria

- [ ] The baseline is reproducible within the accepted run-to-run range.
- [ ] FIFO and Mailbox behavior are characterized separately.
- [ ] Every reported timing has a precise stage definition.
- [ ] Performance captures contain no validation, readback, screenshot, or verbose diagnostic contamination.

---

# Phase 1 - Standardize presentation profiles and deliberate pacing

## Objective

Make presentation policy explicit and prevent Mailbox/uncapped burst behavior from masquerading as renderer instability or hiding true CPU/GPU cost.

## Presentation profiles

- [ ] Add a first-class `Stable` profile: FIFO, refresh-paced, bounded latency, no frame generation.
- [ ] Add a `LowLatency` profile: Mailbox when supported, maximum one queued application frame, deliberate target-rate control.
- [ ] Add an `Uncapped` profile: Immediate when supported, no stability guarantee, intended for headroom diagnosis.
- [ ] Add a `FrameGeneration` profile: separate Streamline/DLSS-compatible present policy and separate promotion evidence.
- [ ] Make `Stable` the default profile for stability-oriented editor/runtime configurations.
- [ ] Preserve an explicit override for diagnostics and existing user settings.

## Pacing implementation

- [ ] Add a target-rate limiter for Mailbox instead of allowing the CPU to run until swapchain or frame-slot pressure forces a burst wait.
- [ ] Use a hybrid sleep/spin limiter only for the final small portion of the interval; never busy-spin for the complete frame budget.
- [ ] Add a `frames_ahead` counter and enforce a configured bound of one or two.
- [ ] Move the only unavoidable frame-slot reuse wait to the earliest legal frame-authority point.
- [ ] Do not perform visibility collection or package construction after the target slot is already known to be unavailable.
- [ ] When game and render authority are separate, publish slot readiness early and allow safe non-render gameplay work to continue.
- [ ] Pace secondary ImGui platform-window swapchains independently so a hidden, occluded, or low-refresh tool window cannot stall the primary viewport.
- [ ] Coalesce rapid resize events and apply only the newest valid extent at one frame-authority boundary.
- [ ] Disable DLSS frame generation and other interop layers for native renderer baselines.

## Optional capability tiers

- [ ] Capability-probe `VK_KHR_present_id` and `VK_KHR_present_wait` for actual presentation completion observation or low-latency waiting.
- [ ] Capability-probe `VK_GOOGLE_display_timing` where available for compositor-visible cadence diagnostics.
- [ ] Keep all optional extension paths behind capability and correctness tests; preserve a portable fallback.

## Telemetry

- [ ] Report selected presentation profile and resolved Vulkan present mode.
- [ ] Report target interval, limiter sleep, limiter spin, queue depth, frames ahead, acquire duration, and actual presentation interval.
- [ ] Separate native `vkQueuePresentKHR` duration from the time the application is paced by image availability or completion.

## Exit criteria

- [ ] FIFO and Mailbox baselines are available for the same XRENGINE revision and benchmark matrix.
- [ ] Mailbox no longer produces uncontrolled CPU bursts.
- [ ] Actual present-interval p99 meets the selected profile's deadline when the GPU has sufficient headroom.
- [ ] Secondary platform windows cannot measurably perturb the primary viewport in the primary stability benchmark.

---

# Phase 2 - Attribute frame-slot and queue contention exactly

## Objective

Convert the current intermittent frame-slot stall from a symptom into an exact causal record.

## Wait taxonomy

- [ ] Time frame-slot timeline reuse waits separately for graphics, compute, transfer, present, OpenXR, and external interop authorities.
- [ ] Time acquired-swapchain-image completion waits separately from frame-slot waits.
- [ ] Time `vkAcquireNextImageKHR` separately from any preceding slot wait.
- [ ] Time native queue admission separately from `vkQueueSubmit`/`vkQueueSubmit2`.
- [ ] Time native present separately from display/compositor pacing.
- [ ] Time explicit frame-limiter sleep/spin separately.
- [ ] Time command-pool and descriptor-arena reuse waits separately.
- [ ] Time lock acquisition for submission state, native queue lease, resource lifetime, descriptor publication, retirement, upload, and pipeline compilation.

## Causal payload

For every wait above 0.1 ms, record without successful-frame string formatting:

- [ ] frame ID, render frame ID, and output authority;
- [ ] frame slot and swapchain image index;
- [ ] waited semaphore/fence identity and target value;
- [ ] current completed value and age in frames;
- [ ] queue family and queue type;
- [ ] last producer that advanced the value;
- [ ] pending command-buffer count and submission serial;
- [ ] current frames ahead and swapchain image availability;
- [ ] concurrent upload, compiler, streaming, descriptor, retirement, shadow, probe, UI, and OpenXR activity;
- [ ] CPU core and migration information where the platform exposes it cheaply;
- [ ] GPU clock/power-state information when available through existing telemetry.

## Diagnostic A/Bs

- [ ] Run two-frame-slot and three-frame-slot A/B captures.
- [ ] Treat three slots as a diagnostic only until p99 benefit, input latency, memory cost, and queue depth are reported together.
- [ ] Run Mailbox with and without the limiter.
- [ ] Run FIFO with identical rendering work.
- [ ] Run with background compiler disabled, streaming frozen, secondary windows disabled, and editor diagnostics hidden, one variable at a time.
- [ ] Run GPU-headroom captures at reduced resolution to determine whether waits are CPU/pacing or GPU completion driven.

## Exit criteria

- [ ] Every recurring slot wait has an exact owner and timeline cause.
- [ ] An uncapped GPU-headroom run has approximately zero slot-wait p95.
- [ ] No wait is attributed to a broad lifecycle scope such as "Present" without a lower-level causal stage.
- [ ] The team can state whether the intermittent 4-7 ms waits are primarily GPU, swapchain, compositor, submission-order, worker, or background-work driven on each test system.

---

# Phase 3 - Implement a sealed resident-submission fast path

## Objective

Validate unchanged resident command artifacts once, then make normal-frame submission proportional to changed generations rather than recorded subresources and object graphs.

## Instrument the existing gateway first

- [ ] Split `SubmitToQueueTrackedWithDisposition` into allocation-free timers for:
  - [ ] image-entry-state contract validation;
  - [ ] queue-ownership validation;
  - [ ] lifetime-pin acquisition;
  - [ ] submission-state serialization;
  - [ ] native queue admission;
  - [ ] native `vkQueueSubmit`/`vkQueueSubmit2`;
  - [ ] lifetime publication;
  - [ ] image-layout publication;
  - [ ] diagnostic publication;
  - [ ] cleanup and pin release.
- [ ] Record p50/p95/p99 and counts for each substage.
- [ ] Confirm whether the gateway is a real tail contributor before deleting or weakening checks.

## Sealed contract

- [ ] Add a `SealedSubmissionContract` owned by each reusable resident command artifact.
- [ ] Validate complete image-transition, queue-family, resource-generation, render-scope, nested-artifact, and native-lifetime requirements when the artifact is recorded or replaced.
- [ ] Store a compact generation vector and immutable dependency manifest with the artifact.
- [ ] On a clean stable hit, compare only the compact generation vector and artifact generation.
- [ ] Bypass subresource dictionary scans and full queue-ownership recomputation when the sealed contract is still valid.
- [ ] Use full validation only when the artifact is cold, dirty, instrumented, running a correctness build, or has a changed dependency generation.
- [ ] Keep validation-layer runs and sampled full-contract parity checks to prove the fast path.

## Data structures and locking

- [ ] Replace common submit-time dictionaries with flat arrays keyed by stable resource indices.
- [ ] Cache resolved queue-family assumptions in the sealed contract.
- [ ] Batch lifetime pins per dependency manifest rather than reacquiring individual dependencies per draw or subresource.
- [ ] Keep submission-state serialization separate from the native queue lease.
- [ ] Hold the native queue lock only across the native queue call sequence.
- [ ] Never hold the queue gate during diagnostics, logging, descriptor updates, retirement scans, host waits, or post-submit publication.
- [ ] Aggregate normal graphics work into one coarse submission per output where practical.
- [ ] Batch transfer and compute timeline waits into that submission.
- [ ] Avoid many tiny submissions.

## Diagnostics

- [ ] Move successful-frame diagnostics to a sampled/deferred sidecar.
- [ ] Format detailed strings only on failure or explicitly sampled frames.
- [ ] Poll diagnostic readbacks several frames later and never wait for the current frame.

## Exit criteria

- [ ] Sealed unchanged submission CPU p95 is below approximately 0.25 ms on the promotion system.
- [ ] Stable submission performs no dictionary iteration over image subresources.
- [ ] Stable submission performs no per-draw lifetime-pin acquisition.
- [ ] Validation and sampled full-path parity report no correctness divergence.
- [ ] Device loss, resize, shader reload, resource replacement, and queue-family mutation correctly invalidate or replace the sealed contract.

---

# Phase 4 - Add reverse-dependency manifests and granular invalidation

## Objective

Stop local material, texture, geometry, pipeline, or pass mutations from conservatively clearing unrelated resident templates.

## Dependency model

- [ ] Add compact reverse dependency arrays for:
  - [ ] material -> resident draws;
  - [ ] texture/material-table row -> materials;
  - [ ] geometry -> resident draws;
  - [ ] pipeline layout -> resident variants;
  - [ ] graphics/compute pipeline -> resident variants;
  - [ ] descriptor layout/table generation -> dependent variants;
  - [ ] render-pass/output generation -> command artifacts;
  - [ ] shader generation -> pipelines, materials, and resident variants;
  - [ ] shadow/probe publication -> only dependent passes or material rows.
- [ ] Give topology and content independent generations.
- [ ] Give frame, view, pass, material, object, instance, texture resource, sampler, descriptor layout, pipeline layout, and shader data independent version domains.
- [ ] Emit dirty ranges at the mutation point rather than discovering changes through a full scan.
- [ ] Preserve tombstones and generation-safe slot reuse until all consumers acknowledge retirement.

## Invalidation behavior

- [ ] Replace table-wide clearing for non-draw structural changes with dependency-directed invalidation.
- [ ] Keep broad invalidation only as a counted correctness fallback when a dependency manifest is unavailable or inconsistent.
- [ ] Track every broad clear by exact reason, owner domain, and affected entry count.
- [ ] Keep stable numeric bins for pipeline, material, geometry, pass, transparency class, and submission strategy.
- [ ] Update bin membership only when topology changes.
- [ ] Do not re-sort stable bins because camera, transform, animation, material value, or view data changed.

## Mutation tests

- [ ] Change one scalar material value and invalidate only the material data range.
- [ ] Change one texture binding and invalidate only dependent material rows/draw variants.
- [ ] Replace one geometry resource and invalidate only dependent resident entries.
- [ ] Reload one shader and invalidate only dependent pipelines/materials/variants.
- [ ] Add/remove/re-add one draw and prove generation-safe slot reuse.
- [ ] Move the camera and prove zero topology invalidation.
- [ ] Move an object and prove data-only publication.
- [ ] Update one shadow cascade and prove unrelated resident entries remain warm.

## Exit criteria

- [ ] Local mutation produces zero resident-table-wide clears.
- [ ] Camera and transform changes produce zero resident structural invalidation.
- [ ] Stable bin membership remains unchanged for data-only updates.
- [ ] Every invalidated resident entry is reachable through a recorded reverse dependency.
- [ ] Broad fallback invalidation is zero in all promotion scenarios.

---

# Phase 5 - Complete canonical resident publication and remove compatibility paths

## Objective

Make the canonical resident database and immutable backend-ready package the only normal-frame data source consumed by Vulkan.

## Complete canonical records

- [ ] Publish real packed material constant words through the canonical material database.
- [ ] Publish texture and sampler bindings through the same material records.
- [ ] Publish material-layout rows and shading-kernel rows.
- [ ] Publish advanced geometry buffer identities, offsets, index/vertex formats, native generations, and lifetime references.
- [ ] Publish compact frame, view, pass, object, instance, skinning, and visibility records in structure-of-arrays form.
- [ ] Carry exact dirty owner/range information in the frame package.
- [ ] Carry the resolved submission strategy and instrumentation mode in the sealed package.
- [ ] Carry stable resident handles directly from producer to Vulkan consumer.

## Unify strategies

- [ ] Make CPU direct, GPU indirect zero-readback, GPU indirect instrumented, GPU meshlet zero-readback, and GPU meshlet instrumented consume the same canonical draw, geometry, material, and view handles.
- [ ] Keep instrumentation as an opt-in sidecar after the production stream is constructed.
- [ ] Do not maintain a second scene database, culling identity model, or material hierarchy for diagnostics.
- [ ] Preserve the compact no-readback indirect-count production contract.
- [ ] Never silently route an unsupported zero-readback pass through CPU enumeration.
- [ ] Keep unsupported transparency/custom shader paths explicit, visible, and counted.
- [ ] Add measured scene-size crossover thresholds for CPU direct versus indirect/meshlet submission.

## Remove transitional paths after parity

- [ ] Remove the legacy `GPUScene` mirror after all strategy publication parity passes.
- [ ] Remove temporary legacy mesh selections from `BackendReadyFramePackage`.
- [ ] Delete `VulkanPreparedMeshOperationCohort` and `VulkanPreparedMeshIngress` after desktop, shadow, UI, explicit target, OpenXR, resize, device-loss, and failure-path parity.
- [ ] Make the immutable backend-ready package the only normal-frame Vulkan input.
- [ ] Remove warm-frame hash-table lookups and structural fingerprint construction.
- [ ] Restrict full structural comparison to cold create/replace paths.
- [ ] Precompute binding schemas, descriptor plans, resource plans, and immutable dependency manifests when resident templates are created or replaced.
- [ ] Reject or defer a complete cohort on capacity/publication failure; never submit a valid-looking prefix.

## Stable-frame zero-work contract

On a stable frame, require zero:

- [ ] binding snapshot construction;
- [ ] program dictionary traversal;
- [ ] reflected auto-uniform member lookup;
- [ ] per-draw descriptor compatibility validation;
- [ ] material serialization;
- [ ] command-signature reconstruction;
- [ ] draw-oriented compatibility record comparison;
- [ ] resident template structural comparison;
- [ ] managed allocation in package build, publication, preparation, recording, submission, or merge.

## Exit criteria

- [ ] Stable preparation scales with dirty ranges rather than visible draw count.
- [ ] All submission strategies use the same canonical identities and produce visual parity.
- [ ] Legacy mirror/cohort/compatibility paths are removed from qualifying production frames.
- [ ] Stable resident hits require direct indexed access and compact generation checks only.
- [ ] A matched post-conversion p50/p95/p99 matrix demonstrates a material improvement over the Phase 0 baseline.

---

# Phase 6 - Close scheduler, allocation, and thread-topology ownership

## Objective

Remove host scheduling variance, periodic wakeups, oversubscription, shared allocator contention, and warmed-frame allocations.

## One execution topology

- [ ] Represent every persistent execution domain in `EngineExecutionTopology`:
  - [ ] general jobs;
  - [ ] renderer-neutral render jobs;
  - [ ] Vulkan command preparation/recording;
  - [ ] OpenXR eye preparation/recording;
  - [ ] pipeline compilation;
  - [ ] texture/upload workers;
  - [ ] remote/editor jobs;
  - [ ] deferred jobs;
  - [ ] any retained compiler, streaming, physics, or telemetry loops.
- [ ] Remove independently chosen worker-count defaults.
- [ ] Report requested and resolved worker counts for every domain.
- [ ] Reject configurations that oversubscribe the processor after foreground reservations.
- [ ] Resolve topology differently for homogeneous desktops and heterogeneous P-core/E-core systems.
- [ ] Reserve capacity for render authority, game/update, window/event handling, audio, and driver work.

## Work scheduling

- [ ] Preserve signal-only idle waits; never reintroduce periodic polling wakes.
- [ ] Use a few coarse preparation/recording tasks rather than one task per draw or material.
- [ ] Establish a minimum estimated work threshold before dispatching to another worker.
- [ ] Execute small batches inline.
- [ ] Let the render thread participate in the render work domain while it would otherwise wait.
- [ ] Keep worker completion order independent from GPU execution order.
- [ ] Give each render lane lane-local scratch arrays, command arenas, descriptor-plan storage, and temporary hashes.
- [ ] Avoid shared allocator traffic between lanes.
- [ ] Use bounded, generation-checked pools for batches, items, dependencies, command arenas, and lane attachments.
- [ ] Reject or defer complete work on queue-capacity failure; never submit partial output.

## Allocation and hot-path code

- [ ] Make warmed build, dispatch, execute, merge, submission, and completion paths zero managed allocation.
- [ ] Replace hot-path LINQ, iterators, closures, delegate captures, and temporary collections with arrays, spans, and explicit loops.
- [ ] Avoid strings and interpolated diagnostics on successful per-frame paths.
- [ ] Avoid `ConcurrentDictionary` and general-purpose dictionaries in draw-frequency operations.
- [ ] Pre-size arrays and preserve capacity across frames.
- [ ] Track managed allocation by stage and worker lane.
- [ ] Track GC pauses, pinned-object counts, and allocation spikes, but do not use forced GC or no-GC regions as a substitute for eliminating allocations.

## Background interference

- [ ] Bound pipeline-compiler concurrency through the same topology.
- [ ] Throttle editor-only background work while the primary viewport targets high refresh.
- [ ] Report worker wake count, queue age, work stealing, migrations, oversubscription, and lock wait separately from useful work.
- [ ] Apply CPU affinity or elevated priority only after a matched A/B proves a benefit on the target processor.

## Exit criteria

- [ ] No warmed promotion frame allocates managed hot-path bytes.
- [ ] No renderer-related persistent thread exists outside the execution-topology report.
- [ ] Idle workers produce no periodic wake cadence.
- [ ] No unexplained worker or render-thread lock wait exceeds 0.1 ms in the stability capture.
- [ ] Heterogeneous and homogeneous systems both resolve a non-oversubscribed topology.

---

# Phase 7 - Stabilize render-graph and GPU pass cost

## Objective

Reduce GPU deadline pressure and eliminate optional-pass, copy, barrier, shadow, probe, motion-vector, and occlusion tails.

## Render graph

- [ ] Preserve the latest conditional forward contact depth/normal prepass behavior.
- [ ] Run the prepass only when a visible material and active feature consume its outputs.
- [ ] Remove remaining G-buffer restore copies that can be represented by graph dependencies and layout transitions.
- [ ] Cache the compiled render graph while topology and resource descriptions are unchanged.
- [ ] Recompile only the dirty graph region after a local mutation.
- [ ] Batch barriers by source/destination stage and access class.
- [ ] Replace broad `AllCommands` barriers with precise stage/access masks.
- [ ] Coalesce adjacent barriers for the same image and compatible subresource ranges.
- [ ] Track barrier count, image-transition count, queue-ownership transfer count, and full-resolution copy bytes per frame.

## Pass consolidation and bandwidth

- [ ] Merge compatible full-screen compute passes when it reduces bandwidth, barriers, and submissions.
- [ ] Evaluate combining AO filtering, lighting preparation, fog/atmosphere, and selected postprocess work when intermediate persistence is unnecessary.
- [ ] Reduce full-resolution intermediate images.
- [ ] Use transient attachment aliasing for non-overlapping graph resources.
- [ ] Use lazily allocated attachment memory where supported and appropriate.
- [ ] Render motion vectors in an existing base pass where feasible.
- [ ] Skip motion-vector work when no active temporal consumer requires it.

## Shadows, probes, and maintenance

- [ ] Update only dirty directional-shadow cascades.
- [ ] Cache static shadow casters and static shadow command artifacts.
- [ ] Preserve coherent shadow generations during camera motion.
- [ ] Stagger reflection probes, environment captures, and noncritical shadow refreshes across frames.
- [ ] Give every asynchronous visual-maintenance system explicit CPU and GPU item/time budgets.
- [ ] Record when a budget defers work and whether visual fallback content is reused.

## Occlusion and GPU-driven work

- [ ] Keep the historically harmful CPU software-occlusion path disabled unless a new implementation demonstrates a positive crossover.
- [ ] Do not re-enable the old Hi-Z path merely because it is GPU based.
- [ ] Require any replacement Hi-Z path to:
  - [ ] use a reverse-Z-correct reduction;
  - [ ] build the pyramid in one or a few GPU dispatches;
  - [ ] perform no per-mip host work;
  - [ ] use visibility hysteresis;
  - [ ] conservatively bypass after camera cuts or invalid history;
  - [ ] keep current-frame visibility, counts, and overflow on the GPU;
  - [ ] use delayed fence/timeline-polled staging only for optional diagnostics.
- [ ] Sort CPU-direct draws by pipeline and descriptor state when CPU direct is selected.
- [ ] Use indirect-count bins for compatible opaque and masked draws.
- [ ] Keep exact transparency on an ordering-correct explicit path.

## Backend A/Bs

- [ ] Benchmark dynamic rendering against legacy render passes on NVIDIA, AMD, and Intel where available.
- [ ] Isolate primary and secondary ImGui viewport GPU cost.
- [ ] Report GPU p50/p95/p99 for every major pass and full-frame GPU completion.

## Exit criteria

- [ ] No optional pass causes unreported full-resolution copies or broad barriers.
- [ ] Stable graph compilation cost is approximately zero.
- [ ] Shadow/probe maintenance stays within configured budgets.
- [ ] No production culling path performs current-frame CPU readback.
- [ ] GPU p99 leaves the engineering margin required by the selected promotion target.

---

# Phase 8 - Stabilize materials, descriptors, uploads, and pipelines

## Objective

Adopt stable material-row semantics, dirty-range publication, bounded descriptor updates, asynchronous upload behavior, and non-blocking pipeline creation throughout the canonical path.

## Materials and descriptors

- [ ] Adopt stable material-table rows throughout the canonical XRENGINE path.
- [ ] Keep a CPU mirror only where it enables exact dirty-range comparison and safe table growth.
- [ ] Upload only the changed material range.
- [ ] Separate material-value, texture-resource, sampler, descriptor-layout, pipeline-layout, shader, and native-resource generations.
- [ ] Preserve the Phase 3 correction that removed global descriptor-write count from resident-template identity.
- [ ] Batch bindless descriptor updates into compact dirty ranges.
- [ ] Avoid full descriptor-table refresh when one texture or material changes.
- [ ] Prevalidate descriptor compatibility when a resident variant is created or replaced.
- [ ] Use a persistent mapped frame/view/pass data ring.
- [ ] Prefer stable table indices, dynamic offsets, or small device-address/index push constants over per-draw descriptor-set mutation.
- [ ] Grow material and descriptor tables with spare capacity before they are full.
- [ ] Stage growth asynchronously and publish only after copy completion.
- [ ] Permit a synchronous growth wait only as a visible, counted emergency path.

## Texture and buffer uploads

- [ ] Preserve the existing asynchronous texture-upload architecture.
- [ ] Coalesce many small uploads into a small number of transfer submissions.
- [ ] Keep per-frame staging arenas persistently mapped.
- [ ] Use overflow allocations only for exceptional demand.
- [ ] Track staging high-water marks, overflow bytes/count, queued jobs, bytes in flight, and oldest job age.
- [ ] Split upload timing into CPU preparation, staging copy, Vulkan allocation, transfer recording, transfer execution, descriptor publication, and old-resource retirement.
- [ ] Give descriptor/resource publication separate per-frame item and time budgets from GPU copying.
- [ ] Publish new texture resources only at a deterministic frame boundary.
- [ ] Update only dependent material rows or descriptor records.
- [ ] Keep demotions lower priority than visible promotions unless memory pressure is critical.
- [ ] Batch old-image retirement rather than destroying immediately after publication.
- [ ] Run a dedicated streaming-stress gate rather than mixing streaming frames into the generic steady-state benchmark.

## Pipelines and shaders

- [ ] Precompile common pipelines during warmup.
- [ ] Persist `VkPipelineCache` data keyed by GPU, driver, engine revision, render-target mode, and shader fingerprint.
- [ ] Continue using graphics-pipeline-library support where measured to help.
- [ ] Never compile a graphics pipeline synchronously on the render thread during steady-state gameplay.
- [ ] Bound background shader/pipeline compilation so it cannot displace render authority.
- [ ] Keep shader reload, asset import, meshlet cooking, disk hashing, and file-system discovery outside measured steady rendering.
- [ ] Treat descriptor buffers as an optional measured tier, not a prerequisite for fixing the current architecture.

## Exit criteria

- [ ] A one-value material mutation uploads only the affected range.
- [ ] A one-texture mutation updates only dependent table/descriptor records.
- [ ] Streaming bursts remain inside publication and retirement budgets.
- [ ] Warming eliminates synchronous pipeline creation from promotion captures.
- [ ] Stable materials and descriptors produce zero per-draw validation or writes.

---

# Phase 9 - Bound resource retirement, command-pool reuse, and swapchain lifecycle

Lifecycle implementation completed 2026-08-31 through master Phase 5.4. The diagnostic acceptance cohort
uses a retirement-stage p99 target below 0.5 ms: cumulative resize/restore p99
is 0.084 ms across 5,881 frames, and four normal/reversed streaming children
measure 0.052–0.306 ms. Individual tail durations and histogram overflow are
retained in the reports. Live desktop and detached ImGui lifecycle validation
reports zero normal-frame device-idle calls and native validation errors, with
zero warmed command allocations. This is scoped lifecycle acceptance, not
portable performance promotion. See the [implementation, limitations, and
validation evidence](../../../investigations/rendering/vulkan-phase54-retirement-and-swapchain-lifecycle.md).

Follow-up: this cohort used discrete resizes. Actual held-drag relayout exposed
a regression that is now repaired and validated with real width/height drags
in `DefaultRenderPipeline`: fresh scene/compute/UI recording, no package
rejection, zero device-idle calls, and zero native validation errors. The
final rebuilt cohort has retirement p99 0.087 ms. Evidence is tracked in the
[live resize investigation](../../../investigations/rendering/vulkan-live-window-resize-relayout.md).

The first mouse-release continuity attempt made the frame attempt latch interactive
mapping, and an explicit resize-release handoff retains the last complete held
image across swapchain recreation until an authored scene/ImGui successor is
presented. Empty, clear-only, overlay-only, stale, and superseded successor
frames are refused. Two real held drags keep both ImGui and the scene visible
through release with no full-surface clear, validation error, or device-idle
call in the sampled acceptance interval. User testing later showed the retained
image could remain frozen for 17 to 53 seconds while the pre-acquire handoff gate
also suppressed ImGui and FPS updates. Advanced can accumulate overlays when its
scene producer fails, and a live Debug Opaque swap can terminal-pause on a
texture upload prepared before renderer ownership is published. Phase 9 live
acceptance remains open pending the master Phase 5.4 repair gates.

## Objective

Ensure destruction, retirement, command-pool reuse, resize, and swapchain recreation cannot create unbounded render-thread spikes.

## Resource retirement

- [x] Meter destruction by resource class: images, views, buffers, pipelines, framebuffers, samplers, descriptor objects, command artifacts, and callbacks.
- [x] Add ordinary per-frame destruction caps and a high-water policy that can temporarily drop the cap when backlog itself threatens memory stability.
- [x] Destroy resources outside global retirement locks.
- [x] Retire only after all relevant queue timeline values or fences complete.
- [x] Report backlog, oldest retirement age, destroyed count, deferred count, and uncapped-drain activation.

## Command pools and command buffers

- [x] Keep one command pool per recording lane and frame slot.
- [x] Reset a pool only after its slot's completion authority proves prior GPU use is complete.
- [x] Allocate no command buffers in warmed steady state.
- [x] Keep command-buffer retirement tied to exact recorded/submitted generations.
- [x] Preserve the separate ImGui overlay command-buffer architecture so dynamic UI does not invalidate reusable scene primaries.

## Desktop swapchain lifecycle

- [x] Preserve asynchronous swapchain-generation retirement; do not replace it with normal-frame `vkDeviceWaitIdle`.
- [x] Coalesce resize requests and create one replacement generation from the newest valid extent.
- [x] Tombstone old views, framebuffers, semaphores, render passes, depth resources, and command artifacts with exact graphics/present completion markers.
- [x] Keep old and new generations alive concurrently only within a bounded retirement limit.
- [x] Inherit the strongest old completion authority when a replacement image index can reuse mapped frame-data storage.
- [x] Report retirement-queue pressure and recreation deferrals.
- [x] Pace and retire secondary ImGui platform-window swapchains independently.

## Exit criteria

- [x] No normal resize path calls `vkDeviceWaitIdle`.
- [x] No warmed frame allocates or frees command buffers.
- [x] Retirement p99 remains below the stage budget during streaming and resize stress.
- [x] Swapchain recreation cannot strand mapped frame-data or command-artifact ownership.
- [ ] Repeated resize/minimize/restore produces no black frames, stale output, overlay accumulation, presentation freezes, validation errors, or broad unbounded stalls.

---

# Phase 10 - Decouple OpenXR submission, completion, and swapchain retirement

## Objective

Remove the synchronous OpenXR eye-submit fence wait from the render hot path while preserving application GPU completion, OpenXR swapchain ownership, command-pool reuse, and shutdown correctness.

## Phase 10A - Map the current lifetime contract

- [ ] Identify every resource whose safety currently depends on the immediate post-submit wait:
  - [ ] eye command buffers and command pools;
  - [ ] frame-data and descriptor arenas;
  - [ ] staging/upload ranges;
  - [ ] Vulkan image views/framebuffers for OpenXR images;
  - [ ] resident-template and native-resource pins;
  - [ ] transient render-graph resources;
  - [ ] OpenXR swapchain image acquisition/release state.
- [ ] Add counters for eye queue-submit time, eye completion wait time, forced-wait count, in-flight eye frame count, oldest in-flight age, swapchain image reuse age, and missed XR deadline count.
- [ ] Verify the lifecycle contract for Monado and at least one hardware runtime.
- [ ] Explicitly determine whether `xrReleaseSwapchainImage` may occur before application GPU completion for each tested runtime/path.
- [ ] Determine whether a Vulkan timeline semaphore can serve as the completion authority; otherwise use a bounded fence ring.
- [ ] Document the fallback when a runtime requires synchronous completion before image release.

## Phase 10B - `OpenXrVulkanSubmissionTracker`

- [ ] Add a bounded tracker keyed by engine frame ID, predicted display time, OpenXR swapchain/image, command buffers/pools, frame-data arena, descriptor/staging ownership, resident pins, completion primitive, completion value, and release state.
- [ ] Submit eye work without waiting for the newly submitted GPU work to complete.
- [ ] Register the complete ownership payload atomically after accepted submission.
- [ ] Poll completion non-blockingly at the beginning of later frames.
- [ ] Recycle command pools, frame-data arenas, descriptors, staging, and resident pins only after completion.
- [ ] Keep the in-flight bound explicit and report when it is reached.
- [ ] Use a short, counted recovery wait only after every safe reuse/defer path is exhausted.
- [ ] Never hide an XR miss inside an unbounded CPU wait.
- [ ] Count late, missed, and reprojected frames explicitly.

## Phase 10C - XR frame-loop integration

- [ ] Preserve `xrWaitFrame` as the XR pacing gate.
- [ ] Preserve valid `xrBeginFrame`, image acquire/wait, rendering, image release, and `xrEndFrame` ordering.
- [ ] Build view-independent visibility, materials, and render plans once per XR frame.
- [ ] Publish compact per-eye or multiview view records rather than reconstructing draw preparation for each eye.
- [ ] Use multiview/single-pass stereo where supported and semantically correct.
- [ ] Keep desktop swapchain acquisition nonblocking while OpenXR owns the frame deadline.
- [ ] If the runtime requires GPU completion before release, move the required wait into a bounded retirement/release authority and report every forced wait.

## Phase 10D - OpenXR swapchain recreation and deferred destruction

- [ ] Detect view-configuration/recommended-dimension changes through the runtime's supported event/query policy.
- [ ] Avoid treating a session-state event alone as proof of a specific resize requirement.
- [ ] Tombstone the old OpenXR swapchain, Vulkan views/framebuffers, and dependent artifacts with the highest application completion value that can reference them.
- [ ] Also track runtime ownership/release state; application GPU completion alone is not sufficient for destruction if the runtime still owns the image.
- [ ] Create and publish the replacement swapchain without a device-wide idle when the runtime permits overlapping old/new swapchains.
- [ ] Route subsequent frames to the new generation.
- [ ] Poll retired generations and destroy only after both GPU and runtime lifecycle conditions are satisfied.
- [ ] Bound the number of live retired generations and expose a visible fallback when the bound is reached.
- [ ] On `XR_SESSION_STATE_STOPPING` or `XR_SESSION_STATE_LOSS_PENDING`, stop new submissions, end the session according to the runtime contract, and drain outstanding work before destroying the Vulkan device or completion primitive.
- [ ] Permit a blocking final drain only during explicit shutdown, runtime loss, or unrecoverable teardown, never during normal rendering.

## Exit criteria

- [ ] Normal eye submission returns without waiting for the submitted eye work to finish.
- [ ] The historical recurring 70-100 ms eye-submit wait leaf is eliminated.
- [ ] No eye command pool, descriptor arena, staging range, resident pin, or swapchain image is reused before completion.
- [ ] Monado and at least one hardware runtime pass validation and visual tests.
- [ ] Swapchain recreation performs no normal `vkDeviceWaitIdle` and produces no use-after-free, device loss, or stale eye image.
- [ ] Shutdown drains outstanding submissions safely and reports any forced final wait.

---

# Phase 11 - Validation matrix and promotion

## Objective

Prove performance, cadence, correctness, lifetime, and visual parity on one frozen integrated implementation.

## Required desktop scenarios

- [ ] Static camera and static scene.
- [ ] Continuous camera motion.
- [ ] Object transform and animation updates.
- [ ] One material-value mutation.
- [ ] One texture/sampler mutation.
- [ ] Texture streaming promotion/demotion burst.
- [ ] Geometry add/remove/reload and generation-safe slot reuse.
- [ ] Shader reload outside the measured steady interval, followed by warm recovery.
- [ ] Directional shadow movement and settle.
- [ ] Reflection probe/environment maintenance.
- [ ] Resize, maximize, minimize, restore, and repeated recreation.
- [ ] Editor UI active and hidden.
- [ ] Secondary ImGui platform windows.
- [ ] CPU-direct, GPU indirect zero-readback, and GPU meshlet zero-readback strategies.
- [ ] Dynamic rendering and legacy render-pass A/B where supported.
- [ ] FIFO Stable, Mailbox LowLatency, and Immediate Uncapped profiles.

## Required OpenXR scenarios

- [ ] Static headset pose.
- [ ] Continuous head motion.
- [ ] Desktop plus OpenXR active.
- [ ] OpenXR image pressure/in-flight bound.
- [ ] OpenXR swapchain recreation/recommended-size change.
- [ ] Session stop and session loss.
- [ ] Monado and at least one hardware runtime.

## Correctness gates

- [ ] Zero Vulkan validation errors in separate validation-enabled runs.
- [ ] Zero device loss, stale descriptor, use-after-free, ownership mismatch, or command-pool reuse-before-completion errors.
- [ ] Two camera-separated images prove live camera-dependent output.
- [ ] Strategy parity preserves draw order, visibility, materials, shadows, transparency, postprocess, and UI.
- [ ] No selected zero-readback path silently falls back to CPU/readback behavior.
- [ ] No current-frame readback, mapping, or host completion wait in production captures.
- [ ] No normal-frame `vkDeviceWaitIdle`.

## Performance gates

- [ ] Stable frames allocate zero managed hot-path bytes after warmup.
- [ ] Stable frames perform zero per-draw material or descriptor reconstruction.
- [ ] Stable package/frame-operation preparation scales with dirty ranges rather than visible draw count.
- [ ] Sealed unchanged submission CPU p95 is below approximately 0.25 ms.
- [ ] Slot-wait p95 is approximately zero in an uncapped GPU-headroom test.
- [ ] No unexplained worker wake or render-thread lock wait exceeds 0.1 ms.
- [ ] Run-to-run p95 range is at or below 7.5%; target 5% for final promotion.
- [ ] Actual present intervals meet the selected profile's target, not merely CPU FPS.
- [ ] Every-N-frame cadence spikes are absent or have an explicit accepted cause.

## Promotion levels

### Level A - Stable 100 Hz

- [ ] Whole-frame p99 < 10.000 ms in all required desktop scenarios.
- [ ] No recurring unexplained >10 ms spikes.
- [ ] Correctness and lifecycle gates pass.

### Level B - Stable 120 Hz

- [ ] Whole-frame p99 < 8.333 ms.
- [ ] Engineering target p99 <= 7.5 ms on the promotion system.
- [ ] Actual present intervals meet the 120 Hz profile.
- [ ] All required desktop scenarios pass; OpenXR is separately classified if unavailable.

### Level C - Stable 144 Hz stretch

- [ ] Whole-frame p99 < 6.944 ms.
- [ ] Engineering target p99 <= 6.25 ms.
- [ ] GPU and CPU each retain measurable headroom.
- [ ] No feature is silently disabled relative to the frozen promotion manifest.

## Final closeout

- [ ] Freeze the accepted revision and manifests.
- [ ] Publish raw reports, summary tables, profiler captures, visual evidence, and validation logs.
- [ ] Document unsupported hardware/runtime rows explicitly.
- [ ] Convert every remaining known tail source into a named follow-up owner; do not waive it silently.
- [ ] Remove obsolete toggles, diagnostic aliases, and legacy paths after the accepted replacement is the only production route.

---

## 12. Required telemetry contract

### Presentation and pacing

- `PresentationProfileRequested`
- `PresentationProfileResolved`
- `PresentMode`
- `TargetRefreshHz`
- `TargetFrameIntervalMs`
- `ActualPresentIntervalMs`
- `FramesAhead`
- `LimiterSleepMs`
- `LimiterSpinMs`
- `AcquireMs`
- `AcquireUnavailableCount`
- `PresentQueueAdmissionMs`
- `NativePresentMs`

### Frame-slot and completion

- `FrameSlotWaitMs`
- `FrameSlotWaitQueue`
- `FrameSlotWaitTargetValue`
- `FrameSlotWaitCompletedValue`
- `FrameSlotWaitAgeFrames`
- `SwapchainImageWaitMs`
- `CommandPoolReuseWaitMs`
- `DescriptorArenaReuseWaitMs`

### Resident publication and invalidation

- `ResidentDirectHits`
- `ResidentColdMisses`
- `ResidentReplacements`
- `ResidentLocalInvalidations`
- `ResidentBroadInvalidations`
- `ResidentBroadInvalidationEntries`
- `ResidentInvalidationReason`
- `CanonicalDirtyOwnerCount`
- `CanonicalDirtyRangeBytes`
- `LegacyCompatibilityVisits`

### Submission gateway

- `SubmitImageContractMs`
- `SubmitQueueOwnershipMs`
- `SubmitLifetimePinsMs`
- `SubmitStateGateWaitMs`
- `SubmitQueueGateWaitMs`
- `NativeQueueSubmitMs`
- `SubmitLifetimePublishMs`
- `SubmitImagePublishMs`
- `SubmitDiagnosticPublishMs`
- `SealedSubmissionHits`
- `SealedSubmissionFallbacks`
- `SealedSubmissionFallbackReason`

### Scheduler and allocations

- `ResolvedWorkerCountByDomain`
- `WorkerWakeCount`
- `WorkerQueueAgeMs`
- `WorkerExecuteMs`
- `WorkerLockWaitMs`
- `WorkerManagedAllocationBytes`
- `RenderThreadManagedAllocationBytes`
- `GcPauseMs`
- `PinnedObjectCount`
- `OversubscriptionRejectedCount`

### Uploads, publication, and retirement

- `UploadQueuedJobs`
- `UploadOldestJobAgeMs`
- `UploadStagingBytes`
- `UploadStagingOverflowBytes`
- `UploadCpuPrepMs`
- `UploadStagingCopyMs`
- `UploadVulkanAllocationMs`
- `UploadTransferRecordMs`
- `UploadTransferGpuMs`
- `DescriptorPublicationMs`
- `DescriptorPublicationItems`
- `RetirementBacklogByClass`
- `RetirementOldestAgeFrames`
- `RetirementDestroyedByClass`
- `RetirementUncappedDrainCount`

### Render graph and GPU

- `RenderGraphCacheHit`
- `RenderGraphRecompiledPassCount`
- `BarrierCount`
- `BroadBarrierCount`
- `OwnershipTransferCount`
- `FullResolutionCopyBytes`
- `GpuPassP50P95P99`
- `GpuFrameP50P95P99`

### OpenXR

- `OpenXrEyeSubmitMs`
- `OpenXrEyeInFlightCount`
- `OpenXrEyeOldestAgeFrames`
- `OpenXrEyeForcedWaitMs`
- `OpenXrEyeForcedWaitCount`
- `OpenXrSwapchainReleaseDeferredCount`
- `OpenXrRetiredGenerationCount`
- `OpenXrMissedFrameCount`
- `OpenXrLateFrameCount`
- `OpenXrReprojectedFrameCount`

All counters must be allocation-free and cheap in normal performance builds. Detailed strings and object dumps are emitted only on failure, explicit sampling, or offline report generation.

---

## 13. Initial implementation priority

### Immediate P0

- [ ] XRENGINE FIFO and Mailbox baselines under matched benchmark settings.
- [ ] Post-Phase-3 p50/p95/p99 matrix.
- [ ] Granular frame-slot/acquire/queue/present wait attribution.
- [ ] Submission-gateway substage timers.
- [ ] Actual present-interval histogram.

### High-impact P1

- [ ] `Stable` FIFO profile and bounded Mailbox limiter.
- [ ] `SealedSubmissionContract` fast path.
- [ ] Reverse-dependency manifests.
- [ ] Complete canonical material and geometry publication.
- [ ] Delete legacy mirror/cohort paths after parity.
- [ ] Finish unified execution-topology ownership.
- [ ] Separate descriptor/resource publication budgets.
- [ ] Replace synchronous OpenXR eye-submit wait.

### Follow-up P2

- [ ] Render-graph cache and narrow barrier program.
- [ ] Shadow/probe/motion-vector maintenance budgets.
- [ ] Material/descriptor dirty-range closure.
- [ ] Streaming/pipeline/retirement stress gates.
- [ ] Optional present-wait/display-timing tiers.
- [ ] Optional descriptor-buffer or other advanced capability tiers after the baseline architecture is stable.

---

## 14. Definition of done

This TODO is complete only when:

- the stable desktop path passes the agreed refresh-rate promotion target through p99, not merely average FPS;
- actual presentation cadence matches the CPU/GPU timing story;
- an unchanged frame performs no draw-centric reconstruction or per-draw descriptor work;
- local mutations invalidate only exact dependencies;
- submission of a sealed unchanged artifact is a compact generation check plus native submission;
- all renderer-related worker ownership is centralized and non-oversubscribed;
- publication, retirement, compilation, streaming, shadows, probes, UI, and swapchain work are bounded and attributable;
- normal desktop and OpenXR paths contain no unbounded hot-path completion waits;
- validation, visual parity, lifetime, device-loss, resize, and shutdown gates pass;
- the legacy mirror, prepared-cohort bridge, and obsolete diagnostic fallbacks are removed from the accepted production path.
