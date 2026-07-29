# Advanced Render Pipeline Output-Purpose And Feature-Contract Slice - 2026-07-28

Status: Complete
Parent TODO:
[01 - Pipeline Identity And Frame Contract](../../todo/rendering/architectural-refactor/01-pipeline-identity-and-frame-contract-todo.md)

## Scope

Separate pipeline selection by output ownership before replacing the copied
frame graph. Desktop, OpenXR eye, and offscreen-capture requests must no longer
be inferred from a mono/stereo Boolean or from the concrete type of another
viewport's pipeline.

This slice also defines the focused feature providers needed for pipelines
with different opaque/output architectures to retain equivalent GI, temporal,
froxel, post-processing, probe, and reusable late-pass behavior.

## Completed

- Added `ERenderPipelinePurpose` and `RenderPipelineRequest`.
- Made `RuntimeEngine.Rendering` and the application factory accept explicit
  desktop-scene, OpenXR-eye, and offscreen-capture requests.
- Kept `NewRenderPipeline(bool)` as a desktop-scene convenience entry point;
  it no longer allows an XR setting to replace desktop rendering with RVC.
- Made every OpenXR eye request create an `RvcRenderPipeline`. In
  `RvcPipelineMode.Off`, the RVC pipeline shell remains the eye owner while its
  additional cache passes stay disabled.
- Removed OpenXR's source-type cloning and reflection fallback. An advanced
  desktop source now supplies shared feature configuration to an independently
  owned RVC eye pipeline.
- Tagged OpenXR and capture viewports with stable requests so later global
  preference changes preserve their purpose.
- Routed scene capture, mirrors, light probes, and impostor capture through the
  offscreen-capture request.
- Added focused GI, PBR/probe-resource, reusable pass-material, and shared scene
  feature providers.
- Replaced the in-scope GI/light command and editor-inspector concrete pipeline
  switches with those providers.
- Added a feature synchronizer for pipeline-level GI/prepass choices and
  schema-keyed camera post-process state.
- Added behavior tests for desktop/capture/OpenXR routing, OpenXR non-cloning,
  viewport purpose preservation, and temporal/froxel/post-process schema
  compatibility.

## Output Ownership

| Purpose | Pipeline owner | View topology | Selection behavior |
| --- | --- | --- | --- |
| Desktop scene | `DefaultRenderPipeline` or `AdvancedRenderPipeline` | Normally mono | Uses the advanced capability policy; editor debug-opaque is desktop-only. |
| OpenXR eye | `RvcRenderPipeline` | Per-eye mono or layered stereo | Always uses the RVC shell. `Off` disables RVC additions without changing eye ownership. |
| Offscreen capture | `DefaultRenderPipeline` or `AdvancedRenderPipeline` | Consumer-defined | Uses the advanced capability policy and ignores desktop debug/RVC preferences. |

Pipeline instances and output-local temporal resources are never shared across
these purposes. Scene data and compatible visual-feature settings may be
shared or synchronized explicitly.

## Migrated-Feature Disposition

This inventory classifies the renamed migration substrate before its old frame
graph is disconnected.

| Surface | Disposition | Required contract |
| --- | --- | --- |
| Visibility/depth identity | Advanced requirement | Replaced by the visibility payload, depth, reconstruction, and classification stages in documents 04-06. |
| GPU scene, geometry, material, light, and texture tables | Advanced requirement | Stable GPU-addressable records shared by desktop visibility shading and RVC eye rendering. |
| Skinning, blendshapes, current/previous deformation | Advanced requirement | Produced once per frame slot and consumed by visibility, shadow, and velocity work. |
| GI mode selection and GI pass eligibility | Shared feature requirement | Exposed through `IGlobalIlluminationPipelineProvider`; the opaque integration point changes in document 07. |
| Probe arrays and PBR light bindings | Shared feature requirement | Exposed through `IPbrLightingResourceProvider`; consumers do not switch on concrete pipeline types. |
| Temporal AA/TSR, motion blur, exposure, bloom, tone mapping, color grading | Explicit late/post requirement | Retained through compatible post-process schemas and output-local histories after native opaque HDR. |
| Fog, atmospheric scattering, volumetric/froxel fog | Explicit late/post requirement | Retained after opaque shading with per-view resources and compatible schema keys. |
| Transparency, refraction, particles, overlays, gizmos, UI | Explicit late/special requirement | Reconnected after the visibility-shaded opaque output; not part of opaque fallback. |
| Motion-vector, depth-normal, and diagnostic override materials | Reusable pass requirement | Supplied through focused material/settings providers until their final advanced consumers are established. |
| Visibility payload/resource inspection, overdraw, reconstructed-attribute targets | Diagnostics only | Created only by explicit debug/capture profiles and never required by the production frame. |
| Classic full GBuffer population | Obsolete advanced implementation detail | Removed when the named advanced-stage skeleton and visibility resources land. |
| Deferred light accumulation and full-frame light combine | Obsolete advanced implementation detail | Replaced by native visibility-buffer material/lighting shading. |
| Ordinary opaque Forward+ color rendering | Obsolete advanced implementation detail | Compatible opaque/masked content uses visibility shading; only legitimate late/special forward work remains. |
| Source-pipeline type cloning for XR | Obsolete integration detail | Replaced by explicit output-purpose selection plus shared feature synchronization. |

## Deliberately Not Completed

- The advanced migration graph still contains its old deferred/forward
  commands and resources.
- No live visual reference captures or performance baselines were produced.
- No visibility payload, native opaque shading, layered advanced resources, or
  RVC/advanced shared GPU-scene implementation was added.
- OpenVR remains on its existing renderer path; this slice changes OpenXR eye
  ownership only.

## Validation

- `dotnet build XREngine.Runtime.Rendering/XREngine.Runtime.Rendering.csproj --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false`
  - Passed with 0 compiler errors.
- `dotnet build XRENGINE/XREngine.csproj --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false`
  - Passed with 0 compiler errors.
- `dotnet build XREngine.UnitTests/XREngine.UnitTests.csproj --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false`
  - Passed with 0 compiler errors.
- Purpose, capability, identity, light-probe, and RVC contracts:
  39 passed.
- OpenXR timing, stereo post-process, VR view-mode, and independent-eye
  contracts: 130 passed.

The builds reported only the pre-existing `Magick.NET` NuGet security advisory.

## Worktree Exclusions

The following pre-existing workspace state was not modified as part of this
slice:

- `Build/Submodules/OscCore-NET9`
- `Build/Dependencies/vcpkg/`
- `Build/Submodules/Flyleaf/`
- `Build/Submodules/MagicPhysX/`

## Next Slice

Replace the copied advanced frame graph with the named stage skeleton, keep
incomplete production stages unavailable through capability selection, and add
command-tree/resource-layout tests before implementing visibility resources.
