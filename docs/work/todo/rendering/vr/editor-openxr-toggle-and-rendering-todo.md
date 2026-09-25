# Editor OpenXR Toggle, Rendering, And Import Responsiveness

Status: Active; implementation exists, acceptance remains open.
Updated: 2026-09-25.

This is the continuation checklist for the editor runtime-toggle investigation.
Work was wrapped up at the user's request. The latest source changes have not
all been built or exercised; a successful earlier run does not validate them.

## Required Behavior

- Keep the saved unit-world setting `VR.Mode=Desktop`.
- Turning on OpenXR prompts for Monado testing, SteamVR headset use, or cancel.
- Construct a temporary VR rig dynamically in the editor scene. Neither pawn
  must pre-exist in the world as a prerequisite for the checkbox.
- Turning OpenXR off restores the exact desktop pawn that was being used and
  destroys only the temporary rig owned by the toggle.
- Keep true single-pass stereo and GPU deformation; do not hide failures with a
  sequential-eye or CPU fallback.
- Keep the editor responsive during import, scene attachment, runtime startup,
  renderer replacement, and shutdown.
- Render the avatar correctly in the actual headset while moving. The editor's
  desktop camera is not a VR mirror and cannot establish headset correctness.

## Implemented And Observed

| Area | Current evidence | Remaining limitation |
|---|---|---|
| Runtime chooser and temporary rig | Monado and SteamVR both submitted single-pass frames. Sampled off transitions restored the same desktop pawn. The rig is attached through editor-scene integration. | Repeat on the final build, including the actual checkbox and cancellation/failure paths. |
| Runtime/renderer lifetime | Callback admission, retirement ordering, publication-pin cleanup, renderer cache invalidation, and resource identity checks were tightened. | Long repeated switching and overlapping import remain acceptance work. |
| Stereo inspection | MCP resolves the OpenXR stereo viewport and reads the exact submitted nested Mirror context. Intermediate captures now work. | This fixes diagnostic access, not avatar shading. |
| Deformation preparation | Native FBX workers prepare immutable packed CPU payloads before attachment. Renderer recreation reuses them. Other paths retain bounded preparation. | Owner-thread influence copying, native allocation, and static GPU append can still stall. |
| Import timing | One measured native import took 28.131 s. Complete desktop publication became Ready within 38 s afterward, versus about 4 min 42 s previously. | This is an improvement, not instant or frame-drop-free loading. |
| Latest desktop freeze | Monado's engine-submitted frame counter advanced while desktop recording/submission/presentation stayed blocked on a required texture. | Upload admission starvation is unresolved in a live rebuilt run. |
| Headset shading | Bad output was captured before headset composition, in both SteamVR and later Monado runs. | Root cause remains unproven. |

The last completed import-payload build had zero warnings and errors. The owned
isolated session `xr-runtime-choice` was stopped during this handoff. No editor
owned by the user was stopped. No new tests were added for this live regression.

## 1. Unblock Desktop Texture Uploads

### Evidence

The desktop frame repeatedly failed required-upload readiness for a texture with
published generation 2 and pending native upload generation 3. The old diagnostic
called it canceled/superseded, but live streaming rows showed the transition was
still pending. Do not treat it as a missing-ticket cancellation without checking
the pending state.

The final sampled queue had 40 pending transitions, 72 pending preparation
packages, zero active preparation packages, zero pending transfers, and zero
pending descriptor publications. Admission deferrals had reached 8,184. Prepared
chunks were 580 and completed chunks 578. This is not ordinary loading progress.
The desktop loop can continue retrying without presenting a new image while
OpenXR continues presenting its ready work; nonzero loop FPS is insufficient.

A source defect was found in `VulkanStagingManager.TryAcquireLease`: best-fit
selection considered both background and foreground-reserved idle entries, then
rejected a selected background entry when a foreground reserve was required.
An ineligible tighter fit could therefore mask an eligible reserved buffer. A
narrow source fix filters eligibility before best-fit selection. It does not
increase capacity, release in-flight owners, or bypass completion checks. Its
connection to the live freeze still requires validation.

### Next Actions

- [ ] Review and build the staging selection fix; it was not runtime-validated
  during wrap-up. Preserve the earlier bounded idle-buffer growth fix in the
  same file.
- [ ] Reproduce import followed by Monado startup and verify actual desktop
  presents advance, not merely render-loop iterations.
- [ ] Check that pending transitions and oldest queue age drain. Record staging
  reserve occupancy and outstanding chunk owners if admission still stalls.
- [ ] If needed, add bounded failure-only diagnostics for texture identity,
  upload service/ledger identity, pending state, rehydration generations, exact
  ticket state, and staging owner eligibility. Verify the current source before
  assuming an earlier diagnostic proposal is present.
- [ ] Repeat with SteamVR and after switching back to desktop. Keep exact
  required-generation readiness and ownership/fence checks intact.
- [ ] Distinguish loop activity, successful desktop presents, XR submissions,
  and oldest blocking upload in editor diagnostics so a frozen image cannot
  appear healthy solely because FPS remains nonzero.

Acceptance: both desktop images and eye images visibly update during motion;
required uploads finish or expose a terminal actionable failure; no indefinite
retry with idle upload capacity.

## 2. Validate Required BRDF Producer Ordering

A source fix moves BRDF framebuffer sampling publication after deferred mesh
draw materialization. The writer's frozen context supplies the publication and
the validated ordered cohort supplies the terminal barrier/fence context.
Previously publication could precede the queued writer, and the terminal marker
could inherit a different ambient context. Strict compatibility checks remain.
The scoped change received source review but was not built in the last session.

- [ ] Build and validate the required-producer change in `AbstractRenderer`,
  `BrdfIntegrationResources`, Vulkan required-producer plumbing, and the frame
  operation queue.
- [ ] Confirm BRDF production reaches a successful published generation without
  repeated abandoned submission or mixed-context failures.
- [ ] Repeat with RenderDoc capture enabled. An earlier capture-enabled run
  submitted no XR frames because this producer failed; it is not valid shading
  evidence.
- [ ] Confirm failure rollback and later retry preserve resource ownership.

## 3. Resolve Black Or Background-Colored Avatars

### Established And Ruled Out

- Both runtimes select the same OpenXR RVC pipeline factory and synchronized
  features. Single-pass output uses the layered command family.
- Failing captures contain correctly aligned avatar albedo, normals, and depth.
- Raw-albedo debug output aligns with the avatar in the HDR target. Normal
  lighting is very dark while emissive details remain visible.
- Disabling the skybox did not restore illumination. AO was nonzero and finite
  in the sampled SteamVR failure. These checks do not prove every later frame.
- The latest world ambient was `(0.03, 0.03, 0.03)` with intensity 1. The texture
  skybox's procedural cycling flags do not alter ambient in that mode.
- No camera-matrix or probe-binding defect has been established. A possible
  within-frame cache risk is not sufficient evidence for a matrix rewrite.
- An older good Monado RenderDoc baseline used `ProbeCount=0`,
  `SuppressProbeDiffuse=0`, and ambient approximately 0.03. Those constants do
  not describe the latest failing draw.

### Next Actions

- [ ] After upload/BRDF validation, capture a current failing stereo deferred
  combine draw with RenderDoc, including constants and bound textures.
- [ ] Inspect `GlobalAmbient`, `ProbeCount`, `SuppressProbeDiffuse`, AO, RMSE,
  irradiance/prefilter resources, BRDF lookup, direct lighting, emission, and
  HDR output at the same avatar pixels.
- [ ] Establish whether the remaining failure is illumination, depth/background
  composition, stale resource publication, or another concrete fault before
  changing shader math.
- [ ] Compare Monado and SteamVR with the same scene, environment, and view.
  Different launch environments and a headset facing away are invalid parity
  comparisons.
- [ ] Capture both eyes before and after movement and after runtime replacement.
  View the exported images; frame counters alone cannot establish correctness.
- [ ] Verify actual headset output with the user after engine-side captures are
  correct. Keep brightness adjustments and speculative matrix changes out of
  the root-cause fix.

## 4. Finish Import Responsiveness

- [ ] Profile the remaining interval from import completion to first complete
  visible avatar, separately from file parsing and worker preparation.
- [ ] Attribute owner-thread influence copying, native buffer allocation,
  static GPU append, texture publication, and scene attachment spikes.
- [ ] Remove or safely bound remaining long work on the editor/render owner.
  A 4 ms preparation budget does not cap an indivisible native call.
- [ ] Measure cold import, warm import, and renderer recreation with desktop and
  XR active. Record frame-time distribution, longest stall, and first-visible
  latency; do not call the path instant based only on total import time.
- [ ] Validate cancellation and source revision invalidation of the immutable
  mesh payload cache. Keep unsafe skin-buffer reads on their owner thread.
- [ ] Review retained memory and payload coverage for binary-cache/non-native
  import paths. Millions of sparse records plus delta/vertex arrays remain in
  managed memory while the mesh lives; use measurements to guide further work.

## 5. End-To-End Toggle Acceptance

- [ ] Exercise the actual checkbox: Monado, SteamVR, cancel, runtime unavailable,
  startup failure, and toggle-off during preparation.
- [ ] Start with `VR.Mode=Desktop`; verify it remains unchanged afterward.
- [ ] Confirm temporary rig ownership in the editor scene, no duplicate roots,
  exact original desktop pawn restoration, and preservation of authored rigs.
- [ ] Repeat desktop → Monado → desktop → SteamVR → desktop, including import
  overlap, and check callbacks, leases, publication pins, and upload progress.
- [ ] Ensure startup guards wait only for actual lifecycle/resource dependencies,
  not arbitrary global texture-queue emptiness.
- [ ] Verify settings/schema and user-facing text accurately describe dynamic
  pawn creation and runtime selection.
- [ ] After live acceptance, obtain explicit clearance before adding regression
  tests under the repository's current feature-debugging workflow.

## Resume And Evidence

Use the named isolated-session workflow in
[editor and tooling workflows](../../../../developer-guides/ai/agent-editor-workflows.md).
Rebuild before validating the new source; do not use `Start -NoBuild` for it.
Preserve unrelated changes already present in the shared working tree.

Durable investigation records:

- [Runtime toggle and stereo investigation](../../../investigations/editor/openxr-runtime-toggle-2026-09-24.md)
- [Avatar import/publication investigation](../../../investigations/asset-import/avatar-scene-publication-stalls-2026-09-24.md)
- [Unit-world workflow](../../../../developer-guides/testing/unit-testing-world.md)
- [Native FBX behavior](../../../../developer-guides/assets/native-fbx-import-export.md)

Disposable evidence is under
`Build/_AgentValidation/20260924-181113-openxr-toggle/`. Useful reports include
`import-payload-build.log`, `desktop-frozen-xr-live-state.json`,
`desktop-frozen-streaming.json`, and `steam-stereo-centered-capture.json`.
The `mcp-captures/` images around `20260925_002056` show raw albedo alignment;
`20260925_002221` shows the normally shaded dark avatar. The older
`renderdoc/stereo-debug_capture_50.rdc` is a good Monado baseline, not the current
failing draw. These artifacts may be cleaned up; the findings above must remain
sufficient to resume without them.

Completion requires responsive desktop presentation and import, correct moving
headset images, and reliable runtime/pawn transitions on the final build. None
of those broad acceptance criteria is marked complete by this handoff.
