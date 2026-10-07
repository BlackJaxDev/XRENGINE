# OpenXR Future Work TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md), [OpenXR Runtime](../../../../developer-guides/vr/openxr-runtime.md)
Validation: [OpenXR Validation](../../../testing/xr/openxr-validation.md), [OpenXR SteamVR Hardware Validation](../../../testing/xr/openxr-steamvr-hardware-validation.md)

## Current State

The OpenXR path has timing stats, post-render frame preparation, visibility policy, input and pose sync cleanup, thread-safety hardening, and a dedicated pacing thread. `RuntimeRenderingHostServiceDefaults.OpenXrRenderPacingMode` now defaults to `OpenXrRenderPacingMode.DedicatedThread`. Extension work, runtime-specific policy decisions, and hardware qualification remain open.

## Open Code Items

### Compositor extensions

- [ ] Add optional `XR_KHR_composition_layer_depth` support. Update `XREngine.Runtime.XR.OpenXR` swapchain creation and frame submission, and add an `OpenXrSubmitDepthLayer` setting that defaults off. Done when: supported runtimes receive `XrCompositionLayerDepthInfoKHR`, unsupported or disabled runtimes use color-only projection layers with a clear diagnostic, and depth range and reverse-Z conventions are documented.
- [ ] Add optional `XR_FB_foveation` and `XR_VARJO_foveated_rendering` support. Update `XREngine.Runtime.XR.OpenXR` foveation probing and the view-set path that consumes `EnableVrFoveatedViewSet`. Done when: a setting selects profile and level, the runtime-reported maximum is respected, and unsupported runtimes report a fallback reason.
- [ ] Add optional `XR_KHR_visibility_mask` support. Update `XREngine.Runtime.XR.OpenXR` session initialization and eye rendering. Done when: mask polygons become a per-eye stencil pre-pass on session start or mask-change events, and masked fragments are skipped without changing the visibility frustum.

### Runtime policy

- [ ] Decide whether `RelocatePredicted` remains opt-in or becomes per-runtime policy. Update `OpenXrCollectVisiblePosePolicy` and the runtime guide. Done when: the policy has a clear default and runtime-specific exceptions are capability-driven or explicitly documented.
- [ ] Decide the gameplay-visible `ViewStateFlags` tracking-loss policy. Update `OpenXrTrackingLossPolicy` and the runtime guide. Done when: freeze, identity, and skip behavior are documented for gameplay systems that read view pose during tracking loss.
- [ ] Document the depth-layer convention and runtime opt-out policy. Update the runtime guide and settings docs when depth-layer submission lands. Done when: depth ranges, projection matrices, reverse-Z behavior, and opt-out controls are clear.

## Decisions Needed

- [ ] Which runtimes must pass extension and pacing validation before OpenXR becomes the default SteamVR path? Owner: Rendering / XR.
- [ ] Which compositor extensions are required for v1, and which remain optional diagnostics or quality settings? Owner: Rendering / XR.

## Out Of Scope

- Reverting the `DedicatedThread` default. The code already sets it as the default.
- Retiring OpenVR before the SteamVR OpenXR hardware matrix is accepted.
