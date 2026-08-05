# Archived Rendering Investigations

These files preserve completed, superseded, or transferred investigation
evidence. They do not own current execution work. Status, “remaining work,” and
“next step” language inside an archived file is a historical snapshot unless a
current owner below explicitly adopts it.

| Historical family | Current owner |
| --- | --- |
| Directional cascades, atlas flicker, mesh displacement, cropped output | [Directional Light Vulkan Stability](../directional-light-inspector-shadow-2026-08-03.md) |
| Vulkan workstreams 03-05, command recording, and zero-readback acceptance | [03-05 Validation](../../../testing/rendering/03-05-optimization-validation-todo.md) and [Zero-Readback Production Scheduling](../../../progress/rendering/vulkan-zero-readback-production-scheduling-2026-08-03.md) |
| OpenXR/Monado rendering and performance | [OpenXR Monado Vulkan 120 Hz Progress](../../../progress/rendering/openxr-monado-vulkan-120hz-performance-2026-06-27.md) |
| Vulkan core-hardening and lifecycle gates | [Vulkan Core Hardening And Device-Loss TODO](../../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md) |
| GPU BVH external qualification | [GPU Scene BVH External-Hardware Qualification](../../../testing/rendering/gpu-scene-bvh-external-hardware-qualification.md) |
| Render-query live and external validation | [Vulkan Render Query System Upgrade TODO](../../../todo/rendering/vulkan-render-query-system-upgrade-todo.md) |
| Advanced pipeline reference and promotion work | Current advanced-render-pipeline progress and testing ledgers under `docs/work/progress/rendering/` and `docs/work/testing/rendering/` |

## Editor, windowing, and presentation

- [Continuous Window Resize Frame Lifecycle](continuous-window-resize-frame-lifecycle-2026-07-23.md)
- [Editor Origin / Eye Camera Flicker](editor-origin-eye-camera-flicker-2026-06-28.md)
- [Editor Resize Black Frame](editor-resize-black-frame-2026-07-17.md)
- [Editor UI Overexposure And Physics Debug Black Output](editor-ui-overexposure-physics-debug-black-2026-07-23.md)
- [ImGui Detached Platform Viewports](imgui-detached-platform-viewports-2026-08-04.md)
- [Vulkan Camera-Motion Black Frames](vulkan-camera-motion-black-2026-07-10.md)
- [Vulkan Command-Buffer Retirement Crash](vulkan-command-buffer-retirement-crash.md)
- [Vulkan Editor Scroll And Depth-Hit Inconsistency](vulkan-editor-scroll-depth-inconsistency-2026-07-22.md)
- [Vulkan Mesh Jitter And Command-Buffer Retirement Failure](vulkan-mesh-jitter-command-buffer-retirement-2026-07-21.md)
- [Vulkan Startup And Render-Graph Black Frame](vulkan-startup-and-render-graph-black-frame-2026-07-21.md)

## OpenXR and stereo rendering

- [OpenXR Vulkan Eye Preview Stale/Partial Render](openxr-eye-preview-stale-2026-07-08.md)
- [OpenXR Monado And Desktop Framerate](openxr-monado-desktop-framerate-invalidoperations-2026-07-15.md)
- [OpenXR Monado VR Framerate](openxr-monado-framerate-2026-07-06.md)
- [OpenXR Monado OpenGL Rendering](openxr-monado-opengl-rendering-2026-06-25.md)
- [OpenXR Monado Vulkan Parallel Rendering](openxr-monado-vulkan-parallel-rendering-2026-06-25.md)
- [OpenXR Monado Vulkan Rendering](openxr-monado-vulkan-rendering-2026-06-24.md)
- [OpenXR SteamVR Eye Culling And Directional Cascades](openxr-steamvr-culling-cascades-2026-07-07.md)
- [OpenXR Vulkan Forward+ OOM](openxr-vulkan-forward-plus-oom-2026-06-30.md)
- [OpenXR Vulkan Single-Pass Stereo](openxr-vulkan-single-pass-stereo-2026-06-30.md)
- [Vulkan CPU-Query And Monado Regressions](vulkan-cpu-query-monado-regressions-2026-07-14.md)

## Lighting, materials, and render-pipeline correctness

- [Default Reference Baseline Capture](default-reference-baseline-capture-2026-07-29.md)
- [OpenGL Uniform Component Count](opengl-uniform-component-count-2026-07-23.md)
- [Render Pipeline Resource Lifecycle Recovery](render-pipeline-resource-lifecycle-recovery-2026-07-13.md)
- [Shadow Atlas Framerate Regression](shadow-atlas-framerate-regression-2026-07-02.md)
- [VR Pickup UI Preview Black Output](vr-pickup-ui-preview-black-2026-07-09.md)
- [Vulkan Deferred Light Probes](vulkan-deferred-light-probes-2026-07-08.md)
- [Vulkan Descriptor Layout/Lifetime Mismatch](vulkan-descriptor-layout-mismatch-2026-07-27.md)
- [Vulkan Descriptor Lifetime Freeze](vulkan-descriptor-lifetime-freeze-2026-07-10.md)
- [Vulkan DLSS Visual Regressions](vulkan-dlss-visual-regressions.md)
- [Vulkan Dynamic Rendering Promotion](vulkan-dynamic-rendering-promotion-2026-07-10.md)
- [Vulkan Material Readiness And Magenta Bloom](vulkan-material-readiness-and-magenta-bloom-2026-07-30.md)
- [Vulkan Uber Pipeline Stall And Black Recovery](vulkan-uber-pipeline-stall-black-recovery-2026-07-27.md)

## Performance, scheduling, and core hardening

- [Current JSONC Framerate](current-jsonc-framerate-2026-07-26.md)
- [Vulkan Camera-Motion Framerate Regression](vulkan-camera-motion-framerate-regression-2026-07-21.md)
- [Vulkan Core Hardening Phase 4 Live Validation](vulkan-core-hardening-phase4-live-validation-2026-07-09.md)
- [Vulkan Core Hardening Phase 5 Live Validation](vulkan-core-hardening-phase5-live-validation-2026-07-09.md)
- [Vulkan CPU Framerate Regression](vulkan-cpu-framerate-regression-2026-07-09.md)
- [Vulkan Editor Steady-Frame CPU Cost](vulkan-editor-frame-time-spikes-2026-07-30.md)
- [Vulkan Framerate And VR Pickup Preview](vulkan-framerate-preview-2026-07-08.md)
- [Vulkan Framerate Root Cause](vulkan-framerate-root-cause-2026-07-28.md)
- [Vulkan Optimization 03-05 Validation](vulkan-optimization-03-05-validation-2026-08-02.md)
- [Vulkan P0.2 Timing And Controlled Baseline](vulkan-p02-controlled-baseline-2026-07-16.md)
- [Vulkan Phase 5.2.5 Live Acceptance](vulkan-phase525-live-acceptance-2026-07-20.md)
- [Vulkan Physics Resize And Query Regressions](vulkan-physics-resize-query-regressions-2026-07-24.md)
- [Vulkan Pipeline Cache And Prewarm](vulkan-pipeline-cache-prewarm-2026-07-16.md)
- [Vulkan Stable Packets And Descriptor Publication](vulkan-stable-packets-and-descriptor-publication-2026-07-16.md)
- [Vulkan Zero-Readback Sponza Device Loss](vulkan-zero-readback-sponza-device-loss-2026-07-17.md)

## Visibility, queries, BVH, math, and physics

- [CPU Async-Query Occlusion During Camera Motion](cpu-query-camera-motion-2026-07-20.md)
- [Desktop Visibility Generation Handoff](desktop-visibility-generation-handoff-2026-07-16.md)
- [GPU BVH Math Preview Missing Nodes](gpu-bvh-math-preview-missing-nodes-2026-07-21.md)
- [GPU Scene BVH Rollout Validation](gpu-bvh-rollout-validation.md)
- [Math Intersections Occlusion Qualification](math-intersections-occlusion-qualification-2026-08-03.md)
- [Math Intersections Render FPS And Debug Batching](math-intersections-render-fps-debug-batching-2026-07-22.md)
- [Physics-Chain Skinned-Mesh Motion Failure](physics-chain-skinned-mesh-motion-2026-07-22.md)
- [Vulkan Render Query System Upgrade](vulkan-render-query-system-upgrade-2026-07-22.md)
