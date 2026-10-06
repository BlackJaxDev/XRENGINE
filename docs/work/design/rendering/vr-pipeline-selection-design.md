# Exact VR Pipeline Selection

Status: **Implemented and validated on Monado for OpenXR (Vulkan).** OpenVR and
test updates are follow-ups (see [Limits](#limits)).

## Requirement

The user selects one VR view mode and one pipeline family. Only that
combination can render. When the combination cannot run, the engine submits
no XR projection layer and writes a named diagnostic. The engine never
substitutes another view mode, pipeline type or command chain.

| View mode | Default | Advanced | RVC |
|---|---|---|---|
| `SequentialViews` (two-pass) | Supported | Supported | Rejected: not offered |
| `SinglePassStereo` | Supported | Rejected: not implemented yet | Supported |
| `ParallelCommandBufferRecording` (Vulkan only) | Supported | Supported | Supported |

- **Default** is a plain `DefaultRenderPipeline`. It is not an
  `RvcRenderPipeline` with its passes off.
- **Advanced** is an `AdvancedRenderPipeline` that uses the OpenXR eye stage
  profile. It owns its command chain, resources and output reservation. It is
  not hosted inside another pipeline.
- **RVC** is an `RvcRenderPipeline`. It runs only the RVC command chain.

## Earlier Behavior

- Every OpenXR eye was an `RvcRenderPipeline`, for every selection.
- A stereo eye always ran the Default chain (`selected &= !Stereo`).
- A two-pass mono eye ran the Advanced family only when its output binding was
  bound and admitted. Otherwise it ran the Default chain.
- The global `AdvancedRenderPipelineMode` (`Available`) controlled the eyes, and
  it fell back to Default after a rejection.
- An unsupported RVC mode fell back to the Forward+ oracle chain.

## Settings

- New enum `EVrRenderPipeline { Default, Advanced, Rvc }`.
- New engine setting `VrRenderPipeline` (category `VR`, default `Default`).
  Default is the only family that runs in every view mode.
- `AdvancedRenderPipelineMode` applies to desktop and offscreen outputs only.
  It has no effect on VR eyes.
- `RvcPipelineMode` selects the RVC mode when `VrRenderPipeline` is `Rvc`.
  `Off`, and any mode that the RVC resolver cannot run, reject the XR frame.
- Unit-testing world: new `VR.RenderPipeline` value and the environment variable
  `XRE_UNIT_TEST_VR_RENDER_PIPELINE`. Both copy into `VrRenderPipeline`.
- The editor shows the setting through the generic engine-settings inspector.
  No custom panel is necessary.

## Resolution

`VrViewRenderModeResolver.Resolve` takes the requested pipeline family. It
rejects the unsupported cells in the table before it examines the backend. The
resolution record carries the family. OpenXR and the Vulkan OpenXR binding
already submit no projection layer when the resolution is unsupported, so the
new rejections use the same path and the same smoke failure record.

## Pipeline Creation

- One factory, `OpenXrEyeRenderPipelineFactory`, creates the eye pipeline for a
  family and stereo flag. It throws when the family has no pipeline for that
  topology (Advanced stereo). The resolver prevents this request in normal
  flow, and the binding refresh does not replace a pipeline with one that the
  factory cannot create.
- The OpenXR slots recreate an eye pipeline when its type, stereo flag or RVC
  mode does not match the current selection.
- `RvcRenderPipeline` no longer hosts the Advanced stage family.
- An Advanced eye binds its output as `Required`. A failed binding does not
  throw from the binding refresh. It records a `Rejected` state and reason.

## Frame Gate

Before the render thread renders the eyes, OpenXR validates each active eye
pipeline:

1. The pipeline type and stereo flag match the selection.
2. For RVC, the resolved RVC mode equals the requested mode, with no fallback
   reason.
3. For Advanced, the output binding is `Bound` after at most one refresh.

A failure submits the frame with no projection layer. It also writes a
warning (`[OpenXR] VR pipeline selection rejected ...`) and a smoke failure
record. A pending Advanced reservation also skips the frame. It does not run
another pipeline.

## Limits

- **Advanced in `SinglePassStereo`** is rejected. The OpenXR layered eye
  resources and the swapchain terminal write for the Advanced family are not
  admitted yet.
- **OpenVR** still creates eye pipelines from the desktop policy, and maps
  `ParallelCommandBufferRecording` to two-pass. It does not read
  `VrRenderPipeline` yet. OpenVR is the tested hardware path, and the engine
  default view mode is `ParallelCommandBufferRecording`, so a strict OpenVR
  change needs its own decision.
- `EAdvancedStereoMode.RvcTwoPass` keeps its name until the unit-test updates
  have clearance. It now means an Advanced OpenXR two-pass eye.

## Validation

The editor and the unit-test project build with zero warnings. On 2026-10-06, a
120-frame Vulkan smoke ran on Monado for each case:

| Case | Eye pipeline created | Submitted / no-layer frames | Result |
|---|---|---|---|
| SequentialViews × Default | `DefaultRenderPipeline` ×2 | 108 / 12 | Pass |
| SequentialViews × Advanced | `AdvancedRenderPipeline` (eye profile) ×2 | 16 / 104 | Pass |
| SequentialViews × Rvc | None | 0 / 120 | Rejected: not offered |
| SinglePassStereo × Default | `DefaultRenderPipeline` (stereo) | 101 / 19 | Pass |
| SinglePassStereo × Advanced | None | 0 / 120 | Rejected: not implemented |
| SinglePassStereo × Rvc (ForwardPlusOracle) | `RvcRenderPipeline` (stereo) | 105 / 15 | Pass |
| SinglePassStereo × Rvc (Off) | `RvcRenderPipeline` (stereo, Off) | 0 / 120 | Rejected: `DisabledBySettings` |
| ParallelCommandBufferRecording × Default | `DefaultRenderPipeline` ×2 | 108 / 12 | Pass |
| ParallelCommandBufferRecording × Advanced | `AdvancedRenderPipeline` (eye profile) ×2 | 22 / 98 | Pass |
| ParallelCommandBufferRecording × Rvc (ForwardPlusOracle) | `RvcRenderPipeline` ×2 | 108 / 12 | Pass |

- The Advanced cases skip frames while the Advanced shaders compile. The log
  reports `VR pipeline output pending ... state=PendingResources`. Earlier, the
  eye ran the Default chain during this time. Now the eye renders nothing until
  its output binds. No Advanced stage rejection occurred after the binding.
- In an isolated session, both Advanced eye previews show the scene. The same
  object is at the same position as in the Default family. Both families are
  almost white in per-eye modes, because `VPRC_ExposureUpdate` skips headset
  auto exposure for per-eye swapchain targets. This exposure limit does not
  depend on the pipeline family; it is part of the exposure-settling issue in the
  [separate findings](../../todo/rendering/vulkan-stall-separate-findings-todo.md).
- Focused unit tests: 3 failures come from this change. They are the
  factory-contract tests that require an `RvcRenderPipeline` for every OpenXR
  eye. The other 28 failures in the same group assert source text that is not in
  `HEAD`, so they failed before this change. Test updates need explicit
  clearance.
