# Vulkan Deferred Light Probe Investigation - 2026-07-08

## Problem

Vulkan deferred Sponza light probes appear to capture or apply incorrectly. The scene shows large black/unlit regions and the visible probe previews do not match the expected OpenGL behavior. Capturing a 36-probe grid also feels slower than expected.

User report included an editor screenshot with `VK[DefaultRenderPipeline]`, deferred Sponza, `LightProbeGridRoot (36)`, and capture status `Completed 36/36 probes`.

## Durable Evidence

- Existing work item `docs/work/todo/rendering/vulkan-deferred-and-probe-gi-fixes-todo.md` documents prior Vulkan deferred/probe fixes from 2026-06-09, but runtime Vulkan visual validation remained open.
- Current validation root: `Build/_AgentValidation/20260708-0000-vulkan-light-probes`.

## Working Hypotheses

- Confirmed: probe capture was using a Vulkan full-device idle wait through `SceneCaptureComponent.SynchronizeCaptureTextureWrites`.
- Likely visual root cause: the non-OpenGL path did not record the framebuffer/texture visibility barrier used by OpenGL before later cubemap/IBL reads. `vkDeviceWaitIdle` is a queue completion wait, not the render-graph dependency the deferred/probe texture consumers need.
- Deferred probe application may still need visual validation after the synchronization fix, but the previous descriptor fallback symptoms were not observed in the copied logs.

## Validation Notes

- Evidence logs copied from `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-08_10-27-25_pid26436` into `Build/_AgentValidation/20260708-0000-vulkan-light-probes/logs/`.
- Lighting log: 36-probe capture completed in `23717.09 ms`, average `658.81 ms` per probe. Early probes ranged up to `2520.33 ms`.
- Source root cause:
  - OpenGL path called `MemoryBarrier(Framebuffer | TextureFetch | TextureUpdate)`.
  - Vulkan and other non-OpenGL backends called `WaitForGpu()`.
  - Vulkan `WaitForGpu()` maps to `DeviceWaitIdle()`.
- Fix applied:
  - `XREngine.Runtime.Rendering/Scene/Components/Capture/SceneCaptureComponent.cs` now records the same renderer memory barrier for all backends.
  - The queued non-render-thread path records the same barrier instead of queueing a full GPU wait.
  - Removed the OpenGL-specific renderer type dependency from the capture sync helper.
- Tests:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter LightProbeCapturePipelineTests --no-restore` passed, 4 tests.
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` passed.
- Known validation gap:
  - MCP editor tools were not exposed in this session, so I did not run the full visual iterate-on-editor loop or capture a post-fix Vulkan screenshot.
  - Builds still report existing `Magick.NET-Q16-HDRI-AnyCPU` NU1902/NU1903 vulnerability warnings unrelated to this change.
