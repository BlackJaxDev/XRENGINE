# Authored WebGPU draw preparation ownership

## Observed failure

Linux job `111601331247` in run `37258776047`, at exact commit
`27c0c5b461d8df719e0de0f533106c1a2214c9e1`, passes authored source-rank
WGSL creation but reaches the existing 45-second preparation deadline in
`gpu-indirect-x4-blended`. The frame has no completed scene submission. Its
first pending owner is an Unlit GPU candidate reporting `IndexedDrawPending`.
The later `DirectRasterPending` summary reflects that same sticky atomic-frame
pending flag and does not establish that the direct source caused the stall.
An original `TexCoord0` buffer and an ordered raster input buffer are physically
ready but remain unclaimed. No native program preparation or resource failure
remains active.

## Source defect and repair

`WebGpuIndirectWork.TryRecord` previously disposed its retained draw and
published replacement cache keys before assigning the result of a new
`WebGpuMeshDraw` constructor. That constructor synchronously generates the
original index and vertex buffers before beginning asynchronous pipeline
creation. A pending buffer receipt can therefore throw before the assignment.
The work owner then retains a disposed old draw under the new keys. Subsequent
attempts match those keys, skip construction, and return pending permanently.
`WebGpuMeshletWork` had the same failure path, additionally transferring its
geometry lease before replacement construction succeeded.

Both owners now construct a local replacement before changing their retained
draw, cache keys, ordered raster commands, or meshlet geometry lease. Successful
construction invalidates the previous ordered raster commands, disposes the old
draw through existing deferred retirement, and publishes the new draw and keys.
Raster state and attachment revision are captured once; meshlet work also
captures the generated index handle. A failed constructor preserves the
previous cache entry and lease so the next attempt retries construction.
A successfully constructed draw whose pipeline is still pending remains the
retained owner and progresses through the existing asynchronous pipeline task.

A constructor that stops during original buffer generation has not started
pipeline creation or allocated draw commands. Its buffer requests remain owned
by renderer-retained wrappers. Ordered snapshot copies likewise remain with
the bounded frame-slot snapshot: reuse claims matching requests, capacity
replacement cancels obsolete requests, and teardown cancels requests and
disposes storage. Source geometry, CPU-owned direct participation, GPU ranking,
visibility, frame atomicity and completion-gated slot reuse are unchanged.
There is no visibility/count readback, fallback, timeout increase, or weakened
acceptance condition.

Partial startup can assign a later pass's work to a different ordinal as an
earlier pass advances. This is not established as a separate persistent churn
defect in this static fixture: the Web tier appends a fixed pass sequence, the
fixture does not begin play or change source membership, and each slot resets
its cursors only once per recording attempt. A work ordinal is consumed once
within that attempt, so later passes cannot overwrite the earliest blocked
owner. Once the preceding preparation prefix is ready, the next attempt
revisits that owner before later passes. A successfully constructed pending
pipeline therefore retains a progression path. This source argument bounds the
repair; runtime CI must confirm progress, and it is not a claim that arbitrary
changing scene traversal cannot cause additional cache churn.

## Validation boundary

Independent source/lifetime review and `git diff --check` pass. A disposable
Node witness executes the extracted baseline and repaired replacement blocks
with mechanical C#-to-JavaScript syntax adaptation and resource/pipeline doubles.
It reproduces the disposed-draw stall across all 23 indirect/meshlet replacement
key triggers and passes 656 assertions for repeated pending and failed
constructors, delayed pipeline readiness, cache reuse, exact original resource
identity, ordered-command invalidation, geometry leases, initial construction,
and state mutation during construction. Its report is
`Build/_AgentValidation/20261001-225000-lit-surface/reports/authored-draw-replacement-witness.json`.
This is extracted source-semantics evidence only, not managed compilation or
browser/GPU execution.

The local environment has no .NET SDK, PowerShell, or Chromium. Exact-commit CI
must still verify the repaired browser blended profile, pixels, resize/restart,
and its unchanged ownership/readback assertions. No production tests,
dependencies, desktop GLSL, active workflows, or browser acceptance checks were
changed for this repair. The broader authored submission contract remains in
[the authored indexed progress record](browser-authored-meshlet-indexed-2026-10-03.md)
and [WebGPU indirect submission](../../../architecture/rendering/webgpu-indirect-submission.md).
