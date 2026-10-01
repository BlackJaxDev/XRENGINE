# Remaining GPUScene and Vulkan storage synchronization

Status: **Deferred**, October 1, 2026. This is the conditional S13h assessment in the [Vulkan stall remediation TODO](../../todo/rendering/vulkan-stall-remediation-todo.md#s13h-change-synchronization-only-for-a-measured-remaining-bottleneck). No synchronization change is retained. The measured workloads do not establish a material contention bottleneck or the concurrent lifetime proof required to narrow or partition these gates. The conditional assessment is dispositioned; the cumulative S13i gate and S15 visual acceptance remain open.

## Ownership and review

GPUScene owns a reentrant `System.Threading.Lock` in `GPUScene.CommandBuffers.cs`. `SwapCommandBuffers` publishes the double-buffered command streams and canonical Advanced scene while holding it. Add/remove, mesh-command mutation, material/LOD registration, atlas registration and diagnostic snapshots acquire the same lock. Reentrant atlas calls are intentional. Normal `VisualScene.GlobalSwapBuffers` publication and `VisualScene3D.OnRenderableSwapBuffers` updates belong to the swap/collect phase; scene membership processing also holds `_renderablesSync`. Ordinary MCP transform churn therefore does not establish simultaneous mutation/publication. Preserve `_renderablesSync` before the GPUScene lock.

`VulkanResourceRuntime.AdvancedVisibilityStorageGate` is shared by every visibility bank as `_gate`. The outer `TryPrepareAdvancedVisibilityOperations` scope includes all primary-plan families. Nested reservation, initialization, buffer preparation, descriptor preparation and retirement calls use that same reentrant monitor. It protects shared arena-lane cursors, preparation scratch, rollback and quarantined-slot state. `TryPrepare` publishes candidate state only after descriptor success; failed rollback quarantines the frame slot. Preserve storage before the reservation gate. Separate scene-resource and pipeline-preparation gates are not substitutes for this measurement.

Parallel OpenXR eye workers can contend for this gate. Emulated sequential views can create several families without simultaneous eye execution. Shortening the lock would require immutable source ownership, generation rechecks, an atomic commit/rollback boundary, bounded retries, and unchanged GPU retirement proofs. Bank-local dictionaries alone do not establish safe partitioning of the shared arena or scratch. New command/descriptor-pool ownership is not justified merely by a long serialized body.

A native read-only agent independently audited lock sites and ownership. Broker reasoning was assessed as useful for concurrency review, but the required broker tools were unavailable; no broker run was simulated.

## Predeclared gate and observer

Entry for a synchronization change requires repeatable waiting of at least 0.10 ms per successful present with an identified competing owner, or at least 0.50 ms of serialized hold per present with proven immutable work that can move safely. A candidate must improve the selected critical-path cost with separate CPU attribution by at least 25 percent with identical accepted work and frame-latency semantics, without shifting cost into holds, retries, adjacent work or backlog. Insufficient concurrent-lifetime evidence requires deferral even if a time threshold is exceeded.

The temporary observer covers all 20 GPUScene acquisitions and all 12 storage-gate acquisitions/aliases. It retains the original `Lock.Enter/Exit` or `Monitor.Enter/Exit` semantics and early-return/exception disposal. Only outermost acquisitions are recorded; nested holds are not double-counted. A bounded, preallocated two-million-row ring records gate instance, managed thread, entry site, observed advisory owner/site and acquisition/hold timestamps. Formatting and file output occur on explicit diagnostic requests after release. Owner zero means none observed, not proof that a wait was impossible; acquisition time includes scheduler and primitive overhead. No overhead subtraction is used, and observer runs are not whole-frame speedup claims.

Evidence root: `Build/_AgentValidation/20261001-150000-synchronization/`. The fixture clones the previous Sponza Vulkan Advanced settings into the ignored run directory. Release runs use the isolated `synchronization-probe` session. Existing unrelated profiling and owner-first LOD initialization changes are preserved. No tests are added or modified.

## Interpretation limits

Acquisition and hold times are elapsed wall time, including scheduling/preemption, not exclusive CPU cycles. Nested observer registry lookups are included in outer holds; the outer registry lookup is outside the acquisition timer. Owner and site are sampled separately, so observed competing-owner samples are advisory overlap evidence rather than proven blocked waits. Multiple GPUScene instances are analyzed separately when attributing owners; sums across instances describe workload cost, not contention on a shared gate.

CSV snapshots surround separately requested statistics, making elapsed lock cost per completed present approximate: the lock numerator covers a slightly wider interval that includes diagnostic calls. Publication/family counters are also separate snapshots. This supports a conservative, far-below-threshold disposition; it is insufficient for a close gate or a speedup claim. The ring sequence increments before row publication, so missing sequence entries could include in-flight boundary samples as well as actual loss. Nonwrapping windows and observed missing-entry counts must be reported.

Job-manager snapshots at approximately ten-second intervals show queue/active-job state; they do not measure worker active-duty percentage or establish peak parallel overlap. Scheduler configuration and dispatch counters supplement them, but dedicated OpenXR worker utilization and concurrent lifetime behavior remain unmeasured. No broad concurrency validation is claimed from sequential emulated views.

The initial desktop camera-motion harness made instantaneous cuts every 0.5 seconds (`duration=0`). The user observed its visibly stepped motion. That is a harness artifact, not evidence that Vulkan validation caused the motion. Subsequent emulated-view motion uses 0.5-second interpolation; these cohorts are not a matched motion/cadence comparison. Standard Vulkan validation remains enabled in both, synchronization validation disabled. There was no validation-on/off performance experiment.

## Measured results

Twelve warmed windows (two approximately 30-second windows per mode/cohort), after completed-frame warm-up, produced 9653 successful presentations. Scene membership remained 396 GPU commands / 398 tracked renderables. Desktop executed approximately one Advanced family / seven stages per present; emulated sequential views executed three / 21. Separate snapshots cause small normalization deviations. Every measured window ended Completed with zero pending retirements and zero new standard-validation errors.

Approximate elapsed milliseconds per completed present, combining the two windows by present count:

| Cohort | Mode | Presents | GPUScene acquire | GPUScene hold | Storage acquire | Storage hold | Scene-publication prepare |
|---|---|---:|---:|---:|---:|---:|---:|
| desktop | stationary | 2461 | 0.000373 | 0.697400 | 0.000463 | 4.522331 | 0.139506 |
| desktop | motion | 1584 | 0.000443 | 0.731993 | 0.006000 | 4.450030 | 0.159998 |
| desktop | mutation | 2622 | 0.001510 | 0.768123 | 0.000439 | 4.293343 | 0.191335 |
| stereo | stationary | 1204 | 0.001044 | 0.672247 | 0.000262 | 12.742736 | 0.231113 |
| stereo | motion | 832 | 0.000470 | 0.787605 | 0.000283 | 12.238214 | 0.242635 |
| stereo | mutation | 950 | 0.002436 | 0.872883 | 0.000278 | 13.101825 | 0.381354 |

Pooled outermost acquisition distributions in milliseconds. These are per-acquisition percentiles, **not all-frame latency tails**:

| Cohort | Gate | Acquisitions | Acquire p50 / p95 / p99 / max | Hold p50 / p95 / p99 / max |
|---|---|---:|---|---|
| desktop | GPUScene | 106196 | 0.000000 / 0.000300 / 0.000400 / 0.580000 | 0.001300 / 0.592400 / 0.738400 / 10.061100 |
| desktop | Advanced storage | 6705 | 0.000400 / 0.000700 / 0.000800 / 8.699300 | 4.080100 / 5.957200 / 8.169000 / 375.233700 |
| stereo | GPUScene | 97209 | 0.000000 / 0.000100 / 0.000400 / 0.623900 | 0.001300 / 0.003400 / 0.750700 / 11.795500 |
| stereo | Advanced storage | 2993 | 0.000300 / 0.000400 / 0.000600 / 0.001600 | 12.073700 / 16.671100 / 21.562200 / 91.497600 |

The mutation windows applied 234 root-transform changes and recorded 91962 transform writes, with zero registration rebuilds. The original transform was restored. All snapshot sequence intervals were nonwrapping, with 0 missing completed entries.

## Decision and remaining proof

The storage gate was acquired on managed thread 2 throughout both warmed cohorts. Three emulated families increased required serialized body work, but did not create a competing eye worker. GPUScene had two distinct lock instances: publication ran on the swap thread, with occasional diagnostic readers on other threads. Its rare advisory overlap samples involve diagnostics/publication; they do not establish a production mutation/publication bottleneck. The large acquisition outlier on the sole storage-owner thread cannot be attributed to another thread holding this gate.

Both acquire-cost measurements remain below the declared 0.10 ms/present threshold. Held-body cost exceeds 0.50 ms/present, but no immutable computation/ownership boundary or adequate parallel lifetime proof was established. Moving the gate boundary does not remove the required work. Consequently no owner is selected for lock narrowing, no extra recording threads are introduced, and no pool, publication-order, retry, rollback, generation or retirement contract changes.

Scheduler snapshots expose six general workers, zero render background workers and one logical render lane in these cohorts. Active jobs and occupied queue slots were zero at the measured sample points. This is sparse sampled evidence, not a utilization percentage or proof of absence of transient jobs. Actual concurrent mutation/publication, parallel OpenXR eye-worker utilization, delayed native completion, cancellation, resize, failure rollback and teardown races have **not** been validated for a changed synchronization design. Prior readiness tests do not replace that proof. Those obligations remain reopening conditions rather than being marked passed.

Reopen only on a workload with measured, attributable competing owners and meaningful critical-path delay, or a specifically proven immutable computation that can leave the serialized region. Capture time-aligned per-instance wait/hold and CPU data, worker overlap/duty, accepted-output and backlog accounting, then review snapshot ownership, lock order, generation recheck, commit/rollback, bounded retries and resource retirement before coding. The large held preparation body remains profiling evidence for the cumulative workload assessment, not permission to remove its gate.

## Images and validation scope

Actual PNGs were inspected at stationary and moved camera positions for both desktop and the desktop view of emulated stereo, and after restoring scene mutation. They show responsive scene geometry with the existing dark lighting and editor highlight/debug overlays. Both emulated-eye PNGs were inspected: they remain dark with low/clipped-looking views and magenta debug lines. This is not an eye-image correctness pass; the pre-existing S15 image-quality limitations remain open. No visual fix or RenderDoc investigation is claimed here.

The observed GPUScene lock identities were 2 and 3 in each process; transform-update traffic used instance 2. Desktop publication used managed thread 59, and diagnostic readers used threads 40/52/54. Emulation publication used thread 58, with diagnostic readers on 34/36/37/51. Three advisory competing-owner samples were recorded across all windows, involving diagnostic snapshot/publication sites. Storage instance 1 used thread 2 exclusively throughout the measured windows. Source-site mapping and per-instance sample counts are retained in disposable evidence; the identities are process-local, not stable runtime IDs.

No production changes or tests are added for this deferral. Temporary lock-site replacements and the observer are removed after measurement. A concurrent edit to GPUScene LOD initialization was preserved by reversing only the instrumentation in that file; other probed files were restored byte-for-byte to their pre-probe content. Earlier readiness allocation removal remains intact. No prior test failure is reclassified as passing by this documentation-only disposition.

## Final cleanup verification

The final uninstrumented Release editor build passed with **zero warnings and zero errors**. Its live desktop smoke completed another **494 presentations**, with 396 GPU commands, zero new validation errors, zero pending retirements and a Completed terminal frame. Stationary and interpolated-motion PNGs were viewed. The early stationary capture was overexposed; the later motion capture returned to the familiar dark-shadow appearance. This is process/progress verification, not matched image-quality or exposure acceptance, and does not close S15.

The temporary observer build had one compiler warning about converting `System.Threading.Lock` to `object`; its implementation explicitly dispatches back to `Lock.Enter/Exit`, and that temporary helper is absent from the final zero-warning build. Source scans confirm no observer references remain. Documentation whitespace checks passed. The owned session was stopped after verification; stop completion is not proof of race-free native teardown. No user-owned editor was stopped. No new tests were appropriate for the retained documentation-only disposition, and the earlier unrelated source-text test failures remain recorded in the readiness investigation.
