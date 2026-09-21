# DDGI working-copy verification

## Completion pass in progress

The requested scope now includes OpenGL and Vulkan in Default and Advanced,
material textures and independent RGB emission, thin-surface transmission,
environment lighting, lifecycle acceptance and the previously listed contract
refresh. The earlier runtime evidence below predates these changes.

- Added a separate 96-byte GPU triangle attribute stream carrying smooth normals
  and UV0/UV1. Packed BVH triangles retain their 64-byte ABI and carry the original
  primitive index through sorting.
- Added semantic texture bindings and imported emissive RGB/strength. glTF
  emission follows the [glTF material specification](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#reference-material):
  the emissive map modulates the independent factor, with black as the default.
- Added a scene-owned GPU material texture array, alpha-aware trace acceptance,
  textured shading normals and emission, and bounded thin-sheet transmission
  with colored shadow attenuation. Refraction and caustics are outside this
  diffuse transport approximation.
- Integrated Advanced resources and commands. Vulkan descriptor classes occupy
  distinct sets while retaining OpenGL's resource-class binding numbers.
- Added interruption invalidation and backend submission receipts before
  advancing update cursors. OpenGL receipt wrappers are pooled after warmup.
- All 18 expanded DDGI/geometry/composite shaders compile with the GLSL
  validator. The first integrated editor build found a duplicate environment
  helper signature, being corrected before live acceptance.
- Prepared a glTF runtime scene containing an emitter with black diffuse color,
  an independent blue emissive texture/factor, normal map, UV1, an alpha-cutout
  panel and a colored transmission panel. It will be reused across the four
  backend/pipeline combinations. No new runtime acceptance is claimed yet.

The Default deferred G-buffer's scalar-only emission is also being extended so
the visible emitted RGB agrees with the radiance transported by DDGI.

## Scope and status

**Current status: the blocking OpenGL DDGI defects have corrective implementation
and bounded runtime evidence; DDGI remains experimental.** The editor build is
clean and all 17 expanded shaders compile. Live validation covers dynamic
lighting, cascades, resource sizing, baked recovery, scrolling, activation, and
normal/reverse-depth composition. The existing targeted suite now has 46 passes
and 8 obsolete contract/fixture failures, documented below; test edits await
explicit clearance. Vulkan and stereo remain unvalidated, and Advanced DDGI
remains unsupported.

The following initial-review sections preserve the original evidence: all 54
old scaffolding contracts passed despite blocking shader and runtime defects.
The later corrective sections supersede those findings where stated.

The review covers GPU shaders, probe state and atlas lifetime, scheduling,
Default pipeline integration, cascades, baked data and stereo source contracts.
Advanced currently rejects non-probe/IBL GI through its production contract and
does not declare a DDGI command chain; its `UsesDDGI` property is scaffolding.
Vulkan, stereo, baked mode and physically correct multi-bounce output were not
validated live during the initial review. Later OpenGL baked validation is
recorded below; broader transport and platform acceptance remain open.

## Validation results

| Check | Result |
|---|---|
| Isolated Debug editor build | Passed, 0 warnings and 0 errors. |
| Existing `DDGIScaffoldingContractTests` | 54 passed, 0 failed, 0 skipped. |
| Expanded GLSL syntax checks | 11 of 12 shaders passed. `ddgi_trace.comp` failed with `Ray: redefinition struct`. |
| DDGI bootstrap scene | Exited during initialization with duplicate `SceneNode` key in `SceneReplicatedEntityWorld.IndexPackageNode`. |
| Same scene with DDGI bootstrap disabled | Started successfully and remained available through MCP. |
| DDGI added to existing root through MCP | Pipeline and atlas updates ran, but no DDGI trace program was created on `CpuDirect`. |
| Intensity set to zero and read back | Runtime value was 0; captured DDGI RGB still reached 0.14550781. |
| Screenshots and atlas captures | Viewed captures from two camera positions and inspected irradiance/visibility outputs. |
| Session cleanup | Named session stopped; no user editor was stopped. |

The shader syntax check recursively expanded `#pragma snippet` references before
running `glslangValidator -S comp` / `-S frag`. It is a syntax check, not proof of
runtime binding or numerical correctness. The runtime snippet resolver retains
the shared `Ray` declaration, so the duplicate is not a preprocessing artifact.

The existing tests cover layouts, CPU helpers and source-string contracts. Their
passing result does not establish that the GPU trace shader compiles or that a
complete probe update reaches the scene BVH.

Scratch evidence is under
`Build/_AgentValidation/20260920-183506-ddgi-verification/`.
The named editor session is `ddgi-verification-0920`.

## Reproduction and runtime evidence

1. Built and started `ddgi-verification-0920` with ImGui, OpenGL,
   `DefaultRenderPipeline`, `CpuDirect`, one physics sphere and floor, no model
   imports, and a task-local settings file. Requested DDGI volume counts were
   `8 x 4 x 8`, with 64 rays per probe. Both `InitializeDDGIVolume=true` and
   `GlobalIlluminationMode=DDGI` were set.
2. The process briefly answered MCP readiness, then exited while indexing scene
   nodes. The stack ended in `SceneReplicatedEntityWorld.IndexPackageNode` with
   an already-added `SceneNode` key.
3. Restarted the same named session with its existing binaries. Changed only the
   DDGI bootstrap selection to disabled. All other scene settings stayed the
   same. This control started and rendered successfully.
4. Added `DDGIVolumeComponent` to the existing root through MCP and explicitly
   set the live pipeline's GI mode to DDGI. Setting `ProbeCounts` through MCP was
   rejected by the generic serializer because `IVector3.Data` is a pointer;
   therefore this live phase retained the default `16 x 8 x 16` grid. Setting
   `RaysPerProbe=64` succeeded and was read back.
5. Read back `UsesDDGI`/GI mode and the component state. `FrameIndex` advanced
   past 900 and `IsInvalidated` became false. Shader logs showed ray generation,
   hit shading, relocation, atlas updates, border copies and screen sampling,
   but no `ddgi_trace.comp` creation. Source confirms the BVH readiness gate
   exits only the trace pass on the active `CpuDirect` path.
6. The first DDGI texture capture had RGB min/max 0/0.14550781 and average
   0.07509233. The irradiance atlas contained a repeating tile pattern with RGB
   range 0.125 to 0.2890625. Nonzero output was therefore not evidence of a
   successful scene-geometry trace.
7. Moved the camera and inspected a second viewport capture. Set `Intensity=0`,
   read back `RuntimeState.Intensity=0`, then captured `DDGITexture`: RGB max was
   still 0.14550781 and average was 0.0634904. This independently confirms the
   zero-intensity shader bug.
8. Stopped the named session, copied the relevant logs into the task run root,
   then completed the existing contract-test run using the isolated artifacts.

## Blocking findings

### P1: DDGI bootstrap inserts the same node twice

Location: `XREngine.Runtime.Bootstrap/Builders/BootstrapLightingBuilder.cs:67`.

The new helper constructs a detached node and calls `rootNode.AddChild`.
That API adds to the transform child collection, whose added callback assigns
`Parent`; the parent-change handler adds the child again. The resulting duplicate
node makes scene replication indexing throw during editor initialization. This
was reproduced live and isolated by the control run.

Suggested correction: construct the node with its parent, as nearby light
builders do, or use the parent-assignment API that owns hierarchy insertion.

### P1: The trace shader cannot compile

Location: `Build/CommonAssets/Shaders/Compute/DDGI/ddgi_trace.comp:22-28`.

The local `Ray` declaration duplicates the unconditional declaration in
`Build/CommonAssets/Shaders/Snippets/BvhRaycastCore.glsl:38-44`.
`XR_CUSTOM_RAY_INPUT` guards `RayInput`, the ray buffer and decoder, but not
`Ray`. Expanded GLSL compilation fails. Any configuration reaching this trace
pass cannot execute the requested shader.

Suggested correction: have one shared `Ray` definition and place the custom
decoder after that definition, or explicitly guard the shared type.

### P1: DDGI has no usable BVH on CpuDirect and commits failed update cycles

Locations: `VPRC_BuildAccelerationStructure.cs:49-61,127-131` and
`Features/GI/VPRC_DDGITracePass.cs:44-58`,
`Features/GI/VPRC_DDGIHitShadePass.cs:41-70`,
`Features/GI/VPRC_DDGIBorderCopyPass.cs:91-93`, all under
`XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/`.

The BVH command disables its GPU BVH when draw submission uses `CpuDirect`.
The trace pass returns on `AccelerationStructureReady=false`, while hit shading
and atlas updates continue and clear invalidation through `OnFrameCompleted`.
The same failure applies during unavailable BVH data or unsuccessful tracing.
Zero/stale hit records become apparent lighting, masking the absent trace pass.
The live run reproduced this exact combination.

Enabling GPU draw submission also cannot supply the intended triangle buffer:
the new publication at line 127 casts the `GPUScene` provider to `GpuMeshBvh`.
These are separate implementations of `IGpuBvhProvider`; `GpuMeshBvh` is sealed
and is not a `GPUScene`. The cast always returns null, removes the triangle
variable, and forces the trace/hit-shade commands onto their one-record dummy
triangle buffers. The published command BVH is not the required triangle BVH.

Suggested correction: supply a triangle BVH for DDGI independently of draw
submission strategy and share one update-success state across the entire cycle.
Do not publish a completed lighting update after a prerequisite failed.

### P1: Dynamic probe positions are never initialized to the authored grid

Locations: `Build/CommonAssets/Shaders/Compute/DDGI/ddgi_raygen.comp:62-68` and
`XREngine.Runtime.Rendering/Rendering/GI/DDGI/DDGIVolumeRuntimeState.cs:756-795`.

Ray generation reads its base position exclusively from the probe buffer.
The dynamic allocation/upload path never fills those positions; the shader's
grid minimum, spacing and counts are unused for ray origins. The CPU helper
`ComputeProbeWorldPosition` is not connected to a dynamic GPU population path.
Thus separate atlas tiles do not trace from their corresponding grid locations.
This is a source finding; ray origins were not individually read back from GPU.

Suggested correction: derive the nominal position from each local grid index
and apply its stored relocation offset, or explicitly initialize and refresh
the GPU probe records before tracing.

### P1: Authored settings can exceed fixed GPU resource capacities

Locations: `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.Resources.cs:1762-1785`
and `XREngine.Runtime.Rendering/Scene/Components/Lights/DDGIVolumeComponent.cs:76-118`.

Resources use fixed default atlas dimensions, 8,192 probe records and 262,144
ray/hit/radiance records, while supported settings allow larger dispatches.
For example, 2,048 probes with 256 rays dispatch 524,288 rays. The documented
`32 x 4 x 32` grid requires a 192-wide irradiance atlas but receives 96 texels.
Three 4,096-probe cascades also exceed the probe-state capacity. These settings
produce invalid indexing or out-of-range GPU writes.

Suggested correction: size descriptors from the actual grid, cascade count and
update budget, or reject unsupported settings before allocation and dispatch.

## Further confirmed source defects

Paths beginning with `Rendering/` or `Scene/` below are relative to
`XREngine.Runtime.Rendering/`; shader paths are relative to
`Build/CommonAssets/Shaders/`.

| Priority | Location | Trigger and consequence | Suggested correction |
|---|---|---|---|
| P1 | `Compute/DDGI/ddgi_raygen.comp:62-68`; `ddgi_relocate.comp:69-74` | Global cascade offsets are reduced modulo a per-cascade count. Cascades above zero trace/relocate cascade-zero records, while sampling reads the correct global range. | Separate local tile/ring indices from the global probe-buffer base. |
| P1 | `Rendering/Pipelines/Commands/Features/GI/VPRC_DDGIRaygenPass.cs:49-73` | SlowUpdate checks an update-only counter before incrementing it. After update 0, counter 1 fails the interval check forever. | Schedule from an independently advancing render-frame counter. |
| P1 | `Rendering/GI/DDGI/DDGIVolumeRuntimeState.cs:347-364` | Only one cascade dispatches, but every due cascade advances its round-robin offset. Partial updates skip slices of undispatched cascades. | Advance only the cascade that actually completed its update. |
| P1 | `Rendering/Pipelines/Commands/Features/GI/VPRC_DDGICompositePass.cs:164-175`; `Rendering/GI/DDGI/DDGIVolumeRuntimeState.cs:454-457` | Camera scrolling changes cascade origins after ray generation, without scrolling/resetting atlas history; a single cascade is also reset to the component origin during synchronization. Sampling assigns existing lighting to different world cells. | Establish grid origins before updating and preserve/reinitialize history through an explicit scroll contract. |
| P1 | `Compute/DDGI/ddgi_relocate.comp:129-168` | Default classification marks embedded probes inactive before relocation, which excludes them and resets their offsets. They cannot escape geometry. The backface fraction also uses hit count rather than total ray count. | Relocate before final classification and use the intended ray-count denominator. |
| P1 | `Compute/DDGI/ddgi_hit_shade.comp:108-113` | Irradiance update stores the cosine-weighted average, E/pi. Feedback divides this already normalized indirect term by pi again, attenuating every additional bounce. | Normalize direct irradiance once and multiply sampled normalized indirect irradiance by albedo without the second pi. |
| P1 | `Compute/DDGI/ddgi_screen_sample.comp:29-51`; stereo counterpart `:34-60` | Hard-coded far depth 1 and `depth*2-1` reconstruction disagree with reverse-Z and zero-to-one clip-depth backends. | Use camera depth mode, clip-depth range and framebuffer orientation helpers consistently. |
| P1 | `Rendering/GI/DDGI/DDGIVolumeRuntimeState.cs:543-563` | The documented `CaptureBakedAsset` path never assigns `Probes`; saved assets omit relocation and classification state. | Capture the actual GPU probe payload alongside atlas bytes before serialization. |
| P1 | `Rendering/GI/DDGI/DDGIBakedAsset.cs:239-272` | Probe CPU bytes change without a GPU push. Child mip invalidation also does not request the parent array upload consumed by Vulkan. Baked mode can sample stale resources. | Explicitly upload probe data and texture arrays, then verify completion. |
| P1 | `Scene/Components/Lights/DDGIVolumeComponent.cs:389-402`; `Rendering/Pipelines/Commands/Features/GI/VPRC_DDGICompositePass.cs:76-94` | Switching a converged volume to baked or replacing its asset does not invalidate state. Upload gating ignores resource-generation identity, so new assets/resources may never receive their payload. | Track uploaded asset and resource generation, and invalidate on mode/asset changes. |
| P2 | `Snippets/DDGISampling.glsl:51-54` | Explicit zero intensity and bias values are replaced with defaults. Zero intensity produced nonzero DDGI in the live run. | Accept zero and clamp only invalid negative values. |
| P2 | `Compute/DDGI/ddgi_hit_shade.comp:73-80`; `ddgi_relocate.comp:87-90` | `objectId==0` is treated as a miss, although the current packed triangles use zero-based source triangle indices. Valid triangle zero is ignored. | Test the invalid triangle sentinel/hit distance instead. |
| P2 | `Snippets/DDGISampling.glsl:157-162` | The default single-cascade path bypasses volume bounds and samples edge probes outside the volume. | Apply an explicit boundary/fade policy to one cascade too. |
| P2 | `Snippets/DDGISampling.glsl:64-80` | An allowed count of one on an axis still generates eight corner offsets containing +1, producing invalid/aliased probe indices. | Require at least two probes per axis or collapse singleton-axis interpolation. |
| P2 | `Rendering/GI/DDGI/DDGIVolumeRuntimeState.cs:324-344,469-477` | The adaptive global probe count is not the count consumed by cascade dispatches, and measured update time has no production writer. FixedTimeBudgetMs does not enforce its advertised budget. | Feed measured GPU time into the actual active-cascade schedule. |
| P2 | `Scene/Components/Lights/DDGIVolumeComponent.cs:389-423` | A volume initially under an inactive hierarchy fails registration and has no activation hook to retry when its node is enabled. | Refresh registration from activation/deactivation hooks. |
| P2 | `Rendering/GI/DDGI/DDGIVolumeRuntimeState.cs:718-749` | Five arrays are allocated on every cascade-uniform upload, invoked by hit shading and composite each frame. | Reuse state-owned fixed-size arrays. |

## Implementation and documentation gaps

- Hit shading binds constant grey albedo, fixed sky/ground colors and an
  unshadowed primary directional light. It does not obtain interpolated
  material normals, emissive/material attributes or shadow visibility. This
  cannot produce the documented material-aware, shadowed diffuse transport;
  colored surfaces cannot bleed their own color and blocked sun-facing surfaces
  can inject unoccluded direct radiance. See `ddgi_hit_shade.comp:87-113` and
  `VPRC_DDGIHitShadePass.cs:96-143`.
- The new unit-testing `GlobalIlluminationMode` setting is read only to create
  the volume, not applied to `BootstrapRenderSettings` or the pipeline. The
  pipeline constructor still reads user settings. Creating a volume therefore
  does not itself select DDGI. The live validation explicitly set the pipeline.
- `Tint` is exposed but not synchronized or bound. `DebugDrawProbes` publishes
  variables/logs but does not draw probe geometry.
- The memory estimates omit actual fixed four-layer allocation and ray scratch
  buffers; do not treat the documented footprint/performance claims as measured.
- The octahedral encode/decode, bordered UV convention and border-copy mapping
  were internally consistent in the source review; no specific defect was found
  in those formulas.

## Review process and limitations

- DDGI is staged alongside unrelated Vulkan work; the review will distinguish
  DDGI defects from unrelated build blockers.
- RenderDoc environment checks passed.
- The broker refused its shader review during context validation because its
  repository policy excludes `Build/`. No broker run started. A native read-only
  agent is performing that shader review instead.

No fixes were attempted during the initial review. The control scene and live component attachment were
diagnostic changes confined to the isolated session; they were not saved into
the user's scene. The user has not reported whether any proposed correction
works. No RenderDoc frame capture was needed for this review because source,
shader compilation, MCP captures and logs already identified blocking failures.

The next implementation pass should first repair startup, trace compilation,
geometry-BVH ownership/readiness, probe initialization and capacity contracts.
Then validate an actual occluded, colored scene before accepting cascade,
baked, depth-convention or performance claims. Test additions remain subject
to the repository's feature-validation sequencing policy.

## Corrective implementation requested by the user

The user requested a remediation checklist in the DDGI TODO and implementation
of the defects. R1–R4 now track fixes and acceptance separately from the older
scaffolding checkmarks. The first corrected isolated OpenGL editor starts with
DDGI selected, links the trace shader, and allocates 48 × 192 irradiance and
128 × 512 visibility textures for an 8 × 4 × 8 grid. Captured atlas and screen
lighting contain finite values, and setting intensity to zero produces exactly
zero indirect RGB. These observations establish startup and basic output, not
full material, geometry, or transport correctness.

Implemented corrections include an independent scene triangle BVH, current GPU
deformation sources, world-space material/shadow hit shading, per-viewport probe
history and update transactions, explicit history initialization, dimensioned
resource generations, depth conventions, tint, cascade-local update cursors,
slow-update cadence, timestamp budgeting, and real GPU probe/atlas bake capture.
Static geometry avoids repeated packing, while animated geometry refits on GPU.
Colored emission and diffuse PBR weighting now match the deferred material
contract. All 17 expanded DDGI, geometry, debug, and composite shaders compile
with `glslangValidator` after these corrections.

Further validation is in progress: colored enclosure captures from two views,
changing geometry/materials, larger grids and multiple cascades, partial and
slow updates, live probe visualization, and capture/save/load/upload of baked
state. Vulkan and stereo remain unvalidated. No new tests have been authored,
and the user has not yet reported whether the corrections work in their scene.

The independent broker geometry review completed on the requested Sol model;
requested and actual model IDs matched. Its evidence supported the aggregate
world-triangle BVH approach. Native reviews identified additional deformation,
material, and lifecycle issues incorporated into the correction pass. Scratch
builds, captures, shader logs and reports remain under
`Build/_AgentValidation/20260920-183506-ddgi-verification/` and the named isolated
session `ddgi-verification-0920`; none are durable build dependencies.

## Corrective validation progress — 2026-09-20

The original findings above describe the pre-remediation working copy. The
following later isolated-session evidence supersedes individual failures where
stated. It does not establish production readiness and does not close an
acceptance criterion whose full validation is still pending.

| Check | Result and limit |
|---|---|
| Isolated editor build | Passed with 0 warnings and 0 errors. |
| Expanded shader syntax | All 17 DDGI, DDGI geometry, probe-debug, and composite shaders passed `glslangValidator`. This remains syntax validation rather than runtime numerical proof. |
| Automatic dynamic startup | `CpuDirect` prepared an aggregate world-space BVH with 1,139 triangles and 2,277 nodes. |
| Resource resize | MCP changed the grid to `8 x 6 x 8`; runtime reported 384 probes, `48 x 288` irradiance, `128 x 768` visibility, and 49,152 ray capacity. |
| Dynamic lighting | A blue emitter and moved block changed coherent indirect lighting in captures from camera A and B. This is bounded-scene evidence only. |
| Baked A/B round trip | Asset A: 256 probes, 32 relocated, 1 inactive. Asset B: 384 probes, 65 relocated, 0 inactive. Saved payloads and GPU RGBA-float atlas hashes were byte-exact after the parent-direct float upload correction. |
| Baked transitions | Dynamic→Baked→Dynamic and A→B→A replacement across sizes passed after settling; ray-buffer regeneration while baked also passed. Returning from a dynamic `4 x 4 x 4` layout to the same baked asset restored `8 x 4 x 8` and both exact atlas hashes. |
| Baked invalid input and layout authority | A corrupt magic header left the editor responsive with DDGI uninitialized. Repairing that same file and toggling modes restored both exact atlas hashes. While baked, edits to counts/cascades/origin restored the asset layout while authored zero intensity stayed zero. |
| Cascades and capacity | `32 x 4 x 32`, three cascades, 256 rays and a 256-probe budget produced 12,288 probes and 65,536 rays. Each 4,096-probe cascade updated and all `192 x 768` irradiance and `512 x 2048` visibility layers remained finite. |
| Scheduling | SlowUpdate interval 30 advanced the completed-update counter from 10,639 through 10,647 with all three cursors advancing. Fixed budgets 0.05 ms and 5 ms submitted one and two probes with nonzero timings. The adaptive estimate and minimum work may exceed the target, so this is not a performance benchmark. |
| Small grids | `1 x 2 x 2` and `1 x 1 x 1` produced finite, correctly sized atlases. |
| Bounds and activation lifecycle | Moving the bounded dynamic volume to `(100, 100, 100)` produced exact zero visible-scene DDGI RGB. Restoring origin and toggling the node inactive/active regenerated 256-probe capacity; after warmup, completed update 15,922 had `updatedProbeCounts[256]` and finite output (max 2.082, mean 0.07657). |
| Lighting controls and debug draw | Zero intensity produced exact zero indirect RGB; green tint produced pure green output; GPU probe billboards drew from live probe data. |
| Camera scrolling | One cascade with 256 probes and a 32-probe update cap snapped origin X from 0 to 3.857143 after a five-unit camera move. The first observation had 32 updated probes and warmup active; after settling it had 256 updated probes, warmup false, and finite screen output. |
| Reverse-Z sampling and composition | After raw-depth unprojection, normal/reverse mean RGB was 0.06386727/0.0638661, maximum 1.8125/1.8134766, with finite values and identical alpha coverage. Earlier immediate comparisons were stale; these captures waited two seconds. Disabling inherited depth testing in both DDGI composite factories then restored the complete enclosure in the final normal/reverse viewport images. |
| Final debug presentation | Final images from two camera positions show coherent bounded lighting. Leaving DDGIOnly restores ordinary lighting without changing authored AutoExposure=true/Exposure=1. |
| Final targeted contracts | 46 passed, 8 failed of 54. The failures are superseded source contracts or malformed bake fixtures, detailed below. No tests were modified. |

OpenGL cold-start diagnostics found a legal-readback defect in the immutable
BVH overflow flag and a lazy image-texture allocation defect. The fixes now
request read-capable immutable storage for the asynchronous four-byte overflow
diagnostic and create/allocate image texture storage before image binding. The
subsequent cold-start run had no high-severity OpenGL invalid-texture or illegal
map-read messages. The DDGI debug white-out caused by metering a diagnostic
image's black background was corrected by neutral exposure; the final viewport
is coherent with camera auto-exposure still enabled.

The first image-storage correction still failed on a cold start: `glGenTextures`
reserved names were used by direct-state-access storage before becoming typed
texture objects. This produced invalid-texture errors and an out-of-range image
readback in the next isolated run. Target-typed `glCreateTextures` at object
creation, preserving the reserved-name path required by texture views, fixed
the next cold run. Merely setting the wrapper's storage flag was not proof of a
successful GPU allocation. The final reverse-depth defect was also independent
of raw sampling: `DepthTest.Enabled = Unchanged` ignored the composite's
`Always`/no-write settings. Its fullscreen triangle failed the inherited
reverse-depth scene comparison. Explicitly disabling depth testing is the
targeted correction; no global depth convention or pass reorder is needed.

Vulkan, stereo, and Advanced pipeline DDGI remain unvalidated or unsupported.
Camera scrolling and node activation have the bounded recovery evidence above.
Final composition and the existing contract suite were rerun. No tests were added
or modified. Startup logs still contain unrelated multiview material compile
failures (`num_views=2`), so the absence of the corrected DDGI resource errors
must not be presented as a clean bill of health for every renderer path.

The final texture review also found that the old cube-array mapping returned
`TextureBindingCubeMapArray`, a query enum. Target-typed texture creation exposed
that error; it now uses `TextureCubeMapArray`. The targeted-test build compiled
this correction. Mutable 2D/3D first-image allocation remains an existing
non-DDGI follow-up for Landscape/MeshSDF, not evidence against the validated
immutable DDGI texture path.

The reversed-depth editor grid still drew over the enclosure after switching
from DDGI to `LightProbesAndIbl`, proving that artifact is independent of DDGI.
Hiding GridFloor produced coherent final reverse-depth DDGI images from both
camera positions. The grid issue remains separately recorded, not silently
counted as fixed. Its six external fragment shaders and five embedded fallback
shaders in `InfiniteGridFloorComponent` flip projected window depth a second
time with `DepthMode == 1 ? (1.0 - depth) : depth`. Projected depth already
contains reversal; these writes should use `depth` directly, followed by a
normal/reverse grid-occlusion validation.

### Targeted test failures awaiting clearance

| Existing test | Required correction |
|---|---|
| `VPRC_DDGIPasses_Defaults_AreConfiguredCorrectly` | Expect the dedicated `DDGIGeometryTriangles` and node bindings. |
| `VPRC_DDGIRelocatePass_Phase5_ConfiguredCorrectly` | Expect the dedicated geometry triangle binding. |
| `DDGIPhase3_Shaders_ExistAndContainExpectedContracts` | Replace the old exact `sampleDDGI(hitPos, normal)` source spelling with the corrected hit-shading contract. |
| `DDGIPhase6_ShadersAndPipelines_ContainCascadeContracts` | Assert active descriptor-sized layers, not fixed `DefaultMaxCascades` layers. |
| `DDGIScreenSampleShaders_Phase8_DeclareAmbientOcclusionContracts` | Account for deferred material diffuse weighting in addition to albedo and AO. |
| `DDGI_Scheduler_FixedTimeMode_AdaptsToBudget` | Respect the authored 256-probe allocation cap instead of expecting 512 probes. |
| `DDGIBakedAsset_Serialization_RoundTripsHeaderAndPayloadAccurately` | Supply both cascades' probe records, RGB-float irradiance payloads and complete visibility layers. |
| `DDGIBakedAsset_ApplyToVolume_ConfiguresVolumeAndState` | Supply valid matching atlas dimensions and complete captured payloads before applying. |

These failures must not be fixed by removing payload validation, increasing
dispatch above allocated capacity or restoring fixed resource dimensions. The
repository requires explicit user clearance before modifying regression tests;
clearance was requested after the live OpenGL validation completed. Remaining
acceptance work is tracked in the remediation checklist. The user has not yet
reported the corrected behavior in their own scene.

## Completion pass across both backends and pipelines

The user subsequently requested every remaining DDGI gap in Default and Advanced
on OpenGL and Vulkan, with correct material emission. This expands the acceptance
scope above; the earlier OpenGL results do not validate the new material path.
The C1–C7 checklist in the DDGI TODO tracks this pass.

Implemented, pending live acceptance:

- Semantic texture bindings retain UV sets/transforms, wrapping, color space and
  scalar channels independently of legacy sampler order. Independent linear RGB
  emission and strength survive native glTF/FBX and Assimp material import.
  GPU triangle attributes carry smooth normals and two UV sets, and DDGI samples
  a GPU-resampled material image array for color, opacity, normal, emission and
  transmission. glTF emission follows the independent emissive factor/map
  contract in the [glTF material specification](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#reference-material).
- Transparent hit transport, colored shadow transmittance and relocation walk
  through layers to the blocking surface. Cumulative transmittance determines
  visibility instead of treating every translucent layer as an opaque wall.
- Geometry accessor edits invalidate storage clones using the mesh revision,
  replaced source buffers retire their old clones, and declared buffer layouts
  and triangle indices are checked before GPU dispatch. Shader address and
  output bounds checks provide an additional guard.
- Render-target and writable-image usage marks textures as GPU writable, so
  retained CPU upload data cannot make DDGI cache a changing GPU image forever.
- Both pipelines declare DDGI geometry, atlases, environment radiance and update
  dependencies. A graphics pass captures the authored sky into an octahedral
  texture; capture variants decode the direction per fragment to avoid folded
  hemisphere interpolation errors.
- Default deferred rendering has a separate RGB emission attachment with a
  legacy scalar fallback. Advanced canonical material emission is being extended
  through its GPU material table and native shading path.
- Completion receipts, interrupted-chain invalidation and per-viewport ownership
  protect probe history. OpenGL fence objects now use a bounded reusable pool.

Validation in this pass: the editor build succeeded with one nullable importer
warning (subsequently assigned for correction); all 18 expanded DDGI/geometry/
debug/composite shaders and twelve ordinary/capture sky variants compiled. These
are compilation results, not GPU acceptance. No tests have been changed.

The first new live run, `ddgi-verification-0920` on OpenGL/Default, was stopped
after startup failed before a viewport frame completed. Concurrent working-copy
buffer-publication changes made `GPUScene.UpdateLogicalMeshTableEntry` call
`XRDataBuffer.PushSubData` on the collect/swap thread, where owner-first buffer
lookup threw because there was no active render owner. The scene consequently
had no materialized render resources and viewport capture timed out. This is a
real validation blocker; the scene must render through the normal path before
any new lighting result is accepted. Evidence is in that named session's
`xrengine_2026-09-20_22-34-55_pid46008` logs. No other editor process was stopped.

The source correction now separates authored CPU commits from exact-owner backend
operations. CPU commits fan out to existing live wrappers without creating a
wrapper on the collect thread, using a cached owner snapshot to avoid per-commit
enumerator allocations. GPUScene writes, including full meshlet snapshots, use
`CommitDirtyBytes`; its zero-length pseudo-upload was removed. Backend retirement
guards reject stale queued work. The correction still requires a successful
rebuild and live startup; buffer-wide upload revision reporting remains a known
multi-owner diagnostic limitation under review.

Additional completed source work includes the shared directional/point/spot light
buffer, finite colored local-light shadows, semantic-only import texture selection,
and normal-only white-albedo defaults. Advanced UV1/transform/color-space metadata
for emissive maps is still being extended. The TODO now separates these source
changes from outstanding runtime acceptance instead of marking the C1–C7 items
complete based on compilation alone.

The next build passed with zero warnings/errors and the named OpenGL/Default
session (`xrengine_2026-09-20_23-04-13_pid62760`) loaded all 108 fixture triangles
into 215 BVH nodes. Probe updates remained at zero. The render-command log,
starting near line 2460, showed `VPRC_DDGITracePass` aborting every frame because
its newly created diagnostic buffer called `Generate()` before owner-first
publication completed. The correction moves this scratch buffer into
`DDGIFrameContext`, publishes CPU bytes with `CommitDirtyBytes`, lets binding
create the current-owner wrapper, and disposes it during context clear. A new
cold diagnostics snapshot exposes the last update stage, environment readiness
and pending submission status. The named session was stopped for rebuild; black
captures from this run establish the failure and do not validate lighting.

Build 7 completed with zero warnings/errors. In session
`xrengine_2026-09-20_23-15-27_pid14764`, the update chain completed more than 1,556
times, with 384 probes and 49,152 rays. The irradiance atlas was finite (maximum
2.625, mean RGB 0.01019) with direct/ambient light disabled. Its viewed capture
contains blue illumination. Logical GPU payload diagnostics total 24,438,512
bytes including scene inputs; this is not driver residency.

The screen remained black: albedo and emission were exactly zero, and every
depth texel was the clear value 1. The scene nevertheless published seven
enabled opaque draw commands with correct transforms. Restarting with bounded
mesh diagnostics produced no mesh draw entry records. Source review confirmed
that newly owner-first `XRMeshRenderer.BaseVersion` objects had no backend
subscribers, while `Render()` only raised their empty events. Fullscreen quads
already prepared their wrappers explicitly and therefore still ran.

The correction resolves one exact owner through `IApiMeshRenderer` for every
draw, retaining the Vulkan canonical identity and removing the shared backend
events. This also prevents cross-owner draw broadcasts. Both implementations
guard retired objects, and the owner lookup iterates a stable snapshot without
hot-path enumerator allocation. BVH scratch buffers now commit initialization
before lazy first binding, removing the remaining startup `Generate()` failure.
Build 8 compiled the rendering modules, then exposed a renderer cleanup call to
a default interface method through its concrete type; that nearby call now uses
the interface owner. The rebuilt runtime must demonstrate populated G-buffers
before the expanded emissive behavior is accepted.

Build 9 passed with zero warnings/errors. OpenGL/Default session
`xrengine_2026-09-20_23-30-39_pid19548` populated the depth and emission buffers
(depth minimum 0.98047, emission maximum 8). With ambient and direct lighting
disabled, the black-base UV1-textured emitter produced mean DDGI RGB 0.005186
at blue strength 8. Disabling emission produced exact zero in both the emission
buffer and DDGI output. Green strengths 8 and 4 produced means 0.007073 and
0.003389. Viewed captures show coherent room illumination, color changes and
occlusion from two cameras. Normal and reversed depth produced finite means
0.005550 and 0.005741 with matching surface coverage. These are stochastic
captures, not an exact numerical equality assertion. This accepts the basic
OpenGL/Default emissive transport path; regular final composition, map changes
and the expanded matrix remain outstanding.

Builds 10 and 11 also passed with zero warnings/errors. An optional emission
sampler warning on neutral materials was corrected by binding a cached black
texture. Vulkan/Default first failed exact-owner wrapper checks: interface
dispatch selected the facade owner rather than the backend object context.
The common renderer now declares a virtual owner identity, overridden by Vulkan.
Its next session, `xrengine_2026-09-20_23-38-04_pid67252`, terminated during MCP
object inspection. The Windows .NET Runtime event identifies a native access
violation in `PhysxScene.Timestamp`, reached by recursive `SerializeValue`,
while physics was disabled. This is an inspection/startup blocker, not evidence
of failed DDGI GPU execution. A guarded native access correction and a shallow
inspection fixture are in progress. No Vulkan lighting result is accepted yet.

The shallow-inspection restart (`xrengine_2026-09-20_23-44-26_pid38320`)
survived startup but Vulkan refused the resource generation: the DDGI composite
declared both storage writes and sampled reads of the same image in one pass.
Its actual operations are sequential. The graph now models the compute resolve
and graphics composite separately, with a dependency and separate execution
scopes. The graphics attachment preserves existing direct lighting for additive
composition. The scene was stopped before rebuilding. This corrects the graph
contract instead of bypassing Vulkan's hazard validation.

Build 13 succeeded with zero warnings/errors after correcting the native physics
inspection guard. Vulkan session `xrengine_2026-09-20_23-50-15_pid69252` accepted
the split graph and populated scene depth/emission correctly. Emission maxima
followed 8, 0, 8 and 4 during the same fixture sequence. Both the irradiance
atlas and DDGI output remained exactly zero despite hundreds of update receipts.
The first rejected frame logged a storage-descriptor failure because the scene
material atlas lacked `RequiresStorageUsage`. The material-copy cache and scene
BVH cache also recorded queued work as complete before that frame was accepted.
The next correction sets storage usage before allocation and retains pending
copy/build receipts, retrying after rejection without GPU readback or CPU tracing.
This run is not accepted as Vulkan illumination evidence.

Build 14 passed with zero warnings/errors and includes the material atlas storage
usage and material-copy/BVH submission receipt corrections. Vulkan session
`xrengine_2026-09-20_23-58-20_pid47552` still produced zero indirect illumination.
The fixture now fails immediately on a black baseline rather than continuing
through an emission sequence that cannot establish light transport.

A fresh capture-enabled session, `xrengine_2026-09-21_00-01-19_pid62628`, produced
`renderdoc/ddgi-vulkan-default_frame752.rdc` under the current ignored evidence
root. RenderDoc shows eight compute dispatches: ray generation at event 288,
hit shading at 310, relocation at 324, irradiance/visibility updates at 338/352,
border copies at 363/372, and screen sampling at 386. The trace dispatch is
entirely absent. Exported irradiance and composite images were viewed and are
black. The named editor and replay session were both closed after inspection.

The trace-only disabled diagnostic SSBO is created lazily and commits CPU data
before an owner wrapper exists. Vulkan captures an inactive buffer with no valid
handle/range, rejects the compute snapshot, and silently returns. The caller
nevertheless advances the DDGI update to Hits. Shared ray/geometry/material
bindings are present in the neighboring dispatches, isolating this extra buffer.
The correction removes disabled diagnostics from this shader and reports rejected
Vulkan compute snapshots through the existing command-chain abort path. Actual
trace dispatch and nonzero hits/irradiance still need a new runtime check.

Additional source changes awaiting the next build isolate geometry/BVH/material
cache ownership by pipeline instance and retain baked-upload submission receipts.
The memory diagnostics now identify pipeline geometry payload rather than
mislabeling it as scene-shared. Baked transfer metadata is being checked for
optional-pass synchronization: declaring a transfer that never executes must not
replace the real dynamic shader producer in the probe-buffer barrier.

The synthetic baked transfer pass was removed before build 15. Vulkan's texture
upload path already performs explicit layout transitions and synchronous staging
copies. The DynamicDraw probe buffer uses a host-visible upload, so there is no
frame-graph transfer command to declare. Host writes still require a separate
lifetime guard when a prior frame may read the same probe allocation; that guard
is being added for dynamic/baked and baked-asset transitions.

Build 15 passed with zero warnings/errors. Session
`xrengine_2026-09-21_00-20-12_pid65368` reported zero completed updates and the new
explicit failure: `DDGI.WorldAabbs` has no ready Vulkan handle/range. This buffer
had never been pushed or explicitly bound through its own owner API. The Vulkan
program's misleadingly named `WrapperLookup.GetOrCreate` only retrieves an
existing wrapper, so no wrapper existed to schedule allocation. The program
binding boundary now establishes the current owner's wrapper while leaving
native allocation scheduling to the backend. Geometry resource imports are
published in a finally block even when preparation is pending, ensuring early
material copies have graph metadata before frame sealing. The named session was
stopped; these source corrections require the next build and live run.

Build 16 passed with zero warnings/errors. Vulkan/Default session
`xrengine_2026-09-21_00-36-03_pid68856` produced real indirect lighting with
108 triangles, 384 probes and 128 rays per probe. The textured blue emitter at
strength 8 produced mean DDGI RGB 0.00679; disabling emission produced exact zero.
Green strengths 8 and 4 produced means 0.00919 and 0.00466. Blue and green room
captures were viewed. Reloading the emission map with black gave exact zero;
white restored lighting at mean 0.01559 versus the checker's 0.00669. This proves
GPU material-copy invalidation as well as emissive scalar/color response.

With emission and ambient disabled, point, spot and directional sources each
produced finite nonzero DDGI (means 0.02420, 0.00253 and 0.00378). Authored blue
and red skies produced means 0.03065 and 0.04026. The point-lit and red-sky DDGI
captures were viewed. Extended occlusion, motion and deformation remain open.

Normal/reversed depth and a second camera worked in DDGI output, but regular
viewport composition was magenta. The scene HDR capture was also magenta, so
post-processing was ruled out. RenderDoc capture
`renderdoc/ddgi-vulkan-default-composition.rdc` identifies deferred combine event
246 as the producer. Its binding 9 (emission) points to a placeholder image,
while bindings 7/8 contain valid neutral PBR arrays. The combine result and
placeholder texture were exported and viewed; the replay session was closed.
The material's positional texture list incorrectly placed emission at slot 7.
Mono/MSAA combine materials and their cache validators now match shader slots
0–9, and binding generation tracks the selected emission attachment. Build 17
and a live regular-composition check are required to accept the fix.

Baked lifecycle review found and corrected repeated dynamic aborts in baked
mode, unnecessary waits on already-published baked data, and receipt publication
before distinguishing rejection from accepted GPU use. Candidate submissions and
accepted resource-use receipts are now distinct; host overwrites wait only when
replacing data. A final review also caught double-poll races in overwrite guards.
Live baked round-trip and replacement checks follow the next build.

Build 17 passed with zero warnings/errors. A fresh Vulkan/Default session,
`xrengine_2026-09-21_00-51-09_pid11840`, without the RenderDoc preset produced
correct regular composition: HDR mean RGB 0.01764, maximum 8; DDGI mean 0.00668.
Viewed HDR and viewport captures show the checker emitter and blue diffuse
bounce. Normal/reversed depth and a second camera produce finite lighting.
This accepts the deferred-combine descriptor correction for the fixture.

The baked round-trip attempt then failed explicitly because Vulkan's facade
did not expose synchronous diagnostic buffer readback. The implementation now
routes that capability through the existing accepted pipeline readback scope;
normal DDGI execution still performs no host readback. The single-cascade bake
guard also no longer rejects an inactive coarse-visibility setting. These
source changes need a fresh build and live round-trip before acceptance.

Sky activation and color changes passed in the earlier Vulkan/Default run, but
sky deactivation did not. The session log at 00:47:55 recorded a terminal frame
rejection for `Skybox.FullscreenTriangle`: its `Triangles` index buffer was
pending an asynchronous build. Subsequent captures would be stale while the
renderer remained paused. The next lifecycle checks must verify advancing
completed-update counters and cleared environment state after deactivation.

The first OpenGL/Advanced run, `xrengine_2026-09-21_00-55-05_pid71036`, completed
857 DDGI probe updates but rejected native opaque shading: the default Light
Probes and IBL provider did not implement DDGI. Advanced also declared no live
albedo, normal, RMSE or emission surface outputs for the shared DDGI composite.
The requested companion capture failed with an invalid mip/layer readback.
This run does not establish Advanced lighting support.

The correction adds a matching built-in DDGI provider and exports the resolved
native surface from the existing opaque shading invocation. Both backends need
four conditional RGBA16F outputs, with immutable Vulkan descriptors, view
ownership and transitions. OpenGL uses image units 4–7 after GTAO. DDGI retains
probe specular IBL while suppressing its diffuse lobe, consumes Advanced's AO
target, and must not relight unlit surfaces. A separate reconstruction pass was
rejected because it duplicates texture/decal work and can diverge from visible
shading. Fresh OpenGL/Advanced and Vulkan/Advanced emission and final-composition
checks follow implementation.

Build 21 passed with zero warnings/errors after compile integration corrections
in builds 18–20. OpenGL/Advanced session `xrengine_2026-09-21_01-17-49_pid16456`
now shades and composites the fixture. After startup shader compilation settled,
blue emission at strength 8 gave mean DDGI 0.00542; emission off gave exact zero;
green strengths 8 and 4 gave 0.00749 and 0.00387. The blue indirect capture and
regular viewport were viewed. Normal/reversed depth and a second camera pass;
regular HDR retains the independent emission maximum 8. Checker/black/white
emissive-map reloads gave means 0.00533/0/0.01275.

The OpenGL/Advanced bake round trip retained mean DDGI 0.00547 and fixed update
count 9817, including after disabling its source emission. A dark replacement
gave exact zero at fixed update count 9827. A missing path cleared contribution,
and restoring the lit asset recovered exactly the original mean without dynamic
tracing. Point, spot and directional lights and authored gradient skies also
produce finite nonzero DDGI. Sky off/on/off gives zero/restored/zero while probe
updates continue. The named editor was stopped before the Vulkan/Advanced run.

The sky correction retains the immutable fullscreen mesh across ordinary
deactivation; final component destruction owns teardown. Advanced's DDGI quad
now publishes screen dimensions, and its stereo fragment shader requires OVR
multiview to activate the corresponding fullscreen vertex path. Vulkan native
surface closure review found no P1 defect; device descriptor limits and live
Vulkan execution still require verification. Memory diagnostics are being
extended to include the four additional Advanced surface exports.

Vulkan/Advanced build 21, session `xrengine_2026-09-21_01-23-50_pid55528`,
passes emission off/on, color and strength: blue strength 8 mean DDGI 0.00532,
off exactly zero, green strengths 8/4 means 0.00753/0.00365. DDGI and regular
HDR captures were viewed; the checker emitter and blue bounce are correct.
Normal/reverse depth and a second camera remain finite. Point, spot, directional
and gradient-sky sources all contribute. Sky off/on/off produces zero/restored/
zero with advancing update counters and no steady-state Vulkan validation errors.

Moving the block and then bringing the skinned/morphed fixture into the room
changes the visible occlusion and indirect image while updates continue. Captures
were viewed and scene state restored. Bone and morph changes must still be
isolated from root motion before accepting GPU deformation independently.

Vulkan bake capture now succeeds, but upload fails visually and numerically:
dynamic mean DDGI 0.00520 becomes 5.89159; the uploaded irradiance atlas contains
16 non-finite samples and reaches 32768, versus a finite dynamic maximum 2.5.
Viewed output shows green strips. Readback and file serialization are valid.
`VkTexture2DArray.PushTextureData` stages RGB32F bytes verbatim into the native
`B10G11R11UfloatPack32` image. A tightly packed copy consumes four bytes per
texel from twelve-byte RGB tuples, decoding float bit patterns as packed colors.
Normalize through `VkFormatConversions.CreateNormalizedUploadData2D`, reuse
that conversion for array mips, and reject incorrect native payload lengths.
Keep the portable baked asset format unchanged. The live bake harness now checks
all companion atlases for non-finite values and compares dynamic/baked brightness.

Builds 22 and 23 passed with zero warnings/errors. The packed upload correction
now uses round-to-nearest-even, including carry from subnormal to normal values,
and validates sizes before allocating conversion storage. Exact native byte
mismatches throw instead of letting callers publish a skipped copy as successful.
The neighboring RGB8 size table incorrectly returned six bytes; it now returns
three. An evidence-only broker review (`fd1483e1ed384a25808f58d0136e4453`, requested
and actual model both `gpt-5.6-sol`) confirmed the bit layout and rounding policy
and identified that size-table issue. Its conditional zero-length concern is
already excluded by `VulkanFrameDataSlice.IsValid`; unconditional validation
keeps the local copy contract explicit. Live verification follows build 23.

Vulkan/Advanced checker/black/white emission map changes pass with means
0.00543/0/0.01261. The first cutout transport comparison is inconclusive: floor
ROI means are 0.1981 baseline, 0.08592 masked and 0.08764 opaque. Global means
differ, but the selected receiver does not distinguish partial from full
occlusion. Both captures were viewed; the opaque plate appears while the masked
plate lacks a visible silhouette. Keep this case open pending source/fixture
isolation. The harness restored material, panel and lighting state.

Build 23 Vulkan/Advanced session `xrengine_2026-09-21_01-40-13_pid65152`
passes the tightened baked check: dynamic DDGI mean 0.00533875, baked mean
0.00522684 with finite companion atlases. Source emission off leaves that exact
baked result and update count unchanged. A dark replacement produces zero;
missing-file recovery restores exactly 0.00522684 without tracing. The baked
image was viewed and shows correct blue bounce. Advanced's four surface exports
are reported as 66,355,200 bytes at 1920x1080, included in the 90,795,632-byte
logical fixture total. These are payload sizes, not driver residency.

The same run isolated bone rotation and morph weight from root motion. Both
change deterministic depth hashes, preserve finite atlases/output, and advance
DDGI updates. Viewed images show the small box rotating, then its upper vertices
stretching independently with the bone restored; the scene is restored afterward.
GPU deformation passes this fixture on Vulkan/Advanced; other matrix cells remain.

Source review explains the initial cutout comparison: the fixture was a closed
box with mirrored front/back UVs. Its even checker mask is complementary across
those two faces, so a ray passing through a front hole hits a solid back square.
The slab therefore behaves as an opaque blocker. DDGI coverage hooks are used
consistently for primary, continuation, shadow and relocation traces. Replace
the transport fixture with one double-sided quad and a clear macro aperture;
the closed transmission slab also applies attenuation twice and must be replaced.

OpenGL/Default build 23 (`xrengine_2026-09-21_01-49-07_pid69592`) passes
single-quad cutout and transmission transport: floor ROI luminance is 0.4836
baseline, 0.4102 masked and 0.2238 opaque. Green transmission gives RGB
0.2152/0.4752/0.1813, half-strength white 0.3766 versus 0.1548 blocked.
Viewed captures confirm the response. Regular HDR composition reaches emission
8 with normal/reversed depth and two cameras. Checker/black/white maps give
mean DDGI 0.005249/0/0.012263. These captures use the desktop viewport.

The same run passes disabled-volume update suspension and final composition,
then two-cascade warmup and one-probe scheduling with both cursors advancing.
Irradiance/visibility layers remain finite. Inactive resources are deliberately
cached; source and runtime verify that no inactive update/composite uses them.
Bake capture/load gives dynamic/baked means 0.004964/0.005393. Emitter-off keeps
exact baked lighting and update count. Dark replacement and missing path give
zero; restoring the lit asset recovers exactly 0.005393. The baked image was
viewed. Two earlier scratch-harness failures were corrected: vector restoration
must parse MCP's valuePreview when serialized Vector3 properties are absent,
and a stronger white-transmission control avoids an inconclusive ROI threshold.

Stereo isolation found two independent blockers. Vulkan preview UI publishes
sampled views before the stereo producer has initialized their images, so
ResourcePrepare waits on Left Eye Preview and starves the producer. Gate preview
activation on both views' shader sampling readiness before the refresh throttle.
OpenGL emulated VR selects NoSpecialExtensions mesh shaders because
CanUseVrSpecificVersions only checks IsInVR. Stereo uniforms still set VRMode;
the mono shader writes model-space gl_Position expecting an absent geometry
stage. This explains the camera-invariant rectangle and depth 0.85 despite
nonzero probe irradiance. Include EmulatedRenderActive in stereo eligibility.
Both source corrections await build 24 and live stereo acceptance.

Raster alpha inspection also found native glTF duplicated its base texture into
legacy opacity, whose shader sampled red instead of base alpha. Remove that
synthetic opacity slot: glTF MASK uses the regular base-alpha cutoff variant.
True independent opacity maps retain alpha variants, corrected to multiply
base alpha by opacity red. A non-white half-mask fixture will verify coverage.

Build 24 succeeds without warnings/errors. OpenGL/Default session
`xrengine_2026-09-21_02-07-22_pid25632` now renders proper stereo G-buffers,
with different deterministic left/right depth hashes and correct room images.
DDGI initially remains black in eye one; direct captures confirm that eye's
AmbientOcclusionTexture is all zero while eye zero reaches one. Disabling
ApplyAmbientOcclusion temporarily gives DDGI means 0.01895/0.01918 in the two
eyes, with visible binocular disparity. AO was restored. Source review found
AO target-bearing XRQuadFrameBuffer constructors implicitly force mono rendering;
all stereo AO providers need explicit multiview quads. The stereo DDGI sample
also uses the Default AO name rather than its configured binding; correct it.

OpenGL/Default isolated bone and morph changes pass with distinct depth hashes
and continued updates; viewed images show the correct rotated/stretched box.
Colored alpha mask and thin transmission pass again: floor luminance
0.4950/0.4046/0.1686 baseline/masked/opaque. The viewed raster now shows the
half-panel aperture, including with non-white texture RGB. Green receiver RGB
is 0.2165/0.4663/0.1762; white half-transmission luminance 0.3821 versus blocked
0.1564. Point/spot/directional and blue/red sky contribute; sky deactivation
returns to zero twice with counters advancing. Transient empty sky sampler
warnings occur while the harness adds then configures a sky component; there
is no accepted steady-state missing sampler. Node deactivate/reactivate stops
and resumes updates in distinct desktop/stereo pipeline instances. This does
not establish resize or final stereo composition acceptance.

Vulkan/Advanced build 24 session `xrengine_2026-09-21_02-12-36_pid69572`
gets past preview preparation but never submits a complete scene frame. First
failure at 02:12:43.244 reports incomplete final-presentation source epoch one,
then epoch two repeats with undefined layout and no descriptor. The recovery
path recreates the swapchain repeatedly. DDGI correctly records Failed receipts
and zero completed updates. No previous Vulkan stereo result is accepted;
investigate final source publication separately from DDGI shading.

Vulkan/Default build 24 session `xrengine_2026-09-21_02-14-46_pid31280`
passes the baked repeat: dynamic/baked means 0.006436/0.006994, exact hold with
emission disabled, zero dark replacement/missing contribution, and exact lit
recovery without tracing. Viewed captures show correct blue lighting. Colored
half-mask and transmission pass (receiver luminance 0.5640/0.4074/0.2057 for
baseline/masked/opaque; green RGB 0.2553/0.5379/0.2119; white half-transmission
0.4341 versus blocked 0.1894). Isolated bone and morph changes give distinct
depth hashes and visibly rotate/stretch the box with finite DDGI.

The light repeat then stops at a real upload rejection: a point-light R16Sfloat
cube stages four-byte red floats where Vulkan requires two-byte half pixels.
Strict validation from the packed-atlas correction now exposes this older cube
path defect. Normalize both cube and cube-array faces through the shared 2D
normalizer and add the explicit red-float to R16 half conversion. The failed
light result and following unrun lifecycle checks are not accepted. A concurrent
whole-camera-component inspection timed out; avoid broad property traversal
and use exact getters for further camera validation.

OpenGL/Advanced build 24 session `xrengine_2026-09-21_02-19-55_pid17928`
passes initial stereo: DDGI means 0.004439/0.004445, matching debug HDR in each
eye and distinct depth hashes. Both images were viewed and show binocular
parallax. Configured AO-name correction and regular HDR emission repeat remain.

That OpenGL/Advanced run also reaches emission maximum 8 in both regular HDR
eyes. Isolated bone and morph changes alter depth hashes and visibly rotate or
stretch the box with finite DDGI. Volume disable stops updates; two-cascade
warmup/fair scheduling and node deactivate/reactivate pass with distinct desktop
and stereo pipeline instances. The TSR resize harness reaches scale 0.5 with
matching DDGI dimensions, but immediately capturing the restored full-size
generation fails during turnover. No resize acceptance is claimed; strengthen
the wait for a stable generation and accepted updates. AA and scale were restored.

Cutout/transmission ray transport passes on OpenGL/Advanced: receiver luminance
0.4753/0.3692/0.1748 baseline/masked/opaque, green RGB 0.2190/0.4759/0.1821,
white half-transmission 0.3802 versus blocked 0.1552. Image review nevertheless
shows the masked raster panel missing. Baseline and masked depth hashes are
identical (`6D2210B8...`), including cutoff values 0.5, 0.1 and zero; opaque
coverage changes the hash (`2583FC2F...`) and shows the plate. Keep Advanced
material acceptance open. The harness needs an explicit masked-vs-baseline
depth assertion as well as transport comparisons. Panel and cutoff were restored.

Vulkan stereo source review isolates another presentation error: an array input
selects an OVR fragment shader even though the desktop swapchain destination is
single-layer and uses a mono fullscreen vertex shader. The desktop mirror needs
a mono array sampler with explicit eye selection. Preallocate presentation quads
and attach the descriptor publisher before initial preparation so first-frame
publication cannot miss the source. Preserve genuinely layered external output
semantics. The named editor is stopped while these source corrections are built.

Build 25 passes without warnings/errors. OpenGL/Default session
`xrengine_2026-09-21_02-40-10_pid50184` confirms the stereo AO correction:
both AO layers reach one with means 0.15966/0.15987 and different hashes;
DDGI means are 0.003092/0.003148 with finite output and binocular geometry.
The right-eye DDGI image was viewed. The stricter stereo check now requires
visible emission and catches a separate combined-program link failure:
`FragBinorm` is not declared by the previous stage for the normal-mapped emitter.
Both emission layers are zero; GI rays still see the authored emitter.
Full Default stereo composition remains unaccepted pending that interface fix.

The same run passes desktop TSR scale 1.0 → 0.5 → 1.0: matching DDGI/internal
dimensions, changing resource generations, eight subsequent updates and finite
captures at each size. All captures succeed on their first attempt; the half-size
image was viewed. Original AA/scale were restored. Saved lit bakes from all four
combinations contain 52 relocated probes out of 384, no inactive probes and no
non-finite records. Existing byte-identical save/load verification therefore
covers relocated state, but does not yet prove inactive-state persistence.

Advanced masked source review finds rejection before rasterization:
`GPUScene.Soa.ResolveStateClass` correctly emits `AlphaTested`, but deferred
masked imports retain `OpaqueDeferred` as their legacy render pass. The Advanced
bridge chose its layout from that pass and rejected the state mismatch. Build 25
promotes exactly this combination to the ABI-compatible canonical masked layout
before validation; projective mirrors and unrelated mismatch guards are preserved.

OpenGL/Advanced build 25 session `xrengine_2026-09-21_02-45-21_pid27404`
visibly renders the expected half-panel. Baseline, masked and opaque depth hashes
now differ. The original narrow floor ROI lies mostly on the unshadowed half,
so its 0.0045 drop misses the 0.01 margin despite correct spatial occlusion.
After image inspection, repeat with ROI (600,875,550,65) spanning both halves
passes: luminance 0.4535/0.3129/0.1687, white half-transmission 0.3595 versus
0.1464 blocked, green transmission biased green. The masked image was viewed.
The stronger raster assertion also passes. Stable-generation TSR resize passes
at scale 1.0, 0.5 and 1.0 with finite images, no retries and restored settings.

Configured AO sampling exposes a separate Advanced producer defect: eye-zero
native AO has maximum one and mean 0.30357, while eye one is exactly zero.
Both emission layers reach eight and both depth layers contain distinct valid
geometry. Corresponding DDGI is 0.003313 in eye zero and zero in eye one.
Prior stereo images that bypassed the wrong AO name are not full acceptance.

Vulkan/Default build 25 session `xrengine_2026-09-21_02-42-35_pid44924`
passes point and spot light response, then rejects the directional fallback
`DummyShadowMapArray`: four-byte authored float depth was sent unchanged to
two-byte `D16Unorm`. Add native normalized-depth conversion beside the half-float
conversion, retaining exact native payload checks. The harness restores emitter
and created nodes, but the failed directional/sky sequence is not accepted.

Build 26 passes without warnings/errors. OpenGL/Default session
`xrengine_2026-09-21_02-54-44_pid45376` now writes emission maximum eight
in both stereo eyes. The no-tangent normal-map path has matching vertex/fragment
interfaces and derives its tangent frame from surface derivatives; both vertex
generators now supply the zero sentinel and alpha shaders evaluate derivatives
before discard. DDGI means are 0.003169/0.003164 with accepted update progress.
Regular eye-one HDR still contains only DDGI, exposing mono light-combine quads
paired with stereo fragment shaders. Correct the Default light combine and
required post-processing quads to request multiview, then repeat in build 27.

Advanced GTAO uses one dispatch per eye with dispatch depth one. Reading
`gl_GlobalInvocationID.z` therefore selects eye zero both times. Build 26 uses
the supplied push view index; the live Advanced repeat remains outstanding.

Vulkan/Advanced build 25 session `xrengine_2026-09-21_02-50-14_pid46076`
intermittently captures valid stereo layers but reports failed DDGI receipts and
continuous normal-presentation rejection. No stereo acceptance is claimed.
Logs show epoch one observing descriptor slot zero then rejecting slot one;
epoch two observes slot one then repeatedly rejects slot zero. Cached-owner
command reuse skipped mutable frame-source refresh, preventing slot-specific
descriptor observation. The reviewed build-27 correction preserves the skip
only for immutable bindings. The stereo harness now requires eight fresh
accepted updates and rejects Failed receipts before and after captures.

The enclosed-box bake in OpenGL/Default contains 13 inactive records. The
initial harness incorrectly treated stored position xyz as nominal world
positions, although ray generation derives those from grid indices. Its center
probe also intersects other fixture surfaces. Select an actually enclosed
inactive record that was active before insertion, then verify that same record
reactivates after removing the box. Existing save/load evidence proves relocated
state on all four combinations; inactive upload/recovery remains outstanding.

The hot-path source audit found reused geometry/material collections, inline
uniform values and Vulkan per-frame uniform-array slots. Diagnostic readbacks
and capture reports intentionally allocate on demand. This is source evidence;
runtime allocation acceptance remains open.

Build 27 extends live acceptance. OpenGL/Default session
`xrengine_2026-09-21_03-06-42_pid7776` and OpenGL/Advanced session
`xrengine_2026-09-21_03-14-13_pid35344` pass the full stereo capture chain:
both eyes have different valid depth, finite DDGI, emission and regular HDR
maximum eight, and nonzero final FXAA output. Right-eye HDR was inspected for
both; Default's final post-process output was also inspected. Both pass actual
inactive-probe baked GPU upload and recovery: selected probe 212 is active
before enclosure, inactive inside the box, remains inactive after upload and
reactivates after Dynamic updates and box removal. All 384 probe records match
across GPU upload/readback, including relocation offsets.

Vulkan/Advanced session `xrengine_2026-09-21_03-04-40_pid69368` still rejects
normal presentation even when isolated stereo captures succeed. Source and
scoped-log review identify unrelated output cohorts: the mono desktop owner
observes slot zero at epoch one, then a stereo-only cohort tries to validate
that retained source against slot one. Epoch two repeats the inverse mismatch.
The build-28 correction checks whether the recorded plan contains the source's
pipeline, viewport and stable scheduling owner before binding a command artifact
or requiring its descriptor observation. Same-owner generation checks remain
strict. The retained tuple stays available for resize replay and the independent
swapchain-writer invariant remains unconditional. Sol independently reviewed
this ownership slice. Runtime verification remains open.

Vulkan/Default session `xrengine_2026-09-21_03-18-09_pid55692` passes the entire
light-source sequence after D16 upload normalization: dark zero, point mean
0.02428, spot 0.00256, directional 0.00399, blue sky 0.03059 and red sky 0.04008.
Sky off/on/off produces zero/0.04016/zero. Directional DDGI was visually inspected.
The same session passes inactive-probe GPU upload/reactivation, suspension,
two-cascade fairness and TSR scale 1.0 → 0.5 → 1.0. One initial capture retries
while the submitted planner generation catches up; dimensions/generation are
rechecked and later captures succeed first try. Half-size output was inspected.

The constant-radiance enclosure check on Vulkan/Default bakes the actual GPU
atlas. All 16 interior texels at probe 220 equal (0.25, 0.5, 1.0) at emission
strength one, double at strength two and become zero with emission disabled.
The harness restores geometry, material and volume settings. This proves atlas
normalization on that combination, not yet the full matrix or feedback behavior.

Build 28 also includes opt-in DDGI managed-allocation diagnostics with a
240-sample rolling window, and an edge-sampling correction: interpolation uses
the coordinate relative to the clamped base cell. The previous fract-before-clamp
selected the penultimate probe at the upper boundary and wrapped negative biased
coordinates toward the second probe. No live allocation acceptance is claimed.
Embedded-probe escape beyond the near-surface radius remains under review.

Builds 28b, 29 and 30 pass with zero warnings/errors. Build 28's initial compile
required the missing core namespace import in the new MCP allocation action.
The owner filter alone does not resolve Vulkan stereo: build-29 tracing confirms
the mono source owner is present in the mixed recorded plan. Build-30 session
`xrengine_2026-09-21_03-46-52_pid64100` separates descriptor diagnostics per
program and proves both mono and stereo final draws bind the stereo FXAA image,
although the required desktop source is the distinct mono FXAA image.

The present command resolves its exact source only around enqueueing the quad;
its finally block clears those fields before Vulkan materializes the queued
request. Its non-deferred binding publisher then resolves the currently active
stereo instance. The source correction snapshots the exact texture, renderer,
sampling state, orientation and generation using the established deferred
publisher protocol. Review found that deactivation must retain capture tokens
for foreground preparation retries; build 31 follows that correction. Strict
source validation remains enabled. Vulkan stereo acceptance is still open.

OpenGL/Default build-30 session `xrengine_2026-09-21_03-47-44_pid36072` passes
constant-radiance normalization and the embedded-probe escape check. Probe 220
is inactive inside a 0.8-unit opaque box with relocation disabled. Enabling
relocation moves it by (0.00385858, 0.00678910, -0.63914424), outside the box
and within its cell's half-spacing bounds, then reactivates it. The report
`gl-default-30b-escape-report.json` records no restoration failures. The actual
GPU atlas has all 16 selected interior texels equal to (0.25, 0.5, 1) at unit
emission strength, exactly double at strength two and zero when disabled.

The same session's first complete 240-sample managed-allocation window fails.
Preparation averages 44,025.6 bytes per command; composition records 10,368,
environment 4,928, hit shading 4,288, border copy 3,392, relocation and trace
1,968 each, visibility 1,616, irradiance 1,552 and ray generation 1,504.
Accepted DDGI updates continue throughout the measurement. A 12-second
EventPipe allocation trace has no dropped events and identifies boxed enum
flag checks, per-uniform allowed-type arrays, preparation-detail strings,
texture binding dictionaries/lists/records, delegates and a cold-path closure.
Source fixes target those call sites; a fresh build/session/window is required
before allocation acceptance. This is measured evidence, superseding the earlier
source-only assessment. No formal regression tests have been changed.

### Build 31c–32: presentation authority, allocation progress and BRDF defect

Builds 31c and 32 pass with zero warnings/errors. Build 31c snapshots deferred
presentation bindings and retains capture tokens for preparation retries. The
Vulkan/Advanced trace session `xrengine_2026-09-21_04-08-16_pid34120` refines the
earlier diagnosis: both final swapchain writers belong to stereo pipeline 3,
and both correctly bind its FXAA image. The eagerly published logical source
belongs to mono pipeline 2, whose unrelated clear incorrectly satisfies the
old owner-presence filter. The next correction marks the exact presentation
candidate at enqueue, carries it through recording, and selects the last marked
swapchain writer from the sealed plan. Descriptor and submission checks remain
strict. Neither Vulkan stereo combination is accepted yet.

OpenGL/Default session `xrengine_2026-09-21_04-10-13_pid31340` produced finite
DDGI before and after invalidating 84 renderer shaders; captures were visually
inspected and accepted update counts advanced from 18,715 to 18,780. This is
shader invalidation recovery, not full scene reload acceptance. The first
sealed/open visibility attempt changed a material shared by the block and floor,
making the receiver black. The fixture now gives the block its own material;
that failed attempt establishes no renderer visibility result.

Build-32 session `xrengine_2026-09-21_04-21-40_pid67696` records seven of ten DDGI
command scopes at zero bytes throughout complete 240-sample windows. Preparation,
composition and environment still average 216, 592 and 792 bytes. A 20-second
EventPipe trace has no lost events and locates the remaining allocations in
formatted diagnostic strings, shader-list enumeration and scalar asset-change
boxing. The source corrections retain notifications and enabled diagnostic
behavior. Fresh runtime measurement remains required.

The same build exposes a specular coexistence defect independently of DDGI:
with DDGI intensity zero, the metallic control has no IBL highlight despite a
captured reflection probe whose prefilter array maximum is 76.875. GPU readback
shows all RGB channels of the 512×512 BRDF lookup are zero, with no non-finite
values. The HDR image was inspected and confirms the missing reflection.
`PrecomputeBRDF` currently attempts a draw during resource creation, ignores the
draw result, and retains no retry or submission state. An owned GPU initialization
pass is being designed for both pipelines. The specular harness also exposed a
nullable-vector restoration parsing bug; its conversion now recognizes the
wrapped nullable type. No formal regression tests have been changed.

### Build 33b: restored specular lighting and remaining startup ownership work

Build 33b passes with zero warnings/errors after correcting four by-reference
marker arguments in the Vulkan presentation change. Independent source review
accepts the explicit presentation-marker invariant; the Vulkan live repeat is next.

OpenGL/Default session `xrengine_2026-09-21_04-42-03_pid680` passes specular
coexistence. The BRDF texture now has finite RGB values up to 1.0. At DDGI
intensity 1 → 0 → 1 the metallic block ROI stays exactly 0.3917451901 while the
floor changes 0.4163371307 → 0.1262091503 → 0.4167862447. The zero-intensity
DDGI texture is exactly zero. Both HDR states were visually inspected and the
harness restores all values without error. The new graphics pass owns its
producer and submission receipt instead of attempting a discarded factory draw.
Advanced forward shading now declares its previously missing BRDF texture;
Advanced native shading retains its analytic integration.

Review requires two further BRDF guards before reset/failure acceptance: an
accepted writer whose fence polling fails must be quarantined, unlike a rejected
submission which may retry; availability must also track renderer owner and the
physical texture descriptor epoch. Both corrections are source-ready for build 34.

Nine DDGI command scopes now remain at zero bytes over full 240-sample windows.
Preparation still allocates 280 bytes. A fresh 20-second trace with no lost events
identifies transient skinning dirty-state property-event arguments and a closure
created before the output-buffer replacement fast path. Cache invalidation is
moving into a non-asset runtime state with explicit revision methods; authored
properties retain normal notifications. The replacement closure is now confined
to the cold allocation helper. Fresh measurement remains required.

With the sky node explicitly disabled, the corrected single-probe enclosure
check passes: sealed indirect RGB and floor ROI are zero; opening the enclosure
produces floor ROI 0.7810825083 and maximum indirect RGB 2.5117188. The open
capture was inspected. This accepts enclosure transport, not a full mixed-probe
visibility-leak stress case. The bone and morph repeat also advances accepted
updates and changes independent depth hashes without non-finite data or restore
errors. Its original depth previews were flattened by a lighting tonemap, so the
harness now saves separately normalized depth previews for visual review.

The specular fixture additionally exposes two startup ownership defects even
though the later lighting check succeeds. IBL retries enter generic main-thread
work before an active renderer scope exists. They now use a coalesced coroutine
that waits for the original renderer. Background probe tetrahedralization also
uploads/registers buffers and rebuilds the grid from a GeneralJobs worker,
triggering owner-first wrapper and frozen-descriptor errors. Its CPU calculation
must remain background work while the complete publication cohort moves into
the requesting pipeline's render scope. No formal regression tests were changed.

### Build 34: zero-allocation DDGI commands and Vulkan BRDF initialization

Build 34 passes with zero warnings/errors. OpenGL/Default session
`xrengine_2026-09-21_04-56-58_pid59264` records zero last, average and maximum
allocation in all ten DDGI command scopes over complete 240-sample windows.
Accepted probe updates advance by 1,127 during the measurement. The counters
cover the DDGI commands, not deferred backend work outside those scopes.
`gl-default-34-ddgi-allocations.json` records no failures or restoration errors.

The bone/morph repeat passes after moving transient skinning bookkeeping into
revision state. Independently normalized depth previews were actually viewed:
the bone rotates the small cube and the morph stretches its upper vertices.
Accepted update counts advance from 1,939 to 2,016 to 2,093; depth hashes differ
and all authored values restore without errors. The report is
`gl-default-34-geometry-summary.json` in the run's MCP output folder.

The preceding Vulkan/Advanced stereo session
`xrengine_2026-09-21_04-54-16_pid41092` fails acceptance. BRDF shader-cache hits
repeat each frame, graphics pipeline key `664C68D3` remains unprepared, and a
duplicate wrapper cache key appears during producer recreation. Presentation
rejects a shader stage being regenerated, so this run does not validate the
reviewed presentation-marker correction. The BRDF helper compared physical
descriptor epochs before the producer's first native publication had settled.
Its correction defers that comparison while the receipt is pending and captures
a ready nonzero epoch after authoring/settlement. A new build/live repeat is
required; the failure remains visible rather than falling back to another backend.

Reflection-probe cache review additionally requires per-pipeline-instance state,
not only moving one upload to the render thread. Both pipeline assets currently
share buffer caches, tetrahedralization jobs and frame binding guards across
their instances. The correction is being implemented with detached CPU snapshots,
request tokens and exact-owner GPU publication. No formal tests were changed.

### Build 35: Advanced OpenGL acceptance and unresolved Vulkan startup

Build 35 passes with zero warnings/errors. The Vulkan/Advanced stereo repeat,
session `xrengine_2026-09-21_05-05-05_pid44276`, still regenerates BRDF shaders
and rejects required presentation pipelines. Deferring descriptor-epoch checks
while a receipt is pending did not fix the recreation loop. Additional bounded
diagnostics now distinguish target identity, owner, descriptor epoch and cache
clearing; an owner-identity check also precedes the pending-receipt return.
Neither Vulkan stereo path is accepted from this run.

OpenGL/Advanced build 35 passes all ten DDGI command allocation windows: zero
last/average/maximum over 240 samples each, with 393 accepted updates. The
normalization fixture's 16 selected irradiance texels read approximately
(0.248047, 0.496094, 0.984375), within two percent of (0.25, 0.5, 1) after
packed-material quantization. Strength two doubles the result; strength zero
clears it. Probe 220 also changes from inactive inside the cube to active with
offset (-0.0021523, 0.0081490, -0.6391690), outside the enclosure and within
the cell's half-spacing bounds.

The Advanced single-probe enclosure has sealed floor ROI zero and open ROI
0.8034276872. Both captures were viewed; the open room is lit and the sealed
image is black. Separately normalized baseline, bone-only and morph-only depth
images were viewed and show the expected geometric changes. All four harnesses
report no restoration errors. Evidence is stored under the run's
`gl-advanced-35-*` report/capture names. These checks still do not establish the
mixed-probe visibility-leak stress case or Advanced specular coexistence.

### Build 36b: Vulkan lifetime diagnosis and mixed-probe visibility correction

Build 36b passes with zero warnings/errors after correcting the diagnostic
helper's nullability/texture-extent compile errors. Vulkan/Advanced session
`xrengine_2026-09-21_05-14-23_pid16760` records repeated `DescriptorEpoch`
resets with unchanged target, extent and owner, an unready descriptor, and a
failed/abandoned preceding submission. Retrying destroyed/recreated the producer.
Review finds that per-program Vulkan shader removal also destroys an owner-global
`VkShader`, invalidating stages shared by the other pipeline instance. The
source correction retains BRDF quad/material/FBO across settled descriptor
retries and only unsubscribes the removed program's shader reference. Poll-failed
work remains quarantined until a proven ready backing-epoch replacement or an
owner/cache/target boundary. These changes await build/live acceptance.

The new mixed-probe fixture uses 48 probes on both sides of a black partition,
with emission only on the left and no ambient/direct/sky lighting. Receivers lie
inside the volume fade bounds, and the partition extends beyond the grid so
outside probes cannot gather light around its ends. OpenGL/Advanced's blocked
floor ROI is 0.09843155 with the original filter. Distance-moment generation
incorrectly used the broad authored Chebyshev contrast exponent as its angular
filter. It now uses a separate fixed exponent of 50, consistent with the
independent distance-filter parameter in the
[RTXGI volume descriptor](https://github.com/NVIDIAGameWorks/RTXGI-DDGI/blob/main/rtxgi-sdk/include/rtxgi/ddgi/DDGIVolume.h).

An initial invalidation-only repeat retained the old source fingerprint and is
not accepted as evidence of the change. A fresh named OpenGL/Advanced session
loads the changed shader: fingerprint `1b129ca1...` replaces `c6817a84...`.
Its blocked/lit/open floor ROIs are 0.00392157 / 0.49254425 / 0.30544848.
OpenGL/Default also passes at 0.00562275 / 0.48339217 / 0.38857281. Sealed and
open captures were viewed in both cases; all are finite and restore without
errors. The sparse fixture shows expected coarse spatial detail but no bright
bleed through the central partition. All 18 expanded DDGI shaders compile.

Source now marks new bakes as algorithm revision 2 without changing payload
layout. Version 1 loads fail with a rebake instruction, since saved broad
moments cannot be repaired without tracing the original scene. No tracked or
nonignored `.ddgi` assets exist in the repository and no existing files were
rewritten. Fresh roundtrip and obsolete-version refusal need the next build.

OpenGL/Default's full scene unload/reattach passes in the next isolated session:
accepted updates advance 3,349 → 3,456, output maxima stay approximately
0.2430 / 0.2413, and both blue-emissive room captures were visually inspected.
Report: `gl-default-36b-scene-reload-report.json`. This exercises scene lifecycle,
not YAML deserialization. A separate renderer restart in session
`xrengine_2026-09-21_05-26-13_pid35832` times out after 60 seconds, with retired
generation 0 errors across DDGI and other passes; renderer replacement remains
unaccepted and the named session was stopped. No formal tests were changed.

OpenGL/Advanced also passes scene unload/reattachment: accepted updates advance
9,121 → 9,239 and output maxima remain approximately 0.2267 / 0.2472. Both
captures were viewed and retain the blue emissive room lighting, with finite
values and no restoration errors. Report:
`gl-advanced-36b-scene-reload-report.json`. This closes the scene attachment
check on both OpenGL pipelines while Vulkan and renderer replacement remain open.

The restart log's first teardown failure is now isolated:
`GLFrameBuffer.PreDeleted → XRFrameBuffer.DetachTargets →
GLTexture.TryResolveAttachIds → GetOrCreateAPIRenderObject` attempts wrapper
creation after `BeginBackendRetirement`. The source correction detaches native
attachment slots directly with the framebuffer's existing ID and owning API,
avoiding logical broadcasts while retaining the explicit NVIDIA detach workaround.
This awaits the next build and restart repeat; no recovery acceptance is claimed.

### Build 37b: probe ownership implemented; Vulkan stereo consumer missing

Build 37b passes with zero warnings/errors after adding the missing static import
to the new Advanced probe partial. Both pipelines now use per-instance forward
probe state, detached CPU topology requests, weak result mailboxes and exact-owner
render-thread publication with staged resources. Final source review finds no
remaining P1/P2 defects. Eight-probe live acceptance remains pending.

Normal Vulkan/Advanced stereo session
`xrengine_2026-09-21_05-51-43_pid55856` no longer recreates BRDF producers each
frame. PresentNow is ready and frame outcomes complete. Layer 0 DDGI is finite,
with maximum 0.251221 and mean 0.00359902, but HDR contains only the emitter
(maximum 8, mean 0.00600029), and FXAA is zero. All three captures were viewed.
This is progress past submission rejection, not accepted stereo composition.

The RenderDoc follow-up, `vk-advanced-37b-stereo_frame430.rdc`, reproduces the
failure in the capture configuration. Stereo DDGI image 6216 is written at event
1245 and has only barriers at 1238/1253; there is no later sampled consumer.
The stereo composition and post-processing FBO draws are absent, while the mono
viewport has those draws. The exported DDGI image contains the blue room and
the exported swapchain at 1464 is black. Logs also report two-layer views being
clamped to one-layer backing images. The next step is to identify why stereo
quad consumers are omitted and then repeat normal, non-capture presentation.

Both OpenGL pipelines pass the revision 2 bake roundtrip and recovery checks in
build 37b. Default's frozen/source-off/recovered mean is exactly 0.00576406;
Advanced's is exactly 0.00605936. Binary headers contain version 2, saved assets
roundtrip, turning the emitter off does not change frozen output, and loading
dark, obsolete version 1 or missing assets gives zero lighting. Valid-path
recovery restores the same captured pixel hash. Lit, rejected and recovered
captures were viewed; both restoration reports are empty. Reports are
`gl-default-37b-bake-v2-report.json` and `gl-advanced-37b-bake-v2-report.json`.
Vulkan repeats remain pending.

The native framebuffer teardown correction allows OpenGL/Default's restart to
succeed in less than two seconds with no rollback. The next capture exposes a
separate DDGI lifetime defect: pipeline instance 2 and resource generation 2
survive replacement, accepted updates advance 3,814 → 3,945, but DDGI and its
irradiance atlas stay black while raster emission remains at maximum 8. The
frame context and generated geometry still trusted the old API owner's GPU
contents. They now invalidate on exact API-owner replacement, before resolving
old completion receipts or reusing generated geometry/material images. This
source correction awaits review and build 38/live validation; successful API
restart alone is not recovery acceptance. No formal tests were changed.

Build 38 passes with zero warnings/errors, but the next restart repeat fails
with `XRShader cannot be wrapped before CPU construction is published` in
`GpuDdgiGeometryService.EnsureProgramsReady`. Geometry, per-mesh BVH and BVH-tree
teardown destroy borrowed `ShaderHelper` assets, so rebuilding returns destroyed
cached shader objects. Source now destroys only owned programs and releases
shared shader references. Review also identifies post-retirement OpenGL fence
disposal reopening the retired native API, and timing state retaining old-owner
queries indefinitely. An intrusive list now tracks live GL fences without
per-frame allocations so native syncs retire before API disposal; late disposal
is managed-only. DDGI timing recreates its logical query ring on retired-owner
reset while preserving the guard against another live renderer. These fixes
await review and build 39/live validation.

Build 39 passes with zero warnings/errors. OpenGL/Default now passes dynamic
renderer replacement with GPU timing enabled: updates advance 475 → 604,
measured duration remains valid at 0.484352 → 0.515072 ms, and both viewed
captures show the emissive room (finite maxima 0.30249 / 0.21777). A subsequent
baked restart retains exactly the same pixel hash and mean 0.0057640644, with
completed updates frozen at 1,300. Its post-restart capture was viewed and
restoration is clean. Targeted log searches find no repeated retired-renderer,
destroyed-shader or timing errors. Reports use `gl-default-39-*restart` names.

OpenGL/Advanced's repeat fails before DDGI resumes. Session
`xrengine_2026-09-21_06-22-06_pid13428` reports five texture deletion failures
because Advanced bindless sampler-pair leases remain active. Later cleanup also
calls `GenericToAPI` for an already-retired Advanced program. Source now adds a
required cleanup step after the GPU-idle boundary but before wrapper retirement,
releases the Advanced output registry and bindless leases there, prohibits dirty
texture regeneration during retirement, and uses noncreating program lookup.
That separate correction awaits build 40/live acceptance.

The build 39 Vulkan/Advanced stereo trace again contains only mono
DDGI-composite/bloom/post-processing draws, with no stereo variants or recorded
draw failures. The next diagnostic exposes eager quad preparation failures.
Source review also identifies descriptor prewarming outside the queued mesh
request's exact resource-planner scope, which can resolve a stereo texture name
against a mono generation. That hypothesis and the pipeline-type-name multiview
heuristic require a scoped correction and fresh normal stereo validation.

Build 40b passes with zero warnings/errors after resolving the new MCP action's
namespace imports and a nullable diagnostic argument. Its Advanced teardown
change also rechecks orphan-only shutdown after the GPU wait, before any native
cleanup. Dynamic and baked Advanced restart acceptance remains pending.

Vulkan/Advanced stereo session `xrengine_2026-09-21_06-36-23_pid18332` has no
`QuadPrepare` failures with draw tracing enabled; eager fullscreen program and
buffer preparation succeeds. Its stereo FXAA layer remains black. The remaining
draw loss is in queued materialization, where descriptor preparation currently
uses the ambient planner generation instead of the queued request's exact
generation. The scoped resource-binding correction and topology classification
are in progress. The new `clear_render_pipeline_cache` MCP action enables the
pending eight-probe publication/recovery fixture. No formal tests were changed.

OpenGL/Advanced build 40b session `xrengine_2026-09-21_06-42-18_pid65756` passes
both restart checks without rollback. Dynamic updates advance 502 → 634 and
hardware timing remains valid at 0.473088 → 0.461824 ms. Both viewed captures
show coherent indirect illumination, with means 0.00596110 and 0.00595949.
Loading the existing revision 2 bake and restarting again preserves the exact
pixel hash `9F78239BBA5C2035E21A69BB3C5C22E071DE9CB03CB0E399EBFE734EED69E393`,
mean 0.0060593644 and frozen count 2,156. The recovered baked image was viewed.
Both restoration reports are empty; targeted logs contain no active lease,
retired-renderer or unpublished-shader errors. Reports use
`gl-advanced-40b-*restart` names. Renderer replacement is now accepted for both
OpenGL pipelines; Vulkan remains pending.

The eight-probe OpenGL/Advanced fixture in build 40b fails before topology can
publish. Session `xrengine_2026-09-21_06-44-22_pid69408` remains at capture 1/8
with all CaptureVersion values zero and no usable IBL textures. The rendering
log repeatedly reports `Exact offscreen output authoring failed` for Advanced
instance 3 because its output reservation is not current for the renderer
generation. The harness times out and preserves its report as
`gl-advanced-40b-probegrid-probe-topology.json`. Investigate the capture output
reservation identity before claiming eight-probe or specular acceptance. Probe
publication markers are emitted in `log_lighting.log`, so subsequent harness
runs must read that file rather than `log_rendering.log`.

The Default viewport repeat (`xrengine_2026-09-21_06-49-51_pid57720`) uses the
same Advanced instance 3 for probe capture and hits the same reservation failure.
It was stopped after reproducing that diagnostic, before the harness timeout;
the harness records the resulting endpoint closure. No probe acceptance is claimed.

Build 41 passes with zero warnings/errors. Exact queued mesh planner-generation
scopes and bounded cached envelopes pass review, as do actual-output multiview
classification, shader-specific mesh topology and mono utility bin isolation.
The normal Vulkan/Advanced stereo repeat
`xrengine_2026-09-21_06-52-46_pid36040` still has black FXAA layer 0 while frames
complete. This correction alone does not explain all missing consumers. Add
successful preparation/enqueue tracing to establish the loss point, and extend
exact context scopes to native framebuffer preparation before validating its
completed target topology. The preserved summary is
`mcp-output/vk-advanced-41-stereo-summary.json`. The DDGI and black FXAA images
were viewed; the live trace still contains only mono fullscreen draws. The earlier
two-layer/one-layer backing warnings no longer appear in this run.

Build 42 passes with zero warnings/errors. Target preparation now captures one
planner generation per replan attempt and enters each operation's exact scope
before generating or refreshing its framebuffer. Mesh ingress validates the
shader's multiview mode against the completed native target view mask.

Normal Vulkan/Advanced stereo session `xrengine_2026-09-21_07-00-48_pid24176`
passes both eyes through DDGI-only and regular composition. The new producer
trace shows stereo quads prepared and scheduled; native draw traces now contain
DDGICompositeStereo, BloomCopyStereo and subsequent stereo post-processing.
DDGI-only FXAA maxima are 0.443115 and 0.418457. Regular HDR preserves the emitter
at maximum 8, with means 0.00958757 / 0.00946925; final FXAA means are
0.0711511 / 0.0712825. Both final images were viewed and show the lit room with
the expected eye displacement. Eye depth hashes differ, all samples are finite,
and restoration warnings are empty. Evidence:
`mcp-output/vk-advanced-42-stereo-summary.json`. Default stereo and independent
viewport ownership remain to be repeated.

Build 42's successful captures do not establish sustained presentation. At
07:01:48 the same Vulkan session rejects pass 100072 because its multiview
presenter has a null captured target and scheduling view mask 1. Submission then
remains backpressured and DDGI freezes at 89 updates. The subsequent ownership
harness times out after reactivation, but the stall precedes the toggle; it is
not evidence that activation caused the failure. Restoration reports no errors.
Evidence: `reports/vk-advanced-42-ownership-report.json` and the session's
`log_vulkan.log` first topology-mismatch diagnostic.

Source review identifies an authored layered OutputFBO that is never bound by
Advanced's final present command. Shader selection reads that output, while
Vulkan's queued target capture sees no bound framebuffer. Bind the exact output
and retain its logical render-target scope through draw capture. A separate
review correction now uses one immutable planner root across the complete
PresentNow/explicit attempt; both changes require build and live validation.

OpenGL/Advanced build 42's exact capture-owner diagnostics report capture
instance 3, resource generation 3, rejected binding and no current reservation.
The offscreen profile is null despite all 199 resource specifications being
realized. Global OpenGL capability discovery can incorrectly select Advanced
before an output reservation succeeds, and binding failure handling overwrites
the original reason with the generic generation message. Correct that boundary
and preserve the original failure before repeating eight-probe acceptance.
Evidence: `mcp-output/gl-advanced-42-probe-binding.json`.

The probe property snapshot further pins the cause: `UseAdvancedCapturePipeline`
is false. Plain `OffscreenCapture` intentionally has no Advanced intent, so the
resolver never requests a reservation. Global GL capability promotion selected
Advanced anyway. The output and stage identities match; neither a face-identity
mismatch nor reservation-bank exhaustion explains this failure. Selection now
starts without a shader family and promotes only an exact successful reservation;
the follow-up returns Default directly for a plain capture under Required mode.

Build 43 passes with zero warnings/errors. Final present binding, shared planner
roots and common cold/cached topology validation pass source review. Vulkan
session `xrengine_2026-09-21_07-17-00_pid56324` no longer reports the target
topology mismatch. However, the stereo warmup harness fails because the final
presentation source repeatedly has an undefined layout and no published native
descriptor set. Recording defers and recreates the swapchain. The last harness
snapshot reports 103 updates with a failed submission, so no stereo acceptance
is claimed. Trace actual final-source ownership and descriptor observation next.
Evidence: `mcp-output/vk-advanced-43-stereo-summary.json` and the session's first
`final presentation source epoch ... is incomplete` diagnostic.

Build 44 passes with zero warnings/errors. OpenGL/Advanced session
`xrengine_2026-09-21_07-21-21_pid40196` completes all eight probe captures, taking
6,637.75 ms for the batch. The old reservation rejection is gone. The publication
marker reports instance 2, generation 2, eight probes, zero tetrahedra and a grid
buffer. The first topology harness also exposed a PowerShell collection-shape
error; that scratch harness was corrected before further acceptance.

Viewed initial captures show a coherent room, a populated BRDF lookup (maximum 1)
and populated reflection texture (maximum 10.6328). The specular repeat still
fails: a pure metallic block's ROI is exactly zero at DDGI intensity 1, 0 and 1,
while the diffuse floor means are 0.418101, 0.047910 and 0.417367. All three
captures were viewed and restoration is clean. Determine whether the empty
tetrahedral topology or Advanced sampling causes this failure, then repeat cache
recreation and specular acceptance. Reports/captures use `gl-advanced-44-*` names.

Manual cache clearing confirms a second probe defect: instance 2 republishes at
generation 3 (request 1998), with healthy DDGI updates and BRDF maximum 1, but
`LightProbePrefilterArray` is entirely zero. Direct capture of the first probe's
own `PrefilterTexture` is also zero, so the failure is not confined to destination
array sampling. The black packed texture was viewed. Investigate borrowed source
texture lifetime and regeneration across cache teardown. Evidence uses
`gl-advanced-44-probe-after-cache-*` and `gl-advanced-44-probe-source-after-cache`.

Source review confirms that zero tetrahedra is not a regular-grid fast path.
The shader falls back to at most four nearest probes. A co-spherical cube can
produce an empty MIConvexHull result; the exact lattice requires deterministic
nondegenerate tetrahedra. That correction is now being implemented separately.

OpenGL/Default build 44 session `xrengine_2026-09-21_07-29-29_pid31256` completes
all eight captures in 6,182.43 ms and also publishes zero tetrahedra. Unlike
Advanced, specular coexistence passes: the metal ROI stays exactly 0.5765996893
at DDGI intensity 1/0/1; floor means are 0.377780, 0.124810 and 0.355848. The lit
and zero-DDGI images were viewed and show the preserved reflection. Restoration
is clean. Cache clearing republishes generation 2 → 3, token 2048 → 2051, and
retains the packed reflection maximum 3.6113281 and BRDF maximum 1. That reflection
capture was viewed. These comparisons isolate the black specular/cache defect
to Advanced; neither accepts the empty eight-probe topology. Evidence uses
`gl-default-44-specular` and `gl-default-44-probe-after-cache-*` names.

Build 45b passes with zero warnings/errors after correcting the fallback's out-
parameter assignment. The complete co-spherical Cartesian lattice now receives
six nondegenerate tetrahedra per cell without perturbing probe positions. Source
review approves index mapping, winding and shared-face consistency.
OpenGL/Default session `xrengine_2026-09-21_07-37-53_pid67220` publishes eight
probes and six tetrahedra for instance 2, generation 2, request 2051. Cache clearing
republishes generation 3, request 2054 with the same probe/tetrahedron counts.
The BRDF and reflection texture pixel hashes are identical before/after clearing;
maximum values remain 1 and 3.6113281. Both HDR captures and the initial BRDF and
reflection images were viewed. No harness failure is reported. Evidence:
`reports/gl-default-45b-probegrid-probe-topology.json`.
Advanced's native opaque IBL uses its own probe records, so the legacy tetrahedral
fix does not by itself explain or resolve the pre-cache black metal. The cache
failure has a separate candidate: `GLTexture` treats all XR mutations as frozen
native parameter changes and recreates the image when Advanced releases its last
sampler-pair lease. Add bounded diagnostics to identify the exact mutation before
changing the lifetime behavior.

Build 46 passes with zero warnings/errors and adds bounded GL/Vulkan diagnostics.
OpenGL/Advanced session `xrengine_2026-09-21_07-48-50_pid34276` publishes the
correct eight-probe/six-tetrahedron topology before and after cache clearing,
but the packed reflection image again becomes zero after clearing. Both the
initial reflection and black republished image were viewed.
The GL trace identifies the root cause exactly: all probe images become dirty
on the `SourceAsset` metadata notification, while an Advanced sampler-pair lease
still exists. At the last lease release, `PrepareForBindlessHandle` destroys and
recreates the GPU image. This first occurs at 07:49:38, before the initial capture,
then repeats at 07:49:42 on cache clearing. The initial packed array was copied
before the source erasure, explaining why that array was nonzero while native
Advanced shading sampled black probe-owned images. The correction excludes
known asset/binding metadata from native texture-parameter invalidation; actual
sampler/storage changes and the existing lease guards remain strict. Build/live
acceptance of that correction is pending. Evidence:
`reports/gl-advanced-46-probegrid-probe-topology.json` and `[GLBindlessMutation]`
entries in the named session's `log_opengl.log`.

Vulkan/Advanced build 46 session `xrengine_2026-09-21_07-51-01_pid28920` again
fails sustained stereo warmup. The trace now distinguishes the failure: accepted
final-draw marker and frozen `SourceTexture` match, and descriptor binding runs,
but final-source selection re-resolves that logical texture to a different native
image/view from the accepted draw's descriptor. `Observe` correctly rejects the
native mismatch and leaves the binding unpublished. The reviewed correction is
to select exact logical ownership first, then establish native image/view/sampler
authority from that accepted draw's descriptor observation. Keep subsequent
native, resource-generation and epoch checks strict. No stereo acceptance is
claimed for build 46. Evidence: `mcp-output/vk-advanced-46-stereo-summary.json`
and the named session's first final-source mismatch/incomplete diagnostics.

Build 47 passes with zero warnings/errors. The metadata guard passes source
review without weakening native-state or lease validation. OpenGL/Advanced
session `xrengine_2026-09-21_07-53-57_pid30180` completes all eight captures in
6,716.30 ms and publishes six tetrahedra at generation 2/request 2004. Cache clear
republishes generation 3/request 2007 with the same topology and exact BRDF and
packed-reflection float hashes. A direct post-clear capture of the probe source
has the same reflection hash, maximum 247.625 and mean 1.1641912. No
`GLBindlessMutation` event occurs in this diagnostic-enabled session. Initial and
republished HDR/reflection images were viewed.
Specular coexistence also passes: the metallic ROI is exactly 0.6323081792 at
DDGI intensity 1/0/1, while the diffuse-floor means are 0.373968, 0.195581 and
0.405278. DDGI is exactly zero at intensity 0. All three composition captures
were viewed; reflection persists while diffuse GI changes. Data is finite and
restoration reports no errors. The shader/record sampling path needed no change.
Evidence: `reports/gl-advanced-47-probegrid-probe-topology.json`,
`reports/gl-advanced-47-specular-report.json` and
`mcp-output/gl-advanced-47-probe-source-after-cache.json`.

A nearby diagnostic defect was also identified in the build 47 log before DDGI
initialization: `SetupDebug` passes explicit IDs with `DontCare` source/type to
`DebugMessageControl`, which is invalid. The resulting startup GL error can also
be consumed by the next cached-program load. The filter now names API/performance
explicitly; the existing callback continues filtering other known non-error
notifications and never suppresses actual API errors. The
[Khronos reference](https://registry.khronos.org/OpenGL-Refpages/gl4/html/glDebugMessageControl.xhtml)
requires concrete source/type when the ID count is nonzero. Build/live validation
of this small diagnostic correction follows with build 48.

The build 48 Vulkan source-authority change passes independent review. The
accepted marker and selected tuple retain the exact deferred publisher reference
and publication token. Only that draw may promote its resolved native descriptor,
preventing another mono/stereo draw using the same logical texture from winning
first observation. Existing context, native-generation, descriptor-slot, epoch
and submission-owner checks remain strict. Runtime acceptance is still pending.

The interrupted-update review found a separate lifetime defect. `Complete`
previously marked its stage complete before acquiring a submission receipt;
failure then called an abort path that ignored the complete stage. The stage
assignment now follows successful receipt acquisition. More broadly, partial
updates had no GPU-write receipt at all. Vulkan DDGI dynamic buffers use
host-visible memory, and baked uploads write that allocation directly, so wrapper
retention alone cannot prevent an upload from racing earlier partial dispatches.
The next correction must retain an ordered non-publishing abort receipt, block
overwrites until its GPU use finishes, and never count the aborted cycle as a
completed update. A development-only exact-instance/generation stage-interruption
control will supply deterministic live evidence; ordinary component toggles do
not reliably interrupt between DDGI passes. No runtime acceptance is claimed yet.

Build 48 passes with zero warnings/errors. Vulkan/Advanced session
`xrengine_2026-09-21_08-08-40_pid64804` passes sustained stereo through FXAA.
Both final eye images were viewed and show the emissive room with different eye
views; final means are 0.08933114 and 0.0889683, and normal HDR retains the
strength-8 emitter. All captured samples are finite. After fixture restoration,
accepted updates advance 380 → 411, closing the prior presentation stall.
Desktop/stereo ownership also passes: distinct instances 2/3 advance
774 → 812 and 775 → 842. Disabling the node leaves desktop updates at 845;
reactivation reaches 858 with geometry ready and generation 4. No harness or
restoration errors occur. The steady-state Vulkan log has no error, VUID,
incomplete-source or native-mismatch match in this run; it still reports use of
the full sealed-submission validator, which is not a failed submission.
Evidence: `mcp-output/vk-advanced-48-stereo-summary.json` and
`reports/vk-advanced-48-ownership-report.json`. Vulkan/Default repeats follow.

Vulkan/Default build 48 session `xrengine_2026-09-21_08-11-31_pid71152` fails
stereo warmup with zero accepted updates. The first causal rejection is frame 35,
`FramePlanSeal`: `BloomMip0FBO` has target view mask 3, while its prepared
`FullscreenQuad:Material` draw has `contextMultiview=False` (pass 100050,
pipeline instance 3). Subsequent final-source readiness warnings and full mesh
queues follow this rejected frame; they do not disprove the accepted-descriptor
authority fix that passed on Advanced. No Default stereo/ownership acceptance is
claimed. Correct the bloom draw's stereo context, then repeat. Evidence:
`mcp-output/vk-default-48-stereo-summary.json` and the named session logs.

Source review isolates that Default mismatch to `VPRC_BloomPass`: multiview was
gated by both `Stereo` and the Advanced pipeline family, despite Default already
selecting stereo bloom shaders and requiring a two-layer target. The constructor
now uses `useMultiview: Stereo` consistently for all bloom mip framebuffers.
Build/live validation of this small correction is pending.

Vulkan/Advanced build 48 eight-probe session
`xrengine_2026-09-21_08-14-54_pid62344` completes all eight captures in 8,307.04 ms
and publishes instance 2/generation 2/request 8 with eight probes and six
tetrahedra. The topology harness then fails because the BRDF lookup is exactly
zero. A separate later capture confirms it stays zero; this is not accepted as
mere startup delay. The packed reflection array is finite and nonzero (maximum
64.8125, mean 0.43962482). The black BRDF and nonzero reflection images were
viewed. Inspect the BRDF producer's accepted draw, target backing and resource
lifetime next, using a GPU capture if logs cannot identify the failed write.
Specular/cache recovery remains open. Evidence:
`reports/vk-advanced-48-probegrid-probe-topology.json`,
`mcp-output/vk-advanced-48-probegrid-capture-initial.json` and
`mcp-output/vk-advanced-48-probegrid-brdf-repeat.json`.

The user requested a wrap-up at this point. Stop the named editor, preserve all
working-copy changes, and resume from these concrete remaining defects rather
than repeating accepted OpenGL and Vulkan/Advanced stereo work. No test changes
were made during this continuation.

Wrap-up build 49b succeeds with zero warnings/errors in 50.93 seconds. It contains
the Default stereo bloom correction and reviewed non-publishing DDGI abort
receipts. The first build 49 attempt caught nullable/out-parameter issues in the
new cold diagnostic helper; those are corrected. Final source review also
corrected the published-build guard and correlated each diagnostic skipped cycle
with its own abort and receipt, including unavailable-receipt failures. This
prevents a pre-arm normal receipt from being reported as an interrupted update
or a skipped update from being reported as successful recovery.

New development-only MCP controls are `arm_ddgi_visibility_interruption` and
`get_ddgi_visibility_interruption`, with strict viewport selection and bounded
`skip_count` (1–120). The arm captures the exact pipeline/resource generation;
cache clearing cancels it. No live interruption run has been performed and no
published-configuration build is claimed. Resume with Default Vulkan stereo,
the zero Vulkan BRDF producer, and the interruption recovery matrix. The named
editor is stopped, agents are finished, and all changes remain uncommitted.
