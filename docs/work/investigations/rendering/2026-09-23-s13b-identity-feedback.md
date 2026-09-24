# S13b: publication identity without scene-content dirtiness

Status: Active. The user explicitly requested execution after the S13a evidence
handoff. S13a and S12 open checks are retained in their own gate records. This
scope decision permits an isolated S13b candidate and validation; it does not
mark those earlier gates passed.

## Entry evidence and mechanism

The current 393-draw fixture repeatedly delivers two canonical identity
`SetField` notifications per mesh command. `RenderCommand.OnPropertyChanged`
currently classifies the caller-member name `PublishCanonicalDrawIdentities` as
scene-content dirty; the next collection queues the command and
`GPUScene.TryUpdateMeshCommandCore` re-runs mesh registration. Historical
temporary exclusion reduced the Release Vulkan collect wait to 3.0–5.7 ms, but
its exact binaries are unavailable. The current matched-source S13a evidence
shows identity feedback on both backends, and elevated sampled stacks locate
the current CPU path in atlas synchronization beneath registration. This is
entry evidence for removing the redundant callback, not a final speed result.

The permanent candidate must leave both `SetField` calls and their exact
canonical/render-buffer snapshot values intact. Only that method's notification
may be excluded from the scene-content dirty cause. Real transform, material,
visibility, mesh, pass and enabled mutations must still enqueue and reach the
rendered output. A publication that did not commit must not leave a consumable
provisional snapshot. A swap may acknowledge only the mutation state it
captured; a later mutation must remain pending.

## Predeclared candidate gate

Use the S13a 393-draw Sponza fixture, Advanced/CpuDirect/TSR, fixed camera
`(-20,2,4)` looking at `(-20,2,-8)`, 1920×1080 output and 1286×723 internal,
and Release Vulkan validation off. Freeze binaries and accepted workload identity
for each paired comparison; keep Debug, debugger attachment, OpenGL and motion
conditions separate. In a warmed stationary interval, identity delivery may
continue but publication-only commands should cause **zero scene-content dirty
callbacks**. An accepted publication must remain visible through the exact
canonical and render-buffer identity. Genuine mutation must alter the intended
accepted output without a stale or lost later state.

The candidate's first performance target is at least a 90% reduction in the
393-command unchanged swap callback count relative to the same-source observer-off
baseline, and `RenderWaitForCollect` p50 below 20 ms and p95 below 60 ms in a
60-second warmed window. These are falsifiable candidate thresholds selected before the
edit, below the current observed roughly 80/130 ms p50/p95 and above the
historical 3–6 ms diagnostic. They are not universal acceptance criteria: the
S00 matched three-pair, observer-overhead, workload, successful-present, GC,
native-resource/descriptor, backlog, mutation/temporal and backend checks in
the TODO still determine validation. A missing or failed gate remains explicit.

## Work and evidence

The first pre-edit Release Vulkan capture used the stopped S13a isolated editor
binary and the specified 393-draw fixture. Its 599 stationary samples reported
`RenderWaitForCollect` p50 90.77 ms and p95 121.69 ms; the reverse direction,
`CollectWaitForRender`, was p50 15.034 ms and p95 35.582 ms. This distinction
corrects an earlier label error in this record without changing the predeclared
20/60 ms target. The 60-second motion phase also completed. A narrow Release
build for the candidate overlapped the baseline capture, so it is not an
uncontended paired baseline. Preserve this limitation and require clean matched
before/after captures before treating its frame-rate ratio as a validated causal
result. This capture also failed the native-resource and descriptor retention
gates, which remain separate open checks.

The candidate currently excludes only the identity-publishing notification
from the command dirty version and acknowledges exactly the mutation version
captured at swap start. Identity delivery now follows accepted database commit,
and its publication is hidden from consumers until delivery completes. Live
same-frame mutation, temporal, backend, and paired performance validation are
still in progress.

The first isolated Release candidate build succeeded with zero warnings and
errors. Its Editor DLL SHA256 is
`005F2726A23ECC1A474484E412CBD3ABA5FC5B2EDE406F7CCF0BE0AE1AAE54C7`
and Rendering DLL SHA256 is
`BC17F2B45A6804F034FFC9B9C24AE769E892AEFEAA5342460C6D9BBEA8728D64`.
The live named Vulkan session used those binaries, 393 resident draws and 393
manifest submissions at 1920×1080 output and 1286×723 internal size. During
a 94.466-second stationary telemetry interval, identity dirty notifications,
other dirty notifications, queue additions, swap callbacks, mesh updates and
mesh-update failures were **all zero**. A complete manifest before and after
the interval advanced accepted publication sequence 2748 → 7567 and frame
2776 → 7595 while a selected stable draw handle remained index 1 and its
current/previous world matrices agreed. This directly confirms that accepted
identity advancement no longer schedules unchanged command updates. It is an
observer-enabled diagnostic interval, not a frame-rate measurement.

One controlled live world-transform mutation moved the imported Sponza root
from world X=0 to X=1. The selected submission's current matrix X changed
from -20 to -19; the later current and previous matrices both settled at -19.
The interval around the move counted 2,751 other dirty notifications, 786
mesh updates and zero update failures. A viewport capture visibly shifted the
Sponza geometry. Restoring the root to X=0 returned both canonical matrices
to -20. The stationary and moved PNGs and complete manifests are under the
task run's `mcp-captures/s13b-final-vulkan-*` and `mcp-output/s13b-final-vulkan-*`.
This covers an actual transform change and next settled state; the single-frame
velocity transition was not sampled by the asynchronous MCP manifest calls.

That first candidate binary also completed one telemetry-off Release Vulkan
measurement with the same camera, 393-draw fixture, frame-output workload hash
`10991459253885323059`, 25-second warmup, 60-second stationary interval and
60-second controlled camera-motion interval. The candidate reported 2,830
stationary samples over 62.46 seconds (45.31 completed frames/s), versus the
pre-edit capture's 599 over 62.11 seconds (9.64/s). `RenderWaitForCollect`
p50/p95 fell from 90.77/121.69 to 3.73/4.92 ms. The reverse
`CollectWaitForRender` p50/p95 was 15.03/35.58 before and 14.33/26.96 ms
after. Stationary render p50/p95 was 17.21/38.99 before and 15.95/29.27 ms
after, while its p99 rose from 44.88 to 55.67 ms; do not describe every tail
as improved. Motion samples rose from about 8.47 to 24.93 completed frames/s,
with motion `RenderWaitForCollect` p50 92.04 → 3.59 ms. Both runs used one
accepted workload identity, verified camera endpoints, no failed Vulkan frame
samples, passing diagnostic-loss evidence and over 99.8% coarse GPU coverage.
This is strong evidence that removing unchanged-command callback work improved
throughput in this fixture, with the baseline build-overlap caveat above.
Neither run passed retention: candidate live Vulkan resources rose by 815 and
descriptor sets by 2,000 during the measured window (baseline +1,491 and
+2,045). The S00 retention and full matched-pair gate remain open.

The mesh swap now seals an explicit GPUScene command-input snapshot before
callbacks. The callback update and its missing-index/structural-rebuild paths
consume that snapshot while retaining the original command as dictionary key.
This prevents an earlier callback subscriber from mixing later command-property
changes into the admitted legacy row. Owner RenderInfo flags/culling and mutable
referenced asset contents are outside that snapshot. A per-command identity
notification guard also prevents a notification subscriber's WorldMatrix setter
from overwriting the sealed render matrix in that publication boundary. These
callback-order, replacement-fallback and abort/retry races still need direct
live fault-injection evidence before the broad S13b matrix can close.

The same first candidate binary was relaunched in the owned named session with
OpenGL and the same fixture and camera. Its complete manifest had 393 unique
resident source/primitive pairs. All 393 joined to the Vulkan manifest by the
fixture-specific imported identity and primitive index; there were zero
backend-only pairs and zero differences in pass, instances, flags, state class,
compatibility/temporal reason, source labels, legacy index or source order.
The 108 mapped pass members matched; each backend also had four package pass
members without a resident row. OpenGL's 63.35-second stationary interval had
zero identity/other dirty notifications, queue additions, swap callbacks,
mesh updates and update failures. Its screenshot visibly showed Sponza, with
the editor overlay included by this backend's capture. Moving the root to X=1
changed a selected current/previous canonical matrix from -20 to -19,
produced 786 real mesh updates and zero update failures, and restoring it
settled both matrices at -20. The session was stopped through its owner manager.
This proves representative stationary and transform behavior across the two
backends on this fixture; it does not reproduce the historical OpenGL
divergence or cover multi-view mutation races.

A telemetry-off OpenGL candidate capture from that binary and the same fixture
recorded 431 stationary completed frames over 62.20 seconds (6.93/s), with
`RenderWaitForCollect` p50/p95 2.68/3.34 ms. Its render dispatch p50/p95 was
80.74/106.92 ms, so the remaining OpenGL frame time is dominated by another
stage; this run is not evidence of a large OpenGL frame-rate gain. Controlled
camera motion recorded 9.11 completed frames/s. Its admission image and camera
were verified, diagnostic loss passed, and retention failed. Its frame-output
workload hash differs from Vulkan because backend identity is included; the
direct 393-row manifest join above supports the matched scene correspondence.

The subsequent focused review found another accidental reliance on recurring
identity callbacks: direct changes to `RenderInfo3D.Layer`, `CastsShadows`,
`ReceivesShadows`, or culling inputs did not dirty their mesh commands, and
same-reference renderer/submesh edits could also leave legacy GPU scene rows
stale. The source now invalidates these owner and referenced renderer/material/
mesh changes, captures owner metadata before swap callbacks, and keeps the
command as the GPU scene registration key. The numbers above describe an
**intermediate candidate** and are not attributed to the final source.

## September 24 current source and live result

The final narrow Release rendering build and isolated Release editor build
succeeded with zero warnings and errors. The Editor DLL SHA256 is
`D8C812A093FFF861D4EE50E06536089D33E41B364AE6D9E91D88C923E45820FA`;
the Rendering DLL SHA256 is
`498290FC179867B1E6FB3BC433E94BBA54B2768715F6249E79C218ADFCA79EE3`.
The exact executable and hashes are recorded in
`Build/_AgentValidation/20260923-183000-s13a-attribution/reports/s13b/final-binary-hashes.json`.
The named editor session was stopped through its owner manager before the
telemetry-off timing run. `git diff --check` passed. No regression tests were
added or run because live integration validation and explicit test clearance
are still outstanding.

On the owner-invalidation candidate before the shadow-flag publication fix, the
Vulkan 393-draw fixture advanced accepted publication sequence 355 to 1910
during a 33.1-second stationary interval with zero identity
or other dirty notifications, queue additions, swap callbacks, mesh updates,
and update failures. A later post-mutation 20.1-second interval also had zero
for all six counts while 393 draws stayed complete. Deactivating the imported
Sponza root produced a complete zero-draw manifest; reactivating it produced
393 unique submissions with new valid draw generations and no stale handle.
Moving its root world X 0 to 1 changed a selected published current/previous
matrix X from -20 to -19 and produced 786 mesh updates with zero failures;
restoring X 0 settled both matrices at -20. The saved restored Vulkan viewport
PNG visibly shows Sponza. The asynchronous readback did not capture the exact
single moving frame's velocity value.

That candidate's OpenGL build also published 393 unique rows at the matched camera. All 393
joined to the Vulkan manifest by this fixture's imported entity and primitive,
with zero differing comparable rows. Its 52.6-second stationary interval had
zero dirty notifications, queue additions, callbacks, updates, or failures.
A live `MeshCastsShadows=false` edit initially exposed a failed publication:
the owner and sealed command snapshot changed, and GPUScene's legacy row flags
changed 258 to 256, but the accepted canonical row remained 258. Its state
reported `publicationRejected=true` with the bounded table-capacity failure.
The publisher had treated draw-level shadow bits as structural geometry changes
and tried to replace 393 immutable geometry rows in one boundary. The fix keeps
these bits in the content signature and draw row, omits them from immutable
geometry/render-state flags, preflights draw replacements, and advances the
recording-topology generation when the accepted draw flags change. A read-only
render-thread probe confirmed the legacy and canonical values separately.

After that fix, the same OpenGL edit published 393 rows with canonical and
legacy flags 258 to 256 and back to 258; accepted sequences advanced and no
publication was rejected. Vulkan repeated the 258 to 256 to 258 change with
393 complete rows and no rejection. Setting the model component's `MeshLayer`
0 to 1 changed the owner, legacy row, and published canonical instance masks
from 1 to 2; restoring 0 returned all three masks to 1, again without a
rejected publication. The on-demand owner/legacy probes have no steady frame
path; their overhead was excluded from timing by running the harness with S13a
telemetry off. The focused read-only publisher review found no concrete new
transaction or consumer defect in this draw-level flag path.

The exact final binary completed a telemetry-off Release Vulkan run with the
same 393-draw workload hash `10991459253885323059`, fixed camera, 25-second
warmup, 61.505-second stationary interval and 61.422-second controlled camera
motion. It recorded 3,578 stationary completed frames (58.17/s), and 1,760
motion completed frames (28.65/s). Stationary `RenderWaitForCollect` was
p50/p95 3.280/4.029 ms, below the predeclared 20/60 ms target; stationary
render time was p50/p95/p99 13.310/17.971/22.337 ms. Camera endpoints and
admission image passed, diagnostic loss passed, and coarse Vulkan GPU timing
covered 99.944% of stationary frames. The optional pipeline GPU-history dump
was unavailable. The prior pre-edit capture reported 9.64 stationary frames/s
and 90.77/121.69 ms wait p50/p95, but a build overlapped that baseline, so
its frame-rate ratio is **not a validated causal pair**. Three successive
candidate variants reported roughly 39 to 58 stationary completed frames/s;
the spread also prevents claiming a stable final fps uplift from one run.

The final run failed retention despite its speed: native live resources rose by
1,806 and descriptor sets by 1,800; required backlogs returned. Its summary is
`Build/_AgentValidation/20260923-183000-s13a-attribution/reports/s13b/candidate-final-release-vulkan/summary.json`.
The pre-edit capture also failed retention (native resources +1,491;
descriptor sets +2,045), so this evidence does not establish that S13b caused
the growth. It also cannot waive the current run's retention gate.
S13b remains **Active**. The S00 matched three-pair/retention gate, callback
mutation and post-capture races, rejected-publication fault injection, material
and mesh replacement, multi-view consumption, and single-frame temporal/velocity
inspection remain open. Keep this implementation as a measured candidate while
those gates are completed; do not promote S13c on the present evidence.
