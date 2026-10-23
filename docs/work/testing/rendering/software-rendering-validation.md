# Software Rendering Validation

Design: [Software rendering integration](../../design/rendering/software-rendering-integration-design.md)

## Setup

Use a future approved software package and implemented explicit mode. The
design names proposed settings; they are not current launch flags. Use the
repository [build and runtime workflow](../../../developer-guides/ai/agent-editor-workflows.md#build-run-debug)
when implementation starts. Keep Windows, Linux X11, Linux Wayland, display-free
output, and each driver/API pair as separate results. Record failures as failures
or blocked checks; skipped checks are not passes.

## Acceptance and benchmark matrix

All rows are **Planned; last evidence: none**. Keep the historical software Vulkan
results in the [existing guide](../../../developer-guides/testing/software-vulkan-validation.md).
Those results do not satisfy the product rows below. No runtime check was run
for this document.

| Check | Procedure/workload | Acceptance |
|---|---|---|
| Policy truth | Request Hardware, Software, and Renderless with a hardware device, CPU driver, no driver, and wrong package. | Exact mode or explicit failure. Renderless starts without a graphics dependency. |
| GL requirements | Probe the real GL context, required shader variants, storage buffers, texture/FBO formats, blend, clip, and indexed draw operations. | Required operations pass; no version spoofing; optional exclusions are named. |
| UI correctness | FPS overlay and each common control; nested clips, translucent overlap, text, resize, DPI, focus. | Native and ImGui fixtures render and respond correctly; no missing glyphs, stale hit targets, or wrong painter order. |
| Basic scene | Fixed camera, deterministic small static scene, one bounded animation, qualified materials and lights. | Nonempty expected output; defined per-image tolerance and error report; no silent dropped operations. |
| Interactive budget | UI at 1280x720 and scene at 640x360; idle, typing, scrolling, dragging, and camera motion. | Initial release target: p95 total frame <=33.3 ms, p95 input-to-present <=100 ms on a declared minimum CPU. If missed, lower the advertised tier or profile with owner review. |
| CPU and memory | Separate cold compile/load from warm runs; collect main/render/worker and driver process CPU, allocations, peak/steady memory, uploads, and queue depth. | Bounded queues; no unexplained sustained memory growth; no claim that main-thread savings imply total CPU savings. |
| Window lifecycle | Repeated startup/exit, resize, minimize, focus changes, DPI, ImGui platform windows, close with work pending. | No resource-use-after-retirement or validation errors, including teardown. |
| Display-free | Run with no display service using the explicit offscreen adapter, then read back. | Valid completed image, finite timeout, correct cancellation; a hidden-window pass does not satisfy this row. |
| Package/cache | Clean install, missing/wrong-arch library, cache miss/hit, changed compiler/driver, corrupt cache. | Reproducible launch or actionable failure; no shared-cache mutation or untrusted library load. |
| Regression | Run equivalent qualified workloads on normal hardware and Renderless server. | Existing renderer defaults and server requirements remain unchanged. |

Use a fixed fixture revision, dimensions, effect settings, CPU/core count, RAM,
OS, power mode, package hashes, and worker limits. For warm measurements, start
with 10 seconds warm-up and 60 seconds collection over at least five fresh
processes. Record p50/p95/p99 frame and input latency plus CPU time per frame.
Report cold startup separately. Compare identical scenes/settings first; label
reduced-profile comparisons clearly. Include concurrent simulation and native UI
layout load to expose worker oversubscription. Publish limits rather than claim
all CPUs or scenes meet one frame rate.
