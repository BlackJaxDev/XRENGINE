# Per-request cost after play round trips (S14g)

Status (October 3, 2026):

- The cause of the per-request rise is identified and corrected: snapshot
  restore attached the material surface-emission publisher to every material.
- The main view drew no scene geometry after a play round trip. This was found
  during validation, opened as S14h, traced to restored meshes without their CPU
  vertex array, and corrected.
- With both corrections the S14g motion criterion is **met** (98-104% of before
  play after three round trips).
- The device-memory growth was the Vulkan resource planner keeping every
  superseded resource generation's full image/buffer set alive (616 MB of images
  plus about 200 MB of buffers per transition). It is corrected, and the memory
  criterion is now **met**: memory is flat from the first to the third round
  trip.

Owner item: [S14g](../../progress/rendering/vulkan-stall-remediation-results.md)
of the Vulkan stall remediation TODO. Opened by the
[S14d disposition](2026-09-27-s14d-post-exit-publication.md#disposition), which
saw mesh requests cost 24 us before play and 95 us after, attributed by sampled
traces to monitor slow paths in texture descriptor getters.

## Workload and method

- Fixture, session and camera motion as in the
  [S15a record](2026-10-03-motion-fps-shadow-recording.md#workload-and-method):
  Sponza, one shadow-casting directional light, Vulkan, Advanced pipeline,
  CpuDirect, Release isolated MCP session, 15 s windows of chained eased moves
  between camera A and camera B, fresh frames from
  `frame_lifecycle.outcome_counts.completed`.
- Round trip: `enter_play_mode`, 20 s, `exit_play_mode`, 20 s, then the next
  motion window.
- Runtime counters (`dotnet-counters`, `System.Runtime`) ran for the length of
  each motion window: lock contentions, GC pause time, allocated bytes, CPU time.
- Material state was read through `get_object_properties` on the first 60 mesh
  renderers' materials (58 opaque, 2 masked).

## Measurement before any change

| Window | Fresh FPS | Recording ms/frame | Operation loop ms/frame | Hole materialization per request | Device-local MB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Before play | 40.3 | 15.1 | 8.4 | 21 us | 1,254 |
| After trip 1 | 10.6 | 55.8 | 41.7 | 143 us | 2,492 |
| After trip 2 | 12.9 | 40.4 | 31.8 | 142 us | 3,206 |
| After trip 3 | 12.8 | 40.2 | 31.6 | 145 us | 3,921 |

The same shape on the HEAD Vulkan binary (33.5 before, 9.6 and 11.6 after two
trips) shows that it predates the October 3 Vulkan changes. The work count does
not change (about 170 generic mesh draws and 86 hole requests per frame before
and after); every draw costs more.

### Where it is not

Monitor lock contentions were 4, 0, 1 and 2 per 14 s window: there is no lock
contention before or after play. The S14d attribution to monitor slow paths was
sample-profiler safe-point bias (the same bias is described in the S15a record).

### Where it is

Allocation rose from 2.2 to 15 MB per frame. Gauges of the last directional
cascade refresh frame (396 binding snapshots in both):

| Gauge per refresh frame | Before play | After one round trip |
| --- | ---: | ---: |
| Binding-snapshot entries | 28,880 | 286,654 |
| Reflected uniform name lookups | 25,601 | 221,263 |
| Legacy auto-uniform full-block bytes | 454,736 | 2,534,096 |
| Fallback storage-buffer descriptors | 2,425 | 29,525 |
| Fallback sampled-image descriptors | 1,164 | 14,172 |
| Legacy auto-uniform fallback draws | 820 | 1,181 |

Mesh descriptor allocation variants rose from 442 to 1,166 and sets from 2,530
to 9,760 (723 new variants of 10 sets, the shape of the per-material
geometry-shader cascade variant). Every caster had moved off the shared opaque
shadow-caster material onto its material's own cascade variant, which carries
the full Uber fragment program and its bindings.

`XRMaterial.CanUseSharedOpaqueShadowMaterial` refuses any material with a
`SettingUniforms` handler. The material read:

| State | Opaque, no handler | Masked, no handler | Opaque, handler | Masked, handler |
| --- | ---: | ---: | ---: | ---: |
| Before play | 58 | 2 | 0 | 0 |
| During play | 0 | 0 | 58 | 2 |
| After exit | 0 | 0 | 58 | 2 |

Material identity was preserved (60 of 60). Entry builds the play world from a
restored snapshot copy, and the restore writes material properties through their
setters. The `EmissiveColor` and `EmissionStrength` setters attached the
surface-emission uniform publisher on every assignment, including an unchanged
null, and `SurfaceTextureBindings` attached it whenever a new array instance
(also an empty one) was assigned. The importer never writes those properties
for these OBJ materials, so before play no material had a handler.

## Change (retained)

`XRMaterial.EnsureSurfaceEmissionPublisher` attaches the publisher only when the
material carries surface-emission state (an emissive colour, an emission
strength or an emissive surface texture). Without that state the publisher would
write exactly the shader's declared defaults (`SurfaceEmissionMode` 0 selects
the legacy path), so nothing it published could change shading; materials with
emission state attach it as before.

| Window | Fresh FPS | Recording ms/frame | Operation loop ms/frame | Hole materialization per request | Device-local MB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Before play | 40.4 | 15.1 | 8.5 | 20 us | 1,254 |
| After trip 1 | 45.0 | 12.9 | 8.0 | 21 us | 2,492 |
| After trip 2 | 47.8 | 11.6 | 7.1 | 21 us | 3,206 |
| After trip 3 | 48.4 | 11.5 | 7.0 | 21 us | 3,921 |

The material read shows 58 opaque and 2 masked materials without a handler
before, during and after play. Before-play images are unaffected by
construction (no setter runs before play) and the view without sky (`floor`) is
pixel-identical to the build without the change.

## Why the gate is not validated: the scene is missing after a round trip

Image checks after a round trip show only the environment map in every view, in
every build tried: the HEAD Vulkan binary, the October 3 Vulkan binary, and both
with and without the material change. Minutes later the scene is still missing.
The Advanced stages are accepted every frame and the GPU scene still holds 396
resident commands; directional casters are still recorded. But
`query_advanced_pick` misses at the centre of a view that faces a Sponza wall
(database epoch 4), and in an unchanged build the per-frame
`advanced_early_visibility_barrier_emissions` counter stops advancing after
play. The post-play frame rate above therefore measures a main view without
scene geometry, and cannot be compared with before play. This defect is
opened as S14h; S14g's gate waits for it.

## S14h: restored meshes had no vertex array (fixed)

The play world (on entry) and the edit world (on exit) are rebuilt from a cooked
snapshot. Meshes without a file path are written inline and read back as new
`XRMesh` objects. `XRMesh.ReadMeshPayload` restored triangles, vertex count and
buffers, but not the CPU `Vertices` array. Only the `Buffers` setter
(`OnBuffersAssigned`) rebuilt that array, and the cooked reader fills buffers in
place without the setter. Read back on the live session after a round trip,
Sponza mesh 0 had `VertexCount` 3,395, 3,640 triangles and its four vertex
buffers, but 0 vertices.

The canonical scene publisher validates `Vertices.Length == VertexCount` and
packs geometry from `Vertices`, so it marked every command unsupported. It then
committed a valid publication with no draws, without a diagnostic. The legacy
GPU scene atlas and the per-renderer path read the buffers, which is why
resident commands and shadow casters were unaffected.

Change (retained): the array rebuild moved into
`XRMesh.TryRebuildVerticesFromBuffers`, which runs both after buffer assignment
(as before) and at the end of the cooked mesh read. Results:

- `query_advanced_pick` at the centre of the wall-facing view hits before play,
  during play and after exit (database epochs 2, 3 and 4). Before the change it
  hit only before play.
- After three round trips the fixed views match before play: mean absolute
  difference 1.6 (`inside`), 0.2 (`atrium`) and 0.0 (`floor`) per 255. In play
  mode they match within 1.9. `camA` differs only in its sky, and its scene
  region matches.

## S14g gate rerun with both corrections

| Window | Fresh FPS | Device-local MB |
| --- | ---: | ---: |
| Before play | 39.2 | 1,254 |
| After trip 1 | 39.7 | 2,492 |
| After trip 2 | 40.8 | 3,206 |
| After trip 3 | 38.3 | 3,921 |

Motion after three round trips is 98% of before play (criterion: at least 90%).
Device-local memory after the third trip is 57% above after the first
(criterion: within 10%), so the gate is not passed.

## Device memory (second S14g question, partly attributed)

Device-local memory grows by 1,237 MB at the first round trip and by 715 MB at
each later one. The figures are byte-identical with and without both
corrections. Active allocations go from 2,282 to about 4,400 to 4,500. Live
Vulkan owner groups across two round trips show:

- First trip: a second Advanced output-reservation bank becomes active (managed
  activation 238 to 480 MB); the first bank stays allocated while idle.
- First trip: every restored mesh gets its own vertex, index and pipeline
  objects, about 394 each for position, normal, tangent, texcoord and triangle
  buffers, plus 402 graphics pipelines and 395 layouts. This happens while the
  original imported meshes keep theirs. The snapshot restore leaves the original
  imported content alive and only destroys copies made by earlier restores.
- Second trip: owner counts barely change (+14 `AdvancedGeometry.ResidentCache`
  buffers, +8 late depth-pyramid views, a few texture views), yet device-local
  memory grows by 715 MB. The bytes therefore sit in a few large allocations.
  Owner counts cannot name them.

### The planner keeps a full resource set per round trip (October 3, later)

`list_vulkan_image_allocation_diagnostics` reports every live image with its
size and source, and the allocator's buffer owners with their bytes. Across three
round trips on a fresh session of the corrected build:

| Snapshot | Device-local MB | Allocator MB | Planner images | Planner image MB |
| --- | ---: | ---: | ---: | ---: |
| Before play | 1,254 | 1,641 | 56 | 616 |
| After trip 1 | 2,492 | 2,807 | 112 | 1,233 |
| After trip 2 | 3,206 | 3,625 | 168 | 1,849 |
| After trip 3 | 3,921 | 4,444 | 224 | 2,466 |

Every trip adds one more copy of each of the 56 images the Vulkan resource
planner allocates for the render pipeline: bloom, TSR history and output,
transparency copies, post-process and SMAA outputs, volumetric fog and the
rest. That is 616 MB of images per trip. The planner's buffer alias groups
duplicate the same way, about 200 MB per trip, led by the 127 MB per-pixel
linked-list node buffer and the visibility persistent state. All 56 image
handles from before play are still alive after three trips, so nothing is
retired.

Owner: the Vulkan resource planner keeps the previous transition's physical
resource set alive.

### Why the old set was never retired (fixed)

The planner caches one `ResourcePlannerRuntimeState` per
`VulkanFrameOpPlannerStateKey` in `FrameOpResourcePlannerSwitchingState.States`.
Each state owns a `VulkanResourceAllocator`, which owns one complete physical
image and buffer set. The key includes the pipeline's resource generation.

A temporary probe logged each planner-state insertion (removed afterwards):

- Every play exit commits a new resource generation for the main viewport.
  The key changes only in `ResourceGeneration` (2 to 6 to 10).
  `VulkanResourceGenerationTransactionService` publishes it with a new
  allocator (7 to 17 to 23).
- After publishing, the transaction retires the previous allocator only if no
  key in the cloned state map still owns it. The previous generation's key was
  never removed, so the check always failed and the set stayed alive (logged
  `retired=0` each time).
- Only the 12-state LRU cap could ever evict it, at several GB.
- The same applies to any generation change. On the pre-fix build, twelve TSR
  render-scale changes left 504 planner images alive (3.2 GB of images, 5.0 GB
  allocator total), up from 56.

Earlier code moved a compatible physical owner's state to the new key
("registry descriptor revisions are publication metadata, not
physical-resource ownership"). That rekey has had no caller since the planner
refactor.

**Change (retained).** When a generation publishes, the transaction removes
states published by older generations of the same pipeline, viewport, output,
logical view and queue family
(`VulkanResourceGenerationTransactionService.RemoveSupersededGenerationStates`).
The generation counter only advances, so such a state can never become current
again. The removed states' allocators then retire through the existing
post-commit path:

- It excludes physical image groups the new generation reused.
- It retires through retirement tickets, so destruction waits for GPU
  completion.
- Frames sealed against the old map fail closed through their allocator
  ownership check.

**Results on the corrected build:**

| Snapshot | Device-local MB | Allocator MB | Planner images |
| --- | ---: | ---: | ---: |
| Before play | 1,254 | 1,641 | 56 |
| After trip 1 | 1,847 | 2,069 | 65 |
| After trip 2 | 1,847 | 2,068 | 65 |
| After trip 3 | 1,847 | 2,068 | 65 |

- **Memory.** Device-local memory after the third round trip equals after the
  first, so the S14g memory criterion (within 10%) is met. The one-time step at
  the first trip is the second Advanced output bank (about 243 MB), restored-mesh
  buffers, and nine images the new generation kept.
- **Render scale.** Twelve TSR render-scale changes keep exactly 56 planner
  images, and the allocator returns to 1,213 MB at every default scale.
- **Images.** After three round trips the views match before play (1.6, 0.2 and
  0.0 per 255; `camA` differs only in its sky), and `query_advanced_pick` hits
  scene geometry.
- **Shader reload.** A six-cycle stress under motion passes with the editor
  alive.
- **Frame rate.** Alternating fixed and pre-fix runs on one session measured
  141-144 against 131-143 stationary and 37.9-38.7 against 37.7-38.6 fresh
  frames per second in motion, so there is no measurable per-frame cost.

Remaining, smaller: each world copy brings its own shadow viewports, and their
planner states (no textures) still accumulate under new keys until the LRU cap
evicts them.

## Tests

No tests were added or modified. Each change was compared with a detached HEAD
worktree on existing tests:

- **Material correction:** 673 tests whose names contain `Material`,
  `SourceToon`, `ForwardDepthNormal`, `Shadow`, `Emission`, `PlayMode` or
  `Snapshot`. 48 fail against 49 at HEAD, with no new failures; one
  reference-image manifest test passes only in the current tree.
- **Mesh correction:** 695 tests whose names contain `Mesh`, `Cooked`,
  `Snapshot`, `Serializ`, `AdvancedGpuScene`, `Canonical` or `PlayMode`. 49
  fail against 52 at HEAD, with no new failures. Three import/export tests pass
  only in the current tree; whether the change or a worktree difference
  explains that was not investigated.

Restore time is unchanged by the mesh correction: entry restore 1.76 s without
it and 1.83-1.94 s with it, later restores 0.77-1.04 s and 0.82-0.88 s.

## Evidence

Evidence root (ignored, disposable): `Build/_AgentValidation/20261003-015628-vk-100hz/`
(`reports/s14g/`, `mcp-captures/s14g/`, `mcp-captures/s14h/`). Required
conclusions are recorded here.
