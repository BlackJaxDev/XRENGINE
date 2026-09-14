# Vulkan 1.4 Phase I policy validation — 2026-09-14

## Status

I1 completed on the NVIDIA GeForce RTX 3090 (driver 610.88) with six fresh
processes per policy. The matched comparison is eligible: all 12 runs passed
their stability gates, produced completed GPU samples, and produced the same
output SHA-256:

`23DD60C548667CED35765A51FA0BD64A3E20E6A20F0CDA84C85EC22A411EB238`.

The retained image-layout policy is **specialized**. I3 collected real
allocation and retirement evidence and now proves retention while the exact
submission remains pending, followed by reclamation after completion and slot
drain. The earlier lightweight probes completed before their host query and
remain failed attempts. The final bounded lifetime result does not justify an
allocator or BDA placement policy change.

## I1: image-layout policy

The bounded harness is
`Build/_AgentValidation/20260910-060112-vulkan14-h/scratch/Invoke-Vulkan14PhaseIValidation.ps1`.
It runs the existing 1920×1080 eight-pass GPU layout recipe in fresh processes,
with a deterministic seeded-random or alternating order of equal specialized
and `GENERAL` repetitions. Each result must have the same executable, adapter,
driver, recipe/workload identity, passing stability gates, and output hash.

The paired analyzer writes `phase-i-measurement-summary.json`. It reports CPU
and completed-GPU median/p95 distributions and deliberately emits no automatic
policy decision. A result with missing/zero completed GPU samples, mismatched
identity, non-identical output hashes, failed gates, or unsupported ordinary
unified-image-layout admission is a failed comparison, not evidence for
`GENERAL`.

The six-by-six matched distributions were:

| Policy | Completed GPU median / p95 | CPU median / p95 | GPU samples |
| --- | --- | --- | --- |
| `specialized` | 107.328 / 224.576 microseconds | 114.800 / 397.100 microseconds | 4,320 |
| `GENERAL` | 107.040 / 338.656 microseconds | 113.800 / 400.200 microseconds | 4,320 |

These are pooled per-frame samples, not medians or p95s calculated once per
run. `GENERAL` has a 0.288 microsecond lower pooled GPU median and a 114.080
microsecond higher pooled GPU p95 (about 51%). The per-run spread was not used
to claim a statistically significant slowdown. Retain `specialized`
conservatively: the bounded result does not establish the material, repeatable
benefit required to promote broad `GENERAL` admission. The experiment does not
change video unified-layout admission, external-image, initialization, transfer,
queue-ownership, or lifetime transitions.

## I2: device-address commands

This remains deferred separately from buffer device address. The existing RTX
3090 inventory did not advertise `VK_KHR_device_address_commands`; no feature,
entry point, or fallback is introduced by this validation plan.

## I3: retained native-buffer placement and lifetime

The corrected harness ran the existing `phase52-buffers` production scenario at
1920×1080 in two repetitions (24 frames per repetition) with standard Vulkan
validation and synchronization validation enabled. Both runs observed the
C-1/C/C+1 boundary as 7/8/9 commands, with DrawMetadata growing from a
512-byte allocation to a new 1,024-byte allocation. These were VMA-owned,
mapped, host-visible and host-coherent buffers on memory type 3 / heap 1
(25,293,660,160 bytes); they were not device-local. The LateDrawIds
post-recording growth changed from 64 bytes/generation 2071 to 80
bytes/generation 2196, also VMA-owned and mapped, on memory type 4 / heap 1
with HostVisible, HostCoherent, and HostCached properties. Each run's native
validation reported 0 errors and 4 loader warnings.

The corrected pending-submission proof is in
`Build/_AgentValidation/20260910-060112-vulkan14-h/reports/followup-i3-pending-sync/`;
its `task-environment.json` records `XRE_VULKAN_VALIDATION=1`,
`XRE_VULKAN_SYNC_VALIDATION=1`, and `XRE_VULKAN_DIAGNOSTIC_PRESET=SyncValidation`.
The two child results report `pendingSubmissionGateArmed=true`,
`pendingSubmissionSampled=true`, `pendingSubmissionGateReleased=true`,
`pendingSubmissionRetentionProven=true`, `prematureReclamationObserved=false`,
and `reclamationObservedAfterCompletion=true` after the slot-drain probe.
The earlier `followup-i3-pending` run had synchronization validation disabled
and is not used as synchronization qualification.

| Placement or mechanism | Evidence in this I3 cohort | Traffic evidence | What remains required |
| --- | --- | --- | --- |
| DrawMetadata retained allocation | Actual VMA type 3/heap 1, mapped HostVisible/HostCoherent, 512 → 1,024 bytes, not device-local | `LastTransferSequence=0`: no renderer transfer **submission** was recorded for the probe; physical CPU writes and GPU reads were not counted | Count committed host-write bytes and GPU consumption/bandwidth before moving this dynamic buffer |
| LateDrawIds retained allocation | Actual VMA type 4/heap 1, mapped HostVisible/HostCoherent/HostCached, 64 → 80 bytes, not device-local | Same zero transfer-submission sequence; lifetime ledger proves references and retirement, not PCIe or UMA bandwidth | Record write ranges, cache flushes where applicable, and GPU-read traffic with a trace/counter method |
| Device-local plus `VulkanStagingManager` | Existing engine route for `GpuOnly` data; it uses host-visible staging followed by a copy | Not exercised or measured by this I3 probe | Compare equal retained workload with staging allocation/reuse, copy bytes, transfer submissions, barriers, and completed-GPU timing |
| BDA parameter pilot | Existing G pilot writes a 64-byte frame allocation and flushes dirty mapped ranges; BDA changes addressing, not a placement guarantee | G measured logical parameter upload bytes, not this cohort's physical memory traffic or a device-local placement | Demonstrate an addressable retained buffer with the intended heap/type and matched lifetime/traffic evidence |
| Retained descriptor heap staging | E records stable owner/slot reuse and recording-byte counters | E counters classify recording work; they do not measure allocation placement or physical transfer bandwidth | Keep its immutable generation/slot proof separate from allocator-placement promotion |

The zero transfer-submission sequence must not be read as zero CPU-write,
PCIe, UMA, cache, or GPU-read traffic. It only says this specific renderer
ledger recorded no transfer-queue/copy submission for the native-recording
probe. The current diagnostics expose placement and lifetime, while
`XRBufferWriteTelemetry` is the required source for route-level upload/staging
bytes and allocations; a hardware trace/counter is still required for physical
bandwidth.

The frozen logical-plan probe rejected its stale packet before acquisition and
required a retry. The native-recording probe retained the old generation,
marked it pending retirement, and reclaimed it after the seven-submission slot
drain. Neither run observed premature reclamation. The corrected
pending-submission probe sampled a still-pending submission before release and
proved retention through completion and slot drain; this is the bounded
lifetime proof requested here, not a claim about physical memory bandwidth.

Retain the current VMA host-visible retained-buffer placement and
completion-lifetime policy. The experiment found no transfer **submission** in
this probe, no premature reclamation, and no BDA-specific placement evidence;
it did not measure physical traffic. The original completed-before-query
attempt remains failed. The final control below supplies the missing pending
submission proof; any later placement promotion still requires the placement
and traffic measurements in the table.

### Final rebuilt pending-submission control

The final rebuilt RenderBench control is recorded under
`Build/_AgentValidation/20260910-060112-vulkan14-h/reports/followup-i3-final/`.
It passed with two normal, three-slot children, each configured for 24 frames;
`logs/followup-i3-final.log` records both cohort starts and `Scenario passed`.
Temporary tracing was removed before the build. The result reports
`diagnosticReadbacks=false`, `performanceEvidence=false`, and
`inFlightLifetimeProven=true`; its intentional timeline-gate lifetime probe
remains enabled.

Both child results report `completionQueryAccepted=true`,
`completedBeforeWait=false`, `pendingSubmissionRetentionProven=true`,
`recordedFrameSlotReused=true`, `reclamationObservedAfterCompletion=true`,
and `prematureReclamationObserved=false`. Each child reports standard and
synchronization validation enabled, zero validation errors, and four loader
warnings. The run environment is preserved in
`reports/followup-i3-final/task-environment.json` (`DescriptorIndexing`,
`GpuIndirectZeroReadback`, `BindlessMaterialTable`, and the validation/sync
validation variables).

This final control supersedes the earlier completed-before-query limitation for
the bounded lifetime result only. The rejected frozen logical-plan probe and
the absence of transfer-submission/physical-bandwidth evidence above remain
historical limitations; this control does not promote a memory-placement or
performance policy.
