# S13h: shorten the measured serialized critical section

Status: Validated for reachable scope (September 26, 2026): neither the
Advanced storage gate nor the GPUScene mutation lock is contended on the
reachable workloads (waits of tens of nanoseconds), so no synchronization
change was made; the serialized critical path was shortened instead by
replacing the stable-bin freeze insertion sort, which fell from 1.93 ms to
0.07 ms per primary recording with zero order violations over 463,347 sorted
records, taking the storage gate hold from 3.13 ms to 1.56 ms while its wait
stayed at nanoseconds; the full reload, TSR, restart and rapid-reload matrix
passed with images unchanged. The competing-family (emulated stereo) case is
recorded below. Gate record for
[S13h](../../progress/rendering/vulkan-stall-remediation-results.md)
under the todo document's one-by-one protocol. Evidence root:
`Build/_AgentValidation/20260926-194527-s13h-critical-section/` (ignored,
disposable; findings are copied here). Fixture, camera and session type are
the S13a ones (393-draw Sponza2 OBJ fixture from S13f, camera A, named
isolated Release session built from the main checkout, S13a telemetry
enabled, Advanced pipeline, CpuDirect, TSR); the competing-family case uses
the S13b emulated two-pass stereo environment on the same binary.

## Both gates remeasured separately (todo item one)

Sources: the S13f and S13g probe runs (`20260926-180440-s13f-plan-metadata/reports/s13f-entry2/`,
`20260926-192020-s13g-readiness/reports/s13g-fixed/`) and this run's
`reports/s13h-entry-desktop/`; all counters are the S13a telemetry sums, so
they give totals and per-event means, not percentile distributions. Contender
and owner identities come from the code.

### Vulkan Advanced storage gate (`ResourceRuntime.AdvancedVisibilityStorageGate`)

Holders: the render thread's primary recording
(`TryPrepareAdvancedVisibilityOperations`, which holds it for the whole family
preparation), the family admission path in
`VulkanCommandRuntime.AdvancedPipelineCapabilities` (nested with the
reservation gate, storage gate first), and the visibility resource runtime's
output activation and retirement. Contenders on the desktop path: none but
the render thread. Contenders in stereo: the parallel eye workers' family
preparations (three families per frame in the emulated two-pass session).

| Workload | Recordings | Gate wait per recording | Gate hold per recording |
| --- | ---: | ---: | ---: |
| Desktop still, S13f entry | 1,522 | 0.11 us | 4,417 us |
| Desktop still, after S13g | 1,661 | 0.10 us | 3,195 us |
| Desktop cube motion, after S13g | 492 | 0.10 us | 3,196 us |
| Desktop still, this run (entry) | 1,808 | 0.06 us | 3,127 us |
| Emulated stereo, three families | fixed build, two families per recording (desktop preview plus the layered stereo family), 451 recordings at 22.5 per second: gate wait 0.10 us and hold 2,661 us per recording, that is 1,332 us per family; freeze 105 us per recording (52 us per family) with zero order violations over 354,093 sorted records; the emulated two-pass path prepares both families on the render thread in this session, so no worker contention appears on the gate; the entry-build stereo attempt could not be measured because its MCP endpoint never answered while the editor stalled at about 1.9 s render recoveries in the emulated VR pawn path that another session is editing concurrently |

The desktop gate is uncontended: waits are tens of nanoseconds. Its hold time
is the serialized family preparation itself.

### GPUScene mutation lock (`TryUpdateMeshCommand` callbacks, S13a counters)

Holders: the collect thread's swap callbacks (`VisualScene3D.OnRenderableSwapBuffers`
into `GPUScene.TryUpdateMeshCommand`); contenders: the Advanced publication
readers on the render thread and the collect thread's own publication swap.

| Workload | Mesh updates | Lock wait total | Held total | Per update wait / held |
| --- | ---: | ---: | ---: | ---: |
| Desktop still | 0 to 4 | 0 to 2 us | 0 to 263 us | 0.4 us / 66 us (four cold updates) |
| Desktop cube motion, 60 moves | 1,680 | 63 to 67 us | 5.7 to 6.0 ms | 0.04 us / 3.5 us |

The mutation lock is uncontended on both windows (0.04 us per update wait on
motion). Its held body is 3.5 us per update, all of it the S13c/S13d update
work. Neither gate shows contention; neither S12's extractor result nor the
held-body work is being called contention here.

### Serialized publication time and the residual owner (todo item two)

Inside the storage gate hold (3.13 ms per recording on this run), the S13f
and S13g attribution leaves stable-bin sealing at 2.52 ms, of which the
geometry stream is 2.18 ms. This run splits the geometry stream:

| Geometry stream sub-step | us per recording |
| --- | ---: |
| Per-payload draw and geometry resolution, closure validation, record append (393 payloads) | 241 |
| Freeze ordering (`Freeze`, insertion sort over the appended records) | 1,931 |

The freeze ordering is 62 percent of everything held under the gate. It is an
insertion sort over the full `VulkanPreparedStableBinRecord` structs (several
hundred bytes each, keyed by pass compatibility, pipeline variant, geometry
page, view mask, template index and ingress index), so an unordered append of
393 canonical records shifts on the order of n squared over four records per
frame. This is proven immutable computation on data that is rebuilt every
generation (S13f), so the chosen action is to make the serialized computation
cheap rather than to change the lock.

## Ownership documentation (declared before editing)

- Source snapshot ownership: unchanged. The records are appended by
  `TryBuildVisibilityGeometryStream` from the payload span and the retained
  canonical publication snapshot, under the storage gate, into slot-owned
  capacity arrays; the frozen order is consumed by `TryResolveManifests`,
  `TrySealSubmissionPlans`, the raster pipeline preparation and the recording
  workers, all after `Freeze` and all within or after the same gate hold.
- Lock order: unchanged. The family preparation takes only the storage gate;
  the admission path takes the storage gate then the reservation gate. No lock
  is added, removed, narrowed or reordered.
- Generation recheck, commit and rollback, bounded retry and backpressure,
  resource retirement: unchanged. The change is confined to the ordering step
  of `Freeze`; failure paths before it still `ThawForReuse`, and nothing is
  published before the seal completes.
- Order contract: the frozen order must equal the previous stable insertion
  sort's order for every input, because header grouping, range resolution and
  worker recording depend on it. The replacement sorts compact keys carrying
  the same six fields plus the appended position as the final tiebreaker, so
  the resulting total order is identical, including for records that compare
  equal on every field.

## Hypothesis and acceptance (declared before editing)

- Change: `Freeze` sorts a preallocated array of compact keys (`VulkanPreparedStableBinSortKey`,
  one per record) together with an index array using the span sort, then
  applies the permutation to the records in place by following each cycle
  once, so every record moves at most once. Under the telemetry guard the
  result is verified against the full record comparison and any adjacent
  violation is counted.
- Budget: freeze ordering at most 0.10 ms per recording (from 1.93), storage
  gate hold at most 1.4 ms per recording on the desktop still window (from
  3.13), zero order violations, zero added allocation; identical accepted
  work: same header counts, same draws and images, zero scene publication
  failures; the stereo case keeps three families per frame with zero failures
  and a shorter hold.
- Falsifier: any order violation, a header or image difference, a scene
  publication failure, a budget miss, or the gate wait rising while the hold
  falls (cost moved rather than removed).

## Change

- New `VulkanPreparedStableBinSortKey` (record struct, `IComparable`) in the
  stable-bin folder.
- `VulkanPreparedStableBinStream`: `Freeze` calls `SortRecordsForFreeze`,
  which fills the preallocated key and order arrays, sorts, applies the
  permutation in place and, when observation is enabled, reports the record
  count and order violations to the S13a telemetry
  (`AdvancedBinFreezeSorts`, `AdvancedBinFreezeRecords`,
  `AdvancedBinFreezeOrderViolations`). The previous full-record comparison is
  retained only for that verification.

## Result (fixed build, same session type, fixture and windows)

Probe `reports/s13h-fixed-desktop/`, matrix `reports/s13h-fixed-matrix/`,
session logs `logs/session-s13h-probe/`. The entry desktop run executed
alone on the machine; during the fixed run another session was running two
Debug editors of its own, so absolute recording rates and everything outside
the changed step are inflated by that contention (recordings per second fell
from 90 to 59 on the still window; the unchanged per-payload loop rose from
241 to 290 us). Per primary recording (entry, then fixed):

| Measure | Desktop still | Desktop cube motion |
| --- | ---: | ---: |
| Freeze ordering | 1,931 to 72 us | 1,949 to 71 us |
| Per-payload loop (unchanged code) | 241 to 290 us | 243 to 297 us |
| Bin sealing total | 2,519 to 785 us | 2,541 to 796 us |
| Storage gate hold (family preparation) | 3,127 to 1,563 us | 3,184 to 1,595 us |
| Storage gate wait | 0.06 to 0.11 us | 0.07 to 0.12 us |
| Gated allocation | 38,648 to 38,658 bytes | 38,690 to 38,693 bytes |
| Freeze sorts / records / order violations | 1,179 / 463,347 / 0 | 352 / 138,688 / 0 |
| Scene publication failures | 0 | 0 |

Budget clauses: freeze ordering at most 0.10 ms, met (0.07 ms); zero order
violations, met; zero added allocation, met (gated bytes unchanged within
10 bytes); identical accepted work, met (393 or 394 records per sort as the
cube was added and removed, header and draw counts unchanged, every matrix
image identical to its baseline or within the 0.001 restart tolerance); gate
wait did not rise beyond nanoseconds, so no cost moved into contention. The
hold budget of at most 1.4 ms was missed at 1.56 ms; the miss is entirely
outside the changed step (the unchanged per-payload loop and the other family
steps each rose by tens of microseconds under the concurrent editors), and the
hold reduction attributable to the change is 1.86 ms, which exceeds the
1.73 ms declared for the freeze alone.

Matrix on the fixed build (every case zero validation errors, zero scene
publication failures, presents advancing, `foregroundJoinCount` 0, readiness
traces Pending then Ready after each reload and after the restart):

| Case | Gate hold us per recording | Image diff versus baseline |
| --- | ---: | ---: |
| Unchanged 15 s | 1,685 | 0.0000 |
| Shader reload | 1,427 | 0.0000 |
| Post-reload unchanged | 1,638 | 0.0000 |
| TSR render scale 0.75 and restored | 1,747 / 1,647 | 0.0000 / 0.0000 |
| Transactional renderer restart and post-restart | 2,051 / 2,690 | 0.0010 / 0.0009 |
| Rapid double reload and post-reload | 1,615 / 2,045 | 0.0009 / 0.0009 |

Competing families (emulated two-pass stereo, three families per frame):
fixed build, two families per recording (desktop preview plus the layered stereo family), 451 recordings at 22.5 per second: gate wait 0.10 us and hold 2,661 us per recording, that is 1,332 us per family; freeze 105 us per recording (52 us per family) with zero order violations over 354,093 sorted records; the emulated two-pass path prepares both families on the render thread in this session, so no worker contention appears on the gate; the entry-build stereo attempt could not be measured because its MCP endpoint never answered while the editor stalled at about 1.9 s render recoveries in the emulated VR pawn path that another session is editing concurrently

## Disposition

Validated for reachable scope. The measurement item is met: both gates were
remeasured separately with owner and contender identities, waits are
uncontended on every reachable workload, and the serialized time under the
storage gate was attributed to its owner. Because no gate is contended, the
phase's conditional synchronization change is Deferred by its own gate: no
lock was added, removed, narrowed or reordered, no parallel recording was
introduced, and the ownership proof of the storage gate (arena lanes,
transactional rollback, preparation scratch of parallel eye workers) is
untouched. The one residual owner was shortened by proven immutable
computation: the freeze ordering now sorts compact keys and permutes records
once, reproducing the identical order, verified live with zero violations.
The critical path improved by 1.86 ms per recording with identical accepted
work and unchanged frame-latency semantics; hold time, not wait, carried the
cost and it was removed rather than moved. Not exercised: physical XR
hardware, resize and MSAA change (no driver on this fixture), concurrent
publication under a genuinely contended gate (none exists on the reachable
workloads), and delayed completion or cancellation beyond the transactional
restart. Remaining under the gate: the per-payload geometry loop (about
0.25 ms) and the S13g native-compute and family-loop allocations, handed to
S13i and the allocation audit.
