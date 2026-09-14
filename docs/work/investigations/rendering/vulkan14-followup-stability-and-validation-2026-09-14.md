# Vulkan 1.4 follow-up stability and validation — 2026-09-14

Status: selected code and validation follow-ups complete. D4 remains blocked by
unavailable GPU-counter access; I2 remains unsupported on the available device.
This follows the user's authorization to repair the
remaining test/API mismatches, reproduce the E/G stability findings, strengthen
I3's pending-GPU-work proof, and pursue D4/I2 only when their evidence gates are
available. Broader Slang conversion remains outside the selected scope.

## Work and acceptance

| Area | Existing evidence / issue | Required result |
| --- | --- | --- |
| Automated tests | UnitTests compilation failed on outdated visibility, picking, swapchain-lifetime and blit contracts; targeted tests did not execute | Preserve test intent while updating current API fixtures and source-contract checks; build and run the narrow relevant tests |
| E mutation / streaming | Historical indexing captures each rejected a frame before recording/submission without a diagnostic terminal cause | Reproduce on current source, identify an actual failure before changing code, fix any confirmed ownership/readiness defect, and repeat sustained controls |
| G resize | Long Immediate and address-root runs declined at the 800×450 logical viewport transition despite initial family admission | Propagate the exact decline reason, correct the responsible history/package/viewport transition, and validate shrink/restore in both variants |
| I3 retention | Both lightweight native submissions completed before the immediate completion query; overlap assertion failed | Demonstrate that the exact submission is pending while old-generation resources are retained, then complete and retire them with bounded failure cleanup |
| D4 / I2 | Prior Nsight counter access failed; the measured adapter did not advertise address-command support | Keep the existing safe policy unless current capability/profiling evidence supports another experiment; report unavailable prerequisites explicitly |

The existing H run is reused at
`Build/_AgentValidation/20260910-060112-vulkan14-h/`. Scratch outputs remain
disposable; this note will retain findings, commands and results. Unrelated
mirror/OpenXR work in the shared checkout is preserved. Tests are explicitly
authorized by the user's follow-up; runtime fixes still require actual feature
validation before adding regression coverage for them.

## Investigation log

- Re-read the final qualification, E/G failure notes and I3 allocation evidence.
- The E2 background replay/scissor fix is complete and separate from the old
  indexing mutation/streaming rejection samples.
- Started a fresh isolated Release RenderBench build for current-source G
  reproduction. The original Phase G fixture is retained under the current H
  run so the old script and parameter choices can be compared directly.
- Test maintenance and the read-only I3 lifetime design review run independently
  from the coordinator's GPU reproduction work.

### Current-source reproduction and build boundaries

- The fresh Release RenderBench build passed with zero warnings/errors in
  29.67 s after transient concurrent OpenXR compilation failures were resolved.
- The original long Immediate/GraphicsOnly resize fixture reproduced its failure
  at the 800×450 transition with 80 warm-up and 400 requested measured frames.
  Active resources matched 800×450, no replacement was pending, and Advanced
  family admission was `Admitted/Ready`. This is a confirmed current failure,
  independent of the address-root variant.
- Added `XRRenderPipelineInstance.LastRenderDeclineReason` and included it in
  RenderBench's failure diagnostic. The value is retained independently of
  logging and clears at the next pipeline render attempt. The diagnostic build
  passed with zero warnings/errors in 29.48 s.
- A later run was blocked during bootstrap by concurrent OpenXR tracking code
  checking full device readiness before the renderer had created its device.
  This is distinct from the reproduced resize failure. A misplaced tracker
  release-index field was moved from the validation ledger to the in-flight
  submission that actually owns and uses it, restoring that compile contract.
- The tracker bootstrap guard now checks lifecycle health instead of full
  device readiness. Its constructor only creates CPU tracking/callback state;
  actual reservation/submission methods still require the initialized,
  operational device. Loss, quiescence and disposal continue to reject access.

### Automated checks

- The six exact tests listed in the final-validation note now pass: 6/6,
  137 ms. The successful build includes the edited fixture sources.
- The initial broader run exposed stale Advanced layout/shader expectations
  and swapchain frame-operation fixture setup. Those fixtures were repaired
  while preserving their intended contracts. The combined current-source
  rerun passed **95/95**, with no failures or skips, in 566 ms.
- Review then strengthened the Advanced frame-contract checks to preserve
  stage ordering, timer enclosure, concrete pass domains, dependencies and
  resources. The final targeted run passed **9/9** in 234 ms.
  Evidence: `reports/followup-tests/followup-combined-current-rerun.*` and
  `followup-advanced-frame-contract-final.*`. These are selected subsystem
  checks, not a claim that every repository test was run.
- A later class-wide expansion ran 335 tests: 284 passed and 51 failed; all
  original 95 selected cases passed within it. Most additional failures use
  stale source paths/signatures after renderer splits. Native review identified
  three stale generation-key/publication expectations and one older, separate
  camera-unavailable AO feature-mask snapshot defect. These broader failures
  are not evidence of a G regression and are not silently marked passed.
  Evidence: `reports/followup-tests/followup-final-contracts.*` and
  `expanded-failure-classification.md`.
- The final E build reran the original selection: **95/95**, zero skips,
  537 ms. Three additional material readiness/publication checks also pass,
  856 ms. One still referred to the removed `HasTimelineValueCompleted` helper;
  it now checks the current `Synchronization.QueryTimelineCompletion` call,
  retaining the surrounding completed-slot and row-publication assertions.
  Evidence: `followup-e-final-exact.*` and
  `followup-e-material-contracts-final.*` under `reports/followup-tests/`.
- The final native frame-slot fixture also passes for two and three slots,
  **2/2**, zero skips, approximately one second. These three final selections
  contain **100 distinct passing cases**. The fixture supplies and disposes its
  own runtime work scheduler when one is not already installed; renderer cleanup
  precedes scheduler shutdown. Evidence: `followup-e-final-native-frame-slots.*`
  and `followup-final-selected-evidence.json`. The separate 335-case expansion
  is still not a passing suite. Its older AO issue is
  `DefaultPipeline_CameraUnavailableResizeThenFramePrepareKeepsOneFeatureSnapshot`,
  where the camera-unavailable snapshot loses AO enable/mode bits.
- The [committed filter](../../../examples/rendering/vulkan14-followup-test-filter.txt)
  then ran all 100 selected cases together successfully, with no failures or
  skips, in approximately two seconds (`followup-final-canonical-selection.*`).
  Reproduction is included in the [final ledger](vulkan14-final-validation-2026-09-14.md#validation-and-handoff).

### Capability and I3 evidence

- Fresh `vulkaninfo` still reports RTX 3090 / driver 610.88 / Vulkan 1.4.341,
  with buffer device address and unified image layouts but no
  `VK_KHR_device_address_commands`. I2 remains a capability-gated exclusion.
- The I3 probe appends a private timeline wait to the exact production
  submission, latches the pending receipt plus old-generation retention evidence,
  then host-signals the gate in `finally`. The semaphore itself stays owned until
  receipt completion. Normal receipt completion and frame-slot drain must then
  demonstrate reclamation. This avoids depending on GPU workload duration and
  proves resource lifetime only, not placement or physical transfer cost.
  The implementation follows the Vulkan timeline
  [submit-value contract](https://docs.vulkan.org/refpages/latest/refpages/source/VkTimelineSemaphoreSubmitInfo.html)
  and [host signal operation](https://docs.vulkan.org/refpages/latest/refpages/source/vkSignalSemaphore.html).

- The corrected `reports/followup-i3-pending-sync` run passed two fresh
  24-frame children at 1920×1080 with three frame slots. Standard and
  synchronization validation were both active: zero errors and four loader
  warnings per child. The gate was armed, sampled and released; the exact
  receipt was pending while the old buffer remained retained. Reclamation was
  observed after completion and slot drain, with no premature reclamation.
  The earlier `followup-i3-pending` invocation used incorrect environment
  variable names and had synchronization validation disabled; it is excluded
  from that qualification. See the [I policy note](vulkan14-phase-i-policy-validation-2026-09-14.md).
- The final rebuilt control `reports/followup-i3-final` repeated
  both 24-frame children with three slots and passed the same pending-retention,
  completion and reclamation gates. Standard and synchronization validation
  remained active, with zero errors and four loader warnings per child. The
  intentional timeline gate was enabled; temporary tracing was removed.
- A fresh Nsight Graphics 2026.2 GPU Trace attempt successfully attached to the
  presenting Vulkan editor, then failed with `GPU Performance Counters
  unavailable`. No global counter permission or clock setting was changed.
  The launcher and its owned children were stopped. D4 still requires usable
  scheduling evidence before another specialization experiment; there is no
  calendar-based deferral. Logs: `logs/followup-ngfx.*`; ownership receipt:
  `reports/followup-trace-owned-processes.json`.

### G resize diagnosis

The retained decline reason exposed inconsistent resource-generation keys:
preparation used the physical 1280×720 output while the viewport/package and
commit validation used the resized logical 800×450 extent. Resource preparation
now uses the viewport's logical extent when present and retains the output's
format/sample/layer contract. The explicit benchmark coordinator also treats a
bounded incremental generation build as pending admission rather than a fatal
error. Its existing time/attempt limit remains in force.

Both root variants then completed 480 requested frames plus the intermediate
capture frame with zero standard/synchronization validation errors. However,
visual inspection found the first shrink frame uniformly black; the later
shrunken frame contains the boxes and restoration reproduces the initial bytes.
Those runs are intermediate evidence, not a passed resize qualification.

A RenderDoc capture narrowed the remaining error: the native HDR output contains
all six boxes, but the postprocess draw's pass-uniform block has `ScreenWidth=0`
and `ScreenHeight=0`. Shader debugging shows screen coordinates collapsing to
the clamped corner instead of sampling the scene. Other view/color-grade
parameters are populated. Native readback is black; replay's uninitialized
stencil content additionally produces a yellow outline, so the replay color is
not used as the original symptom. Evidence: `renderdoc/followup-g-resize/` and
`reports/followup-g-renderdoc-02/`.

The cause was a frame-slot-count mismatch. The explicit target used three
slots, while descriptor/uniform storage still used the default two. On slot 2,
the resized pass values were written into the third buffer, but its descriptor
selected slot 1's buffer and an uninitialized range. The frame-loop constructor
now publishes the explicit target's slot count before descriptor or uniform
allocation. Both preparation paths also agree on logical viewport dimensions.

Final Immediate and BufferDeviceAddress controls each submitted **481 frames**
and returned 397 completed GPU timings. Both had zero standard/synchronization
validation errors and four loader warnings. The first resized image matches
the steady resized image, restoration matches the initial image, and all four
images match between variants. Resized/restored PNGs were viewed. SHA-256 and
full workload details are retained in the [G note](vulkan14-address-shading-root-2026-09-10.md#september-14-resize-closeout).
The temporary tracing was removed before these builds. This closes the resize
correctness issue without changing the default Immediate policy.

### E current-source evidence

The forced-recording streaming cohort reproduced an attempted descriptor write
against retired buffer generation 5569. The lifetime authority correctly
rejected it. Compute snapshots carried a raw handle without a captured native
generation or reservation, allowing buffer replacement between snapshot capture
and descriptor publication. Exact-generation validation and a typed pre-acquire
retry now reauthor a superseded snapshot. Supplied UBO/SSBO bindings are checked;
absent generated/fallback UBOs still resolve through their normal path. Stable
descriptor-set failures remain terminal. Final native review accepted this
scoped fix; initial fresh streaming controls contain no retired-buffer exception.

A separate all-sample counter failure was attributed to `VkDataBuffer` mapping
8192 bytes during warmup, while capture-window bytes remained zero. Its queued
mapping callback obscures the original producer, so a temporary buffer-identity
probe was used. The same trace identified an eight-byte startup CPU
auto-exposure fallback. The 8192-byte map is consistent with the LOD transition
buffer (512 four-uint records with matching usage/storage/range flags), but its
queued stack and empty name do not prove the original producer. Both temporary
readback probes are now removed.

The generic harness now has an explicit `All` (default) or `Capture`
zero-readback validation scope. The Vulkan baseline selects `Capture`, matching
the existing self-iteration steady-state policy, and preserves all startup
counts. Interval-10 samples had missed these isolated events; the final
correctness runs use interval 1. A nonempty render-exception log independently
invalidates a cohort, including exceptions outside sampled frames. These
workflow changes are documented in the [profiler guide](../../../developer-guides/diagnostics/profiler.md).

Interval-1 controls also exposed occasional pre-acquire rejected frames that
the sparse samples missed. Five of eight mutation/streaming cohorts passed all
gates; three had one or two rejected frames. All eight had zero capture-window
readback bytes or mapped buffers and no render-exception file. Every-frame
startup totals were 8 bytes/one map for material edits and 8200 bytes/one map for
streaming, reconciling the earlier sparse-sample blind spot.

Schema v9 now retains the existing PresentNow diagnostic with renderer authority,
frame, slot and output-generation correlation. The subsequent 60-second
synchronization-validation run recorded 1596 capture samples, zero VUIDs,
zero capture readbacks, but three rejected frames. Correlated failures identify
`PipelineCompilation / immutable-storage`: material-table backing growth was
queued on the native allocation worker and preparation requested another frame.
This is separate from the superseded compute-buffer fix. Missing ImGui events
on rejected frames also changed the workload identity. Neither the strict
rejection gate nor the workload hash is weakened to conceal that consequence.
The reserve implementation and final controls below close that allocation gap
for the qualified mutation/streaming workloads.

### E bounded reserve implementation

The material map now keeps at most one completed and one pending ownerless
reserve for each arena identity, arena generation and frame slot. Exact and
same-owner banks are preferred. A new owner can claim an adequate completed
reserve; claim assigns the current slot reset epoch, clears publication/page
state and forces a full upload before publication. The reserve itself has never
been submitted, so its identity does not depend on a reset epoch. It cannot be
used to steal another owner's live bank.

Successful preparation, including exact reuse, schedules replenishment one
allocation class above that slot's largest successful demand, capped at the
device storage-buffer and managed-array limits. Replenishment is advisory:
setup/allocation failures preserve the already-ready publication, are retained
in diagnostics, and use bounded retry suppression. At most three of the four
allocation positions can hold reserve work, preserving one for demand. A burst
larger than the available reserve still takes the existing typed pre-acquire
retry path; arbitrary growth is not promised to be free of retries. The shared
FIFO worker can place demand behind up to three already-queued reserves.

Teardown joins the allocation worker before retiring completed or pending
storage. Separate cumulative queued, ready, claim and failure counts plus
reserve occupancy are available through the native diagnostic API and schema-9
profiler. Existing demand-growth/bank/pending counters keep their original
meaning. Native review found no remaining blocker in lifetime, allocation
admission, exception containment, capacity bounds or diagnostic lock ordering.

The graphics persistent-artifact cache is also contained: ordinary buffer
snapshots without artifact-owned allocation leases use the existing frame-owned
snapshot path. An unrelated engine or publisher generation no longer admits
their raw native handles into a cross-frame artifact. The selected Vulkan
backend and GPU submission strategy remain in use.

The isolated Release Editor and RenderBench builds containing this implementation
and schema-9 diagnostics passed with zero warnings/errors in 44.16 s and
39.11 s. The later RenderBench timeout-diagnostic-only build passed in 3.31 s;
it does not change the frozen Editor used for desktop controls.

### E native material control

The first invocation used only 48 streaming boundaries and failed four children
while their upload was still progressing. Additional timeout diagnostics showed
ten submitted chunks, nine completed chunks and one in flight, with no failed
ticket or stalled preparation worker. These failures remain retained in
`reports/followup-e-standby-native/` and
`reports/followup-e-material-diagnostic/`.

Using the existing guide's 240-boundary budget, all four normal/reversed-depth
children passed at 1280×720 with three frame slots. Each submitted 164 production
frames, completed all 31 texture chunks, and matched three receipt-gated native
material snapshots to their immutable CPU bytes, owner and generation. Scalar
and texture/sampler changes each touched exactly one 64-byte row. Descriptor
closure generations were 1, 1 and 4. Every slot was warmed; subsequent idle
counts remained page writes 9→9, descriptor writes 5→5 and closure acquires 4→4.
There were three owned material banks and zero pending demand allocations.
Standard and synchronization validation were enabled with zero errors and four
loader warnings per child. Evidence: `reports/followup-e-material-standard/`.
Review traced the older guide's different admission timing to the intentional
published-generation-only policy introduced by commit `65ad14a03`. A new texture
has published generation zero and is omitted from the required upload manifest;
ordinary streaming advances across accepted frames. The affected indirect pass
is skipped until descriptor publication. No accepted plan references an
unpublished image, but those intermediate receipts do not prove a last-good
textured draw or strict required-texture admission. This is separate from the
ordinary buffer-artifact containment and is not reversed by this follow-up.

The scenario, evidence names and [material guide](../../../developer-guides/rendering/renderbench-phase53-materials.md)
now state that policy explicitly. The material scenario's CLI default is 240
boundaries, matching the streaming lane. Its final reporting build passed with
zero warnings/errors in 4.68 s. Running without `--scenario-frames` validated that
default and repeated all four passing children with the same counts above.
Each final child records `TexturePublicationPolicy=PublishedGenerationsOnly`,
`StrictRequiredTextureAdmissionProven=false`, and 153 accepted receipts whose
following query still found an unpublished texture. Final evidence:
`reports/followup-e-material-reporting-final/` and
`reports/followup-e-material-final-evidence.json`.
The final metadata-only correction explicitly marks these cold native reads as
`DiagnosticReadbacks=true` in parent and child reports. Its Release build passed
with zero warnings/errors in 4.75 s, and `reports/followup-e-material-closeout/`
repeated all four passing controls with the same 164/153 frame counts, 31
completed chunks and clean validation. These native reads are not part of the
desktop zero-readback qualification.

### E final desktop qualification

Eight fresh Editor processes exercised both reuse policies twice, reversing the
second round's order. Each used a 20-second warmup, 1920×1080, the committed
Vulkan14 cohort, Vulkan/`GpuIndirectZeroReadback` with descriptor indexing,
`BindlessMaterialTable`, `DevelopmentProfile`, and synchronization validation.
Samples were retained every frame. This is correctness evidence, not a timing
comparison against the earlier release benchmark.

| Workload | Capture seconds per run | Allowed samples, rounds 1 / 2 | Forced-recording samples, rounds 1 / 2 |
| --- | --- | --- | --- |
| Material edits | 45 | 1220 / 1098 | 1184 / 1172 |
| Streaming | 60 | 1674 / 1600 | 1666 / 1466 |

All **11,080 capture samples** report standard and synchronization validation
enabled, with a maximum validation error count of zero. Every cohort has one
stable workload identity, zero rejected frames, zero matched readiness failures,
zero capture readback bytes/maps, and no render-exception log. Across captures,
eight completed reserves were claimed while demand-growth and reserve-failure
counts remained unchanged. Both reuse policies retained the selected GPU path.

All 12,211 samples, including warmup, remain available. Startup contained six or
seven correlated readiness retries per process; material-edit startup recorded
eight readback bytes/one map, and streaming startup recorded 8200 bytes/one map.
Those costs are retained and excluded only by the explicitly selected capture
scope. Cold allocation and bursts exceeding the bounded reserve may still retry.
The earlier failed cohorts remain failed; no failure or workload-identity gate
was relaxed. The final matrix is in `reports/followup-e-final-matrix.json`, with
per-run raw samples, exact arguments/environment, assembly hashes and correlated
diagnostics in `reports/followup-e-standby-*/`.

Reproduce with `Tools/Measure-GameLoopRenderPipeline.ps1` and the committed
`XREngine.Benchmarks/VulkanPerformance/Cohorts/vulkan14-phase-c.jsonc`. Pin
`RenderBackend=Vulkan`, `Strategies=GpuIndirectZeroReadback`,
`ZeroReadbackMaterialDrawPath=BindlessMaterialTable`,
`ZeroReadbackValidationScope=Capture`, `ProfileMode=DevelopmentProfile`,
`VulkanValidation=true`, `VulkanDiagnosticPreset=SyncValidation`,
`SampleIntervalFrames=1`, `WarmupSec=20`, and the durations in the table.
Use desktop, static camera, canonical directional light, uncapped presentation,
dynamic rendering, GPU Hi-Z, and a 1920×1080 window/viewport at render scale 1.
Enable command chains, parallel chain recording and parallel secondaries.
Allowed enables primary reuse with force-rerecord false; ForceRecording disables
primary reuse with force-rerecord true. Disable MCP diagnostics and P3 logging,
set `NoStabilityGate=true` for the continuous mutation workloads, require at least
one GPU scene command, and keep `AllowWorkloadIdentityChanges=false`.

For each process, set `XRE_VK_DESCRIPTOR_BACKEND=DescriptorIndexing`,
`XRE_UNIT_TEST_RENDER_PIPELINE=DefaultRenderPipeline`,
`XRE_PROFILE_MUTATION_WORKLOAD=MaterialEdits|Streaming`,
`XRE_VULKAN_SYNC_VALIDATION=1`, `XRE_VULKAN_VALIDATE_SPIRV=1`,
`XRE_VULKAN_ALLOW_CPU_MESH_SAFETY_NET=0`, and `XRE_SKIP_IMGUI=1`.
Streaming uses `Assets/Rive/SplashScreen.scale-200.png` through
`XRE_PROFILE_STREAMING_ASSET`. Use a task-local Vulkan pipeline-cache root,
warm-cache mode, an already-built frozen Release Editor and a fresh report
directory for each process. Read actual validation-enabled/error counters and
correlate readiness failures by authority/frame/slot/output generation; do not
infer a clean run from requested flags alone.

### Remaining gates

D4 is still the sole unchecked implementation item in the modernization TODO:
counter access failed, so no new barrier specialization or performance promotion
is selected. I2's evaluation is complete with an unsupported-device decision.
The selected E/G/I3 follow-ups and 100 automated checks are complete; the separate
51 broader test failures, strict required-generation texture admission,
cross-vendor coverage and physical-headset qualification are outside this
follow-up's delivered claims. Broader Slang conversion remains out of scope.
All editor/benchmark/test processes started for these controls exited. No
external Monado service or user-owned editor was stopped. Final PowerShell
parsing and `git diff --check` pass; all 85 local Markdown targets checked in
the affected guides/notes resolve. The TODO has 35 checked items and D4 unchecked.
