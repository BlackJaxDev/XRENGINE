# Render Queries And Occlusion Validation

Scope: runtime, hardware, and visual checks for the render-query system and the occlusion paths that consume it.

Architecture: [Render Queries](../../../developer-guides/rendering/render-queries.md), [CPU Query Async Occlusion](../../../developer-guides/rendering/cpu-query-async-occlusion.md), [Frame Lifecycle And Dispatch Paths](../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)

Code todos: [Vulkan Core Frame Loop Master TODO](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md), [Vulkan Core Hardening TODO: occlusion policy](../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md#9-make-occlusion-modes-bounded-and-effective)

## Imported Checks

### From vulkan-render-query-system-upgrade-todo.md

The July 2026 loader failure before device creation is historical. Check the current Vulkan startup condition before you run these checks.

- [ ] Capture the Vulkan device and queue query features, enabled features, loaded extensions, timestamp period, timestamp valid bits, and transform-feedback query support in a machine-readable report.
- [ ] Capture an occlusion-enabled desktop baseline and, when available, a true SPS/OpenXR baseline. Record per-output submissions, resolutions, forced-visible reasons, recovery age, coverage proof, and cull counts.
- [ ] Run validation layers over query recording. Expected: no begin/end, reset, pool, index, render-scope, or inheritance VUIDs.
- [ ] Repeat the occlusion-off ground-truth comparison. Expected: final-image and known-visible-sentinel parity, per-output candidate subsets, owning-view proof for every omitted mesh, and both-eye proof for SPS.
- [ ] Compare migrated desktop and VR/SPS occlusion with the baseline. Expected: no additional forced-visible, stale, pending, or recovery-age regressions.
- [ ] Compare timestamp and elapsed results with controlled CPU/GPU ordering on live hardware. Expected: results within documented tolerance and no unconverted device ticks reported as nanoseconds.
- [ ] Run at least one backend execution test for each supported query family where the hardware and API allow it.
- [ ] Iterate in the editor with the Unit Testing World, MCP, per-run logs, and captures from more than one camera position for any visible occlusion regression.
- [ ] Validate desktop mono CPU-query occlusion through a still camera, slow motion, fast motion, a camera cut, command-set mutation, resize, and pipeline recreation.
- [ ] Validate sequential stereo, OpenVR two-pass, OpenVR SPS, and OpenXR true SPS when their runtimes and hardware are available.
- [ ] Repeat the strict-SPS occlusion evidence and final-image parity check that the core-hardening occlusion policy requires.
- [ ] Run Vulkan validation and synchronization validation. Expected: no new query, command-buffer, render-scope, reset, pool-lifetime, or inheritance messages.
- [ ] View the captured desktop and both-eye images. Tool success and nonzero telemetry are not visual evidence.
- [ ] Run `dotnet restore`, `dotnet build XRENGINE.slnx`, and the full unit-test project. Report unrelated failures separately.
- [ ] Confirm that desktop and all available VR modes keep occlusion correctness, bounded recovery, per-output ownership, nonblocking behavior, and useful culling after warmup.
- [ ] Confirm that the query system causes no compiler warnings, Vulkan validation errors, device loss, global waits, or steady-state resource growth.
- [ ] Confirm that CPU async hardware occlusion behavior, settings, telemetry, output ownership, recovery, and stereo proof contracts still pass.

### From collect-visible-render-wait-decoupling-todo.md

- [ ] After the render-thread stalls are fixed, capture a clean scene where `EngineTimer.CollectVisibleThread.DispatchCollectVisible` is the hot path, not `WaitForRender`. This capture gates the collect-visible optimization code item in the Vulkan master todo.
- [ ] Measure CPU-direct and GPU-driven command collection separately in that capture.

### From cpu-async-hardware-query-occlusion-todo.md

- [ ] Run `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter "FullyQualifiedName~CpuRenderOcclusionCoordinatorTests|FullyQualifiedName~Occlusion" --no-restore` and `dotnet build XREngine.Editor/XREngine.Editor.csproj --no-restore`. Report the known `MaskedSoftwareOcclusionCullingTests.StereoVisibilityKeepsObjectVisibleWhenEitherEyeSeesIt` failure separately if it remains.
- [ ] Start the editor in the Unit Testing World with CPU query occlusion enabled. Capture still-camera, slow-camera, fast-camera, and camera-cut profiles.
- [ ] Run scene-only emulated VR with sequential views and CPU query occlusion enabled. Capture left and right draw, cull, and query counts.
- [ ] Run an OpenVR two-pass smoke when hardware and runtime are available.
- [ ] Run an OpenVR single-pass stereo smoke when available.
- [ ] Run a Monado-backed or vendor OpenXR smoke when available. Record the predicted-to-late pose delta and the active collect-visible pose policy.
- [ ] In a high-occlusion static scene, move the camera slowly and steadily. Expected: `CpuRendered / CpuTested` does not return to about 1.0 after warmup.
- [ ] Expected: no visible false occlusion during editor camera motion, near-plane movement, stereo, OpenVR, and OpenXR smokes, and object add or remove.
- [ ] Make a camera cut. Expected: culling recovers within the configured number of frames.
- [ ] Move the HMD continuously. Expected: CPU query culling stays useful after warmup, with no stereo eye popping.
- [ ] Expected: an object visible in either eye stays visible in shared stereo and single-pass stereo modes.
- [ ] Compare query cost and proxy draw count with the earlier path for the same scene. Expected: equal or lower, and no unexpected doubling in sequential VR.
- [ ] Force each fallback-to-visible case. Expected: telemetry reports each case.
