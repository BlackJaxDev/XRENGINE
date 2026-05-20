# OpenXR Future Work TODO

Last updated: 2026-05-13

OpenXR timing/pipeline Round 1+2 (Phases 0-8 of the now-retired `openxr-timing-todo.md`) shipped: observability stats, post-render frame prep, visibility policy, input/pose sync cleanup, thread-safety hardening, dedicated render-pacing thread, and Round 1 polish. This doc tracks what is still genuinely open after that work.

Sibling test tracker: [openxr-timing-tests-todo.md](../../tests/openxr-timing-tests-todo.md).
No-HMD runtime testing design: [OpenXR Monado Testing Pipeline](../../../design/VR/openxr-monado-testing-pipeline.md).

## Phase 6 — Compositor extensions (P3, gated on hardware metrics)

Hold these until Phase 7 (`DedicatedThread`) has hardware-validated numbers; depth/foveation interactions are easier to interpret against a stable pacing baseline.

- [ ] **`XR_KHR_composition_layer_depth`.** Probe extension support at session init. Allocate a depth swapchain alongside each color swapchain; submit a `XrCompositionLayerDepthInfoKHR` chained off the projection layer. Gate behind a setting (`OpenXrSubmitDepthLayer`, default off) so misbehaving runtimes can opt out. Validate depth range and reverse-Z convention match the engine's projection matrix.
- [ ] **`XR_FB_foveation` / `XR_VARJO_foveated_rendering`.** Probe at session init; wire into the existing foveated `RenderCommandCollection` ViewSet path (`EnableVrFoveatedViewSet`). Add a profile/levels setting; respect runtime-reported max level.
- [ ] **`XR_KHR_visibility_mask`.** Probe at session init; convert mask polygons to a stencil pre-pass per eye on session start / mask-change events. Skip masked fragments in the eye render pass.

Acceptance: each extension lands behind a setting, default off; profiler shows lower GPU time per eye with the extension on; no regression in `VrXrMissedDeadlineFrames`.

## Promote `DedicatedThread` to default (P2)

- [ ] After the hardware validation matrix in the [tests doc](../../tests/openxr-timing-tests-todo.md) is green, flip `Engine.Rendering.Settings.OpenXrRenderPacingMode` default from `PostRenderCallback` to `DedicatedThread`.
- [ ] Update [openxr-vr-rendering.md](../../../architecture/rendering/openxr-vr-rendering.md) "Render Pacing Mode" section to reflect the new default.

## Open Design Questions

- [ ] Some OpenXR runtimes may serialize `xrWaitFrame` internally; pacing-thread benefit depends on whether the runtime returns the next predicted time eagerly. Confirm on SteamVR and Oculus.
- [ ] `RelocatePredicted` cost on Oculus vs. SteamVR: keep opt-in or promote per-runtime?
- [ ] `ViewStateFlags` policy choice (freeze vs identity vs skip) is gameplay-visible; needs design sign-off if any gameplay system reads view pose during tracking loss.
- [ ] Depth swapchain submission can interact badly with some runtimes if depth ranges or projection matrices disagree; keep behind a setting and document the convention.

## Related

- [OpenXR Timing Tests TODO](../../tests/openxr-timing-tests-todo.md)
- [OpenXR Monado Testing Pipeline](../../../design/VR/openxr-monado-testing-pipeline.md)
- [OpenXR VR Rendering (architecture)](../../../architecture/rendering/openxr-vr-rendering.md)
- [OpenXR Implementation Comparison (design)](../../../design/VR/openxr-implementation-comparison.md)
- [OpenVR VRClient GPU Handoff TODO](../gpu/openvr-vrclient-gpu-handoff-todo.md)
