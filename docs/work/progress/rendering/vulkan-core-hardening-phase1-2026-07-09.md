# Vulkan Core Hardening Phase 1 - 2026-07-09

## Implemented

- Added named Vulkan diagnostic presets: `Off`, `StandardValidation`, `SyncValidation`, `GpuAssisted`, `BestPractices`, `CrashDiagnostics`, and `RenderDocFriendly`.
- Added independent `EVulkanDiagnosticFlags` so validation, sync validation, GPU-assisted validation, crash breadcrumbs, device-fault requests, NV diagnostics, and RenderDoc labels can be described separately.
- Wired project settings, runtime effective settings, host services, editor environment preferences, and environment variables:
  - `XRE_VULKAN_DIAGNOSTIC_PRESET`
  - `XRE_VULKAN_DIAGNOSTIC_FLAGS`
  - legacy `XRE_VULKAN_VALIDATION`
  - per-feature toggles such as `XRE_VULKAN_SYNC_VALIDATION`, `XRE_VULKAN_GPU_ASSISTED_VALIDATION`, `XRE_VULKAN_CRASH_BREADCRUMBS`, and `XRE_VULKAN_RENDERDOC_FRIENDLY`
- Vulkan startup now logs the resolved diagnostic matrix, validation layers, validation features, instance extensions, source overrides, and overhead warnings.
- Validation feature pNext wiring now enables sync validation, GPU-assisted validation, and best-practices checks through `VkValidationFeaturesEXT`.
- Diagnostic device extensions are requested only when the resolved flags ask for them:
  - `VK_EXT_device_fault`
  - `VK_EXT_device_address_binding_report`
  - `VK_NV_device_diagnostic_checkpoints`
  - `VK_NV_device_diagnostics_config`
- Device creation now enables available diagnostic feature bits and logs unavailable diagnostic extensions/features explicitly.
- Device-loss diagnostics now append a compact footer with:
  - last submit context,
  - crash breadcrumb tail,
  - structured validation summary,
  - EXT device-fault report counts and details when available,
  - address-binding report correlation when available,
  - NV checkpoint counts and resolved marker metadata when available.
- Device-fault collection now persists a human-readable `vulkan-device-fault-report.log` with description, address records, vendor records, address-to-object correlation, `VK_INCOMPLETE` status, and vendor binary status.
- KHR device-fault reports and debug info are callable through the local `GetDeviceProcAddr` shim added in Phase 1.1, while EXT remains the compatibility fallback.
- KHR and EXT address records, vendor records, report counts, and vendor binary payloads have configurable hard caps. Count growth, bounded batch draining, `VK_ERROR_NOT_ENOUGH_SPACE_KHR`, `VK_INCOMPLETE`, and truncation are reported explicitly.
- Vendor binary payloads are written before teardown when reported and within the configured cap; unavailable, failed, incomplete, truncated, or disabled payloads are labeled explicitly.
- Address-bearing buffers and descriptor-heap storage are registered in a bounded GPU-address range table, and `VK_EXT_device_address_binding_report` callbacks populate a recent binding-event ring.
- `VK_NV_device_diagnostic_checkpoints` markers use stable opaque serial values and resolve through bounded renderer-owned metadata instead of overwritable pinned marker slots.
- Breadcrumbs now include the submitted command-buffer handle and recording generation, actual frame-op/planner/resource/descriptor generations, command marker serial/kind, pass and batch index, a bounded image-layout transition tail, queue-operation history, and the first failing Vulkan/OpenXR API.
- Descriptor table generation is bumped for descriptor set allocations and descriptor writes across swapchain, bindless material textures, ImGui, compute, mesh renderer, material, renderer-owned, and descriptor-heap paths.
- Added debug names for framebuffers, descriptor sets, command-buffer frame-op contexts, and OpenXR swapchain image views, in addition to the existing image/view and sync-object names.
- Added a no-runtime-dependency AMD/Intel vendor hook report that names the standard fallback artifacts when no native vendor hook is loaded.

## Diagnostic Launch Examples

PowerShell examples:

```powershell
$env:XRE_VULKAN_DIAGNOSTIC_PRESET="SyncValidation"
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -- --unit-testing
```

```powershell
$env:XRE_VULKAN_DIAGNOSTIC_PRESET="CrashDiagnostics"
$env:XRE_VULKAN_DIAGNOSTIC_FLAGS="DeviceFault,NvDiagnosticCheckpoints"
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -- --unit-testing
```

```powershell
$env:XRE_VULKAN_DIAGNOSTIC_PRESET="RenderDocFriendly"
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -- --unit-testing --mcp --mcp-port 5467
```

Normal runs keep the preset at `Off`, so validation and labels remain disabled unless settings or environment variables request them.

## Known Gaps

- The RTX 4070 Laptop GPU used for the July 9 Phase 2.1 validation does not advertise `VK_KHR_device_fault`; it advertises `VK_EXT_device_fault`. The callable KHR shim therefore remains source- and unit-tested but still needs a real device-loss run on hardware that exposes the KHR extension.
- Runtime validation is still needed on hardware exposing device fault, address-binding report, and NV checkpoint extensions with `CrashDiagnostics` enabled to confirm each capability produces driver data in a real device-loss session.

## Validation

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=VulkanPhase1Diagnostics_DefinePresetFlagsSettingsAndEnvironmentBridge|Name=VulkanPhase1Diagnostics_WireValidationFeaturesAndCapabilityReports|Name=VulkanPhase1Diagnostics_DeviceLossFooterIncludesBreadcrumbsFaultsAndNamedSyncObjects|Name=VulkanPhase1Diagnostics_CollectFaultArtifactsAddressBindingsAndCheckpoints|Name=VulkanPhase1Diagnostics_DebugNamesCoverPhase1ObjectTypes"`
- Phase 0 through 2.1 focused lane: 93 passed, 0 failed.
- Hardware and software evidence: [vulkan-core-hardening-phase21-validation-2026-07-09.json](../../testing/rendering/vulkan-core-hardening-phase21-validation-2026-07-09.json).

The renderer build, focused diagnostics tests, and the broader Phase 2.1 lane passed. Build output still includes the repo's existing Magick.NET advisory warnings. SyncValidation hardware runs did not lose the device, but they remain validation-unclean for synchronization, query lifecycle, cube-view, and teardown issues recorded in the linked manifest.
