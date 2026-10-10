# Distance Cadence and GPU Pose Presentation

Date: 2026-10-07
Status: Proposed. The user authorized distance-based lower physics rates with interpolated GPU poses.

[Steady-state CPU design](physics-chain-steady-state-cpu-design.md) ·
[Investigation](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md) ·
[Code items](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md) ·
[Validation](../../testing/physics/physics-validation.md#gpu-skinned-chain-scale)

## Scope and current limits

Separate physical simulation time from CPU input gathering and rendered pose
publication. Lower distance tiers can gather inputs and submit physical work
less often. Rendered poses must still update between solves. Keep solved
physics data on the GPU. GPU timing diagnostics are separate from solved-data
readback.

Existing quality policies provide reduced rates and deterministic phases.
They do not provide smooth GPU presentation. `PhysicsChainBonePalette.comp`
uses current solved positions directly. Its dispatcher binds no interpolation
alpha or mode. `PreviousPhysicsPosition` stores the prior simulation substep,
while output pages and Advanced deformation retain separate render history.
Existing reduced-rate time scaling also changes the physical step reference.
Selecting 15 Hz with `Interpolate` is therefore not proof of equivalent
physical time or interpolated GPU poses.

Due admission must precede full rest-input gathering and component preparation
to remove their cost on updates without a solve. Preserve required lifecycle,
wake, reset, and dependency behavior. The first prototype did not show a
performance gain: its repeated validation increased preparation cost. A
replacement needs retained input ownership and invalidation that avoid those
repeated checks. The [investigation](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md)
records the comparison and rejection. Due admission alone does not complete
smooth distance LOD or change the existing physical-time contract.

## Physical clock and pending GPU work

For the first complete interpolation path, use authored 60 Hz physical ticks,
with `h = 1/60 second`. A 60, 30, or 15 Hz scheduling tier batches one, two, or
four physical ticks. Each tick uses the same authored coefficient reference.
Increase shader substep capacity where needed; the current solver limit of
three cannot execute a four-tick batch.

Use one authoritative clock and integer target ticks. Store the consumed tick
and reset epoch in a retained per-tree GPU state arena. New input packets may
replace older pending packets, but must not erase unconsumed physical time.
Compute pending work from target and consumed ticks. Publish successful
consumption with the same native lifetime and failure contract as the solve.
Do not acknowledge dropped work as simulated time. A bounded catch-up policy
must expose remaining debt.

Apply tier changes at a committed batch boundary. Preserve pending ticks;
do not reinterpret elapsed time using the new scheduling interval. Initial
admission is limited to supported standard, single-root, translation-only
chains. Basis changes and unsupported root dependencies must explicitly use
the full-rate policy or reset under a defined contract. Do not silently use
a CPU solver.

## Presentation clock and endpoint history

Each retained endpoint needs particle positions, matching root translation,
physical tick, reset epoch, and resource generation. Capture the pre-batch
endpoint once before all physical movement and substeps. `PrevPosition`
remains Verlet state. It must not become a presentation endpoint.

A single pre/post pair is insufficient when a new batch replaces endpoints
that the renderer still needs. The reviewed reference retains consecutive
physical samples in a ring with at least `ceil(D / h) + 2` entries. Each entry
keeps its matching root translation, physical tick, reset epoch, and resource
generation. Preserve samples held by in-flight GPU readers; ring capacity
alone does not permit overwriting a retained entry.

The reviewed safe reference policy uses a fixed presentation delay for the
entire opt-in lifetime, including the near 60 Hz tier. Use activation-only
opt-in for the first feature; runtime enable blending remains outside this
initial contract. Require `D >= 4h + L`, where `L` is the measured bound on
producer lateness. About 67 ms assumes `L = 0`. For example, `L = 2h` requires
at least 100 ms delay and eight consecutive samples. This example is neither
chosen nor validated. The policy remains a reference, not the final latency.

For presentation time `q = authoritativeRenderTime - D`, require sample tick
tags `floor(q / h)` and `ceil(q / h)` from the same reset epoch. Select their
physical timestamps `t0` and `t1`. Calculate
`alpha = clamp((q - t0) / (t1 - t0), 0, 1)` from those timestamps. Do not use
the current tier interval or CPU frame alpha as an endpoint timestamp.

If either required sample is missing, hold the last displayed pose and
increment an underrun counter. Do not extrapolate or rewind the presentation
cursor. A long stall requires an explicit reseed or continuous recovery policy;
merely clamping alpha and replacing endpoints can cause a visible jump. Equal
sample timestamps use a constant pose. Reset or rebind seeds identical samples
and roots, resets the time mapping, and invalidates old render and motion-vector
history.

The final delay, history capacity, delivery bound, and latency-reduction
policy remain open. Lower latency must preserve continuity through tier
changes and delayed batches. Normalized cadence phase alone does not prove
that property.

## Root motion and palette publication

Sample root translation each update, including updates without a solve.
Retain the prior solve root and the current sampled root. Distribute physical
root movement and anchors over the batch's fixed ticks. Do not pin every
substep to the final anchor. Consume accumulated physical root movement once.

For translation-only presentation, use root-relative endpoints:

```text
pRender = rootNow + lerp(p0 - root0, p1 - root1, alpha)
```

This display translation does not modify solver state or add another inertia
term. A true coordinate rebase must transform both Verlet positions; it is a
different operation. Root inertia, velocity smoothing, custom transforms,
shared roots, and dependent roots need explicit admission rules before they
enter this path.

Build bone rotations from interpolated parent and child positions and the
retained rest direction. Use a finite deterministic fallback for degenerate
directions. Do not linearly interpolate full bone matrices. Publish the
resulting palette every render frame when its presentation state changes,
even when no solve is due. The current dispatcher can return early without
an active request, so presentation needs its own refresh condition.

Keep previous rendered palettes and Advanced motion-vector history separate
from physical endpoints. Preserve output-page source tokens, renderer and
bone generations, reset witnesses, and fence-safe page reuse.

## Bounds and view relevance

GPU bounds must describe the actual current and previous rendered palettes.
CPU committed bounds must also include presentation root translation and
skin extents for the interpolated bone directions. A union of particle
endpoint AABBs alone does not bound all rotated skin offsets. Refresh bounds
for pose-only and root-only publication. If bounds persist across updates,
include the permitted root sweep. Keep current and prior render witnesses
consistent with those bounds.

Supply relevance from actual viewing cameras in the same world. Combine
multiple views conservatively by selecting the highest required quality.
Do not let a shadow camera or whichever camera is currently rendering select
the simulation tier. Desktop, stereo, spectator, and other output views need
an explicit relevance policy. A Math-world desktop observer can be a bounded
first integration; it does not complete general multi-view relevance.

## Ownership and open decisions

`PhysicsChainWorld` owns due admission and physical scheduling.
`PhysicsChainComponent` supplies authored settings and required root/input
observations. The rendering bridge carries immutable tick and presentation
data. `GPUPhysicsChainDispatcher` owns pending GPU tick state, endpoint
storage, palette publication, bounds, and native lifetime. Advanced
deformation consumes exact published output and retains render history.

Resolve the endpoint delivery bound, final presentation latency, history
capacity, catch-up behavior, root admission limits, collision-input sampling,
and multi-view relevance before declaring the full feature complete. Keep
implementation items in the code todo and runtime checks in the validation
document. The current measurements identify CPU input preparation as a major
cost. They do not prove smooth presentation, physical equivalence, or the
100 Hz target.
