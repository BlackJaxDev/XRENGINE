# S13b: publication identity without scene-content dirtiness

Status: Blocked. The user explicitly requested execution after the S13a
evidence handoff. That scope decision permitted the isolated S13b candidate,
but the retention and correctness gates below have not passed. S13a and S12
open checks remain in their own gate records.

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
At this earlier checkpoint, S13b remained **Active**. The S00 matched three-pair/retention gate, callback
mutation and post-capture races, rejected-publication fault injection, material
and mesh replacement, multi-view consumption, and single-frame temporal/velocity
inspection remain open. Keep this implementation as a measured candidate while
those gates are completed; do not promote S13c on the present evidence.

## September 24 primitive and material follow-up

A fresh isolated Release Vulkan editor run exposed two additional correctness
defects while exercising a second primitive on one imported Sponza renderer.
The existing GPU-scene update visited only previously registered primitive
indices, so adding a valid submesh could leave that primitive absent. A bounded
membership check now detects added, removed, or newly valid primitives and uses
the existing structural rebuild. The first live append then changed the complete
canonical manifest from 393 to 394 rows, with primitive indices 0 and 1 for
`sponza_00` and a valid new draw handle. This is publication evidence, not proof
of a successful Vulkan present.

That same run later rejected all publication attempts with "The canonical
material tables cannot accept the complete ownership transition." A temporary
read-only table probe found 25 live materials and zero retired material rows,
but the material publication journal rose from 50 to 2,025 of 2,048 entries.
The 1,975-entry increase equals 25 replacements across 79 publications. The
material header comparison omitted the standard kernel's required `TexCoord1`
attribute even though the database merges it into every stored row. All 25
otherwise unchanged material headers therefore compared unequal and generated
replacement deltas every frame. The expected header now includes the same
kernel requirement. This changes the comparison, not the stored material mask.

A second isolated Release run with that correction accepted the controlled
`393 → 394 → 393` append/remove sequence, kept an added null primitive out of
the manifest, and accepted its later transition to a valid primitive. Two more
remove/re-add cycles alternated 393 and 394 complete rows with no publication
rejection. The material journal stayed at one entry through the primitive
sequence and at two after one `Roughness` edit, instead of filling. The saved
readbacks are under
`Build/_AgentValidation/20260924-102959-s13-closeout/mcp-output/`, including
`fixed-repeated-primitive-cycles.json` and the `fixed-*-state.json`,
`fixed-*-manifest.json`, and `fixed-*-tables.json` snapshots.

The second run also showed why canonical acceptance alone is an insufficient
gate: Vulkan entered a terminal PresentNow readiness failure on the first append
at frame 991. Stable-bin sealing rejected visibility payload 393 because it
reported 3,395 vertices while its immutable canonical geometry reported 541.
The preparation extractor selected the renderer's primary mesh for every
submission, including primitive 1, instead of that primitive's mesh. The
terminal result and native counters are saved in `fixed-vulkan-terminal.json`.
At that boundary, the exact-primitive preparation repair and successful-present
validation were pending; none of the accepted-row results above count as
visible-output validation. The old GPU publication pin in that terminal
process does not by itself establish a leak; recheck retention after successful
frames resume.

The next isolated Release build captured the exact renderer and primitive mesh
with the publication sidecar and derived visibility vertex count and topology
from the immutable canonical geometry. On the same append, the complete
manifest reached 394 rows and Vulkan reported a completed, accepted present
with no terminal failure. Null append kept 393 rows; filling it, removing it,
and adding it again produced `393 → 394 → 393 → 394`, each with an accepted
present, no publication rejection, and material journal usage of zero. The
source's primitive indices and valid draw generations matched each intended
state. Evidence is in `primitive-fixed-baseline-summary.json`,
`primitive-fixed-append-summary.json`, and
`primitive-fixed-repeated-cycles.json` in the same task run.

Two Vulkan viewport screenshots after the fix were viewed from different
camera positions. Their scene views differ and both readbacks completed on
the GPU; the selected donor primitive was not visually isolated in the
before/after pair, so these images support live rendering but do not prove a
visible donor-pixel delta. In the warmed editor, the native live-resource and
tracked descriptor-set gauges held exactly at 11,038 and 7,471 at t0, t+72
seconds, and t+133 seconds while successful presents continued. Descriptor
pool and allocation-variant counts also held at 19 and 724. This is a useful
plateau for this one process, not the required matched observer matrix.
Pending retirements stayed at 24, while their oldest age rose from about 111
to 244 seconds; that backlog still needs ownership and gate disposition.
The three readbacks are `primitive-fixed-retention-t0.json`, `-t60.json`,
and `-t120.json`. The named editor session was stopped by its session manager.

## Cross-command callback boundary

The render-thread path had a separate after-capture risk. While command A
delivered a swap or accepted-identity notification, a synchronous property
handler could set command B's world matrix. The existing guards covered only
A. If B had already swapped, its authoring mutation remained dirty but its
render matrix could change immediately, leaving B's render snapshot out of
step with the canonical state captured earlier in the boundary.

A shared, nesting-safe thread-local callback scope now covers swap callbacks
and both identity `SetField` notifications. During that scope a world-matrix
setter still updates authoring state and its mutation version, but defers the
late render-matrix write until B's next swap. A temporary detached-command
probe ran on the editor's actual Vulkan render thread. Both callback modes
reported B's authoring X=2, captured render X=1, mutation version 4 and
acknowledged version 3 inside the callback. B's next explicit swap changed its
render X to 2. A render-thread change outside either callback still updated
render X to 2 immediately. The probe saved its result in
`Build/_AgentValidation/20260924-102959-s13-closeout/mcp-output/cross-command-callback-probe.json`;
the editor continued accepting Vulkan presents afterward. The temporary MCP
probe was removed from the source after use. This validates synchronous
cross-command notification handling, not every asynchronous or multi-view
ordering case in the S13b matrix.

## September 24 lifetime, reuse, and closeout review

Final review found that an unchanged geometry/material/transform signature
could reuse a retained submission sidecar after its source renderer changed.
Reuse now compares the planned renderer and exact primitive mesh references
with each retained source row. A changed reference forces a fresh publication.
The comparison holds a scoped package lease while reading the sidecar because
acknowledged, unpinned publications may be reclaimed concurrently; it declines
reuse if releasing that inspection lease retires the candidate. This fixes a
concrete stale-renderer path, but a live renderer-replacement fixture with
distinguishable pose/blendshape output remains a required S13b check.

The new exact-mesh source reference exposed two managed lifetime gaps. The
submission sidecar now clears discarded rows when a capture shrinks and clears
all managed sources at the database's acknowledged-unpinned reclamation,
free-slot replacement, disposal, and uncommitted fault boundaries. The
publisher's plan and submission-source scratch arrays also clear their prior
used prefixes before the next publication, including one that rejects early.
The ring still leaves pinned snapshots intact. Review verified that planned
supported rows and retained submission sources share command order, and that
the database's normal clear occurs only after acknowledgements and zero pins.
This is an ownership repair, not proof that native descriptor retention passes.

The primitive membership check was changed from repeated scans of registered
indices to a stack/pooled membership map. It retains the append/remove behavior
observed in the prior Vulkan cycle while avoiding quadratic work for a command
with many primitives. The focused Release Rendering build passed. The exact
lease-fix isolated Release Vulkan editor then exported complete 393/393
canonical manifests before, during, and after a `sponza` world-X 0 -> 4 -> 0
mutation. The accepted publication sequences were 54, 76, and 78; topology
generation stayed 393. Vulkan viewport screenshots before and during the move
were viewed and showed the intended scene change. Readbacks are
`Build/_AgentValidation/20260924-102959-s13-closeout/mcp-output/lease-*-manifest.json`
and the images are in the same run's `mcp-captures/lease/`. The named session was
stopped through the manager; its rendering/Vulkan logs contained no publication
rejection, readiness terminal, structural-rebuild warning, or validation VUID.
This covers an ordinary 393-command transform, not the unrun replacement,
multi-view, failure/retry, or per-frame temporal cases.

Three runs with a 60-second stationary phase on the same frozen fixture and
workload hash `10991459253885323059` constrain the retention claim. The first
used a 25-second warmup and grew native resources 15,370 -> 15,637 and tracked
descriptor sets 11,801 -> 12,071. A separate 180-second warmup held both flat
at 14,131 and 10,141. A further 180-second warmup after the sidecar/scratch
repair grew them 13,588 -> 15,549 and 9,991 -> 11,946. All three had one
captured workload identity, lossless diagnostics, required backlogs returned,
and zero sampled failed Vulkan frames. The first and third runs also had a
60-second camera-motion phase **before** their retention endpoint; the deep-warm
run did not. The third run's `render<-collect` p50/p95 was 3.253/4.478 ms,
but its combined stationary-plus-motion retention gate failed. These are distinct
processes and source revisions, not matched causal pairs. Their summaries are
`Build/_AgentValidation/20260924-102959-s13-closeout/reports/final-clean-vulkan/summary.json`,
`final-clean-vulkan-deepwarm/summary.json`, and
`final-post-lifetime-vulkan/summary.json`. Native/descriptor ownership and
repeatability still need a discriminating capture.

Enabling the final-presentation ledger in the pre-lease isolated editor froze
twice on an accepted present with frame slot 0, swapchain image 1, and source
descriptor slot 0. Its check compared the descriptor slot with swapchain image
instead of frame slot even though descriptor capture and production binding use
frame slot. The comparison has been corrected in the Vulkan authority; that
diagnostic then ran in a fresh isolated Release Vulkan editor. Its latest 128
ledger entries were all accepted, 64 had different frame-slot and swapchain-image
indices, none had an invariant failure, and the ledger remained unfrozen. The
complete 393/393 identity manifest was also present. The readback is
`Build/_AgentValidation/20260924-102959-s13-closeout/mcp-output/ledger-corrected-128.json`.
The isolated Editor, Rendering, and Vulkan DLL SHA256 values were respectively
`CC1D61D5DA4A9A2C8180614C7D1AD6770FEE58A8BE54172C0A15F73D3C01EB65`,
`0885928E945B5C086188FCE31B56F70EAA7F4CFB629F6D370AC1B2BB96E5432B`,
and `1D8FF3F81B53BF6F123A30DA360523870683F84FF1C64B23426AA49C3AA652C5`.
The isolated Editor build had zero warnings and errors. The earlier ledger
freeze did not stop rendering or contradict the accepted presentation, but its
later observations were stale until cleared.

## September 24 descriptor-retention owner capture

An isolated Release editor on the frozen fixture identified the camera-motion
retention owner. During 60 seconds at one settled view, accepted presents rose
from 2,847 to 5,805 while tracked descriptor sets stayed at 10,061 and mesh
allocation variants at 1,209. Moving the camera to the profile pose added 360
mesh variants and 1,800 sets; the next 60 seconds held both at 1,569 and
11,861 total sets. Moving to the controlled-motion pose added another 360 mesh
variants and 1,800 sets. Returning to the profile pose added 202 variants and
1,010 sets, while repeating the already visited second pose added none. These
increments are discrete view-transition events, not a measured ongoing rise
while the view is stationary. The two saved Vulkan viewport images differ with
the camera and their GPU readbacks completed; the scene's high contrast limits
pixel-level conclusions. The time series is under this task run's `mcp-output/`
as `retention-owner-before-camera.json`, `retention-owner-profile-camera.json`,
and `retention-owner-series.json`.

A second isolated Release editor used an on-demand read-only key probe. The
single-worker build passed with zero warnings and errors after an MSBuild child
process exited prematurely during the first parallel build. The probe copied
cache scalars under its owner lock and was removed after capture. Its
same-process snapshots were:

| View | Mesh allocations | Local sets | Structural owner groups | Groups with multiple immutable fingerprints | Extra fingerprints |
| --- | ---: | ---: | ---: | ---: | ---: |
| Settled starting view | 408 | 2,040 | 408 | 0 | 0 |
| First controlled camera motion | 780 | 3,900 | 408 | 339 | 372 |
| Return to prior view | 982 | 4,910 | 408 | 339 | 574 |
| Repeat controlled motion | 982 | 4,910 | 408 | 339 | 574 |

Program, material, view-family, and owner-slot distinct counts stayed at 407,
39, 2, and 2 across the first motion. The added keys therefore represent new
`ImmutableResourceFingerprint` values for existing structural owners, not new
logical mesh owners. The cache retains old full-key entries; a new fingerprint
does not release an earlier key, and `LastUsedSerial` has no eviction consumer.
The native ledger's final readback still reported completed presents and no
terminal frame failure. Evidence is `mesh-key-before-camera.json`,
`mesh-key-motion-camera.json`, `mesh-key-return-camera.json`,
`mesh-key-repeat-camera.json`, and `mesh-key-final-profiler.json` in the same
task run's `mcp-output/` folder. Both named sessions were stopped through the
manager.

This identifies the owner of the large motion-phase retention increments.
The sampled component capture below narrows one owner's changing fingerprint;
it does not prove every older variant can be retired. Multiple exact variants
can be needed in one frame.

### Sampled fingerprint component and native texture capture

An additional on-demand probe followed program binding 44 through settled,
profile-camera, and controlled-motion views in an isolated Release editor. It
logged only new allocation keys and rechecked each sampled fingerprint; every
recheck matched. After startup shader linking settled, the sampled structural
owner kept its program, material, view family, owner slot, layout, mapped-arena
identity/generation, mesh-buffer signature, and prepared material-table
signature while its immutable-resource fingerprint changed. Its snapshot had
published binding signatures and no read-only storage bindings. The changing
component was the published exact-sampler and persistent-resource signature,
not the directional-shadow storage-slice candidate.

The same managed `Texture0` and `Texture1` objects remained bound. Their
`VkTexture2D` descriptor generations advanced across the sampled transitions:
`Texture0` was observed unready at generation 0, then ready at generations 2,
4, 5, and 6; `Texture1` was unready at generation 0, then ready at generations
2, 3, and 4. Each later ready generation had different native image, view, and
sampler handles. `SurfaceEmissionTexture` stayed unready at generation 0 in
these samples. This establishes a real native texture replacement as an input
to **this sampled owner's** extra descriptor keys. It does not establish why
the texture wrapper regenerated, that this input explains every multi-variant
owner, or that old descriptors are safe to free while captured commands might
still reference them. The exact key and resource fields are in
`mesh-key-components-first.txt`, `mesh-key-components-second.txt`, and
`mesh-key-components.txt` under this task run's `mcp-output/` folder.

At the final third-session readback, the latest frame outcome was Completed,
6,396 current-generation presents had completed, and Vulkan validation reported
zero errors. Telemetry showed 2,063 mesh allocation variants and 10,315 local
sets. The retirement ledger also held 12 Images, 12 ImageViews, and 12 Samplers
for about 144 seconds. Those old native resources need an ownership trace; the
counter alone does not prove the descriptor cache owns their remaining lease.
The readback is `mcp-output/mesh-components-profiler-third.json`. All three
probe builds passed with zero warnings and errors, the named editor was stopped
through the session manager, and the temporary source probe was removed.

### Exact retired-texture owner and candidate cleanup

A subsequent isolated Release run generalized superseded descriptor-owner
handoff from buffers to images, views, and samplers. Its first live attempt
still retained old textures. A temporary bounded pin probe then sampled the
lifetime ledger at 600 and 1,200 preparation calls. It found respectively 18
and 36 pending image/view/sampler resources; every sampled resource had
descriptor pins, with zero template, recorded, or queued pins. Matching
descriptor owners were `Material[...].Program[...].Frame...` sets, not mesh
local sets. Some old resources had 40 descriptor pins despite their last
graphics sequence already being below the completed sequence. The probe
capture is `mcp-output/resource-pin-pre-material.txt`; the first live readback
is `mcp-output/resource-pin-pre-material.json`.

The candidate now detaches only a `VkMaterial` program state whose live
descriptor set still pins the exact retiring resource generation and whose
native lifetime slot still identifies that set. Its pool and uniform resources
use the existing deferred retirement path; a later bind creates current
material sets for all frame slots. Mesh local-set cleanup likewise uses native
set lifetime slots rather than a mutable descriptor publication counter. In
the next live run, image, view, and sampler retirement backlogs were zero at
frames 738 and 2,546, and bounded probe samples from 600 through 8,400 calls
reported zero pending resources. The frame-8,338 readback had 8,254 completed
presents and zero reported Vulkan validation messages. The visible profile and
controlled-motion viewport readbacks rendered different Sponza views. Vulkan
validation layers were disabled in that run, so a zero message count is not a
layer-enabled validation pass. The temporary probe was removed after capture.
Evidence is `mcp-output/resource-pin-post-material-profile.json`,
`resource-pin-post-material-motion.json`,
`resource-pin-post-material-controlled-motion.json`, and
`resource-pin-capture.txt`, with images under
`mcp-captures/resource-owner-material/` in this task run.

The material correction does **not** close the retention gate. A separate
camera position visited before the controlled motion added about 360 mesh
allocation variants and 1,800 local sets; those variants remained cached even
after the old texture retirement backlog cleared. The extra position also
looked into dark geometry, so its black viewport readback is not used as a
scene-correctness result. The controlled profile and motion images were
visibly populated. The mesh cache hashes a full published resource signature
even when the material set is shared, while its targeted cleanup owns only
local sets. A bounded eviction rule for obsolete full-key variants needs a
separate command-use and frame-preparation lifetime proof.

### Probe-free final-source retention check

The next isolated Release editor was rebuilt after removing the pin probe:
zero build warnings and errors (`logs/final-probe-free-editor-build.log` under
this task run). Its Editor, Runtime Rendering, and Vulkan DLL
SHA256 values were respectively
`62E8C453C39FF1231F291F0D993A83E459B0847C849500F3823D822B23C9F9E1`,
`5840D897392C9E014F2F4B33518F268D4208CA5A119D9112FA316343E2C55ADA`,
and `3B5011863229AFC6819B5200067E502F58CCF6E53493DEB7DB6B1D435259A2D0`.
The frozen Unit Testing World settings SHA256 stayed
`0691A180F2A838A2D97C92E6D067AEDDB666147F9F3FE8E58C0FCD73D4F73B8A`;
the profiler reported workload identity `10991459253885323059`. The fixed
profile view was `(-20,2,4)` and the controlled second view `(-15,3,2)`, both
with identity rotation. A roughly 180-second warmup preceded the stationary
60-second interval. The camera then changed once and held for 60 seconds.

| Endpoint | Completed presents | Native live resources | Tracked descriptor sets | Mesh allocation variants | Image/view/sampler backlog |
| --- | ---: | ---: | ---: | ---: | ---: |
| Warmup end | 11,236 | 15,203 | 11,651 | 1,557 | 0/0/0 |
| Stationary +60 s | 15,019 | 15,203 | 11,651 | 1,557 | 0/0/0 |
| Second view +60 s | 19,272 | 16,844 | 13,276 | 1,873 | 0/0/0 |
| Return to first view | 23,009 | 18,234 | 14,621 | 2,117 | 0/0/0 |
| Repeat second view | 24,570 | 18,234 | 14,621 | 2,117 | 0/0/0 |

The stationary interval passed this narrow retention comparison. The first
view transition retained another 316 mesh variants, 1,625 descriptor sets,
and 1,641 native resources; the return added another 244 variants. Repeating
the already visited second view reused those allocations. This is a
**motion-phase retention failure**, not evidence of stationary accumulation.
Latest frame outcomes were Completed and profiler validation errors stayed at
zero; validation layers were disabled, so this does not satisfy a layer-enabled
validation gate. The second-view Vulkan capture had completed GPU readback and
visibly rendered Sponza geometry. The named editor was stopped by its session
manager. The profiler endpoints are `final-probe-free-warm180.json`,
`final-probe-free-stationary60.json`, `final-probe-free-motion60.json`,
`final-probe-free-return.json`, and `final-probe-free-repeat-motion.json`
under this task run's `mcp-output/`; the final image is under
`mcp-captures/final-probe-free/`.

At this point, a bounded mesh full-key eviction rule was the next hypothesis.
The local-payload probe below superseded it: the affected variants have identical
physical descriptor writes, so correcting their allocation identity avoids
creating them. No age-based eviction was introduced. Multiple genuinely distinct
payloads can still coexist, and existing command-use and retirement ownership
remain authoritative.

S13b remains **Blocked**, not Validated or Closed. A repeated retention failure,
the full required mutation/temporal and multi-view matrix, rejected/retried
publication and disposal proof, matched final-source performance comparisons,
and applicable test clearance remain open. S12 and S13a retain the dependencies
recorded in the parent TODO. No regression tests were added while the live
integration is still under validation.

### Local descriptor identity correction: entry evidence and acceptance

The next live key probe compared the published allocation key with the existing
authoritative local binding walk. Across the first two camera transitions,
360 structural owner groups retained 675 extra published fingerprints while
each group's local physical fingerprint stayed unchanged. Every affected
program's reflected bindings consisted solely of dynamic uniform buffers;
the sampled shadow programs did not consume the textures included in their
captured binding snapshot. This narrows the defect to allocation identity
including unconsumed snapshot resources. Shared-material ownership is one
possible exclusion, but is not required to trigger the defect.

An extended baseline probe then serialized each local dynamic uniform binding's
set/binding, frame slot, native buffer handle and generation, offset, and range.
All 360 affected owner groups had identical physical tuples across their extra
keys, including all five frame slots. Returning to the first view brought the
total to 918 unnecessary variants. The probe endpoints had 1,556, 1,871, and
2,114 total mesh variants; all retirement backlogs were zero. This establishes
duplicate local payloads rather than merely equal hashes. Durable counts are
recorded here; disposable full tuples are in
`Build/_AgentValidation/20260924-102959-s13-closeout/reports/descriptor-payload-baseline.json`.

The reviewed correction is limited to conventional descriptor sets whose local
bindings are known dynamic uniform buffers backed by the mapped arena. Use
the existing local physical fingerprint as allocation identity. A fixed-size
memo may use broad publication and native-buffer revisions to invalidate a
calculated result, but those invalidators must not become native allocation
identity. Normalize the related owner lookup too. Preserve full command
resource signatures, accepted publication checks, and deferred retirement.
Unsupported binding shapes retain the existing path. This introduces no
age-based descriptor eviction.

Acceptance requires repeated and previously unseen camera views to reuse
identical local binding payloads in the same structural scope; genuinely
different payloads may require distinct allocations. Compare actual buffer
handles/generations, offsets and ranges, not counts alone. Require continued
completed presents, inspected images from multiple views, settled retirement
backlogs, and correct refresh after a real local resource change. Remove the
temporary probe and repeat the final-binary stationary and camera-transition
checks. Performance measurements with the diagnostic probe enabled are not
acceptance timing evidence. The existing S13a/S13b gates remain open until
their individual evidence passes.

### Local descriptor identity correction: candidate results

The candidate uses the physical local buffer tuple for both full allocation
identity and owner lookup. Its eight-entry per-mesh memo owns no native resources
and is cleared when descriptors are released. Program/layout/material, captured
resource, native-buffer revision, frame-arena generation, and resolver-source
checks invalidate the memo. An eligible layout whose current tuple cannot be
proved defers preparation and owner reuse instead of silently creating a broad
snapshot-keyed variant. Other descriptor layouts retain their existing path.
Independent source review found no remaining correctness blocker in this scope.
The Vulkan specification separates a dynamic buffer descriptor's buffer,
base offset, and static range from the offset supplied at bind time
([descriptor sets](https://docs.vulkan.org/spec/latest/chapters/descriptorsets.html),
[binding dynamic offsets](https://docs.vulkan.org/refpages/latest/refpages/source/vkCmdBindDescriptorSets.html)).
This supports the local-write identity boundary; engine native-generation checks
also distinguish reused handle values. The correction preserves the existing
descriptor-update and GPU-completion rules instead of updating immutable sets
that recorded commands still use
([descriptor updates](https://docs.vulkan.org/refpages/latest/refpages/source/vkUpdateDescriptorSets.html)).

The isolated Release build succeeded. The candidate Vulkan DLL SHA256 was
`50D3BBF9A581D086E54F08808BFEA00B6CD3E430ABE4CC2B5366873B756FA0B9`.
Settings and workload identity matched the baseline recorded above. Views A and
B were unchanged; previously unseen C was `(-10,2,4)` with identity rotation.
After the approximately 180-second warmup, each A/B/C transition held for
60 seconds, followed by another 30 seconds at C and a 30-second return to A.

| Endpoint | Completed presents | Native live resources | Tracked sets | Mesh variants | Mesh sets | Pending retirement |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| A, warmup complete | 7,454 | 13,753 | 9,821 | 1,194 | 5,970 | 0 |
| B | 10,245 | 13,427 | 9,861 | 1,194 | 5,970 | 0 |
| Return A | 13,979 | 13,572 | 9,981 | 1,194 | 5,970 | 0 |
| Repeat B | 17,864 | 13,573 | 9,981 | 1,194 | 5,970 | 0 |
| Unseen C | 20,794 | 13,573 | 9,981 | 1,194 | 5,970 | 0 |
| Hold C | 23,684 | 13,574 | 9,981 | 1,194 | 5,970 | 0 |
| Final A | 26,251 | 13,574 | 9,981 | 1,194 | 5,970 | 0 |

All sampled frames completed. Image/view/sampler backlogs were zero. Actual
local buffer tuples across 1,600 structural groups produced zero duplicate
allocation variants throughout this sequence, versus 918 in the probed
baseline. The result removes the observed motion-triggered mesh duplication;
total native counts also include other classes and are not asserted constant.
Captures from A, B, and C were inspected and showed different Sponza geometry.
They retain the fixture's existing dark/high-contrast lighting; this is not a
pixel-quality or temporal-antialiasing clearance.

Reloading 122 shader dependency roots produced 14 new structural allocation
identities and no duplicate payload variants. After 60 seconds, presents reached
29,734, mesh variants/sets were 1,208/6,040, tracked sets were 10,121, native
resources were 12,940, and pending retirement was zero. The post-reload capture
was inspected. This checks program refresh; the sampled local arena buffer
handles/generations remained unchanged, so it does not by itself prove an
in-place arena-buffer replacement scenario. No performance claim is made from
this instrumented run. The probe was removed from source before the final build.

### Local descriptor identity correction: final build retention

The probe-free isolated Release editor build succeeded with no warning/error
lines in its build log. Its Vulkan, Rendering, and Editor DLL SHA256 values were,
respectively:

- `911DED29B8CBD834F9366757C6DFD99D25A84620D5BBE74AD3A6D73CA621C2E8`
- `BCF8D55BAD8DEF063BFB992AB45951953E1AE1B371E4DA2F3D88C583A2C61C09`
- `04A71EBCD4673F35F3FAF4D1E619FCAC63FB4B429FDE45672FC2F5F4B64DDBEB`

The binary includes concurrent pipeline changes; do not attribute its lower
starting allocation count, or compare its timings, solely to the descriptor
correction. Settings SHA256 and workload identity remained the values recorded
above. This is a within-binary retention check. The diagnostic key probe is
absent from the final source and build.

| Endpoint | Completed presents | Native resources | Tracked sets | Mesh variants | Mesh sets | Pending retirement |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Warmup 60 s | 2,636 | 11,170 | 7,696 | 801 | 4,005 | 0 |
| Warmup 120 s | 5,127 | 11,187 | 7,696 | 801 | 4,005 | 0 |
| Warmup 180 s, before capture | 7,420 | 11,192 | 7,696 | 801 | 4,005 | 0 |
| A +60 s, after capture | 9,476 | 11,408 | 7,856 | 801 | 4,005 | 0 |
| B +60 s | 12,113 | 11,409 | 7,856 | 801 | 4,005 | 0 |
| Return A +60 s | 14,750 | 11,409 | 7,856 | 801 | 4,005 | 0 |
| Repeat B +60 s | 17,399 | 11,409 | 7,856 | 801 | 4,005 | 0 |
| Unseen C +60 s | 19,903 | 11,410 | 7,856 | 801 | 4,005 | 0 |
| Return A +60 s | 22,391 | 11,410 | 7,856 | 801 | 4,005 | 0 |
| Stationary A, another 60 s | 25,961 | 11,410 | 7,856 | 801 | 4,005 | 0 |

The first interval after warmup includes a screenshot and gained 160 tracked
sets outside the mesh allocation count. That interval is not reported as flat
stationary native retention, nor is the capture assumed to be its proven cause.
The later stationary interval had no intervening capture or camera mutation and
was exactly flat. Mesh variants and local sets were flat throughout; all sampled
frames completed, binding failures/skipped draws were zero, and every retirement
class backlog was zero. Captures from A, B, and C were viewed. This validates the
observed duplicate-local-descriptor correction within the stated fixture; the
broader phase retention and observer matrix remains open.

Disposable endpoints are `mcp-output/local-final-*.json` and exact binary hashes
are `reports/descriptor-final-hashes.json` beneath the current task run. The
table preserves the required results independently of those ignored files.

### Renderer restart follow-up exposed during validation

The same probe-free binaries were restarted with standard and synchronization
validation requested. The runtime reported `validation_enabled=true`. A/B/A
samples completed at 3,100/5,539/7,669 presents, with 803 mesh variants and 4,015
mesh sets throughout and no pending retirement. The last-frame validation and
binding-failure counters were zero. These counters are frame-local; Release
debug logging is compiled out, so empty log files do not establish cumulative
zero validation errors. A cold cumulative snapshot is now being exposed through
`get_render_profiler_stats.vulkan.validation.cumulative` using the existing
device-owned diagnostic store.

The supported `restart_renderer` operation returned success, but the recreated
renderer stopped advancing after 73 presents. A later sample had 4,836 frame
attempts and 4,762 recorded readiness failures. It rejected at
`ResourcePrepare` / `RequiredUploadCompletion`, with dependency chain
`DesktopScene -> canonical scene texture -> exact descriptor` and ticket
`texture-upload:0:0`. The diagnostic was:

> Required texture generation 2 was canceled or superseded before Vulkan upload registration (published=2, currentUpload=2).

The captured viewport was black. The device remained operational; pending
retirement was zero. This is a failed positive lifecycle check, and the restart
tool's initial success does not count as sustained rendering success. Source
inspection shows this message can mean that a new backend has no upload ticket
for an already published logical texture generation; it is not proof that an
existing Vulkan ticket was canceled. The isolated session was stopped after
capturing evidence. `mcp-output/local-validation-renderer-restart.json` and
`local-validation-restart-A.json` record this attempt. Investigation continues;
the ordinary camera-retention result above is preserved independently.

The root cause is a device-lifetime mismatch: the process-wide texture streaming
registry preserves its published generation while the new Vulkan upload service
starts with an empty native generation ledger. The repair atomically claims a
newer streaming generation from retained resident mip data and submits it through
the exact requesting service. The old accepted frame retries until a later frame
captures the newly published generation; it never treats the old generation as
native-ready. Recovery allows a canceled competing transition to have advanced
the upload counter, preserves payload dimensions, and bounds each service to one
owned recovery attempt. Admission and successor failures remain explicit, while
a genuine newer transition or publication can supersede a prior failure.
Focused independent source review found no remaining actionable blocker. Live
restart validation of the corrected binary is recorded below.

The corrected isolated Release build completed with zero warnings and errors.
Its Vulkan, Rendering, and Editor DLL SHA256 values are, respectively:

- `16124F5D2295FB72F95667815A8E442DDC8BDB2862193F5CDFA5E56FBF0879EE`
- `2E66A88C4F6DE0CD479C41384D43D7275ECAAAF6B83649377EE345F688FDA192`
- `A2C6A4BE217BA8201B3946A4821DAAC4AABCB9BEE9A7096AFC40D20C1B8CB973`

The cumulative diagnostic snapshot confirms standard validation,
synchronization validation, and the debug messenger are active. It exposes two
startup errors per device: `VUID-VkPhysicalDeviceProperties2-pNext-pNext` for
structure type `1000135008`, and `VUID-VkDeviceCreateInfo-pNext-pNext` for
`1000135009`. The installed validation layer identifies its header as 1.4.328
and also warns that `VK_EXT_descriptor_heap` is unknown. Those two numeric
types match the engine's descriptor-heap properties/features declarations;
the [current Khronos extension reference](https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_descriptor_heap.html)
lists both structures as legal extensions of those chains. This supports a
validation-layer compatibility diagnosis, not zero-error validation clearance.
Keep the messages visible; no suppression or machine-wide SDK change was made.

### Corrected renderer restart: repeated live validation

Using the same corrected binary and settings, two successive in-process Vulkan
restarts were followed by A/B/A camera sequences, with 60-second holds at each
endpoint. Resource authority changed from 1 to 2 to 3, confirming device-context
replacement. Every sampled frame completed, binding failures/skipped draws were
zero, and pending retirement and all retirement-class backlogs were zero.

| Endpoint | Completed presents on current device | Native resources | Tracked sets | Mesh variants | Mesh sets |
| --- | ---: | ---: | ---: | ---: | ---: |
| Before restart, A | 1,106 | 13,373 | 9,831 | 1,195 | 5,975 |
| Restart 1, A | 2,075 | 4,357 | 3,761 | 14 | 70 |
| Restart 1, B | 4,793 | 9,298 | 5,846 | 407 | 2,035 |
| Restart 1, return A | 7,891 | 9,348 | 5,886 | 407 | 2,035 |
| Restart 2, A | 1,799 | 4,366 | 3,761 | 14 | 70 |
| Restart 2, B | 4,506 | 9,352 | 5,886 | 407 | 2,035 |
| Restart 2, return A | 6,678 | 9,353 | 5,886 | 407 | 2,035 |

The latest-readiness-failure field retains historical retries. In restart 1 its
sequence stayed 111 from B to return A, while completed presents advanced
4,793 to 7,891. In restart 2 it stayed 168 while presents advanced 4,506 to 6,678.
Those transient retries correctly waited for newly claimed upload generations;
the persistent 73-present black-viewport failure did not recur. Every A/B/A
capture was viewed, with changed geometry at B and restored geometry at A.
The fixture still has dark/high-contrast lighting, temporal-quality differences,
and editor selection outlines; no image-quality or temporal gate is cleared.

The mesh counts measure live generic `VkMeshRenderer` allocations, not all
Advanced scene draws. Device teardown removed old cached allocations. The first
B view rebuilt 393 mesh variants and 1,965 sets; return A added none in either
restart. Advanced canonical scene rendering has a separate descriptor/draw path.
The second restart's A/B diagnostics confirmed `executionAdmitted=true`,
`reservationCurrent=true`, preparation `Ready`, and a `Prepared` canonical frame
package. Stage frame IDs advanced 12,694 to 15,526, all 13 stages matched the
current output, and seven had `BackendEnqueueAccepted`. These markers are not
individual GPU-completion certificates. Together with advancing presents and
camera-dependent captures, they establish fresh rendering after recovery. Do
not compare pre/post-restart generic counts as a matched retention benchmark.

Cumulative errors stayed at exactly the two startup compatibility reports for
each device; there were no additional rendering/recovery errors and no message
overflow. Warnings were 14 before restart and 13 on each replacement device,
including 10 suppressed unused-color-attachment warnings. The snapshot resets
on device recreation, so this does not certify teardown of the preceding device.
The cold snapshot remains available in Release builds and separates cumulative
evidence from the existing last-frame counters.

This passes the reproduced restart/rehydration failure and the repeated-view
mesh-allocation check for this fixture. It does not complete the full failure
injection, in-place arena replacement, world replacement, multi-view, observer,
matched-performance, or test-clearance gates. S13a/S13b remain **Blocked**.
Evidence is `mcp-output/rehydrate-*.json`, viewed PNGs, and
`reports/descriptor-rehydration-hashes.json` under the current task run. The named
isolated editor was stopped after validation; no new regression tests were added.

### Automated evidence collection awaiting review

At the user's request, `Tools/Collect-VulkanLifecycleEvidence.py` was written and
run to collect evidence first, leaving screenshot and log interpretation for a
subsequent review. Its [usage and scope](../../testing/rendering/vulkan-lifecycle-evidence-harness.md)
describe the collection contract and untested cases. The isolated Release build
completed with zero warnings and errors. The collector was corrected to wait
for world readiness after MCP startup and to avoid a Python argument-name
collision; missing mutation cases were then rerun with the same binaries.

The completed collection contains 21 screenshots: 14 warmup/stationary/camera/
shader-reload/restart observations, followed by seven warmup and reversible
transform/activation/material observations. Requests, raw replies, per-interval
samples, cumulative validation messages, binary hashes, and available logs were
saved. Both runs ended with the named session stopped. The world-roundtrip case
is incomplete because the editor returned `Serialization failed during
'snapshot_world_state'`; no restoration success is claimed.

The disposable review index is
`Build/_AgentValidation/20260924-102959-s13-closeout/reports/python-lifecycle-review.md`,
linking `python-lifecycle-ready` and `python-lifecycle-mutations`. Collection
statuses are not reviewed rendering outcomes. Screenshot/log review is deferred
by the user's explicit instruction; S13a/S13b remain **Blocked**, with no new
gate clearance or regression-test claim from this run.

The collection's Editor, Rendering, and Vulkan DLL SHA256 values were,
respectively:

- `4A1BDC3BFD1899C56FC3CA6487A4800DA7EB0772D69DDB6078F915C20F328F36`
- `9FE370A5434F0481AA5BF0A7FAA5432453E159122A63F38839BE271AA9F8D07F`
- `984E458E27C6DAEEDB56254DC70A27698C13A91080553055601E657502FD7E30`

Follow-up source inspection narrowed the snapshot failure to
`AssetManager.Serializer.Serialize(world)` in
`EditorMcpActions.ExtendedWorkflow.TrySerializeYamlForMcp`. The catch block emits
the exception only through `Debug.LogError`; that method's body is guarded by
`DEBUG || EDITOR` and is absent in the Release runtime used here. The MCP reply
retains only the generic serialization failure, and the captured logs contain
no underlying exception. The offending object/property is therefore unknown.
Restoration was never attempted. Add bounded diagnostic detail, reproduce only
the snapshot call, and fix the identified serialization contract before retrying
world replacement. This is not evidence of a Vulkan resource-lifetime defect.

The [remaining closeout work](../../todo/rendering/vulkan-stall-remediation-todo.md#september-24-handoff-remaining-closeout-work)
is the current resumption checklist, including required evidence and ordering.
The current work session is wrapped up with the isolated editor stopped;
S13a/S13b remain **Blocked**, and screenshot/log review and test clearance remain
pending.
