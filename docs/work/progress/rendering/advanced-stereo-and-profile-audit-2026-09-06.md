# Advanced stereo and profile audit — 2026-09-06

This source audit records the current admission boundaries for ARP-I21, I22,
I26, I27, ARP-A03, and ARP-A04. It is not runtime validation.

## Stereo and OpenXR

Advanced resource declarations already reserve texture-array layers from the
profile view count. For example, [classification resources](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.Classification.cs)
and native shading resources calculate their capacity from `ViewCount`; native
compute closure capture also acquires a single immutable image view for its
explicit `viewIndex` in [VulkanAdvancedVisibilityResourceRuntime](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Advanced/VulkanAdvancedVisibilityResourceRuntime.cs).
The Advanced shader path indexes texture arrays and froxels by a pushed view
index.

That is insufficient for ARP-I21. [AdvancedRenderPipeline.StereoAndViews](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.StereoAndViews.cs)
only retains `StereoMode` and invalidates resource allocation. It does not
turn a located eye set into immutable per-eye visibility, classification,
shading, and history operations. In contrast,
[ViewSetPlan](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/ViewSetPlan.cs)
already freezes located OpenXR view identities and distinct history keys.

The bounded I21 implementation is an Advanced view-set adapter that freezes
the logical eye ID, resource layer, view index, and history key at frame-plan
admission, then emits a complete stage chain for each eye. Occlusion verdicts
must remain per-eye, as specified by
[AdvancedStereoContract](../../../../XREngine.Runtime.Rendering/Rendering/Stereo/Advanced/AdvancedStereoContract.cs).
The existing stereo rejection must remain until every Advanced resource and
closure consumes that same frozen mapping.

ARP-I22 has a higher-level routing gap. [RuntimeEngine](../../../../XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs)
chooses `RvcRenderPipeline` for `OpenXrEye` and `AdvancedRenderPipeline` for
`DesktopScene`. RVC owns the explicit OpenXR graph stages in
[VPRC_RvcPass](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_RvcPass.cs).
An Advanced OpenXR profile therefore needs a capability-gated purpose resolver
and an adapter into the RVC/OpenXR eye owner. It must not move predicted pose,
late-latching, deadline, or `xrEndFrame` ownership into Advanced.

ARP-I26 is not implemented. The `OpenGlSinglePassStereo` value is presently a
mode declaration, while the generic Advanced capability resolver only checks
`SupportsStereoArrayResources`. No Advanced OpenGL SPS bridge selects layered
attachments, drives view-local histories, or gates `OVR_multiview` capability.

ARP-I27 is also declaration-only. [AdvancedFoveationContract](../../../../XREngine.Runtime.Rendering/Rendering/VR/Advanced/AdvancedFoveationContract.cs)
computes a conservative LOD bias, and canonical frame publication flags a
foveated view, but no immutable per-view foveation-region/quality row is bound
to Advanced visibility or native shading. The required slice publishes those
rows, binds them to both passes, applies the derivative/LOD policy, and keeps
all eye reuse disabled until validated.

## Effect-family admission

The only named special-effect material factory found is
[`XRMaterial.CreateDynamicWaterMaterialForward`](../../../../XREngine.Runtime.Rendering/Objects/Materials/XRMaterial.cs).
It declares the Refraction lane, scene-color snapshot consumption, and order
dependence. Its metadata deliberately supplies a temporal unsupported reason:
the water tessellation/displacement path has no coverage-preserving temporal
variant.

[ParticleEmitterComponent](../../../../XREngine.Runtime.Rendering/Scene/Components/Particles/ParticleEmitterComponent.cs)
uses the particle shader family, including optional weighted OIT, but has no
`AdvancedLatePassMetadata`; it is rejected by
[AdvancedLatePassEligibilityValidator](../../../../XREngine.Runtime.Rendering/Rendering/Transparency/Advanced/AdvancedLatePassEligibilityValidator.cs)
with the existing missing-metadata diagnostic. No concrete hair, trail, beam,
or portal factory was found. [AdvancedForwardMirrorComponent](../../../../XREngine.Runtime.Rendering/Scene/Components/Misc/AdvancedForwardMirrorComponent.cs)
uses the background lane, and [MirrorCaptureComponent](../../../../XREngine.Runtime.Rendering/Scene/Components/Capture/MirrorCaptureComponent.cs)
is a traditional render-to-texture component, so neither is an Advanced late
effect.

The smallest A03 change is family identity on late metadata plus one central
resolver that names the water, particle, and mirror outcomes. It should report
explicit unsupported reasons for absent families rather than treating the
generic `SpecialEffects` lane as family support.

## Vendor upscaler profiles

[VPRC_VendorUpscale](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs)
contains the authoritative current VR support matrix: all vendor upscale and
frame-generation paths are unsupported for VR/stereo, while fallback blit is
supported. Desktop native dispatch requires committed resources, a Vulkan
renderer, an admitted runtime capability/session, and valid input resources.
OpenGL can use only the explicitly requested `IOpenGlVendorUpscaleBackendCapability`
bridge. [VendorUpscaleRuntime](../../../../XREngine.Runtime.Rendering/Runtime/RendererModules/VendorUpscaleRuntime.cs)
is a façade over an optional registered module, so the API surface does not
certify any device.

ARP-A04 should create separate validation IDs for Vulkan desktop DLSS upscale,
Vulkan desktop DLSS frame generation, Vulkan desktop XeSS upscale, Vulkan
desktop XeSS frame generation, and each actually exposed OpenGL bridge vendor.
Each result must record backend, GPU/vendor/device, HDR, depth/motion/history
inputs, and committed resource generation. Every absent module/capability and
all XR/stereo profiles remain explicitly unsupported.
