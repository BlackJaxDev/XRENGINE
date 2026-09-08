# Advanced special effects and upscaler audit (2026-09-07)

The original September 7 source audit for ARP-A03 and ARP-A04 performed no runtime or build validation. The September 8 closure below adds build and live diagnostic read-back evidence for the unsupported-family dispositions.

## ARP-A03

Advanced late admission is implemented by `AdvancedRenderPipeline.AppendAdvancedLatePassCommands` in `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.LateAndPostCommands.cs`. It executes the weighted, transparent, on-top, PPLL, and depth-peeling lanes and snapshots scene color only for declared consumers or feedback. `TryValidateLatePassProfile` in `AdvancedRenderPipeline.LateEligibility.cs` rejects non-Advanced lanes, disabled late transparency, disabled weighted OIT, and exact transparency for stereo/startup-safe profiles.

Particles have a real submitted lane. `ParticleEmitterComponent` creates `RenderInfo3D` for `TransparentForward` and maps additive to `Additive`, premultiplied/alpha to `WeightedBlendedOit` in `Scene/Components/Particles/ParticleEmitterComponent.cs:551,598-613`. This proves pass submission and material selection only: `XRMaterial.AdvancedLatePassMetadata` is nullable and has no default (`Objects/Materials/XRMaterial.AdvancedLatePass.cs:5-16`), so the particle material does not establish Advanced metadata. The referenced `ParticleBillboard.vs` path also has no matching Common shader in the audited tree.

Water has an authored forward material factory: `XRMaterial.CreateDynamicWaterMaterialForward` in `XREngine.Runtime.Rendering/Objects/Materials/XRMaterial.cs`. It uses `Build/CommonAssets/Shaders/Common/WaterDynamicForward.tesc`, `.tese`, and `.fs`. Its explicit Refraction metadata requests a scene-color snapshot and motion participation, but also reports `Dynamic water has no coverage- and displacement-preserving temporal motion variant.` Scene-color validation belongs to ARP-V27; transparent motion remains ARP-V26. Do not certify a temporal variant from the participation flag.

Hair is represented by physics-chain simulation (`docs/architecture/physics/overview.md:125-132`); no Advanced render material or lane is established by that evidence. No trail or beam renderer/admission was found in the runtime or common shader inventories. Mirrors have an explicit `MirrorCaptureComponent.UseAdvancedCapturePipeline` opt-in and an owned Advanced registration; the traditional capture path remains the default. `PortalCaptureComponent` and `ThumbnailCaptureComponent` derive from `AdvancedOffscreenTextureCaptureComponent`, which owns its private camera/viewport/output, writer fence and completion-gated consumer leases. ARP-I23 records this implementation; mirror/portal/thumbnail runtime acceptance remains V36/V47/V49. Landscape displacement is authored in `LandscapeComponent.cs:243,1296` and `TerrainLayer.cs:50`, but the Advanced visibility contract admits only `EAdvancedVisibilityDisplacementMode.None`: `AdvancedVisibilityShaderVariantContract.cs:11-25`; non-None modes are represented by `EAdvancedVisibilityDisplacementMode` and have no admitted variant.

ARP-A03 remains open. Particles require the missing shader and explicit admission metadata or an actionable rejection. Missing dedicated hair/trail/beam implementations do not establish a supported special lane; ordinary imported geometry still follows its authored material contract. The earlier missing-owner finding is superseded by ARP-I23; owned mirror/portal/thumbnail source paths exist and still need their runtime matrices. Displacement's enum/variant contract is not proof that authored landscape displacement reaches that rejection. Preserve existing validation ownership: water ARP-V26/V27, mirrors ARP-V36, portals ARP-V47, thumbnails ARP-V49. No duplicate validation IDs are created by this audit.

### September 8 closure

The preceding paragraph records the original open findings. ARP-I68 now exposes
`AdvancedRenderingUnsupportedReason` on the particle and landscape components,
displays it before the mode toggle in both custom ImGui inspectors, and guards
their render callbacks whenever the active pipeline hosts the Advanced family.
Default particle materials also carry explicit unsupported late-pass metadata.
The landscape rejection covers the whole displaced/chunked producer, including
height displacement, morphing and parallax; toggling parallax alone cannot admit
the component. These are explicit unsupported profiles, not implemented particle
or terrain integrations.

Build90 passes with zero warnings/errors. The running Vulkan editor (Build91,
PID47372) returned both exact reasons through MCP on inactive component instances,
so this observation does not initialize GPU simulation or claim rendered effects.
Evidence: `mcp-output/vk68-{ParticleEmitterComponent,LandscapeComponent}.json` under
the September 6 validation root. Broker inventory
`059e1b0fd0064c5b86b3a40cd8de1125` completed with requested/actual Luna.

ARP-A03 is closed as an inventory with dispositions: water has explicit refraction
and unavailable temporal replay; particles and landscape have inspector-visible
unsupported reasons; mirrors, portals and thumbnails own admitted capture lanes
with separate V rows; dedicated hair/trail/beam renderers do not exist in this
inventory. Imported hair or effects represented by ordinary meshes continue to
use their material's native/late admission and rejection contract. No nonexistent
family is claimed as a newly implemented feature.

## ARP-A04

Native Vulkan and the OpenGL-to-Vulkan bridge are separate. `XREngine.Runtime.Rendering.Vulkan/VulkanRendererBackendModuleEntry.cs` registers `Rendering/VulkanVendorUpscaleService.cs`, whose dispatch methods delegate to the vendor runtime. Native command entry points under `Rendering/API/Rendering/Vulkan/Commands/FrameOps/` include `DlssUpscaleOp.cs` and `DlssFrameGenerationOp.cs`. `VPRC_StreamlineDlssUpscale` calls the DLSS dispatcher and `VPRC_VendorUpscale` calls the XeSS dispatcher. `XREngine.Runtime.Rendering.Vulkan/Rendering/XeSS/IntelXessNative.cs` resolves Vulkan requirements, creates a context and dispatches `xessVKExecute`. Its `TryDispatchFrameGeneration` always returns false: even after library availability it reports that dispatch is not wired to the DirectX12 swapchain path. This is an unimplemented dispatch, not proof of working DX12 frame generation. Native super-resolution paths do not inherit the bridge's OpenGL-window or same-GPU predicates.

The bridge's `DetermineAvailability` in `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridge.cs` requires Windows, an OpenGL window backend, one viewport, non-stereo, HDR support where requested, GL/Vulkan external-memory and semaphore interop, successful Vulkan probing, same physical GPU, and a supported runtime. Stereo rejection is explicit: `bridge MVP excludes stereo/XR pipelines`. `BuildFrameResources` selects DLSS for NVIDIA DLSS or DLAA and XeSS when enabled unless AA is DLAA. `ResolveVendorRequirements` in the neighboring `VulkanUpscaleBridgeSidecar.cs` loads the selected native requirements, with DLSS/XeSS preference and fallback.

The bridge surface enumerates DLSS and XeSS; that enum alone cannot certify every renderer backend. No FSR dispatch was found in this audit. DLAA selects the DLSS path at native resolution. DLSS frame generation has Streamline session/recording code, but bridge output/swapchain provisioning needs separate end-to-end tracing. Advanced `RuntimeEnableVendorUpscale` returns false for an external swapchain before selecting a vendor command; this suppression is not an error-visible required-profile rejection.

ARP-A04 is closed as a source/profile inventory, with canonical children in the active tracker:

| Validation | Backend and feature | Admission requirements |
|---|---|---|
| ARP-V61 | Native Vulkan DLSS SR | Available Streamline runtime and runtime-supported physical device; valid current color/depth/motion, camera, viewport and positive input/output extents; mono application-owned output. |
| ARP-V62 | Native Vulkan DLAA | The DLSS requirements with native-resolution evaluation. |
| ARP-V63 | Native Vulkan XeSS SR | XeSS Vulkan library/context and its queried device extension/feature requirements; valid mono color/depth/motion and extents. |
| ARP-V64 | Native Vulkan DLSS FG | Streamline frame-generation feature admission plus provisioned Vulkan frame resources and a compatible presentation path. SR admission does not imply FG admission. |
| ARP-V65 | OpenGL/Vulkan bridge DLSS SR | Windows OpenGL window, one mono viewport, requested HDR support, GL/Vulkan external memory and semaphores, successful Vulkan probe, same physical GPU and DLSS runtime admission. |
| ARP-V66 | OpenGL/Vulkan bridge DLAA | The bridge DLSS requirements at native resolution. |
| ARP-V67 | OpenGL/Vulkan bridge XeSS SR | The bridge interop/window requirements plus XeSS runtime feature/device admission. |

The bridge has no frame-generation dispatch. XeSS frame generation has an explicit unwired-dispatch result, and FSR has no executable path in this inventory. XR/stereo and external swapchain vendor upscaling are excluded by command/bridge validation. Required requests must expose these exclusions; ARP-V02 must inspect the Advanced external-swapchain suppression described above as well as command-level rejection. None of these exclusions is silently reclassified as a supported profile.

This inventory records the device predicates used by the implementation, not a claim that a particular installed GPU/SDK satisfies them. Exact hardware, driver and SDK results belong to each V child. Independent broker source inventory `9ec0bcbb02da45578bc058fba6a9829c` completed with requested/actual `gpt-5.6-luna`; local source tracing above supplies the implementation boundaries.
