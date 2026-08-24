# Vulkan default skybox freeze and monitor brightness flicker

Opened: 2026-08-14  
Last updated: 2026-08-14  
Status: missing-texture frame deferral fixed and live validated; HDR capture conversion and Vulkan VRR pacing remain open

## Reported symptoms

1. Adding a `SkyboxComponent` to a scene that has no skybox freezes the visible
   3D view. Camera input continues to update, and disabling the component makes
   the view jump to the latest camera pose.
2. The Vulkan editor causes visible brightness flicker on the detected
   2560x1440 240 Hz LG UltraGear+ display. Windows Dynamic Refresh Rate is off.
   Changing the fixed monitor refresh rate changes the flicker cadence, and
   Windows HDR makes it more visible.

## Skybox diagnosis

The newly constructed component is not renderable in its default state:

- `SkyboxComponent` defaults to `ESkyboxMode.Texture`.
- Activation immediately calls `RebuildAll()`.
- Texture mode builds the equirectangular material with `Texture0`, but
  `CreateDefaultTexture(...)` intentionally returns `null`.
- Vulkan classifies that sampled image as required. Descriptor preparation
  therefore defers the skybox draw instead of binding an invalid descriptor.
- The hardened desktop frame loop rejects the incomplete command package and
  presents the last completed content. Update/input state continues to advance,
  which produces the observed jump when the invalid component is disabled.

The isolated Vulkan reproduction recorded:

```text
[WriteDesc] FAILED to resolve image binding 'Texture0' ... Skybox.Equirectangular
[FrameFailure][RejectedDesktopFrame] policy=PresentLastCompletedContent
reason=ReuseCompletedContent ... rejectionStage=RecordDeferred
```

The failure repeated for every sampled interval while the texture-less skybox
was active. The final-presentation ledger changed from two normal scene
swapchain writes per frame to one rejected-frame recovery write and no scene
write. Removing the component, or setting `Mode=Gradient`, restored normal
scene writes and reduced descriptor failures/skipped draws to zero.

The existing Unit Testing World helper already avoids this state: it selects
`Gradient` when no sky texture is available and selects `Texture` only after a
texture is assigned. The underlying unsafe component default predates the
current Phase 5 diff; Phase 5's last-good-frame recovery makes the invalid state
look like a deliberate freeze instead of publishing incomplete Vulkan work.

## Missing-texture correction

The renderer now distinguishes an unassigned sampled-texture slot from an
assigned texture that is still uploading or otherwise not GPU-ready:

- Unassigned sampled textures always receive a valid descriptor placeholder;
  they no longer enter the required-resource deferral path.
- The default material policy is diagnostic magenta.
- `SkyboxComponent` explicitly selects an opaque-black fallback.
- Vulkan fallback images expose compatible 2D, 2D-array, cube, and cube-array
  views, so every skybox projection can use the same policy.
- Sampled inputs in mesh, material, and compute descriptor paths follow the
  non-deferring rule. Assigned-but-not-ready textures retain the existing
  readiness gate.
- Storage images and input attachments are intentionally excluded: they have
  concrete write/render-pass contracts and cannot safely be replaced by a
  sampled-image placeholder.

Validation evidence:

- `dotnet build XREngine.Runtime.Rendering.Vulkan/XREngine.Runtime.Rendering.Vulkan.csproj --no-restore --warnaserror`
  completed with zero warnings and zero errors.
- In isolated final session `skybox-missing-fallback-final`, an active
  `SkyboxComponent` remained in `Texture` mode with `Texture = null`.
- The render frame ID advanced from `5579` to `13024` while the camera moved
  from `(0,0,0)` to `(0,2,0)`.
- The final capture visibly shows opaque black through the open roof rather
  than magenta, and the session contains none of the earlier `Texture0`
  descriptor failures, `RecordDeferred` recovery, rejected-frame messages,
  device-loss messages, or Vulkan validation diagnostics.

## Brightness-flicker diagnosis

The current Vulkan presentation configuration is:

```text
format=B8G8R8A8Srgb
colorSpace=SpaceSrgbNonlinearKhr
presentMode=PresentModeMailboxKhr
extent=1920x1080
images=3
```

Effective editor timing is `VSync=Off` and `TargetFramesPerSecond=0`, so the
render loop is uncapped. Vulkan does not currently translate `EVSyncMode` into
swapchain present modes: the primary and detached ImGui swapchains prefer
`MAILBOX` regardless of the editor VSync setting. `XRWindow.ApplyVSyncMode`
changes the window backend's VSync property, but only the OpenGL path explicitly
sets a swap interval.

A static-camera sequence captured 60 Vulkan viewport frames and computed pixel
differences:

| Measure | Result |
| --- | ---: |
| Mean luminance minimum | 0.220745910 |
| Mean luminance maximum | 0.220748829 |
| Mean luminance range | 0.000002919 |
| Maximum frame-to-frame MAE | 0.000001135 |
| Maximum changed-pixel ratio | 0 |

This rules out a meaningful alternating-brightness signal in the rendered scene
for the static reproduction. The remaining leading cause is variable-refresh
panel gamma/near-black flicker triggered by irregular windowed presentation.
Windows Dynamic Refresh Rate is a power/cadence policy and is separate from
driver/monitor VRR (NVIDIA G-SYNC Compatible or AMD FreeSync/Adaptive-Sync).
Disabling DRR therefore does not disable windowed VRR.

Windows HDR can amplify the visibility because this run still used an SDR sRGB
swapchain, which the desktop compositor maps into the HDR desktop, while a
VRR-capable high-refresh panel can expose cadence-dependent gamma, near-black,
or local-dimming changes more strongly.
If engine `OutputHDR` is enabled instead, the startup path can request an HDR
surface, but changing `OutputHDR` at runtime does not currently update
`XRWindow.PreferHDROutput` or explicitly recreate the swapchain. That is a
separate HDR-output lifecycle gap, not an explanation for flicker that also
occurs in SDR.

The user subsequently disabled globally enabled G-SYNC and the brightness
flicker stopped. That result strongly confirms the VRR/presentation-cadence
boundary. It also explains why ordinary games may not reproduce it: the editor
uses an uncapped windowed `MAILBOX` path, can present multiple ImGui platform
swapchains, and has much less regular frame timing than a game using one
direct-flip swapchain with a stable cap or explicit VRR-aware pacing.

## Why HDR viewport captures look darker

The MCP viewport capture is not currently color-managed for HDR output:

- The observed capture source is `R16G16B16A16Sfloat`, the camera's linear
  floating-point output.
- `VulkanCommandRuntime.TryConvertColorPixelsToRgba8(...)` clamps each float
  directly to `[0,1]` and quantizes it to eight bits. It does not apply the
  presentation tone map, an HDR-to-SDR operator, or a linear-to-sRGB transfer.
- The resulting `MagickImage` PNG has chromaticity metadata but no sRGB chunk,
  gamma chunk, or ICC profile.
- The actual window instead passes through the Vulkan swapchain and Windows HDR
  compositor/monitor mapping. The PNG viewer therefore receives different
  numeric values and display metadata than the live window.

That is why the capture is darker; it is not evidence that alternating darker
frames are being rendered. The correct follow-up is an explicit capture-output
contract: convert a linear/HDR source to a color-managed SDR sRGB PNG for normal
screenshots, or preserve HDR values in a format with correct HDR metadata.
Blindly adding gamma in the byte converter would be incorrect because not every
readback source has the same transfer function.

## Discriminating checks

Perform these one at a time:

1. **Confirmed:** disabling G-SYNC for the LG display removes the editor
   brightness flicker. A full-screen-only application policy can keep windowed
   editor surfaces out of G-SYNC without disabling it for games.
2. Keep `VSync=Off`, but cap the editor at a rate it can hold continuously, such
   as 120 Hz. A stable divisor is a better diagnostic than a 237 Hz cap when the
   scene cannot sustain 237 FPS.
3. Force VSync on for `XREngine.Editor.exe` in the driver as a true fixed-cadence
   comparison. The editor's Vulkan VSync toggle alone does not currently select
   FIFO.
4. Compare OpenGL with Vulkan. OpenGL applies the requested swap interval; a
   Vulkan-only result isolates the swapchain/pacing policy.
5. With VRR disabled, compare Windows HDR off/on. Flicker that remains only in
   HDR belongs to the compositor/monitor HDR path or the engine HDR-surface
   lifecycle, rather than VRR cadence.

## Evidence

- Isolated session:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260814-000836-skybox-flicker-diagnosis`
- Fixed final session:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260814-005905-skybox-missing-fallback-final`
- Fixed final capture:
  `Build/_AgentValidation/20260814-000827-skybox-monitor-flicker/mcp-captures/fixed-missing-skybox-final/Screenshot_20260814_010059_828_025b83ba6bd843b8bb57219763914a21.png`
- Capture manifest:
  `Build/_AgentValidation/20260814-000827-skybox-monitor-flicker/mcp-captures/ViewportSequence_20260814_071754_841_13fd3253183d4ff597f9aa97b5dc5837/manifest.json`
- RenderDoc environment check passed with RenderDoc 1.44 and a registered Vulkan
  layer. A GPU capture was unnecessary because descriptor admission, the final
  presentation ledger, and pixel-sequence evidence identified both boundaries.
- Microsoft distinguishes Windows DRR from ordinary game VRR:
  <https://support.microsoft.com/en-us/windows/hardware/display-graphics/change-the-refresh-rate-on-your-monitor-in-windows>
- NVIDIA documents that G-SYNC can be enabled for windowed applications:
  <https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-us/mergedProjects/nvdsp/To_use_variable_refresh_rates.htm>
