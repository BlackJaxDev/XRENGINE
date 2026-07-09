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
- Vendor binary payloads are written before teardown when reported and reasonably bounded; unavailable or disabled binary payloads are logged explicitly.
- Address-bearing buffers and descriptor-heap storage are registered in a bounded GPU-address range table, and `VK_EXT_device_address_binding_report` callbacks populate a recent binding-event ring.
- `VK_NV_device_diagnostic_checkpoints` markers are recorded per frame operation with pinned backing storage that remains valid until renderer teardown.
- Breadcrumbs now include command marker serial/kind, pass and batch index, image-layout transition serial, descriptor table generation, and the first failing Vulkan/OpenXR API.
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

- Current Silk.NET 2.23 bindings in this repo expose `VK_EXT_device_fault`, but not callable `VK_KHR_device_fault` report/debug-info entry points. Startup logs KHR exposure when present and uses the EXT compatibility collector for persisted artifacts until the binding layer is upgraded.
- Runtime validation is still needed on hardware exposing device fault, address-binding report, and NV checkpoint extensions to confirm each capability produces driver data in a real device-loss session.

## Validation

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=VulkanPhase1Diagnostics_DefinePresetFlagsSettingsAndEnvironmentBridge|Name=VulkanPhase1Diagnostics_WireValidationFeaturesAndCapabilityReports|Name=VulkanPhase1Diagnostics_DeviceLossFooterIncludesBreadcrumbsFaultsAndNamedSyncObjects|Name=VulkanPhase1Diagnostics_CollectFaultArtifactsAddressBindingsAndCheckpoints|Name=VulkanPhase1Diagnostics_DebugNamesCoverPhase1ObjectTypes"`

The renderer build and five focused Phase 1 source-contract tests passed. The output still includes the repo's existing Magick.NET advisory warnings.
