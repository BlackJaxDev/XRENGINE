# Vulkan Primary Command-Buffer Reuse Contract

Last Updated: 2026-08-11

This document is the correctness contract for reusing Vulkan primary and
secondary command buffers. Reuse is enabled by default for desktop and OpenXR;
`XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE` and
`XRE_OPENXR_VULKAN_PRIMARY_REUSE` are explicit diagnostic overrides, not
separate production policies.

## State Ownership

Image state is tracked per physical image generation, mip, array layer, aspect,
and queue family.

| Boundary | Owner | Rule |
|---|---|---|
| Before primary execution | submitted image-state map | The acquired swapchain image and every shared image must match the primary's recorded entry contract. State from another swapchain image is never substituted. |
| During primary recording | primary command-buffer overlay | Transitions update only the recorded overlay. They do not mutate submitted state. |
| During secondary execution | secondary entry/exit snapshot merged into the primary overlay | A secondary's entry must agree with the primary state established before `vkCmdExecuteCommands`; its exit becomes the following primary state. Descriptor requirements are established by barriers in the primary before the secondary executes. |
| After successful queue submission | submitted image-state map | Only `QueueSubmit` success publishes the primary's touched subresources and queue sequence. |
| After fence/timeline completion | completed image-state map | Submitted state becomes completed state only when the owning queue sequence completes. |
| Present/acquire | swapchain-image-local state | Present state remains attached to that exact swapchain image. Acquire selects that image's state; recreation starts a new physical generation. |

A missing submitted state is **unknown**, not a conflict. The first command
buffer recorded against it may execute once, but its snapshot is incomplete
and cannot be reused. After successful submission establishes the state, both
the secondary and its enclosing primary are re-recorded once with complete
entry snapshots. A known tuple that differs is a **conflict** and always forces
a record or rejects an ordered submission.

Failed, rejected, abandoned, or device-lost frame attempts never publish their
recorded overlays.

## Current Frame-Data Authority

A reusable command buffer owns frozen command structure. It does not own the
camera, transform, material, light, or other mutable values for the next frame.
Those values must be published through a current refresh cohort before reuse is
accepted.

The cohort is authoritative only when all of these identities match the reuse
attempt:

- frame-plan generation;
- render-frame ID;
- frame-data image index;
- exact current static/dynamic operation signatures; and
- the producer-to-recorded operation order captured by the last fresh primary.

Thread-local recording scratch is temporary workspace, not retained frame-data
authority. Reuse must rebuild the cohort from current operations and project it
through the recorded order. A missing, stale, differently ordered, or
differently slotted cohort rejects reuse and falls back to full plan sealing and
recording.

The acquired command-buffer image index and frame-data image index are separate
identities even when a desktop path currently assigns them the same value.
Command artifacts remain owned by the acquired image. Frame-data reservations,
descriptor refresh writes, cohort stamps, and completion checks use the
frame-data image index.

Completion domains are likewise explicit:

- frame-in-flight resources use the frame-slot timeline;
- desktop descriptor/frame-data image slots use the desktop swapchain-image
  timeline;
- OpenXR image/frame-data slots use their external-runtime completion authority.

Swapchain recreation carries the strongest retired-image graphics-timeline
requirement into the replacement image ledger so a mapped slot cannot be
reopened before accepted work on its old image generation completes.

## Equality And Synchronization

The reusable entry tuple is:

`(image generation, mip, layer, aspect, layout, stage mask, access mask,
descriptor layout, queue family)`.

The diagnostic transition serial is deliberately excluded. A recorded source
stage/access mask may be broader than the current submitted state, but never
narrower. Equal layouts do not eliminate a barrier when stage or access
dependencies differ. A published tuple must also be semantically compatible:
for example, `ShaderReadOnlyOptimal` cannot retain color-attachment-write
stage/access masks from an earlier use. Compatible precise scopes are
preserved; incompatible scopes are normalized from the final layout and image
aspect. `General` retains its explicit access domain because the layout alone
cannot identify one.

Every mismatch is classified by
`EVulkanPrimaryEntryStateMismatch` and exported through the
`vulkan_primary_entry_state_*` clean-profile fields. String formatting is
reserved for trace diagnostics; the steady-state check records scalar fields
only.

## Cache Identity

The following changes invalidate the affected primary and/or command chain:

- render-pass or dynamic-rendering structure, attachment identity, format,
  render area, sample count, view mask, or inheritance;
- pipeline, pipeline-layout, descriptor-layout, descriptor-set publication,
  mesh/index/vertex binding, resource-plan, physical allocation, frame-slot,
  or external-target identity;
- image generation, entry layout, stage/access dependency, descriptor layout,
  queue ownership, overlay topology, query cadence, or profiler topology;
- resize, swapchain recreation, shader/pipeline replacement, or capacity
  growth that replaces a bound buffer.

The following remain data-only while binding topology and backing capacity are
stable:

- camera, transform, animation, and material values;
- frame-slot offsets and frame-indexed uniform/storage contents;
- visibility, indirect command, and count-buffer values;
- `LinesBuffer` and other capacity-backed logical sizes below capacity.

ImGui, dynamic text, profiler overlays, streaming uploads, and debug commands
use volatile command chains so they do not invalidate stable scene chains.

## Acceptance Gate

The four `primary-reuse-*` CPU-direct cohorts in
`XREngine.Benchmarks/VulkanPerformance/vulkan-performance-cohorts.json`
exercise Deferred/Uber and static/moving-camera paths. Each requires:

- at least 99% primary reuse during the post-warmup capture;
- no command-buffer recording allocations;
- a stable workload identity and required rendered output;
- no rejected submission or forbidden fallback.

The ratio is `reused / (recorded + reused)` from exact primary decisions. The
benchmark evaluator fails missing decisions rather than treating them as
reuse.
