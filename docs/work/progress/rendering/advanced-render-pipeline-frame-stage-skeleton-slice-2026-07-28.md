# Advanced Render Pipeline Frame-Stage Skeleton Slice

Status: Complete
Date: 2026-07-28

## Outcome

The advanced desktop pipeline no longer constructs or declares the copied
deferred, ordinary opaque Forward+, and full-frame light-combine graph. It now
exposes one backend-neutral ordered stage contract shared by OpenGL and Vulkan.

The stage skeleton is intentionally unavailable for production execution.
OpenGL and Vulkan advertise `EAdvancedShaderFamily.None`; only the future
complete `VisibilityBuffer` family satisfies capability selection. Consequently:

- `Available` retains `DefaultRenderPipeline`;
- `Required` rejects pipeline creation with `MissingShaderFamily`;
- `Diagnostic` reports the missing family while retaining the default pipeline;
- explicit synthetic capability snapshots remain available to contract tests.

OpenXR ownership is unchanged by this slice: RVC remains the eye renderer, while
the standard selector owns desktop and offscreen requests.

## Ordered Frame Contract

| Order | Stage | Primary domain |
| --- | --- | --- |
| 1 | Frame begin | Transfer |
| 2 | Deformation | Compute |
| 3 | Visibility preparation | Compute |
| 4 | Visibility raster | Graphics |
| 5 | Depth pyramid and late visibility | Compute |
| 6 | Work classification | Compute |
| 7 | Attribute reconstruction | Compute |
| 8 | Native opaque shading | Compute |
| 9 | Late passes | Graphics |
| 10 | Temporal and post-processing | Graphics |
| 11 | Output | Graphics |
| 12 | User interface | Graphics |

Every stage is represented by a stable `VPRC_AdvancedRenderStage` command and
is surrounded by a named annotation and GPU timer scope. Render-pass metadata
uses the same stage identities, domains, and linear dependencies.

## Disconnected Migration Graph

`AdvancedRenderPipeline.CommandChain.cs` no longer contains or calls:

- deferred G-buffer rendering or MSAA G-buffer resolve;
- the forward depth/normal pre-pass and G-buffer restore;
- deferred light accumulation or light combine;
- ordinary opaque or masked Forward+ rendering;
- the copied transparency, temporal, output, or UI execution chain.

The pipeline also no longer registers the default-render-pass sorter map or
warms deferred/Forward+ shaders during construction. Legacy post, transparency,
and feature helpers remain dormant migration material until their corresponding
late stages are deliberately reconnected in later documents.

## Resource Layout Boundary

`AdvancedRenderPipeline.Resources.cs` now has a fixed zero feature mask and
declares no pipeline-owned resources. This is true for mono, stereo, every
anti-aliasing mode, and arbitrary inactive feature bits.

When a caller supplies an external target, the layout declares exactly one
`$ExternalOutput` resource with explicit ownership and synchronization:

- window output: window-owned, frame-boundary synchronization;
- caller framebuffer: caller-owned, caller-provided synchronization;
- external swapchain: XR-runtime-owned, acquire/release synchronization.

GPU-scene, frame-slot, visibility, history, and late-pass resources are not
speculatively declared. Their ownership and generation rules remain work for
the next contract slice.

## Test Contract Changes

Tests that previously required `AdvancedRenderPipeline` to mirror default
deferred, Forward+, capture, post, and upscale wiring were narrowed to
`DefaultRenderPipeline`. New behavior tests instead assert:

- exact ordered stage commands, annotations, and timers;
- no deferred G-buffer, ordinary opaque-forward, Forward+ culling,
  light-combine, or MSAA G-buffer-resolve commands;
- matching pass metadata domains and dependencies;
- no pipeline-owned resources for inactive profiles;
- exact external-output ownership;
- OpenGL and Vulkan do not advertise an incomplete visibility shader family.

## Validation

- Runtime rendering build: passed with zero compiler errors.
- Unit-test/editor build: passed with zero compiler errors.
- Consolidated affected rendering contracts: 445 passed, 0 failed.
- The only reported warning is the existing `Magick.NET` NuGet advisory.

Live visual and performance validation was not run because production advanced
stage execution is intentionally capability-gated in this slice.

## Next Slice

Define the immutable resource and state contract: pipeline-owned versus
frame-slot versus history/imported ownership, current/previous slot reuse,
OpenGL/Vulkan synchronization boundaries, and the topology/resource generation
keys that invalidate recorded command packets. Then begin the GPU scene and
material data contract in document 02.
