# Vulkan DLSS visual regressions

## Problem statement

After the initial Vulkan Streamline integration work, the user reported three visual regressions:

- the scene is vertically inverted by default;
- enabling DLSS or DLSS frame generation produces a black scene;
- enabling either feature causes part of the native UI text to disappear.

The previous validation confirmed native DLSS evaluation and Streamline presentation without API or validation errors, but it did not visually inspect the final composed viewport. This investigation requires image-based validation of the final scene and UI.

## Investigation matrix

Each result must be captured from the same camera view and inspected as an image.

| Clip-space Y | DLSS | DLSS-G | Expected |
| --- | --- | --- | --- |
| YUp | Off | Off | Upright scene and complete UI |
| YDown | Off | Off | Toggle-resolved orientation and complete UI |
| YUp | On | Off | Upright upscaled scene and complete UI |
| YUp | On | OneX | Upright generated presentation and complete UI |

## Issues and hypotheses

1. The vendor final-blit shader's non-AA Vulkan source-Y flip is a presentation invariant. The clip-space toggle is already applied by Vulkan viewport construction; making the final flip conditional double-applies the policy.
2. DLSS output is transitioned to `GENERAL` before evaluation and sampled afterward, but visual correctness of that output has not yet been verified independently from the final swapchain blit.
3. DLSS-G tags depth, motion vectors, and HUD-less color. NVIDIA's integration guide requires HUD-less data to remain valid through present and recommends an explicit UI buffer for correct UI recomposition. The current native UI is drawn directly to the intercepted swapchain and no UI mask/color buffer is tagged.
4. Streamline Vulkan device requirements are fixed at renderer creation. Runtime feature toggles must either provision the requirements up front or perform an explicit renderer recreation; partially enabling a feature on an unprovisioned device is invalid.

## Tooling status

- `rdc doctor` passes all checks except the Vulkan implicit-layer registration. No registry mutation was performed.
- Until that layer is registered, use editor MCP viewport/pipeline captures and Vulkan/Streamline logs for the live debug loop.

## Attempted solutions and validation

### Baseline: YUp, DLSS off, DLSS-G off

- OS window capture: `Build/_AgentValidation/20260719-vulkan-dlss/visual-regression/baseline/mcp-captures/baseline-window.png`
- Pipeline capture: `Build/_AgentValidation/20260719-vulkan-dlss/visual-regression/baseline/mcp-captures/RenderPipeline_FinalPostProcessOutputTexture_20260719_174729.png`
- Effective capture policy: Vulkan, clip-space `YUp`, framebuffer texture `YDown`, 1767x994.
- Result: non-black scene; complete ImGui/native overlay; pipeline texture and presented scene have matching orientation.
- Conclusion: the black/UI problem is feature-path-specific. The default source value is YUp and looks internally consistent in this run; test the YDown branch explicitly next.

### YDown, DLSS off, DLSS-G off

- OS window capture: `Build/_AgentValidation/20260719-vulkan-dlss/visual-regression/ydown/mcp-captures/ydown-window.png`
- Result: the 3D scene and screen-space engine diagnostic text map positive clip-space Y downward, while the later ImGui editor overlay remains top-left oriented.
- Interpretation: this is the documented `YDown` semantic, not the intended default. The final source flip must remain invariant; an attempted conditional flip did not correct the view because viewport construction already owns the clip-space policy.
- Final default is restored to `YUp`. Final OS capture: `Build/_AgentValidation/20260719-vulkan-dlss/visual-regression/final-default/mcp-captures/default-yup-window.png`.
- Final result: upright non-black scene with complete UI.

### Streamline 2.12.0 and managed ABI correction

- Updated the official SDK archive to `v2.12.0`, pinned to SHA-256 `f5c0a3d870707dddc3570fb4bcd3655cf48a8a68c3a9d342910cfa21b77dcf48`.
- Updated the Streamline SDK version token to `0x0002000C0000FEDC`.
- Audited managed structure versions against the 2.12.0 headers. `sl::Preferences` is version 1; the managed binding incorrectly advertised version 3.
- Before the ABI correction, the first populated native Vulkan DLSS evaluation terminated with Windows heap corruption `0xc0000374` in `ntdll`. With Streamline 2.12.0 and `Preferences` version 1, the same path remains live and renders normally.

### YUp, DLSS on, DLSS-G off

- OS window capture: `Build/_AgentValidation/20260719-vulkan-dlss/visual-regression/dlss-2.12/mcp-captures/dlss-window.png`.
- Result: upright, non-black upscaled scene with complete ImGui and native text.
- Runtime evidence: the copied Streamline binaries report 2.12.0 and native Vulkan DLSS evaluation succeeds without a Streamline error.

### YUp, DLSS on, DLSS-G OneX

- Initial 2.12 capture: `Build/_AgentValidation/20260719-vulkan-dlss/visual-regression/dlssg-2.12/mcp-captures/dlssg-window.png`.
- Root cause of missing UI: the engine tagged depth, motion, and HUD-less color but supplied no `kBufferTypeUIColorAndAlpha` resource, left `uiBufferFormat=0`, and disabled `enableUserInterfaceRecomposition`.
- Fix:
  - allocate a transparent color/alpha target per proxy-swapchain image;
  - clear it every acquired frame;
  - render ImGui and the late native dynamic-text overlay into it with premultiplied alpha;
  - transition it to `GENERAL` and tag it as `kBufferTypeUIColorAndAlpha` with `ValidUntilPresent`;
  - set the UI format and enable Streamline UI recomposition.
- Final captures: `Build/_AgentValidation/20260719-vulkan-dlss/visual-regression/dlssg-ui-2.12/mcp-captures/dlssg-ui-window-1.png` through `dlssg-ui-window-6.png`.
- Result: all six sampled frames retain the complete UI and an upright, non-black scene. Streamline logs confirm `kBufferTypeUIColorAndAlpha`, `UserInterfaceRecompositionEnabled: true`, active DLSS-G interpolation, Streamline 2.12.0, and NGX feature version 310.7.0.

## Resolved startup command-buffer lifetime issue

The Sponza startup failure was not caused by texture retirement itself. The ImGui overlay called `ResetCommandBufferBindState`, which begins the renderer's tracked recording batch, but completed recording through raw `vkEndCommandBuffer`. The native command buffer ended successfully while the engine-side batch remained marked as recording. Reusing that per-swapchain overlay buffer therefore failed with `command buffer is still recording`; texture promotion only made the timing deterministic.

The overlay now completes through `EndCommandBufferTracked`, retaining normal cached-variant ownership until reset and closing both the Vulkan recording and engine tracking states together. A first diagnostic attempt used `cacheVariant: false`; that removed the managed exception but opened a frame-data ownership gap and exposed native heap corruption, so it was rejected. The final implementation uses the normal cached ownership contract.

Validation evidence:

- `Build/_AgentValidation/20260719-vulkan-dlss/visual-regression/startup-race-fix/run3-window.png` shows a complete rendered Vulkan scene and editor UI during the texture-promotion burst.
- Stable runs lasted 42 and 45 seconds and published 72 and 71 Vulkan texture generations respectively.
- Both reached `pending=0`, `importsActive=False`, and 31 promoted textures without a stale-recording exception, native crash, retired-imported-image error, Vulkan VUID, or ImGui recovery swapchain recreation.
- `VulkanImGuiOverlay_ClosesTrackedCommandBufferRecording` passes and prevents returning to raw `Api.EndCommandBuffer` for the tracked overlay.

## Runtime toggle retirement fixes

The remaining user-reported black output and disable-time crash were generation-transition failures rather than DLSS evaluation failures:

- A frame-generation op captured before the feature was disabled could still call Streamline with an inactive zero-frame configuration. Frame-generation recording now exits when the feature is no longer requested.
- Registry-wide Vulkan descriptor prewarm treated every declared render-graph texture as frame-critical. An unused stale `FullOverdrawCountTex` consequently rejected every frame after some feature-off transitions. Registry prewarm is now best-effort; draw and dispatch binding paths remain the authoritative validation gate for resources actually recorded.
- A texture descriptor view could remain natively live while pending retirement. It was therefore repeatedly selected for a new `SourceTexture` write, which descriptor lifetime validation correctly rejected. Descriptor snapshots now recreate a retiring view against the current image before publishing it.
- A resource generation could retire between primary recording and tracked-batch publication. That transition now rejects and re-presents one completed frame instead of rebuilding the swapchain or escalating the handled race through the editor circuit breaker.

Final live toggle validation used one editor process and the sequence baseline -> DLSS on -> off -> DLSS-G OneX on -> off. Phase times and clean shutdown are recorded in:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-19_21-47-08_pid24944/`
- `Build/_AgentValidation/20260719-vulkan-dlss/final-live-toggle-4/os-captures/dlss-on.png`
- `Build/_AgentValidation/20260719-vulkan-dlss/final-live-toggle-4/os-captures/dlss-off.png`
- `Build/_AgentValidation/20260719-vulkan-dlss/final-live-toggle-4/os-captures/framegen-on.png`
- `Build/_AgentValidation/20260719-vulkan-dlss/final-live-toggle-4/os-captures/framegen-off.png`

All four inspected captures are upright, non-black, free of placeholder magenta, and retain the complete editor/native text. Scene diagnostics differ between captures, confirming that the renderer advanced frames instead of presenting one stale image. The full run exited normally. Its Vulkan log contains no `DrawSubmitRejected`, command-buffer recording failure, `ErrorInvalid`, device loss, Streamline error, or unhandled exception. One completed frame was safely re-presented at each resource-generation transition while new descriptors/views were published.

## User retest: sampled depth descriptor layout mismatch

The user reported that the preceding toggle-retirement fixes did not resolve their run. The debugger stopped on an exact command-recording invariant failure:

`VkTextureView.RefreshedDepthOnlyDescriptor` published `GENERAL` while the depth image subresource was tracked as `DEPTH_STENCIL_READ_ONLY_OPTIMAL` for a combined-image sampler.

The newest user-run log is `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-19_22-05-41_pid28948/`. It reaches Streamline 2.12.0 initialization and native D24S8/RG16F allocation immediately after the DLSS toggle, then stops without a normal shutdown. This is consistent with the debugger assertion or forced termination; no independent teardown crash is recorded.

The first attempted fix preserved every tracked readable layout, including `GENERAL`. The user's next run, `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-19_22-26-00_pid30464/`, disproved that policy. Native DLSS evaluation succeeds, then the rebuilt motion-vector draw binds the refreshed depth-only view. The log identifies image `0x2BA306EF390` as the render graph's `DepthStencil` allocation with sampled/depth-attachment/transfer usage (not storage usage). Its descriptor had been prepared while generation refresh still exposed the preceding Streamline `GENERAL` state, but the recording command buffer had already transitioned the exact subresource back to `DEPTH_STENCIL_READ_ONLY_OPTIMAL`.

The corrected contract distinguishes a declared storage/sampling contract from a transient globally tracked layout:

- storage-image descriptors use `GENERAL`;
- images created for both storage and sampling may intentionally use `GENERAL` for their sampled descriptor;
- ordinary sampled color and depth/stencil images use their aspect-appropriate read-only layout, even if the global tracker temporarily reports `GENERAL` after Streamline evaluation;
- exact tracked read-only variants are preserved, while attachment, transfer, and undefined states fall back to the descriptor's requested layout;
- DLSS output and framebuffer publish behavior for explicitly storage-capable sampled images remains unchanged.

The broader regression run also exposed a still-active direct window-present Vulkan UV flip. Direct presentation now relies exclusively on the backend viewport/image-display policy, preventing a second Y inversion. The vendor-upscale fallback retains its explicit source-orientation rule and additionally applies the configured `ClipSpaceYDirection == YDown` policy.

Validation completed so far:

- focused sampled-layout/orientation tests for the first attempt: 13 passed, but the subsequent user run rejected its `GENERAL` preservation rule;
- corrected descriptor-policy tests include both the reported non-storage depth case and the intentional storage-plus-sampling `GENERAL` case: 14 passed in an isolated artifacts build while the user's editor held the normal output DLLs open;
- editor build: succeeded with only the two pre-existing surfel-GI unassigned-field warnings;
- the broader `VulkanP0ValidationTests` class contains 17 unrelated stale path/source-contract failures; the relevant orientation failure from that run is fixed and passes in isolation.

Live toggle validation remains pending explicit approval to launch the isolated editor instance with MCP auto-approval limited to `Mutate` tools. The normal `AllowReadOnly` policy cannot change the DLSS settings unattended, and no permission-policy bypass was used.

## User retest: transient DLSS output and no visible DLSS-G FPS increase

The user run at `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-19_22-52-47_pid9912/` explains both remaining symptoms.

- DLSS is enabled at 22:56:56.479 and requests a new 1267x712 internal resource generation.
- Streamline starts a native DLSS session at 22:56:56.709 while that generation is only 4/121 resources complete. The session therefore starts with the old 1920x1080 inputs.
- The correct generation does not commit until 22:56:59.393. This old-generation/new-setting overlap matches the reported few seconds of incorrect output followed by stable rendering.
- DLSS-G `OneX` reaches Streamline's enabled interpolation state. The engine FPS overlay only counts rendered frames, so it cannot show generated presentations.
- Selecting `ThreeX` requests three generated frames, but Streamline reports `numFramesToGenerateMax=1`; `slDLSSGSetOptions` then fails with `ErrorInvalidState` every frame and interpolation is disabled.

The current fix keeps vendor dispatch on the active fallback presentation path while a different render-resource generation is pending. DLSS session creation therefore begins only after the requested internal dimensions and feature resources are active. DLSS-G now starts conservatively with one generated frame until Streamline reports its supported maximum, clamps an oversized request to that maximum with a repeated visible diagnostic instead of failing the feature, and exposes `numFramesActuallyPresented` plus a running total. The Unit Testing World FPS overlay shows estimated presented Hz, the latest generated-present count, the running total, and the runtime maximum so generated presentation can be distinguished from base render FPS.

Validation:

- editor build completed with 0 warnings and 0 errors;
- focused `DlssRuntimeToggle_WaitsForMatchingResourcesAndReportsPresentedFrames` regression passed;
- a normal-permission Vulkan startup (`xrengine_2026-07-19_23-14-33_pid29496`) confirmed Streamline 2.12.0 reports `Multi-frame not supported, max generated frames 1 (SL Plugin supports 5, NGX feature supports 1)` on this system and successfully creates the proxy swapchain;
- that live startup had DLSS/DLSS-G disabled in persisted preferences, so it validates provisioning but not the enabled interpolation path; Vulkan MCP readback is disabled by the engine's watchdog guard and broad MCP mutation permission was not granted.

## User retest: startup descriptor-generation flicker and handled exceptions

The follow-up run at `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-19_23-25-56_pid5160/` confirms that the vendor-resource gate fixed the earlier wrong-resolution DLSS session:

- DLSS requests 1267x712 internal resources at 23:27:37.055 and remains on the active fallback presentation path while the new generation builds;
- the 1267x712 generation commits at 23:27:41.532;
- only after that commit does Streamline resolve the expected 1280x720 optimal input and create the native DLSS context with 1267x712 current inputs and 1920x1080 output.

The remaining flicker came from a separate descriptor handoff at that commit. Four handled first-chance `InvalidOperationException`s rejected material descriptor writes referencing image views in the just-retired generation. The affected resources included `GTAOBlurIntermediateTexture`, `AlbedoOpacity`, `AmbientOcclusionTexture`, and `AtmosphereColor`. Subsequent frames repeatedly skipped `AutoExposureTex` and `BRDF` because their cached primary views remained descriptor-dirty false but were no longer available to new descriptor sets. Draw deferral and missing exposure/BRDF inputs account for the transient incorrect frames and debugger-visible exceptions.

The corrected lifetime contract now:

- prevalidates descriptor writes without exceptions for recoverable retirement races;
- holds the resource-lifetime lock through native `vkUpdateDescriptorSets`, closing the validate-then-retire window;
- retries a material update once in the same frame after rebuilding all descriptor snapshots;
- treats a pending-retirement primary image view as not descriptor-ready, replaces it, and advances the descriptor generation before publishing planner-backed, attachment, storage, or dedicated texture descriptors.

Validation:

- editor build succeeded; only the two pre-existing unassigned Surfel GI field warnings remain;
- focused `VulkanDescriptorGenerationHandoff_RebuildsRetiredViewsWithoutFirstChanceExceptions` regression passed;
- `git diff --check` reports no whitespace errors;
- enabled live-toggle visual validation is pending the next user run because the permitted automated editor profile cannot mutate DLSS settings and RenderDoc's Vulkan implicit layer remains unregistered.

## User retest: command-buffer publication race after descriptor refresh

The next run at `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-19_23-49-52_pid18616/` shows that descriptor updates no longer throw and `AutoExposureTex`/`BRDF` no longer remain descriptor-unready. It exposes the next lifetime layer instead:

- the 1267x712 generation commits at 23:52:00.274;
- native DLSS creates the correct 1267x712 -> 1920x1080 context and evaluates successfully;
- at 23:52:01.009, primary command recording ends with one handled first-chance exception because an image view retired after it was added to the command-local dependency batch but before that batch was published;
- the frame loop safely re-presented completed content, but that recovery is visible as another startup discontinuity.

The repeated view-refresh warnings revealed the underlying ownership bug. `AcquireImageHandle` called `RetireDedicatedImageBeforeBorrowingPhysicalGroup` for every planner-backed texture acquisition. When the wrapper already borrowed the exact same physical group, that helper still destroyed all of its views. Descriptor resolution recreated them, later acquisition retired the replacements again, and command recording could land between those operations. This affected render targets and otherwise stable textures such as Sponza material maps and `BRDF`.

The correction now:

- preserves views when reacquiring the same planner-owned physical image group;
- retires current views only when ownership or the borrowed group actually changes;
- refuses pending-retirement primary, attachment, cached, and structurally reusable views for new command-buffer use;
- reports end-of-recording dependency publication races without first-chance exceptions;
- retries primary command recording once in the same acquired frame against the stabilized generation instead of immediately presenting recovery content.

Validation:

- editor build succeeded; only the two pre-existing unassigned Surfel GI field warnings remain;
- the focused Vulkan upscale/lifetime and uniform-buffer generation-cache suites passed: 36 passed, 0 failed;
- `git diff --check` reports no whitespace errors;
- enabled Vulkan hardware validation remains pending the next enabled run because RenderDoc's Vulkan implicit layer is not registered and the permitted automated editor profile cannot change the persisted DLSS setting.

## User retest: persistent retired `VkTextureView` descriptor submission

The user run at `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-20_00-14-23_pid32516/` disproved the prior assumption that correcting planner-backed primary views was sufficient. The generation transition reaches native DLSS evaluation, but image view `0x1E6551A1040` remains in the `SourceTexture` material descriptors after entering pending retirement. Queue submission rejects that descriptor on every frame, so completed-content recovery appears as a permanently black or frozen render.

This handle is owned by `VkTextureView.View`, a separate wrapper used by render-to-window and other view resources. Its freshness check accepted any live view backed by the same live image, even when the view itself was pending retirement. The interned image-view cache used the same incomplete test, so a refresh could reacquire the exact pending handle and never advance the material descriptor snapshot.

The corrected contract now:

- treats pending-retirement `VkTextureView` primary, aspect-only, attachment, and descriptor views as stale even when the backing image is unchanged;
- evicts a pending-retirement entry from the interned image-view cache instead of incrementing its reference count;
- creates and publishes a fresh view, allowing the frame-source `SourceTexture` signatures and material descriptor generations to advance.

## Broad-MCP hardware iteration: ImGui descriptor copy-on-write

The approved isolated editor loop reproduced one additional retirement owner after the material and texture-view fixes. On the DLSS off transition, `ImGui.Texture.DescriptorSet` continued to reference a retired `VkTextureView.RefreshedView`. Updating the same descriptor set in place was unsafe because previously recorded ImGui overlay command buffers could still consume it.

The ImGui Vulkan texture registry now:

- records the source descriptor generation as part of each registration;
- rejects image views that are no longer available for new descriptors;
- allocates and publishes a replacement descriptor set whenever the view, sampler, layout, or descriptor generation changes;
- retires the old descriptor set after publication instead of rewriting an in-flight set;
- advances `VkTextureView`'s descriptor generation whenever a spontaneous viewed-image refresh publishes a new Vulkan view handle.

Iteration 3 (`xrengine_2026-07-20_00-55-08_pid30260`) validated this change. Startup and the DLSS/DLSS-G on/off sequence produced zero descriptor submission rejections, descriptor layout mismatches, assertions, descriptor skips, or exceptions. The capability response also exposed `NumFramesActuallyPresented`: with OneX selected, Streamline reported a maximum of one generated frame and a running presented total, proving that the proxy swapchain was presenting generated frames rather than merely accepting the setting.

## DLSS-G disable race and final hardware validation

Iterations 4 through 7 identified a separate native-state race. The engine setting and presented-frame total became disabled and stable, but Streamline's Present log showed `enabled -> disabled -> enabled`: one already-queued enabled Present could win after the first `DLSSGMode::Off` call. The attempted single-call and host-latch-only fixes did not work and were rejected based on that native transition log.

The final contract is:

- resolving the frame-generation mode respects both the mode and the separate enable setting;
- stale recorded work rechecks the live request while holding the same lock used for `slDLSSGSetOptions`;
- a live disabled request authoritatively clears any stale host-enabled latch;
- successful Off configuration is reasserted for a bounded two-present drain, overwriting options associated with frames queued before the preference transition;
- a genuinely new enabled pipeline request clears the drain and is the only path that re-enables native interpolation;
- redundant same-frame startup `slDLSSGSetOptions` calls remain suppressed.

Final iteration 8 used session `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-20_01-24-48_pid31952/` and the sequence DLSS Quality on, DLSS-G OneX on, DLSS-G off, then DLSS off.

- DLSS-G's presented total increased from 189 to 303 over five seconds while enabled; the runtime maximum was one generated frame.
- After disabling, the presented total stabilized at 499 and Streamline's complete native transition tail was exactly `disabled -> enabled` followed by `enabled -> disabled`, with no later re-enable.
- The final session contains zero descriptor mismatches, rejected submits, assertions, exceptions, `ErrorInvalidState`, repeated DLSS-G options warnings, or missing Streamline constants/tags. The single textual `DeviceLost` match is the startup diagnostic field `khrDeviceLostOnMasked=False`, not a device-loss event.
- Inspected captures `iteration-8/mcp-captures/framegen-off-dlss-on.png` and `iteration-8/mcp-captures/dlss-framegen-off.png` are upright, non-black, and retain the complete hierarchy, inspector, toolbar, and native UI text.
- The editor build succeeded with only the two pre-existing Surfel GI unassigned-field warnings.
- The focused Vulkan upscale/lifetime and uniform-buffer generation-cache suites passed: 36 passed, 0 failed.

The broad `--mcp-allow-all` editor process was closed after validation.
