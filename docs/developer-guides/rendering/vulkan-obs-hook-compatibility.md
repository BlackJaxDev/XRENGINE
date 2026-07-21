# Vulkan OBS Hook Compatibility

Last updated: 2026-07-21

XRENGINE's Vulkan renderer is compatible with OBS Studio's Windows Vulkan game-capture layer, `VK_LAYER_OBS_HOOK`. OBS owns and installs that implicit layer; the engine does not ship or emulate it. The engine-side feature is a startup compatibility contract that keeps the OBS layer visible by default, reports whether the selected Vulkan device can support OBS's shared-texture capture path, and provides explicit controls for disabling or requiring OBS capture support.

This feature is Windows-first and applies to the Vulkan renderer's normal swapchain present path.

## Runtime Shape

OBS captures Vulkan applications through an implicit Vulkan layer. When the layer is active, OBS intercepts the Vulkan instance/device/surface/swapchain/present chain, tracks Win32 surfaces, adds `VK_IMAGE_USAGE_TRANSFER_SRC_BIT` to swapchain images when possible, and captures from `vkQueuePresentKHR`.

XRENGINE supports that model by:

1. Inspecting enabled Windows Vulkan implicit-layer registry entries and their JSON manifests for `VK_LAYER_OBS_HOOK` before `vkCreateInstance`, without calling the Vulkan loader.
2. Leaving the implicit layer enabled by default.
3. Enabling `VK_KHR_external_memory_win32` whenever the selected device exposes it.
4. Probing the selected device for D3D11 texture KMT import support, matching OBS's shared-texture capture path.
5. Creating swapchain images with `VK_IMAGE_USAGE_TRANSFER_SRC_BIT`, which OBS needs for transfer-based capture.
6. Logging a clear startup diagnostic when OBS capture is available, unavailable, disabled, or required but impossible.

## Policy Control

Use `XRE_VK_OBS_HOOK` to control the engine's OBS hook policy:

| Value | Behavior |
|---|---|
| `Auto` | Default. Leaves OBS's implicit layer alone and logs compatibility details if an enabled manifest registration is present. |
| `Disable` | Sets `DISABLE_VULKAN_OBS_CAPTURE=1` before Vulkan instance creation so OBS's Vulkan hook does not attach to the process. |
| `Require` | Fails Vulkan startup if `VK_LAYER_OBS_HOOK` is unavailable, disabled by environment, or the selected device cannot support OBS shared-texture capture. |

Accepted aliases:

| Policy | Accepted values |
|---|---|
| `Auto` | `auto`, `default`, `1`, `true`, `on`, `enable`, `enabled` |
| `Disable` | `disable`, `disabled`, `0`, `false`, `off` |
| `Require` | `require`, `required`, `strict` |

`Require` also treats these environment states as blockers:

- `DISABLE_VULKAN_OBS_CAPTURE=1`
- `VK_LOADER_LAYERS_DISABLE` mentioning `VK_LAYER_OBS_HOOK`, `OBS`, `~implicit~`, or `~all~`

## Capability Requirements

The engine considers OBS capture-ready only when all of these are true:

- An enabled Windows Vulkan implicit-layer manifest registration declares `VK_LAYER_OBS_HOOK`.
- OBS capture is not disabled by `DISABLE_VULKAN_OBS_CAPTURE`.
- Vulkan implicit layers are not disabled by loader environment settings.
- The selected device reports `VK_KHR_external_memory_win32`.
- The engine enables `VK_KHR_external_memory_win32` for the logical device.
- `vkGetPhysicalDeviceImageFormatProperties2` reports importable external memory for a `R8G8B8A8_UNORM` 2D optimal-tiled D3D11 texture KMT image with OBS-compatible usage.

The probe mirrors OBS's capture requirements closely enough to catch the common "OBS sees the process but captures black" class of failures before streaming or recording starts.

## Diagnostics

Startup logs use the `[Vulkan][OBS]` prefix.

Expected healthy examples:

```text
[Vulkan][OBS] VK_LAYER_OBS_HOOK registered by implicit-layer manifest 'C:\\ProgramData\\obs-studio-hook\\obs-vulkan64.json' (policy=Auto, disabledByObsEnv=False, disabledByLoaderEnv=False).
[Vulkan][OBS] Capture-ready device path confirmed: VK_KHR_external_memory_win32=enabled, D3D11 texture KMT import result=Success, features=ImportableBit.
```

Expected unavailable examples:

```text
[Vulkan][OBS] No enabled Windows Vulkan implicit-layer manifest registers VK_LAYER_OBS_HOOK (policy=Auto).
[Vulkan][OBS] Capture device path is not ready: VK_KHR_external_memory_win32 is not reported by the selected Vulkan device.
```

Use `XRE_VK_OBS_HOOK=Require` for streaming or capture test setups where a missing hook should be a launch failure instead of a runtime surprise. Use `XRE_VK_OBS_HOOK=Disable` when debugging Vulkan validation, RenderDoc captures, or driver issues where an interception layer would add noise.

## Manual Validation

Basic capture validation:

1. Install OBS Studio with Game Capture support.
2. Set the editor or test world to Vulkan.
3. Launch normally with `XRE_VK_OBS_HOOK=Auto`.
4. Confirm logs report `VK_LAYER_OBS_HOOK` and capture-ready device support.
5. In OBS, add a Game Capture source targeting the editor process.
6. Confirm the OBS preview updates while the editor presents frames.

Strict validation:

```powershell
$env:XRE_VK_OBS_HOOK='Require'
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -- --unit-testing
Remove-Item Env:XRE_VK_OBS_HOOK
```

Disable validation:

```powershell
$env:XRE_VK_OBS_HOOK='Disable'
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -- --unit-testing
Remove-Item Env:XRE_VK_OBS_HOOK
```

The strict run should fail early if OBS is not installed or if the device path cannot support OBS shared textures. The disable run should log that `DISABLE_VULKAN_OBS_CAPTURE=1` was set for the process.

## Implementation References

Primary implementation:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.ObsHookCompatibility.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanImplicitLayerDiscovery.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Instance.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs`

Related docs:

- [Vulkan Renderer](../../architecture/rendering/vulkan-renderer.md)
- [Vulkan Manual Validation Guide](../../work/todo/vulkan.md)
