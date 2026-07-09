# Vulkan Core Hardening Phase 0 - 2026-07-09

## Status

Phase 0 implementation is partially complete and source-validated.

- Branch created: `rendering-vulkan-core-hardening`.
- Device-loss diagnostics now preserve the last Vulkan queue-submission context.
- The July 9 light-probe/OpenXR failure has a durable summary here instead of
  depending on ignored `Build/_AgentValidation` files.
- Hardware reruns for desktop, OpenXR mirror, shadow, scene capture, and UI
  preview baselines are still pending.

## Code Implementation

Added a first device-loss context snapshot in Vulkan:

- `VulkanRenderer.DeviceLossDiagnostics.cs` records the last queue submit's
  submission kind, frame-op kind, output target, dimensions, frame id, frame
  slot, swapchain/OpenXR image index, command-buffer dirty generation, frame-op
  signature, queue kind, command-buffer count, first command-buffer handle,
  fence handle, and timeline values where present.
- `SubmitToQueueTracked` records that snapshot immediately before `QueueSubmit`
  or `QueueSubmit2`.
- `MarkDeviceLost` is now first-observation guarded and enriches the first
  device-lost reason with the last submission context.
- Swapchain draw submits are tagged as `SwapchainDraw`.
- OpenXR eye, eye batch, mirror, mirror publish, stereo-layer publish, and
  parallel-eye submits pass OpenXR-specific context when their caller has the
  eye index, image index, frame slot, and extent.

The resulting device-loss reason is shaped like:

```text
Reason=<first failing API/result>; LastSubmit kind=<kind> frameOp=<frame-op>
target=<target> extent=<WxH> internal=<WxH> frame=<id> slot=<slot>
image=<image> cmdGen=<generation> frameOps=<signature> planner=<revision>
queue=<queue> waits=<n> signals=<n> cmds=<n> firstCmd=<handle>
fence=<handle> timeline(wait=<value>,signal=<value>) caller=<member>
```

## July 9 Baseline

Source log session:

```text
Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-09_10-21-28_pid7932
```

Observed sequence:

- `LightProbeBatch` was active.
- The batch reached probe 10 of 36.
- Device loss was detected at the OpenXR Vulkan eye fence wait.
- The reported Vulkan result was `ErrorDeviceLost`.
- The Vulkan logs immediately before loss showed capture-sized resource-planner
  churn for scene-capture, light-probe, G-buffer, post-process, bloom,
  auto-exposure, and final-output resources.
- The capture had a caller-owned cubemap FBO, but still went through the full
  viewport/post/temporal path before the immediate mitigation.

Interpretation:

- The OpenXR fence wait is the detection point, not necessarily the root cause.
- The strongest Phase 0 risk classification is cross-context resource churn:
  auxiliary light-probe capture was mutating or reallocating resources adjacent
  to OpenXR eye submission.
- Secondary plausible contributors are stale descriptors/image views, resource
  retirement hazards, layout transitions, and oversized capture work causing
  OpenXR timing pressure.

Immediate mitigation already implemented before this Phase 0 pass:

- Vulkan light-probe capture uses the direct FBO render path.
- Capture viewports disable automatic internal-resolution planning.

## Repeat Repro Manifest

Use this as the minimum manifest for future Phase 0 baseline reruns:

```json
{
  "scenario": "OpenXR Vulkan light-probe batch capture",
  "date": "2026-07-09",
  "branch": "rendering-vulkan-core-hardening",
  "commit": "<git commit>",
  "dirty_worktree": true,
  "command": "dotnet .\\Build\\Editor\\Debug\\AnyCPU\\Debug\\net10.0-windows7.0\\XREngine.Editor.dll --unit-testing",
  "environment": {
    "XRE_UNIT_TEST_RENDER_API": "Vulkan",
    "XRE_UNIT_TEST_USE_OPENXR": "1",
    "XRE_OPENXR_VULKAN_TRACE": "1",
    "XRE_VULKAN_VALIDATION": "1"
  },
  "world_settings": {
    "file": "Assets/UnitTestingWorldSettings.jsonc",
    "hash": "<sha256>",
    "probe_count": 36,
    "probe_resolution": "<fill from settings/log>",
    "scene": "<fill from settings/log>"
  },
  "runtime": {
    "gpu": "<vendor/device>",
    "driver": "<driver version>",
    "vulkan_api": "<version>",
    "vulkan_sdk": "<version>",
    "openxr_runtime": "<runtime/version>",
    "headset_refresh_hz": "<hz>"
  },
  "result": {
    "duration_seconds": "<seconds>",
    "frames": "<count>",
    "completed_probes": "<count>",
    "device_lost": "<true|false>",
    "first_failing_api": "<api/result>",
    "last_submit": "<LastSubmit summary from log>",
    "log_session": "Build/Logs/<session>"
  }
}
```

## Baseline Matrix

| Scenario | Phase 0 status | Evidence |
| --- | --- | --- |
| Light-probe batch capture | Failing baseline recorded from July 9 logs; rerun pending after diagnostics | Probe 10/36 before device loss |
| OpenXR Vulkan eye rendering | Failing baseline recorded from July 9 logs; rerun pending after diagnostics | Eye fence wait returned `ErrorDeviceLost` |
| Editor desktop viewport | Pending rerun | Use `XRE_UNIT_TEST_RENDER_API=Vulkan` with `--unit-testing` |
| OpenXR mirror rendering | Pending rerun | Use OpenXR Vulkan trace and mirror mode from settings |
| Scene capture | Pending rerun | Capture command/path still needs a dedicated manifest |
| Shadow rendering | Pending rerun | Capture shadow atlas resource/log summary |
| UI preview rendering | Pending rerun | Include preview FBO dimensions and target name |

## Risk Taxonomy

| Risk | Phase 0 classification |
| --- | --- |
| Stale descriptor/image view | Plausible; needs descriptor-generation logging in Phase 6 |
| Layout transition mismatch | Plausible; needs layout snapshot correlation in Phase 5 |
| Unsafe resource retirement | Plausible; needs timeline/fence retirement audit in Phase 4 |
| Wrong frame-op context | Strong candidate; capture and OpenXR resource state alternated before loss |
| OpenXR swapchain synchronization | Strong candidate for detection path; Phase 7 owns deeper sync audit |
| Oversized/long GPU work | Plausible; probe batch and post chain can exceed OpenXR/TDR budgets |
| Driver/runtime teardown race | Secondary fallout unless reproduced before first device-loss call |
| Incomplete framebuffer/render-pass metadata | Plausible for capture paths; Phase 3/5 should validate |
| Memory pressure/fragmentation | Plausible; resource churn was visible before loss |
| Descriptor-pool exhaustion/update-while-in-use | Plausible; requires descriptor lifetime diagnostics |
| Shader out-of-bounds/GPU fault | Unknown; Phase 1 device-fault tooling should classify |
| External-synchronization race | Plausible; queue submit is serialized today but adjacent producer state is not fully isolated |

## Validation

Source-contract validation passed:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=VulkanDeviceLossDiagnostics_IncludeLastSubmissionContext|Name=VulkanDeviceLossDiagnostics_TagSwapchainAndOpenXrSubmissions" --no-restore
```

Result:

- Passed: 2
- Failed: 0
- Existing unrelated NuGet advisory warnings for `Magick.NET-Q16-HDRI-AnyCPU`
  were still emitted.
