# Testing Work Docs

Validation docs hold manual, runtime, visual, hardware, profiler, benchmark, and soak checks. They also hold acceptance matrices and reproduction steps. Code work goes in `../todo/`. Debug history goes in `../investigations/`. Use the validation doc template in [Work Doc Templates](../../developer-guides/ai/work-doc-templates.md#validation-doc).

[← Work docs index](../README.md)

## Validation Docs

### Rendering

- [Default And Advanced Pipeline Validation](rendering/default-and-advanced-pipeline-validation.md)
- [Advanced World Ambient Validation](rendering/advanced-world-ambient.md)
- [Ambient Occlusion Validation](rendering/ambient-occlusion.md)
- [Global Illumination Validation](rendering/global-illumination-validation.md)
- [Shadow Validation](rendering/shadow-validation.md)
- [Poiyomi Toon 9.3 Parity Validation](rendering/poiyomi-parity-validation.md)
- [GPU-Driven Submission Validation](rendering/gpu-driven-submission-validation.md)
  - [Optimization 01-08 Acceptance Closeout Validation](rendering/01-08-optimization-acceptance-closeout.md)
  - [GPU Scene BVH External-Hardware Qualification](rendering/gpu-scene-bvh-external-hardware-qualification.md)
  - [Math Intersections BVH Validation](rendering/math-intersections-bvh-tests.md)
  - [XRDataBuffer RHI Write Model Validation](rendering/xrdatabuffer-rhi-write-model-validation.md)
- [Render Queries And Occlusion Validation](rendering/render-queries-and-occlusion-validation.md)
  - [Math Intersections Occlusion Validation](rendering/math-intersections-occlusion-tests.md)
- [GPU Deformation Validation](rendering/gpu-deformation-validation.md)
- [Retinal Visibility Cache Validation](rendering/retinal-visibility-cache-validation.md)
- [Vulkan Core Validation](rendering/vulkan-core-validation.md)
- [Vulkan Backend Parity Validation](rendering/vulkan-backend-parity-validation.md)
- [Window And Render Thread Validation](rendering/window-and-render-thread-validation.md)

### XR

- [OpenXR Validation](xr/openxr-validation.md)
- [OpenXR SteamVR Hardware Validation](xr/openxr-steamvr-hardware-validation.md)
- [OpenVR VRClient GPU Handoff Validation](xr/openvr-vrclient-gpu-handoff-validation.md)
- [MonkeyBall VR Release Matrix](xr/monkeyball-vr-release-matrix.md)

### Other Subsystems

- [Local Agent Broker Validation](ai/local-agent-broker.md)
- [Animation Validation](animation/animation-validation.md)
- [Asset Import Validation](assets/asset-import-validation.md)
  - [glTF Import Validation](assets/gltf-import.md)
- [Avatar Validation](avatar/avatar-validation.md)
- [Modeling Validation](modeling/modeling-validation.md)
- [Native UI Validation](ui/native-ui-validation.md)
- [Networking Validation](networking/networking-validation.md)
- [Physics Validation](physics/physics-validation.md)
  - [Physics-Chain Correctness And Benchmark Contract](physics/physics-chain-correctness-contract.md)
- [Platform Validation](platform/platform-validation.md)
- [Runtime And AOT Validation](runtime/runtime-and-aot-validation.md)
  - [Memory-Control Investigation Template](runtime/memory-control-investigation-template.md)
- [Texture Validation](texturing/texture-validation.md)
  - [Texture Management Runtime Baseline - 2026-05-01](texturing/texture-management-runtime-baseline-2026-05-01.md)
