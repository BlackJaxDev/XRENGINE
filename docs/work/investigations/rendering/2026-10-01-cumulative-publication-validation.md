# Cumulative publication and recording validation

Status: **Executed; cumulative acceptance NOT PASSED**, October 1, 2026. This records execution of
the [cumulative gate](../../todo/rendering/vulkan-stall-remediation-todo.md#s13i-prove-the-cumulative-fix-on-the-reported-workload).
The only retained code correction restores omitted existing profiler fields.
No rendering optimization, test change, or quality reduction is part of this
investigation.

## Predeclared gate and provenance

The gate requires three warmed, at least 60-second repetitions of stationary,
camera motion, and real scene mutation on each compared source; identical
accepted work and effective rendering settings; all-frame p50/p95/p99/maximum;
independent GPU coverage; bounded resources; and each child mechanism remaining
effective. Adjacent-stage tail tolerance is the greater of five percent or the
repeated baseline spread. An attributable source treatment, accepted fixture, and
observer accounting are prerequisites to a speedup claim.

Evidence root: `Build/_AgentValidation/20261001-140000-cumulative/`. Both editors
use named isolated Release sessions, no debugger, required Vulkan, validation off,
Advanced pipeline, CpuDirect, TSR 0.67, 1920 Ã— 1080 output and 1286 Ã— 723 internal
resolution. Dense GPU timestamps are off. Existing every-frame capture is enabled
with sample interval one, ReleaseBenchmark mode, CPU profiling and editor UI on.
The runtime classifies this configuration as **IntrusiveConfiguration**, with
`profile_comparison_suitable=false`; the UI prevents clean comparison and the
unspecified warm-cache metadata also prevents promotion. Matching instrumentation does not establish its overhead.

The historical source is clean commit
`cee30cd5790a019c2fe42ed163208d91bd1dc8ce`, rebuilt in a detached checkout. Its
pinned submodule source and native LFS payloads were recovered locally; moved
native payloads were copied only after matching the historical LFS SHA-256.
The initial Windows launch failed because of the nested executable path; a
temporary drive alias shortened only the launch path. No baseline source was
patched. The build passed with zero warnings and errors.

Current source is `f81f944967c8ef1e1df5e0c382bde2062e4da905` plus the retained
readiness allocation fix and concurrent workspace changes. Its isolated full
Release build also passed with zero warnings and errors. Build, copying, startup,
and cold admission are outside the measurement windows. Only one owned editor
runs during the windows. Both isolated binaries remain fixed during measurement.

| Binary | Vulkan assembly SHA-256 |
| --- | --- |
| Rebuilt original | `FA346C0A5EF7ED635FF3AA82AC5B3B2B277E3FF51A8640BB168E3B495CF01B04` |
| Current | `ABCA291129A99CC4C9957F02BB42FDC4D541D8759A80DE37C91D68AA87FFB8AF` |

The exact September settings artifact expired. The fresh reconstruction imports
the unchanged Sponza2 OBJ without mesh optimization or post-import merging, using
the October fixture. Settings model/import code matches across the two sources,
but this is **not an exact replay of the historical fixture**. Camera A is
(-8, 2, 0), looking at (0, 2, 0); motion uses x = -8 + 3 sin(t/7),
z = 1.5 sin(t/5), with repeated 0.5-second focus commands. Mutation moves the Sponza root
by 0.15 sin(t) on x at approximately two requests per second and restores it
afterward. Screenshots and diagnostic snapshots occur outside timed windows.

During this run the user observed periodic speed jitter. Source inspection shows
that each `set_editor_camera_view` calls `FocusOnView`, cancels the old focus,
and starts a new `EaseInOut` interpolation. RPC time followed by a 0.5-second sleep
also leaves gaps. These are matched **eased-segment motion** windows, not a
constant-speed or continuous-velocity smoothness test. Validation layers are off;
the periodic acceleration/deceleration is authored by the harness. Real frame
spikes can add separate stutter. Changing editor focus behavior would be the wrong
fix for this harness. A single uninterrupted engine-driven sweep is a separate
control; temporal smoothness remains outside this cohort's acceptance claim.

The original source predates the per-submission identity and publication tools.
Its resident counts, package counts and publication tuple are available, but no
source-row correspondence can be established from counts alone. The current
identity manifest reports 393 submissions with complete source labels, but
`fixtureKeysStable=false`. This remains an admission limitation.

The exact previous validated binary/workspace snapshot is also unavailable.
Synchronization retained no code after the readiness increment, so those two
child dispositions have the same retained treatment; that does not make the new
whole binary identical to the previous validated build. Thousands of intervening
non-child source changes further prevent exclusive attribution of a cross-version
timing difference to these children.

## Measurement semantics

The first current run exposed an observer defect: schema 11 emitted 1,675 fields,
while the original emitted 1,706. All 31 missing identity, outcome, failure and
material-table fields were inside an obsolete `XRENGINE_STATIC_VULKAN` guard in
`XREngine.Runtime.Host/Engine/Engine.ProfileCapture.cs`. Host does not define the
symbol that Bootstrap defined before the source moved. A lexical comparison of
822 literal field expressions had missed this compilation condition; actual
runtime schema admission supersedes that comparison.

The two obsolete preprocessor directives were removed. The block already uses
backend-neutral diagnostics capabilities, and runtime backend/authority checks
remain. The narrow Host Release build passed with zero warnings/errors, using the
isolated build's existing dependency references. Only its Host DLL/PDB were
replaced in the stopped owned editor; every other engine assembly hash was
unchanged. Host SHA-256 changed from
`070873DFCA45133257C8438EB99B732F9DB496CF0178DB085CF7286059701E5B` to
`706003FB44438C7176AB7F9E3BFBDF6DF109D15987FF6E9CC90218FB20F4C588`.
The incomplete cohort is excluded. No identity is invented from neighboring
counters. A fresh current cohort requires the correlation fields before timing.

Raw rows are re-extracted from the complete log by UTC measurement bounds to avoid
dropping a leading line when an offset happens to end at a newline. Every
render-loop attempt, including zero-valued stage samples and rejected/deferred
outcomes, belongs in the CPU distributions. Cumulative allocation and GC counters
use endpoint differences. Stage wall times overlap and are not summed into an
exclusive CPU total.

Successful presentation uses completed frame identities and accepted presents.
Coarse GPU samples retain authority, source frame, sequence and image slot;
duplicates and out-of-window results are counted separately. GPU coverage is
against unique captured Vulkan frame numbers. CPU and GPU distributions are
reported independently. Observer allocations remain included in process totals.

## Retained child treatment

| Child | Retained mechanism and disposition | Limits preserved |
| --- | --- | --- |
| [Identity publication](2026-09-23-s13b-identity-feedback.md) | Identity-only publication does not dirty mesh state; captured mutation versions are acknowledged without losing newer edits; canonical publication commits before identity delivery. Validated. | Original temporal/user report remains separate. |
| [Logical registration](2026-09-26-s13c-registration-retention.md) | Logical mesh/LOD registrations survive transform-only changes. Validated for reachable scope. | Multi-LOD thresholds and streaming were not exercised live. |
| [Auxiliary state](2026-09-26-s13d-auxiliary-state.md) | Typed equality and changed-row writes remove update allocation and unchanged auxiliary/transparency writes. Validated for reachable scope. | Instance count, overrides, texture/sampler replacement and deformation coverage remain limited. |
| [Family preparation](2026-09-26-s13e-family-preparation.md) | Compatible family stages reuse one immutable scene preparation while retaining live freshness checks. Validated for reachable scope. | Resize/MSAA, physical XR and stronger in-flight teardown coverage remain open. |
| [Operation metadata](2026-10-01-advanced-operation-metadata.md) | Deferred/Not Applicable: measured scans below entry gate; no cache retained. | Other hashing and stage work were not dispositioned. |
| [Readiness](2026-10-01-warmed-pipeline-readiness.md) | Indexed fixed-count shader traversal: 880 to zero bytes per poll; prior body reduction 27.9% stationary and 42.1% moving. Allocation-only scope validated. | No readiness cache; broader reuse and deterministic late-completion proof deferred. |
| [Synchronization](2026-10-01-remaining-synchronization-gates.md) | Deferred: acquisition below entry gate; no synchronization change retained. | Held computation and genuine parallel lifetime proof remain separate. |

## Results and acceptance decision

The two diagnostic cohorts contain **27,821 render-loop attempts**: 7,075 original
and 20,746 current, across eighteen at-least-60-second windows. All sampled
outcomes are Completed, with zero internal missing render IDs and zero recorded
code-profiler overflow/discard events. Each process retained one authority.
Every scene-output row reports Advanced/TSR, 1920 × 1080 / 1286 × 723,
FreshRender, rendered scene content and no skip. Both runtime rows now have
1,706 fields. There are 7,064 original and 20,719 current unique in-window coarse
GPU results; delayed boundary results are excluded explicitly.

Both versions report 393 resident draws, instances and geometries, 25 materials,
18,599,108 committed geometry bytes and 393 canonical package submissions at the
checked endpoints. Camera parameter values match by stage/key: bloom, GTAO and
auto exposure are enabled, motion blur is disabled. Per-frame camera feature
state is unavailable; its false effect fields are **not evidence of bypassed
passes**. Runtime identities, source-row correspondence and full executed-effect
coverage remain incomplete despite these matching counts and values.

These are post-readiness diagnostic windows, not an accepted clean speedup
comparison. The original first stationary window still uploaded 0.95 MiB of
textures; camera motion uploaded 13.33 MiB in both versions. Readiness plus 100
completed presents did not establish zero cold/streaming work. Original admission
images contained magenta surfaces that later captures no longer showed. All
windows and tails are retained rather than silently removing these observations.

All timings below are milliseconds, shown as **p50 / p95 / p99 / maximum**.

| Source / workload | All attempts | Successful-present interval | Render dispatch | Collect wait | Coarse GPU |
| --- | ---: | --- | --- | --- | --- |
| original/stationary | 2520 | 85.07 / 109.33 / 135.20 / 471.52 | 13.15 / 21.83 / 29.43 / 381.21 | 72.02 / 92.62 / 109.36 / 458.47 | 19.30 / 25.45 / 27.91 / 31.51 |
| original/motion | 1939 | 92.91 / 150.62 / 182.70 / 572.35 | 17.40 / 74.28 / 98.94 / 526.26 | 65.22 / 87.20 / 96.71 / 164.70 | 21.60 / 30.87 / 33.32 / 37.21 |
| original/mutation | 2616 | 63.85 / 106.17 / 117.61 / 377.80 | 12.93 / 20.22 / 25.56 / 349.73 | 47.83 / 90.74 / 99.62 / 223.77 | 18.98 / 21.64 / 26.36 / 30.14 |
| current/stationary | 8289 | 19.18 / 27.24 / 53.07 / 1901.90 | 18.45 / 26.28 / 50.59 / 1901.15 | 0.66 / 0.98 / 3.94 / 50.38 | 18.66 / 23.65 / 27.74 / 30.93 |
| current/motion | 3805 | 27.98 / 90.94 / 162.22 / 653.77 | 26.95 / 89.73 / 155.70 / 592.55 | 0.82 / 1.11 / 5.15 / 607.99 | 21.22 / 24.42 / 25.91 / 29.55 |
| current/mutation | 8652 | 20.24 / 25.59 / 33.47 / 462.05 | 19.38 / 24.50 / 32.65 / 461.45 | 0.70 / 1.94 / 4.00 / 19.78 | 19.28 / 22.47 / 23.76 / 33.09 |

| Source / workload | Collect visible | Package publication | Primary prewarm | Primary encoding |
| --- | --- | --- | --- | --- |
| original/stationary | 1.32 / 2.10 / 7.14 / 18.88 | 0.01 / 0.01 / 0.01 / 0.43 | 0.46 / 0.72 / 1.01 / 10.67 | 2.44 / 3.86 / 8.19 / 12.52 |
| original/motion | 1.93 / 2.86 / 11.55 / 81.63 | 0.01 / 0.01 / 0.01 / 0.06 | 0.53 / 7.71 / 16.08 / 142.48 | 3.49 / 27.06 / 33.23 / 96.71 |
| original/mutation | 1.24 / 51.12 / 60.56 / 390.48 | 0.01 / 0.01 / 0.01 / 0.05 | 0.45 / 0.69 / 0.90 / 5.98 | 2.37 / 3.48 / 7.96 / 43.26 |
| current/stationary | 2.33 / 5.75 / 8.01 / 692.18 | 0.00 / 0.01 / 0.01 / 0.95 | 0.46 / 0.77 / 1.13 / 4.29 | 2.54 / 4.06 / 5.99 / 58.86 |
| current/motion | 3.32 / 13.44 / 19.29 / 612.02 | 0.01 / 0.01 / 0.01 / 0.05 | 0.59 / 9.71 / 16.29 / 146.75 | 3.65 / 30.30 / 46.50 / 445.57 |
| current/mutation | 2.31 / 5.96 / 8.29 / 441.03 | 0.00 / 0.01 / 0.01 / 1.23 | 0.48 / 0.74 / 1.12 / 4.98 | 2.60 / 3.97 / 5.84 / 10.43 |

| Source / workload | Allocated GiB | MiB / attempt | GC pause ms | Gen0 / Gen1 / Gen2 | Buffer upload MiB | Texture upload MiB | GPU coverage |
| --- | ---: | ---: | ---: | --- | ---: | ---: | ---: |
| original/stationary | 11.068 | 4.498 | 6189.43 | 911 / 346 / 10 | 48.41 | 0.95 | 99.88% |
| original/motion | 15.175 | 8.014 | 12438.43 | 1288 / 745 / 31 | 37.25 | 13.33 | 99.85% |
| original/mutation | 11.551 | 4.522 | 5493.09 | 946 / 380 / 4 | 50.26 | 0.00 | 99.81% |
| current/stationary | 18.792 | 2.322 | 6822.34 | 1501 / 382 / 3 | 159.24 | 0.00 | 99.89% |
| current/motion | 23.164 | 6.234 | 19009.29 | 1955 / 1152 / 51 | 73.10 | 13.33 | 99.76% |
| current/mutation | 19.710 | 2.333 | 6031.77 | 1572 / 390 / 3 | 166.21 | 0.00 | 99.90% |

The larger current total allocations and upload totals partly reflect more
frames in the same time. Per-frame process allocation fell, but remains about
2.3 MiB stationary/mutating and 6.2 MiB during motion, with profiling included.
Capture-thread allocation is whole-thread allocation, not an isolated estimate
of observer cost. No exclusive CPU allocation or GPU speedup is claimed.

### Mechanisms and resource limits

Current endpoint telemetry spans the timed windows and their diagnostic brackets;
these counter totals are not divided by the narrower all-frame slice counts.

| Current cohort | Identity dirty notifications | Mesh updates | Registration hits / rebuilds | Transform / bounds writes | Preparation calls / families / reuses |
| --- | ---: | ---: | --- | --- | --- |
| Stationary | 0 | 0 | 0 / 0 | 0 / 0 | 8,416 / 8,416 / 50,496 |
| Eased motion | 0 | 5 | 5 / 0 | 0 / 0 | 3,938 / 3,938 / 23,628 |
| Real mutation | 0 | 276,672 | 276,672 / 0 | 138,336 / 138,150 | 8,748 / 8,748 / 52,488 |

Real mutation issued 352 root-transform requests. Required updates remain live:
there are 153,934 swap callbacks, zero registration rebuilds, zero metadata,
cull-control, classification or visibility element writes, and zero transparency
bytes for that transform-only cohort. All families prepared once and reused six
times, with zero preparation failures. The five motion updates are non-identity
metadata changes; the capture shows a hover/selection outline, but there is no
event-to-command join proving its cause. Do not label them identity feedback.

Selected mesh-update allocation was zero stationary, 192 bytes moving and 50,304
bytes under real mutation, all in the material lookup span. Other instrumented
registration/typed-row allocation columns remained zero. This does not prove
zero total update allocation. `meshUpdateStateClassWrites` counts resolutions
unconditionally and must not be described as actual material-state writes.

Selected mutation lock acquisition totaled 5.902 ms and held spans 360.752 ms
across the three mutation brackets. Those are the existing mesh-update spans,
not a new measurement of every renderer gate. The operation-scan, readiness-poll
and broader synchronization probes were removed in their child investigations;
their earlier evidence remains scoped there, not fabricated as fresh counters.

Current native-object endpoints range from 9,415 to 9,424, descriptor sets stay
at 5,891, and every timed endpoint has zero retirement backlog. Original endpoints
range from 11,488 to 13,753 objects and 7,936 to 10,181 descriptor sets; pending
retirement reaches 18 and remains there. These are **endpoint observations**.
The every-frame lifetime/descriptor/lease fields remain default zero in both
versions despite nonzero MCP snapshots, so they do not establish all-frame
retention or lease bounds. Repair that observer path before accepting that gate.

### Remaining stalls and failed gate

Typical collect wait fell from tens of milliseconds to below one millisecond,
consistent with the removed publication churn. This is not sufficient for
cumulative acceptance:

- Current stationary render-dispatch p99 is 50.59 ms versus 29.43 ms original;
  moving dispatch p99 is 155.70 versus 98.94 ms. Moving encoding p99 is 46.50
  versus 33.23 ms. Source/observer/fixture confounds prevent attributing those
  differences to one retained child, but they cannot be called passing tails.
- The worst stationary dispatch is **1,901.147 ms**, at render frame 11,777.
  Recorded Vulkan time is 13.390 ms; **1,887.757 ms lies outside it**. The process
  GC-pause counter increased 242.403 ms across the adjacent capture rows, which
  does not explain the full interval. Foreground shader compile count and pipeline
  compile time are zero; native submit/present are about 0.15/0.13 ms. A quiet
  Vulkan attribution-gap flag covers its inner frame, not this outer CPU gap.
- Two approximately 409 ms motion dispatches coincide with 316.384 and
  334.219 ms GC-pause deltas. Another 389.698 ms dispatch has only 10.322 ms of
  observed GC pause and 240.228 ms in command encoding. These are correlations,
  not scheduler/CPU-execution proof. Preserve an observer-off comparison and
  allocation/GC/scheduling trace as the next CPU attribution task.

The cumulative gate therefore remains **NOT PASSED**. Missing original row
correspondence and the exact previous validated workspace/binary, unvalidated
observer overhead, unavailable camera-state/lifetime series, cold streaming,
unexplained CPU time and outstanding image correctness each retain their own
open gate. Dense GPU observer validation and one-effect-at-a-time attribution
were not completed; the separate GPU item below preserves that requirement.
No child result is promoted into an integrated performance or temporal pass.

### Image and adjacent-path checks

Stationary and changed-position Vulkan screenshots were captured and viewed.
They show changing scene geometry, while dark/high-contrast regions persist.
The current motion image also contains a yellow editor outline, so exact pixel
or overlay equivalence is not claimed. Still captures cannot validate motion
vectors, history stability or disocclusion sequences.

The separate single 20-second focus sweep issued only one nonzero-duration
command. Sampled positions progressed from (-7.462, 2, 0.269) at roughly five
seconds, through (-6.489, 2, 0.756), to (-5.000, 2, 1.500); the saved images were
viewed and change with the camera. This removes repeated ease resets from the
authored path, but neither still images nor the command's interpolation prove
displayed smoothness. User confirmation remains absent.

OpenGL reported Advanced admitted/bound and 393 canonical draws, but its two
viewed captures show a mostly black scene with colored blobs and editor overlays.
The mutation readback then timed out at 20 seconds. The scene remained queryable;
this is a **failed visual check**, not a shared-path acceptance pass. RenderDoc
doctor passed; child injection and target attachment were verified, but the
managed-launch capture timed out and the attached editor supplied no capture
after an explicit trigger. No replay/root-cause finding is claimed.

The fresh emulated-stereo smoke completed stationary, moving-camera and root-
mutation stages (20 seconds each, 37 mutation requests followed by restoration).
The configured mode was Emulated/SequentialViews. Between endpoint snapshots,
render frame IDs advanced from 814 to 2,052; 3,658 family preparations made
3,658 scene-publication prepare calls and 21,948 stage reuses, with zero recorded
publication failures. Final pending retirement was zero. These endpoint deltas
include diagnostic/readback time and are not a timed all-frame performance gate.

Desktop motion and left/right eye captures were viewed. They contain scene
geometry and distinct eye views, but also large dark regions, yellow outlines
and purple debug geometry. This establishes bounded execution/readback evidence,
not image correctness, temporal stability, parallel eye recording or physical-XR
acceptance. The zero-valued aggregate VR draw counters do not establish absence
of eye work: the output records identify left/right submissions and the eye
readbacks contain scene content.


## Residual ownership

Cold canonical pipeline admission belongs to the existing stall TODO's S03/S04
family/program lifetime owners and S05 native pipeline creation owner. Required
texture completion belongs to
[Unblock Desktop Texture Uploads](../../todo/rendering/vr/editor-openxr-toggle-and-rendering-todo.md).
Any child there must preserve pending/failure/stale-completion behavior,
transfer-before-binding, bounded progress and retirement; waiting longer is not
proof of those properties.

Unattributed Advanced GPU cost is tracked separately in
[Advanced pipeline GPU attribution](../../todo/rendering/optimization/editor-profiler-ui-render-cost-todo.md#open-code-items-moved-from-advanced-pipeline-gpu-attribution-todomd).
The existing Default-pipeline measurements do not establish current Advanced costs.
Dense executed-pass timing in a separately validated observer and one-effect-at-a-
time comparisons are required before selecting a GPU fix. No effect is disabled
as part of this cumulative gate.

The existing S15 temporal/visual gate and S16 integrated closeout remain open.
Earlier OpenGL and emulated-stereo child evidence retains its documented scope;
it is not a fresh cumulative cross-version or physical-XR pass. No new tests are
authorized or added by this report.

## Validation and cleanup

The original and current full Release builds and the corrected Host Release
build completed with zero warnings and errors. The retained source change is
limited to the two obsolete conditional-compilation directives described above;
the fresh captures confirm the restored schema. Independent read-only review
checked the desktop tables, counters and acceptance limitations; the subsequent
stereo smoke is recorded separately above.

All owned editor sessions were stopped, the RenderDoc session was closed, and
the temporary drive alias and owned original-source worktree were removed after
preserving its build log. Timed rows, endpoint snapshots, images and provenance
remain in the investigation scratch root. Unrelated workspace edits are retained.
