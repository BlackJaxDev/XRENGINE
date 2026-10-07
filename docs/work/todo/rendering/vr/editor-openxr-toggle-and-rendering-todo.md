# Editor OpenXR Toggle, Rendering, And Import Responsiveness TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [OpenXR Runtime](../../../../developer-guides/vr/openxr-runtime.md#startup-behavior), [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md)
Validation: [OpenXR Validation](../../../testing/xr/openxr-validation.md), [OpenXR SteamVR Hardware Validation](../../../testing/xr/openxr-steamvr-hardware-validation.md)

## Current State

The ImGui Unit Testing editor can start from `VR.Mode=Desktop`, prompt for Monado or SteamVR, create a temporary VR rig, preserve the desktop pawn, and retire/recreate renderer resources for a new runtime choice. Earlier investigation found unresolved desktop texture upload starvation, BRDF producer-ordering risk, avatar shading failure, and import latency. Live runtime validation remains open.

## Open Code Items

### Texture upload and desktop presentation

- [ ] Review and keep the staging lease eligibility fix in `VulkanStagingManager.TryAcquireLease`. Done when: best-fit selection filters for foreground reserve eligibility before it chooses an idle staging entry, and bounded idle-buffer growth still works.
- [ ] Add bounded failure-only diagnostics for stalled required texture uploads if validation still stalls. Update texture streaming diagnostics, upload ledger identity output, pending state output, rehydration generation output, ticket state output, and staging owner eligibility output. Done when: a frozen desktop image reports one actionable oldest blocker without unbounded per-frame logging.
- [ ] Add editor diagnostics that distinguish loop activity, successful desktop presents, XR submissions, and oldest blocking upload. Done when: a frozen image cannot appear healthy solely because FPS remains nonzero.

### BRDF producer ordering

- [ ] Preserve the required-producer ordering change in `AbstractRenderer`, `BrdfIntegrationResources`, Vulkan required-producer plumbing, and the frame operation queue. Done when: BRDF framebuffer sampling publication occurs after deferred mesh draw materialization and uses the validated ordered cohort for the terminal barrier and fence context.
- [ ] Keep failure rollback and later retry ownership-safe for BRDF publication. Done when: abandoned submission, mixed-context failure, and retry paths retain resource ownership until a real terminal state.

### Avatar shading root cause

- [ ] Add or refine bounded diagnostics for avatar deferred combine inputs only after current capture evidence identifies the missing data. Update the relevant shader constants, resource-publication diagnostics, or lighting input diagnostics. Done when: the remaining black or background-colored avatar failure has one concrete engine-side cause and a targeted code fix.
- [ ] Keep speculative brightness, matrix, and CPU-fallback work out of the fix. Done when: no new setting or fallback hides the failure without a documented root cause.

### Import responsiveness

- [ ] Remove or safely bound long work on the editor or render owner during import and renderer recreation. Update owner-thread influence copying, native buffer allocation, static GPU append, texture publication, and scene attachment paths. Done when: measured indivisible owner-thread work is either bounded or reports a clear blocking owner.
- [ ] Keep immutable mesh payload cache cancellation and source revision invalidation safe. Update the native FBX and mesh payload cache paths. Done when: stale payloads are rejected and unsafe skin-buffer reads remain on their owner thread.
- [ ] Reduce retained managed memory for binary-cache and non-native import payloads when measurements show excess retention. Done when: sparse records, delta arrays, and vertex arrays are retained only while needed for the live mesh or cache contract.

### Runtime toggle ownership

- [ ] Ensure startup guards wait only for real lifecycle and resource dependencies. Update editor runtime switching and OpenXR startup orchestration. Done when: startup does not wait for global texture-queue emptiness, arbitrary desktop frame counts, or unrelated command-buffer quiet periods.
- [ ] Verify and correct settings, schema, and user-facing text for dynamic pawn creation and runtime selection. Update Unit Testing World settings, generated schema, and editor UI text. Done when: the UI explains Monado, SteamVR, cancel, runtime failure, temporary rig ownership, and desktop-pawn restoration accurately.
- [ ] Add regression tests only after live feature validation and explicit clearance. Update the relevant editor or runtime tests. Done when: tests cover the accepted runtime-toggle behavior without blocking live regression debugging rules.

## Decisions Needed

- [ ] Which parts of import preparation must remain on the owner thread, and which can move to worker-owned immutable payloads? Owner: Assets / Rendering.
- [ ] What minimum live evidence is required before adding regression tests for runtime switching? Owner: XR / Testing.

## Out Of Scope

- Stopping a user-owned editor process.
- Replacing true single-pass stereo or GPU deformation with a sequential-eye or CPU fallback.
- Treating the desktop camera as proof of headset output correctness.
