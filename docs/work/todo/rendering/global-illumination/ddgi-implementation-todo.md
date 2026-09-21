# DDGI Implementation TODO

Last Updated: 2026-09-21
Current Status: paused at the user's request. Final editor build 49b passes with zero warnings/errors; the isolated editor is stopped and changes remain in the working copy. Both OpenGL pipelines pass the recorded emissive, transport, reflection/specular, bake revision 2, lifecycle, renderer-replacement and allocation checks; interrupted-update recovery remains open. Vulkan/Advanced now passes sustained stereo through FXAA and desktop/stereo ownership. Default Vulkan stereo bloom and interrupted-update receipt fixes are implemented and reviewed, with live validation pending. Vulkan/Advanced eight-probe capture produces eight probes/six tetrahedra but exposes a zero BRDF lookup, blocking specular/cache acceptance. The matrix below records the other remaining Vulkan checks. DDGI is not yet feature complete or production-supported. No formal tests were changed or run in this continuation; the previous targeted suite reported 46 passing and 8 outdated failures, with refresh awaiting live validation and explicit user clearance.
Scope: implement the DDGI roadmap from [../../design/global-illumination/ddgi-integration-plan.md](../../design/global-illumination/ddgi-integration-plan.md) as a real renderer feature, starting from honest scaffolding and ending at a usable dynamic diffuse GI path with large-scene scaling, baked fallback, and hybrid integration hooks.

## References

- [../../design/global-illumination/ddgi-integration-plan.md](../../design/global-illumination/ddgi-integration-plan.md)
- [../../../../developer-guides/gi/global-illumination.md](../../../../developer-guides/gi/global-illumination.md)
- [../../../../developer-guides/gi/light-probes.md](../../../../developer-guides/gi/light-probes.md)
- [../../../../developer-guides/gi/surfel-gi.md](../../../../developer-guides/gi/surfel-gi.md)
- [../../../../developer-guides/gi/restir-gi.md](../../../../developer-guides/gi/restir-gi.md)
- Morgan McGuire DDGI article series (2019), parts 1-4
- Majercik et al., "Dynamic Diffuse Global Illumination with Ray-Traced Irradiance Probes", JCGT, April 2019

## Completion pass requested 2026-09-20

The user requested all remaining gaps on OpenGL and Vulkan in both Default and
Advanced pipelines, explicitly requiring material emission to contribute. This
includes the previously listed regression-test backlog; test updates follow live
feature validation as required by the repository policy. Earlier validation
remains useful evidence, not acceptance of this expanded scope.

- [x] C1 Define semantic material bindings and preserve independent emissive
  color/intensity/maps through imported and authored materials. Add GPU UV and
  smooth-normal attributes without changing the shared BVH triangle ABI.
- [ ] C2 Sample material textures on both backends, respect cutout opacity,
  propagate thin-surface transmission and shadow transmittance, and sample
  directional environment radiance. Prove emissive-only illumination responds
  to color, texture and intensity with zero direct/ambient light.
- [ ] C3 Integrate actual DDGI resources, update passes, composition and debug
  presentation in Advanced, including dimensioned resource generations and
  preserving specular while suppressing duplicate diffuse ambient.
- [ ] C4 Resolve Vulkan descriptor namespaces, resource barriers, GPU deformation,
  baked array uploads and stereo layers without CPU tracing/deformation fallback.
- [ ] C5 Harden interrupted updates, deferred submission acceptance, scene reload
  and viewport ownership; validate failure recovery and managed allocations.
- [ ] C6 Run the same emissive/material/occlusion and dynamic/baked scene on all
  four pipeline/backend combinations, including normal/reverse depth and stereo.
- [ ] C7 Refresh obsolete contracts and add targeted behavioral regressions after
  live validation, then update stable docs and this evidence record.

### Current progress and next actions

Latest evidence and immediate work (through build 49b):

- Work paused at the user's request after the build 48 live checks. The isolated
  editor is stopped. Final build 49b passes with zero warnings/errors; source
  corrections are retained in the working copy. Do not treat source review or
  successful capture statistics as final
  acceptance of the still-open matrix.
- [x] Identify and correct Default stereo bloom's pipeline-family gate: every
  bloom mip now derives multiview from `Stereo`, matching its stereo shader and
  two-layer target. Build 49b passes; the live repeat remains pending.
- [ ] Diagnose Vulkan/Advanced's zero BRDF lookup. Build 48 completes all eight
  probe captures in 8.31 seconds and publishes eight probes/six tetrahedra, but
  the BRDF image remains exactly zero in initial and later captures. The packed
  reflection image is nonzero (maximum 64.8125), and both images were viewed.
  Specular coexistence and cache recovery are not accepted until the BRDF
  producer is corrected and the full harness passes.

- [x] Review the Vulkan final-source authority correction for build 48. The
  selected source carries the exact deferred publisher and publication token;
  only that accepted draw can establish native image/view/sampler authority.
  Context, native-generation, descriptor-slot and epoch checks remain strict.
- [x] Build 48 passes with zero warnings/errors. Vulkan/Advanced stereo passes
  both eyes through FXAA, preserves the strength-8 emitter in HDR and keeps
  advancing after restoration (380 → 411 accepted updates). Both final images
  were viewed. Desktop/stereo ownership and node reactivation pass with no
  harness errors; the previous final-source stall is resolved in this repeat.
- [ ] Repeat sustained stereo and ownership on Vulkan/Default, then continue
  both Vulkan pipelines' remaining acceptance matrix. Build 48 Default fails
  before its first accepted DDGI update: stereo `BloomMip0FBO` has view mask 3,
  but its prepared draw context is single-view. Fix this target-topology
  mismatch before accepting Default stereo; later source-readiness warnings
  and queue backpressure are downstream symptoms.
- [ ] Close the interrupted-update GPU lifetime gap. A partial update currently
  invalidates history without retaining a receipt for its GPU writes, so a later
  baked upload can race host-visible Vulkan buffers. Retain an ordered,
  non-publishing abort receipt and wait before overwriting. Missing/failed
  receipts must fail closed. Completion is now marked only after a receipt is
  acquired. Build 49b passes; live validation of the full receipt correction
  remains pending.
- [x] Implement non-publishing abort receipts and development-only exact-pipeline
  interruption diagnostics. The wrap-up build caught an out-parameter/nullable
  issue in the new helper; review also caught a published-build guard and
  diagnostic event-correlation defect. Those corrections pass final review;
  build 49b passes with zero warnings/errors. Live acceptance remains pending.
- [ ] Add a development-only, exact-instance/generation interruption control,
  then prove skipped visibility-stage updates never publish or advance the
  completed-update counter, their abort receipts are accepted, and complete
  updates recover on all four backend/pipeline combinations.

- [x] Build 47 passes with zero warnings/errors. OpenGL/Advanced now passes
  eight-probe topology/cache recovery and DDGI/specular coexistence. Generation
  2 → 3 retains all eight probes, six tetrahedra and exact BRDF/reflection pixel
  hashes. Direct capture of the probe-owned source matches the packed array
  after clearing. No bindless-mutation/recreation diagnostic is emitted.
  The metal ROI remains exactly 0.632308179 at DDGI intensity 1/0/1, while the
  diffuse floor responds (0.37397/0.19558/0.40528). All three composition images
  were viewed, data is finite and restoration is clean.

- [x] Build 46 passes with zero warnings/errors. Advanced now publishes the same
  eight-probe/six-tetrahedron topology, including generation 2 → 3 on cache clear.
  The GL trace pins source-image loss to `SourceAsset` metadata notifications:
  those falsely dirty frozen native parameters, causing image recreation after
  the last Advanced lease release. This also happens before the initial native
  specular frame, while its already-copied array still contains radiance.
- [x] Build and validate the GL metadata correction. `SourceAsset`, names,
  binding labels and GPU-write bookkeeping no longer dirty texture parameters;
  real native mutations retain the existing guards. Repeat Advanced capture,
  cache clearing and DDGI intensity 1/0/1 with the metallic fixture. Build 47
  passes the complete repeat as recorded above.

- [x] Build 45b passes with zero warnings/errors. The co-spherical Cartesian
  probe grid now gets six valid tetrahedra without moving the probe positions.
  OpenGL/Default publishes all eight probes for instance 2, generation 2, then
  republishes the same six tetrahedra at generation 3 after cache clearing.
  BRDF and packed reflection pixel hashes are unchanged; the HDR, BRDF and
  reflection images were viewed. This closes Default's eight-probe topology and
  cache-publication check. Repeat Advanced and both Vulkan combinations.
- [x] Finish the Advanced native reflection repeat after the source-image lifetime
  correction. The new evidence gives both symptoms a common cause; investigate
  probe-record/handle sampling further only if the live repeat still fails.
  Build 47 confirms that no shader change was needed.
- [x] Trace Vulkan's final descriptor publication with bounded diagnostics.
  Build 46 confirms source-owner selection and descriptor binding execute, but
  selection re-resolves a different native image/view from the accepted draw.
  Descriptor observation correctly rejects the mismatch.
- [ ] Establish Vulkan native final-image authority from the accepted draw's
  resolved descriptor after exact logical-owner selection; retain strict native,
  generation and epoch checks afterward. Then repeat stereo and ownership.

- [x] Build 44 passes with zero warnings/errors. OpenGL/Advanced now completes
  all eight reflection-probe captures in 6.64 seconds once the startup batch begins.
  The BRDF lookup and packed reflection texture are finite and nonzero, and their
  images were viewed. The original Advanced-reservation loop is resolved.
- [x] Finish Advanced eight-probe topology and specular validation. Build 45b
  fixes the empty lattice topology and validates Default as recorded above. The
  Advanced metal fixture stays black at DDGI intensity 1/0/1 despite valid BRDF
  and reflection textures. Its diffuse floor responds (0.41810/0.04791/0.41737),
  and restoration is clean. Inspect probe sampling and compare Default; cache
  recreation is accepted in build 47 above.
- [x] Preserve probe-owned textures when clearing a pipeline cache. Build 44
  republishes instance 2 at generation 3 after clearing, with DDGI updates and
  BRDF initialization healthy, but both the packed reflection array and a direct
  probe source capture become black. Fix resource ownership before rerunning
  specular and cache recovery. Build 45b separately closes the empty lattice
  topology defect; build 47 accepts the Advanced repeat.
- [x] OpenGL/Default build 44 repeats specular coexistence with eight captured
  probes: the metal ROI is exactly 0.576599689 at DDGI intensity 1/0/1, while the
  diffuse floor responds. Its cache clear advances resource generation 2 → 3,
  republishes all eight probes and retains reflection maximum 3.6113281 and BRDF
  maximum 1. Captures were viewed and restoration is clean. The empty tetrahedral
  topology still prevents full eight-probe interpolation acceptance; the black
  specular/cache behavior above is specific to Advanced in this comparison.

- [x] Build 43 passes with zero warnings/errors. Final presentation now binds its
  exact authored output through clear/draw capture. Cold and cached mesh paths
  share target-topology validation, and each preparation attempt retains one
  planner root. Source review passes.
- [ ] Vulkan/Advanced build 43 no longer reports the lost-target mismatch, but
  sustained submission still fails: the final presentation source lacks a
  published descriptor/layout, and frame recording repeatedly defers. Diagnose
  final-source ownership/publication before accepting stereo or recovery.
- [x] Pin the eight-probe blocker: plain offscreen capture has no Advanced intent,
  yet global OpenGL capabilities promoted it into Advanced without a reservation.
  The selection correction is built; the follow-up explicitly keeps plain capture
  on Default even under Required mode. Repeat all eight captures and cache clear.

- [x] Build 42 passes with zero warnings/errors. Vulkan/Advanced stereo captures pass
  both eyes in DDGI-only and normal composition through FXAA. Normal HDR retains
  the strength-8 emitter; final-output means are 0.0711511 and 0.0712825. Both
  final images were viewed, eye depths differ, and restoration is clean. Scoping
  native framebuffer preparation by each operation's exact planner generation
  restores the previously missing stereo draws. Sustained presentation is still
  blocked by the failure below; these captures alone do not accept stereo recovery.
- [x] Fix final presentation losing its authored layered output framebuffer.
  After the successful build 42 captures, pass 100072 reaches Vulkan as a null
  target with a multiview shader and single-view scheduling mask. The new guard
  rejects it, stalls submission, and freezes DDGI at 89 accepted updates before
  the ownership toggle begins. Bind the exact output framebuffer while capturing
  the present draw, preserve its logical target scope, then repeat sustained
  stereo and viewport ownership on both Vulkan pipelines. Build 43 corrects the
  target; the remaining descriptor-publication failure is tracked above.
- [x] Keep one immutable planner root across the entire PresentNow/explicit
  preparation attempt. Source now passes that same generation to planning,
  target preparation, barrier freezing and sealing; build 43 and review pass.
- [x] Build 41 passes with zero warnings/errors. Exact queued planner scopes,
  bounded generation caching, shader-specific multiview context and corrected
  mono/stereo bin classification pass source review.
- [x] Vulkan/Advanced build 41 still produces black stereo FXAA despite completed
  frames. Trace successful quad preparation and actual queue insertion to locate
  the remaining missing consumers. Scope native framebuffer preparation by the
  same captured context and validate shader/target topology once targets exist.
  Build 42 closes this blocker as recorded above.
- [x] Eight-probe startup fails for either OpenGL viewport pipeline because both
  use an Advanced capture pipeline whose output reservation is rejected. Fix that
  capture path before accepting reflection-probe publication/cache recovery.
  Exact capture-owner diagnostics confirm instance 3 is rejected with no current
  reservation. Global OpenGL capability discovery can currently select Advanced
  without a reservation and overwrite the real failure with a generic message.
  Require an exact successful reservation for selection and preserve its failure.
  Build 44 validates this correction with all eight captures on both pipelines.

- [x] Build 40b passes with zero warnings/errors. The Advanced teardown fix now
  releases bindless leases before wrapper retirement and rechecks orphan-only
  shutdown after waiting for the GPU. Both dynamic and baked live repeats pass:
  updates advance 502 → 634 with timing 0.473 → 0.462 ms; baked output preserves
  its exact pixel hash and mean 0.00605936 with 2,156 updates frozen. Captures
  were viewed, restoration is clean, and targeted logs have no teardown errors.
  A targeted MCP cache-clear action is available for the eight-probe fixture.
- [x] Correct Vulkan queued mesh preparation to use the request's exact resource
  planner generation while activating bindings and prewarming descriptors. Build
  40b logs no eager fullscreen-quad preparation failures, yet stereo FXAA remains
  black. Source inspection identifies ambient mono resource lookup during stereo
  materialization. Remove the pipeline-name multiview heuristic and retain the
  actual target/shader topology in the queued context. Revalidate both pipelines
  with normal stereo presentation and viewport ownership after source review.
  Builds 41–43 implement and review this; remaining acceptance is tracked above.

- [x] Build 39 passes with zero warnings/errors. OpenGL/Default now passes
  dynamic and baked renderer replacement. Dynamic updates advance 475 → 604
  and hardware timing resumes (0.484 → 0.515 ms); baked output preserves its
  exact pixel hash and mean 0.00576406 with a frozen update count. Captures were
  viewed and restoration is clean. The shared BVH shader lifetime, GL sync
  retirement and DDGI timing/owner-reset corrections pass source review.
- [x] Complete OpenGL/Advanced renderer replacement. Build 40b resolves build 39's
  bindless lease teardown failure and accepts both dynamic and baked recovery.
  Both Vulkan pipelines still require their renderer replacement repeats.

- [x] Build 37b passes with zero warnings/errors, including per-instance probe
  publication, BRDF retention, shared Vulkan shader lifetime, visibility filtering,
  bake revision 2 and native OpenGL framebuffer teardown. Build 37's missing
  static namespace import in the new Advanced partial file was corrected.
- [ ] Finish Vulkan stereo composition. Build 37b removes the BRDF recreation
  loop and reaches completed normal frames, with finite nonzero stereo DDGI.
  Advanced layer 0 still has an emission-only HDR target and black FXAA output.
  Viewed captures confirm the failure; inspect the composite/post-processing
  passes and stereo view warnings before accepting presentation.

- [x] Fix mixed-probe light leakage by separating the narrow angular distance
  filter (exponent 50) from authored Chebyshev visibility contrast. In the same
  opaque partition fixture, OpenGL/Advanced's blocked floor ROI falls from
  0.098432 to 0.003922; its lit/open controls remain 0.492544 and 0.305448.
  OpenGL/Default also passes at 0.005623 blocked and 0.388573 open. Captures were
  viewed, with finite data and no restoration errors. All 18 expanded shaders
  compile. Repeat the mixed-probe check on both Vulkan pipelines.
- [ ] Validate new baked algorithm revision 2: reject obsolete version 1 moments
  with an explicit rebake diagnostic, then capture/load fresh assets on all four
  combinations. OpenGL/Default passes in build 37b: version 2 is verified in the
  file header; source-off retains mean 0.00576406, dark/obsolete/missing assets
  give zero, and valid-path recovery restores 0.00576406. Captures were viewed
  and restoration reports no errors. OpenGL/Advanced also passes: the frozen,
  source-off and recovered means are exactly 0.00605936, with zero output for
  dark/obsolete/missing assets. Its captures were viewed and restoration is clean.
  Both Vulkan combinations remain open for revision 2 acceptance.
  The payload layout is unchanged; existing files are not migrated.
- [x] Both OpenGL pipelines resume accepted DDGI updates after unloading and
  reattaching the whole scene. Default counts advance 3,349 → 3,456; Advanced
  advances 9,121 → 9,239. All captures are finite and visually coherent. This
  does not establish disk deserialization or renderer replacement recovery.
- [ ] Resolve renderer-replacement recovery separately: the OpenGL/Default
  restart request times out after 60 seconds while multiple passes attempt to
  use retired renderer generation 0. DDGI reports geometry unavailable instead
  of accepting stale output. Teardown first fails because framebuffer deletion
  tries to create wrappers after retirement. The source correction detaches
  native attachment slots directly through the framebuffer's owning renderer;
  it preserves explicit driver cleanup. Build 37b restarts successfully without
  rollback, but DDGI reuses initialized history/geometry across the new renderer
  owner and turns black while the raster emitter remains correct. Reset both
  DDGI helpers on exact API-owner change, then repeat dynamic and baked recovery.
  Build 38 passes with zero warnings/errors, but its restart repeat exposes
  shared BVH shader assets being destroyed by service teardown. The correction
  retains shared shader assets, retires outstanding OpenGL syncs before native
  API disposal, and recreates DDGI timing handles after owner replacement.
  Build 39 accepts the Default repeat above. Advanced exposes the separate
  bindless teardown ordering failure and both Vulkan repeats remain open.

- [x] Replace the discarded one-shot BRDF draw with a per-instance graphics pass
  that retains its producer, retries preparation/submission and gates PBR use.
  Declare the missing Advanced forward BRDF resource. OpenGL/Default's lookup
  now contains finite values up to 1.0; the metallic control ROI remains exactly
  0.391745 at DDGI intensity 1 → 0 → 1, while the floor changes
  0.416337 → 0.126209 → 0.416786. Captures were visually inspected.
- [x] OpenGL/Default's corrected enclosure fixture passes with sky disabled:
  sealed indirect RGB and floor ROI are zero; opening the enclosure gives floor
  ROI 0.781083. This proves enclosed/open transport for a single probe, not the
  full mixed-probe visibility-leak stress case. Restoration reports no failures.
- [ ] Validate reviewed BRDF reset/failure hardening: distinguish rejected work
  from an accepted writer whose completion is unknown, quarantine the latter,
  and invalidate availability when renderer owner or physical texture epoch changes.
  Build 36b diagnostics identify descriptor invalidation after rejected frames.
  The next correction retains the producer across those retries. Review also
  finds per-program teardown destroying shared Vulkan shaders; it now releases
  only the program's reference. Build 37b reaches completed normal Vulkan stereo
  frames without that recreation loop. Stereo composition remains blocked by
  missing consumer draws and must pass before presentation is accepted.
- [x] Complete OpenGL/Default allocation acceptance. Build 34 records zero last,
  average and maximum bytes in all ten command scopes over full 240-sample windows
  while 1,127 probe updates are accepted. Transient skinning revision state no
  longer raises asset-property events; authored-property notifications remain.
- [x] Repeat OpenGL/Default bone-only and morph-only changes after the skinning
  and GPU ownership corrections. Normalized depth captures visibly show both
  deformations; accepted updates advance and restoration reports no failures.
- [x] Complete OpenGL/Advanced allocation and deformation repeats. Build 35 has
  ten zero-byte 240-sample windows with 393 accepted updates. Bone-only and
  morph-only depth captures were viewed and show the expected changes.
- [x] OpenGL/Advanced passes normalization within its packed-material precision:
  selected texels are (0.248047, 0.496094, 0.984375), double at strength two and
  zero at strength zero. Probe 220 escapes its enclosing cube within cell bounds
  and reactivates. Sealed/open floor ROIs are 0 and 0.803428, with captures viewed.
- [ ] Repeat allocation and deformation acceptance on Vulkan after BRDF recovery.
- [x] Implement reflection-probe retry/publication ownership. IBL retry waits for
  the captured renderer's active scope. Both pipelines now keep probe caches,
  buffers, job tokens and binding guards in per-instance state. Background jobs
  use detached CPU snapshots; the owning render scope stages and publishes the
  complete buffer/grid cohort after checking owner, world, generation, layout,
  order and request token. Final source review passes with no P1/P2 findings.
- [ ] Validate that refactor with an eight-probe fixture on both backends and
  pipelines, including cache clear/reload and multiple viewport ownership.
  Require a successful topology-publication marker and visible specular lighting.
  Build 40b's OpenGL/Advanced fixture stops at the first capture with zero usable
  probes: the offscreen Advanced output reservation is rejected as stale for the
  renderer generation. Correct capture output ownership before repeating the
  publication/cache-recovery check. This is separate from successful DDGI restart.

- [x] Build 32 succeeds with zero warnings/errors. OpenGL/Default retains finite,
  visually coherent DDGI output after invalidating 84 renderer shaders and
  resumes accepted probe updates. This establishes shader invalidation recovery,
  not full scene reload acceptance.
- [ ] Select Vulkan's desktop source from the actual marked swapchain draw in
  the sealed plan. Build 31c traces show both final writers belong to the stereo
  pipeline; an unrelated mono clear had incorrectly satisfied the earlier owner
  filter. Preserve descriptor, generation and submission validation; repeat
  stereo final-output and viewport-ownership checks on both Vulkan pipelines.
- [x] Identify the shared BRDF lookup initialization defect.
  OpenGL/Default's reflection probe contains radiance (maximum 76.875), but every
  BRDF texel is zero and the metallic control loses its IBL highlight with DDGI
  disabled. Build 33b fixes and verifies OpenGL/Default; repeat the other three.
- [x] Correct the measured OpenGL allocation sites: diagnostic formatting,
  shader-list enumeration, scalar asset-change boxing, transient skinning
  notifications and the cold output-buffer closure. Both pipeline repeats pass;
  enabled diagnostics and authored-property notifications are preserved.
- [x] Repeat the OpenGL/Default enclosure fixture with an independent block
  material. The first attempt unintentionally changed the floor's shared
  material, so its dark receiver is rejected as fixture evidence. The generated
  scene now separates those materials; the corrected enclosure result is above.
- [ ] Finish the outstanding backend/pipeline stress matrix below.

Implemented in source, awaiting the expanded live checks:

- Semantic material maps preserve UV0/UV1, transforms, wrapping, color space and
  scalar channels. glTF/FBX/Assimp import retains independent emission color,
  strength and texture. Normal-only imports no longer use the normal map as albedo.
- GPU geometry carries smooth normals and both UV sets, rejects invalid layouts
  and indices, refreshes changed mesh attributes, and releases replaced buffers.
  DDGI samples material images on the GPU and tracks changing render textures.
- Transparent transport and relocation continue through thin layers; shadow
  rays accumulate colored transmission and respect local-light distance.
- Directional, point and spot lights now feed DDGI through a bounded light
  buffer. Exceeding its 255-light capacity produces a diagnostic and pauses the
  update. Static lights retain the renderer's existing bake-only contract.
- Both pipelines contain the DDGI resource/update/composite/debug chain and
  authored sky capture. Default has a separate RGB emission attachment; Advanced
  has independent emission color, strength and texture in its material table,
  including UV1, transforms, derivatives and color-space metadata. Missing UV1
  produces a reconstruction failure rather than silently using UV0.
- Default's DDGI stereo composite now requests the multiview quad variant, matching
  its stereo fragment shader and the Advanced implementation; runtime stereo
  validation remains open.
- Per-viewport state, completion receipts and interrupted-update invalidation
  protect history. OpenGL fence objects have a bounded reuse pool.
- Geometry/BVH caches now belong to a pipeline instance instead of the scene,
  preventing one renderer owner from reusing another owner's completion state.
  Baked uploads also track submission acceptance and retry rejected uploads.
  Build 16 includes these changes. Review found further baked receipt and reuse
  ordering defects; their correction and live transitions remain pending.
- Explicit diagnostics capture now reports live per-cascade and per-pipeline logical
  GPU payload sizes, separate from driver allocation/residency estimates.
- The startup blocker has a source correction: CPU buffer commits publish to
  existing owner queues without creating off-thread wrappers, and GPUScene
  collect-thread writes now use that commit path. Exact-owner backend calls
  remain restricted to their render owner.
- The subsequent live run reached real geometry, then exposed the same premature
  publication pattern in trace diagnostics. That scratch buffer now belongs to
  the per-viewport DDGI context, uses a CPU commit, and is disposed on cache clear.
  Diagnostics also report the stopped update stage and submission/environment state.
- The black scene was traced to owner-first mesh versions raising empty draw
  events before any backend wrapper existed. Draws now resolve and call one
  explicit renderer owner through `IApiMeshRenderer`, preventing both a skipped
  first draw and cross-owner event broadcasts. BVH scratch initialization also
  uses committed CPU data followed by explicit first binding.

Validation recorded so far for this completion pass:

- Editor builds 5, 6 and 7 succeeded with zero warnings and errors, including the
  startup correction and Advanced material ABI changes. Build 7 includes the
  subsequently found trace-diagnostic initialization correction.
- All 18 expanded DDGI/geometry/debug/composite shaders and twelve sky shader
  variants passed syntax compilation. This does not establish Vulkan execution.
- All 18 affected deferred material fragment shaders also passed syntax checks.
- The first OpenGL/Default launch hit the buffer-publication failure before a
  frame completed; no new material lighting result is accepted from that run.
- The next launch rendered the editor and published a real 108-triangle,
  215-node DDGI BVH. Trace initialization still aborted updates; build 7 fixes
  that failure. The new run completed more than 1,556 updates and produced
  finite irradiance (maximum 2.625, mean RGB 0.01019) from the textured emitter.
  However, albedo/emission are empty and depth remains at its clear value, so
  the final DDGI image is black. Probe lighting alone does not establish screen
  composition acceptance. The missing scene rasterization was corrected and
  subsequently verified in the build 9 run below.
- Live diagnostics report 24,438,512 logical GPU payload bytes for this fixture,
  including shared scene inputs; these are resource sizes, not driver residency.
- Build 9 passed with zero warnings/errors. Its OpenGL/Default run populated
  depth and surface inputs and visibly lit the room from a black-diffuse,
  UV1-textured emitter. Blue strength 8 produced mean DDGI RGB 0.00519;
  emission off produced exact zero. Green strength 8 and 4 produced means
  0.00707 and 0.00339, respectively. Viewed captures show the color change and
  coherent illumination from two camera positions. Normal and reversed depth
  both produce finite illumination with matching surface coverage.
- That run exposed an unbound optional emission sampler on neutral materials.
  Materials now explicitly bind a cached black texture when no emissive map is
  authored; builds 10 and 11 include that correction.
- Vulkan/Default initially rejected valid backend wrappers because interface
  dispatch used the facade renderer's identity. A virtual owner identity on the
  common renderer now dispatches Vulkan's backend context correctly. Build 11
  passes. Its next run crashed while MCP recursively inspected an uninitialized
  physics scene; the Windows runtime stack identifies `PhysxScene.Timestamp`,
  before Vulkan lighting could be accepted. That inspection blocker is being
  corrected separately from GPU execution.
- A shallow inspection restart passed that blocker and exposed a Vulkan graph
  error: DDGI screen sampling wrote `DDGITexture` and the composite sampled it
  inside one declared pass. The command now declares separate compute and draw
  passes with an explicit dependency and executes each in its own graph scope.
  Build 13 passed without warnings/errors, and Vulkan accepted this graph.
- Vulkan now rasterizes the fixture correctly (depth minimum 0.98047; emission
  maximum follows strengths 8, 0 and 4), but its irradiance and DDGI images remain
  zero. The material-copy target lacked storage-image usage at allocation.
  Further, rejected frame submission left one-time material copies and the BVH
  build cached as complete. Build 14 includes storage usage and submission-receipt
  retry fixes and passes with zero warnings/errors, but indirect lighting remains
  black without a reported backend error.
- A RenderDoc Vulkan frame capture confirms eight compute dispatches: ray
  generation, hit shading, relocation, atlas updates, border copies and screen
  sampling. The trace dispatch is absent. Its disabled diagnostic SSBO was
  created after resource preparation and never acquired valid owner storage;
  Vulkan silently rejected the snapshot while DDGI advanced its update stage.
  Remove this unused binding, report dispatch rejection explicitly, and verify
  actual hits/irradiance before accepting Vulkan. Exported irradiance and final
  output were inspected and are black; update counters are not lighting evidence.
- The physics inspection guard is included in builds 13 and 14. Shallow MCP
  volume inspection avoids traversing unrelated native scene objects.
- Build 15 passed with zero warnings/errors, with trace diagnostics compiled out
  and named dispatch rejection diagnostics enabled. The next Vulkan run stopped
  correctly at an unprepared `DDGI.WorldAabbs` buffer instead of reporting false
  completed updates. Program buffer binding now creates the exact owner's
  wrapper before lookup-only backend snapshot capture. Partial geometry/material
  imports are also published when preparation is pending or fails. These latest
  corrections are included in build 16.
- Build 16 passed with zero warnings/errors. Vulkan/Default now produces coherent
  textured-emitter illumination: blue strength 8 mean DDGI RGB 0.00679, emission
  off exact zero, green strength 8 mean 0.00919, and green strength 4 mean 0.00466.
  Blue and green DDGI captures were visually inspected. Normal/reversed depth
  and a second camera produce finite DDGI lighting, but regular viewport captures
  are magenta. Final composition is therefore not accepted. This run uses the
  RenderDoc-friendly diagnostic preset; the normal preset remains to be checked.
- Vulkan/Default also responds to emission texture reloads: black gives exact
  zero; white restores mean DDGI RGB 0.01559 versus the checker's 0.00669.
  Isolated point, spot and directional sources and authored blue/red skies each
  produce finite nonzero DDGI; point-light and red-sky captures were viewed.
- A new RenderDoc capture isolates the magenta output to deferred combine's
  emission input: its positional material list placed emission in slot 7 while
  the shader expects slot 9. Mono/MSAA material arrays, resource validators and
  binding-generation tracking are corrected in source. Live verification follows
  build 17. The replay session and named editor were closed after inspection.
- Build 17 passed with zero warnings/errors. A fresh Vulkan/Default run without
  the RenderDoc preset shows correct regular HDR and viewport composition,
  including the visible checker emitter and diffuse bounce. Normal/reversed
  depth and a second camera pass. The HDR and viewport captures were viewed.
- Vulkan baking currently stops explicitly because the renderer facade lacks
  the synchronous diagnostic buffer-readback capability. Integrate its existing
  scoped GPU readback service before accepting baked operation. A single-cascade
  bake also incorrectly rejected the otherwise inactive coarse-visibility flag;
  the source guard now requires an actual outer cascade.
- The sky lighting captures pass, but deactivating that sky caused a Vulkan
  terminal frame rejection for `Skybox.FullscreenTriangle` index-buffer lifetime.
  Source investigation and a repeat activation/deactivation run remain required.
- Baked uploads already perform synchronous explicit backend transfers and image
  transitions; no synthetic transfer pass is needed. A separate lifetime fix is
  included in build 17 to defer host writes until earlier GPU probe reads finish
  during dynamic/baked or baked-asset changes. Candidate publication and accepted
  resource-use receipts are separate; single-poll guards avoid completion races.
  Live transitions remain blocked by Vulkan's missing scoped buffer readback.
- OpenGL/Advanced build 17 completed 857 probe updates but rejected native opaque
  shading because its GI gate only admits Light Probes and IBL. It also lacks
  the actual albedo, normal, RMSE and emission outputs consumed by shared DDGI
  composition. Add a matching DDGI provider, export the resolved native surface
  from the existing opaque shader, publish both backend resource closures and
  graph writes, and use Advanced's real AO target. Keep specular IBL while
  suppressing its diffuse lobe. Neutral exports must keep unlit surfaces unlit.
- No regression tests have been changed during implementation.
- Vulkan/Advanced build 21 now passes basic emission off/on, RGB and strength:
  blue mean 0.00532; off zero; green strengths 8/4 means 0.00753/0.00365. Viewed
  DDGI and regular HDR captures show correct bounce and the textured emitter.
  Normal/reverse depth, a second camera, direct lights and sky off/on/off also
  pass with advancing update counters and no reported Vulkan validation errors.
- Vulkan baked readback now succeeds, but the uploaded irradiance atlas is
  corrupted: dynamic mean DDGI 0.00520 becomes 5.89159 after loading the bake;
  the atlas contains 16 non-finite samples. This is a failed bake acceptance,
  even though file serialization and update suspension work. Correct the native
  upload format conversion and repeat both Vulkan pipeline bake sequences.
  The harness now rejects non-finite companion atlases and large brightness
  changes, rather than accepting any nonzero baked output.
- Vulkan/Advanced emissive checker/black/white reloads give means
  0.00543/0/0.01261. Its first alpha transport check remains inconclusive:
  both the masked and opaque panel darken the sampled floor similarly. Inspect
  cutout tracing, visibility and the fixture before accepting this case.
- Build 22 passes with zero warnings/errors, including Advanced surface memory
  accounting and Vulkan descriptor-limit admission. Packed array upload is
  implemented; numeric boundary and byte-count review corrections will be in
  the next build before the live Vulkan bake repeat.
- Build 23 passes with zero warnings/errors. Vulkan/Advanced baked capture and
  load now retain finite, correct lighting: dynamic mean 0.00534 versus baked
  0.00523. Source-off holds the exact baked output without tracing; dark-asset
  replacement gives zero; missing-file recovery restores exactly 0.00523. The
  baked image was viewed and the previous green-strip corruption is gone.
  Diagnostics report 66,355,200 logical bytes for Advanced's four 1920x1080
  RGBA16F surface exports, included in the 90,795,632-byte fixture total.
- Vulkan/Advanced bone-only and morph-only changes pass isolated depth/silhouette
  checks with finite DDGI and continued updates. The first emulated stereo run
  instead rejects native shading because its deformation GPU output slot is not
  reusable; both viewport submissions stall. Investigate multi-viewport resource
  ownership and repeat stereo before accepting C4/C5.
- The apparent cutout failure was traced to the fixture's closed slab: mirrored
  checker masks on its two faces make every ray hit an opaque square. Replace
  both transport panels with single double-sided quads before repeating alpha
  and colored-transmission checks.
- With the single-sheet fixture, OpenGL/Default passes cutout and colored/white
  transmission transport. Receiver luminance is 0.484 baseline, 0.410 through
  the mask and 0.224 with opaque coverage. Half-strength white transmission
  gives 0.377 versus 0.155 blocked; green transmission changes receiver RGB to
  0.215/0.475/0.181. Viewed captures confirm the spatial and color response.
  Raster inspection separately found that Default's alpha shader ignores base
  texture alpha in this imported material; correct it before final acceptance.
- OpenGL/Default regular HDR composition, depth modes and two cameras pass on
  build 23. Disabling its DDGI volume stops updates; re-enabling with two
  cascades and a one-probe budget warms both 384-probe layers, advances both
  cursors and yields finite irradiance/visibility. Retained inactive resources
  are intentional caches; the inactive composite exits before using them.
- Stereo source fixes are ready: preview UI waits for both eye images to be
  sampleable, and emulated VR now selects a stereo mesh shader. The former
  avoids Vulkan's preview/producer startup wait; the latter corrects the static
  model-space rectangle observed in OpenGL eye G-buffers. Rebuild and repeat.
- OpenGL/Default build 23 also passes checker/black/white emission map reloads
  (means 0.00525/0/0.01226) and the tightened baked lifecycle. Dynamic/baked
  means are 0.00496/0.00539; disabling the emitter preserves the exact baked
  result and fixed update counter. Dark replacement and missing-file clearing
  give zero; restoring the lit asset recovers exactly 0.00539. The baked image
  was viewed. Native glTF no longer duplicates base color as a legacy opacity
  map; legacy alpha shaders multiply actual base alpha and independent opacity.
  Validate raster coverage with a colored cutout map after rebuilding.
- Build 24 passes with zero warnings/errors. OpenGL/Default now renders the
  colored half-mask correctly and passes isolated bone/morph, point/spot/
  directional and sky off/on/off checks. Desktop and stereo pipeline instances
  remain distinct through node deactivation/reactivation. Both stereo G-buffer
  eyes now contain correct geometry, but AO writes only eye zero. Temporarily
  disabling AO isolates finite DDGI in both eyes; this is diagnosis, not stereo
  acceptance. Make all stereo AO fullscreen quads explicitly multiview and use
  the configured AO resource in the stereo DDGI composite.
- Vulkan/Advanced build 24 clears the earlier preview startup wait. Submission
  now fails because the frozen final-presentation source remains incomplete
  (undefined image layout and no descriptor); both viewport histories correctly
  stay at zero completed updates. Resolve that publication blocker and repeat.
- Vulkan/Default build 24 passes packed baked upload/replacement/recovery:
  dynamic/baked means 0.00644/0.00699, exact source-off hold and missing-file
  recovery, and zero for dark replacement. Colored cutout/transmission and
  isolated bone/morph checks also pass with viewed images. Adding a point light
  then exposes another raw upload mismatch: its R16 cube receives 32-bit red
  pixels. Normalize cube/cube-array faces and convert red float to native half;
  retain exact native-size rejection and repeat light/sky/lifecycle checks.
- OpenGL/Advanced build 24 produces finite DDGI and matching debug HDR in both
  stereo eyes with different depth hashes and viewed binocular disparity.
  Regular HDR also reaches the authored emission maximum 8 in both eyes.
  Repeat after the configured AO binding correction before accepting stereo.
- OpenGL/Advanced build 24 passes isolated bone/morph changes, volume suspension,
  two-cascade scheduling and node deactivation/reactivation with distinct
  desktop/stereo pipeline instances. Cutout/transmission ray transport passes
  (floor luminance 0.4753/0.3692/0.1748 baseline/masked/opaque), but the masked
  raster panel disappears entirely. Its deterministic depth equals baseline
  even with cutoff zero, while opaque mode draws the plate. Fix the Advanced
  masked draw path and require both raster coverage and ray transport to pass.
- The first OpenGL/Advanced TSR resize check reaches scale 0.5 and matching DDGI
  resource dimensions, but readback immediately after restoring scale 1.0 fails
  during resource turnover. This is inconclusive; wait for a stable generation
  and accepted updates before recapturing. AA and scale were restored.
- Source corrections awaiting build 25: explicitly multiview stereo AO quads,
  configured AO name in stereo DDGI, and normalized Vulkan cube/cube-array face
  uploads including red float to native half. Vulkan desktop presentation of an
  array source also needs a mono eye-zero mirror and descriptor publication
  attached before initial quad preparation; source work is in progress.
- Build 25 passes with zero warnings/errors. OpenGL/Default now writes AO and
  finite DDGI to both stereo eyes (means 0.00309/0.00315), with distinct depth
  and AO hashes. Its desktop TSR scale 1.0 → 0.5 → 1.0 check also passes stable
  generation, matching dimensions and finite captures with no readback retries.
  The stronger stereo check exposes a separate material shader link failure:
  `FragBinorm` is not supplied by the stereo vertex stage, so the normal-mapped
  emitter is absent from raster emission even though it still contributes to
  GI rays. Correct that interface and repeat full stereo HDR acceptance.
- Advanced masked routing is corrected in source: a deferred material carrying
  `AlphaTested` state now selects the canonical masked layout instead of being
  rejected as `LegacyStateMismatch`. OpenGL/Advanced build 25 now passes visible
  half-mask coverage plus ray transport. A floor ROI spanning both shadowed and
  unshadowed halves gives luminance 0.4535/0.3129/0.1687 baseline/masked/opaque;
  the three depth hashes differ. White half-transmission gives 0.3595 versus
  0.1464 blocked. The prior narrow ROI mostly covered the unshadowed half.
- OpenGL/Advanced stable-generation TSR resize now passes all three captures
  with no retries and restored AA/scale. Its stereo repeat remains blocked:
  the correctly bound native AO texture contains zero in eye one. Fix the
  Advanced AO producer's layer selection rather than bypassing AO in DDGI.
- Vulkan/Default build 25 passes point and spot lighting after cube normalization.
  The directional stage exposes a float-to-D16 dummy-shadow-array mismatch;
  native normalized-depth conversion is now implemented for build 26. The
  interrupted light sequence and unrun lifecycle checks remain open.
- Inspection of saved runtime bakes confirms all four combinations serialize
  52 relocated probes out of 384, with finite records. These fixtures contain
  no inactive probes; classification/reactivation and inactive-state persistence
  still require a deliberately enclosing geometry case.
- Build 26 passes without warnings/errors. OpenGL/Default stereo now writes the
  authored emission maximum eight in both eyes after the tangent interface fix.
  DDGI remains finite in both eyes (means 0.003169/0.003164), but regular HDR in
  eye one still equals only the DDGI contribution. Default's light combine and
  required post-processing quads now request the multiview variants selected by
  their stereo fragment shaders; build 27 and a full stereo repeat follow.
- Advanced GTAO dispatched each eye with depth one but read the global dispatch
  z index, repeatedly selecting eye zero. Build 26 reads the supplied view index.
  Its live Advanced stereo repeat remains required.
- Vulkan/Advanced build 25 can intermittently capture finite stereo images, but
  normal presentation still rejects frames and reports failed DDGI receipts.
  Those captures are not acceptance. Logs identify alternating descriptor slots:
  epoch one publishes slot zero then rejects slot one; epoch two does the reverse.
  Cached command reuse skipped mutable frame-source refresh. The reviewed source
  fix refreshes those draws for each active slot while preserving immutable reuse.
  The stereo harness now requires fresh accepted updates and rejects failed receipts.
- Source review found no additional steady-state DDGI collection or cascade
  uniform allocation; Vulkan uniform arrays use reusable per-frame slots.
  Runtime allocation measurement remains outstanding. The first enclosed-box
  bake contains 13 inactive probes; same-record reactivation and upload validation
  remain open while the harness selects a probe clear of intersecting fixtures.
- Build 27 passes without warnings/errors. Both OpenGL pipelines now pass the
  complete stereo capture chain, including finite AO/DDGI, different eye depth
  hashes, emission/HDR maximum eight and nonzero final FXAA images. Default
  DDGI means are 0.00303/0.00320; Advanced means are 0.00329/0.00335. The
  right-eye regular HDR images and Default final post-process image were viewed.
- Both OpenGL pipelines pass actual inactive-probe persistence: probe 212 was
  active before enclosure, inactive in the box, remained inactive after Baked
  GPU upload, then returned active after Dynamic updates and box removal. All
  384 probe records compare exactly across upload/readback, including relocation.
  Output remains finite, and volume/material/transform settings are restored.
- Vulkan/Advanced build 27 still rejects normal presentation despite intermittent
  successful captures. Keep stereo unaccepted. The next correction must distinguish
  desktop presentation from interleaved bound stereo output; reflected source
  bindings now also prevent unsafe owner-only descriptor refresh. Scoped tracing
  will verify the remaining branch before final acceptance.
- Vulkan/Default build 27 now passes point, spot and directional lighting, both
  authored sky colors and sky off/on/off. This validates the native D16 shadow
  upload correction. Directional and half-resolution DDGI captures were viewed.
  Inactive-probe state and all 384 relocation records survive actual baked GPU
  upload; the selected record reactivates after removing its enclosing geometry.
  Volume suspension, two-cascade fairness and TSR scale 1.0 → 0.5 → 1.0 also
  pass. One baseline readback waited for the matching submitted generation;
  subsequent captures use stable dimensions/generations and finite output.
- An isolated constant-emission enclosure on Vulkan/Default produces exactly
  (0.25, 0.5, 1.0) in all 16 interior irradiance texels of the selected probe,
  doubles at strength two and reaches zero when emission is disabled. This
  establishes the atlas normalization on that combination; repeat elsewhere.
- The Vulkan stereo root cause is now identified: a frame containing only bound
  stereo output was required to observe the retained desktop source descriptor.
  The reviewed correction gates descriptor/artifact validation by the recorded
  pipeline, viewport and stable output owner, retaining same-owner generation
  checks, replay state and independent swapchain-writer validation.
- Source-ready allocation scopes cover DDGI compute, composition, environment
  and debug work. A live collector requires fresh full 240-sample windows and
  accepted update progress; no allocation result is accepted before build 28.
  Sampling also now computes edge interpolation after clamping the probe cell,
  fixing the upper-boundary selection and below-volume wraparound.
- Builds 28b, 29 and 30 pass with zero warnings/errors. Vulkan/Advanced stereo
  still rejects normal presentation. Per-program descriptor tracing proves the
  mono final draw binds the stereo FXAA image after its transient source fields
  are cleared. The present publisher now captures source texture, renderer,
  sampling state, orientation and generation at enqueue time; deferred retry
  semantics are being reviewed before the build-31 runtime repeat.
- OpenGL/Default build 30 passes actual GPU atlas normalization: all 16 interior
  texels match constant emission (0.25, 0.5, 1), double with strength two and
  become zero when disabled. Probe 220, enclosed in a 0.8-unit box, is inactive
  with relocation disabled; enabling relocation moves it outside the box by
  approximately (0.00386, 0.00679, -0.63914), within its half-cell bounds, and
  reactivates it. Geometry and volume settings are restored afterward.
- The first full OpenGL/Default allocation window fails: geometry preparation
  averages 44,025.6 managed bytes per command and composition allocates 10,368.
  A 12-second runtime trace identifies boxed flag checks, uniform type arrays,
  unconditional preparation strings, texture-binding bookkeeping and delegates.
  Source fixes target those measured sites; no zero-allocation claim is made.
- Build 21 passes with zero warnings/errors after correcting compile integration
  issues in builds 18–20. OpenGL/Advanced now exports the real resolved surface,
  uses its AO target, preserves specular IBL and supplies composite dimensions.
  Its DDGI means are blue strength 8: 0.00542; emission off: exact zero; green
  strengths 8/4: 0.00749/0.00387. The blue bounce and regular viewport were viewed.
- OpenGL/Advanced emissive map reloads give checker/black/white means
  0.00533/0/0.01275. Regular HDR reaches the authored emission maximum 8; normal
  and reversed depth and a second camera produce finite illumination.
- OpenGL/Advanced baked capture/save/load retains mean DDGI 0.00547 with no
  dynamic update progress, including after emission is disabled. Replacing it
  with a dark bake gives exact zero. A missing path clears contribution and
  restoring the lit asset recovers the original lighting without tracing.
- OpenGL/Advanced point, spot, directional and gradient-sky sources all produce
  finite nonzero DDGI. Sky off/on/off reaches zero/restored/zero while update
  counters continue advancing. Vulkan/Advanced also passes this lifetime check;
  Vulkan/Default passes the complete repeat in build 27. Stereo composite requires
  OVR multiview so both shader and quad setup agree; both OpenGL pipeline captures
  now pass, while Vulkan presentation acceptance remains open.

| Backend / pipeline | Expanded emissive fixture | Remaining acceptance |
|---|---|---|
| OpenGL / Default | Passed emission off/on, RGB/strength, maps, mono/stereo composition through FXAA, depth modes, two cameras, earlier baked replacement/recovery, inactive-probe upload/reactivation, cutout/transmission, repeated bone/morph, lights/skies, lifecycle, two-cascade fairness, ownership, TSR resize, exact atlas normalization, bounded embedded-probe escape, specular coexistence, single/mixed-probe occlusion, scene detach/reattach, bake revision 2, dynamic/baked renderer replacement, eight-probe topology/cache recovery, specular repeat after ownership changes and all ten command allocation windows | Interrupted-update recovery |
| Vulkan / Default | Passed emission off/on, RGB/strength, maps, mono composition, depth modes, two cameras, earlier baked replacement/recovery, inactive-probe upload/reactivation, cutout/transmission, bone/morph, lights/skies, lifecycle, two-cascade fairness, TSR resize and exact atlas normalization | Sustained stereo, ownership, bake revision 2, renderer replacement, scene reload, embedded-probe escape, single/mixed-probe occlusion, eight-probe/specular scenes, interruption and allocations |
| OpenGL / Advanced | Passed emission off/on, RGB/strength, maps, mono/stereo composition through FXAA, depth modes, two cameras, earlier baked replacement/recovery, inactive-probe upload/reactivation, lights/skies, masked raster/transport, repeated bone/morph, lifecycle, fairness, ownership, TSR resize, atlas normalization, bounded embedded-probe escape, single/mixed-probe occlusion, scene detach/reattach, bake revision 2, dynamic/baked renderer replacement, specular coexistence, eight-probe topology/cache recovery and all ten command allocation windows | Interrupted-update recovery |
| Vulkan / Advanced | Passed emission off/on, RGB/strength, maps, mono composition, depth modes, two cameras, earlier baked replacement/recovery, lights/skies, isolated bone/morph, sustained stereo through FXAA and ownership in build 48 | Masked raster/transport, lifecycle/fairness, resize, normalization, bake revision 2, renderer replacement, scene reload, embedded-probe escape, single/mixed-probe occlusion, eight-probe/specular scenes, interruption and allocations |

Continue in this order:

1. Validate Default's stereo bloom correction from build 49b, then repeat both eyes,
   post-restoration progress and ownership. Advanced passes these in build 48.
   Resolve the zero Vulkan BRDF lookup before repeating the eight-probe
   topology/cache and specular scenes on both Vulkan pipelines; both OpenGL
   pipelines now pass them.
2. Complete Vulkan/Advanced masked transport, inactive upload, lifecycle, fairness,
   resize and normalization repeats. Both OpenGL normalization checks now pass.
3. Repeat bounded embedded-probe escape and single/mixed-probe occlusion on
   Vulkan. Both OpenGL pipelines pass these checks after the distance-filter fix.
4. Inspect backend shader, descriptor, synchronization and validation failures
   in each run; do not accept a silent backend or rendering-path fallback.
5. Complete interrupted-update recovery, specular coexistence and live allocation
   checks; retain existing emission, deformation, bake and memory evidence unless
   changes require a repeat.
6. Refresh the obsolete contracts after live acceptance and user clearance for
   test edits, run focused validation,
   and update stable support documentation with the measured limits.

Detailed attempts and evidence remain in the
[working-copy investigation](../../../investigations/rendering/2026-09-20-ddgi-working-copy-verification.md).

## Working Rules

- Keep DDGI as a distinct diffuse GI family. Do not smuggle it into `LightProbeComponent` ownership or the current Delaunay interpolation path.
- Keep diffuse GI selection separate from specular IBL ownership. DDGI must be able to coexist with current reflection probes and sky IBL.
- **Zero-CPU-Readback GPU Execution**: Do NOT route probe rays through the host-query `BvhRaycastDispatcher.Enqueue/ProcessCompletions` path (which performs GPU-to-CPU fence stalls, PCIe readback, and managed array allocations). Probe ray generation, BVH traversal, hit shading, and atlas updates must execute entirely on the GPU via compute shaders against scene BVH SSBOs (`GpuBvhTree`, `Nodes`, `Triangles`).
- Treat visibility atlases, probe relocation, and persistent atlas-backed resources as core feature work, not polish.
- No steady-state managed allocations in DDGI hot paths.
- Size ray and hit buffers by the per-frame probe update budget (`MaxUpdatedProbesPerFrame * RaysPerProbe`), not by total grid volume size.
- Validate each phase with the smallest targeted build and scene coverage that proves the new behavior.

## Correctness Remediation — 2026-09-20 Review

Evidence and reproduction details:
[working-copy verification](../../../investigations/rendering/2026-09-20-ddgi-working-copy-verification.md).
Repair the dependency chain in this order. Mark a step complete only when the
implementation and the stated validation are complete; record partial progress
and remaining evidence explicitly. Do not add or modify tests before the user
clears test work after feature validation.

### R1 — Restore a complete GPU update path

- [x] R1.1 Fix duplicate DDGI node insertion in `BootstrapLightingBuilder` and apply the unit-testing GI selector to the created scene pipeline. Validate a fresh isolated DDGI startup without manually attaching the component or selecting the pipeline.
- [x] R1.2 Give the shared BVH `Ray` type one owner and compile all DDGI shaders with expanded engine snippets. Validate the actual trace shader links on the live OpenGL backend.
- [x] R1.3 Supply a world-space triangle BVH independently of CPU/GPU mesh draw submission. Remove the impossible `GPUScene` to `GpuMeshBvh` cast and dummy triangle fallback. Validate real triangle hits under the default `CpuDirect` path and explicit diagnostics when geometry preparation fails.
- [ ] R1.4 Make the probe update a single transaction: prepare resources and schedule once, run dependent passes only after their inputs succeed, and advance history only after all writes complete. Validate no atlas/history update on missing BVH data or shader failure.
- [ ] R1.5 Initialize nominal probe positions from grid coordinates and separate cascade-local tile/ring indices from global probe-buffer indices. Validate distinct ray origins and correct state ranges for two or more cascades.
- [x] R1.6 Size atlases and probe/ray/hit/radiance buffers from validated active-volume dimensions and the per-frame update budget. Reject overflow/backend-limit violations before dispatch. Validate the documented `32 x 4 x 32` grid, multiple cascades and 256 rays per probe without out-of-range access.

### R2 — Correct lighting and sampling

- [ ] R2.1 Fix the extra division by pi in indirect feedback and accept explicit zero intensity/bias values. Validate zero intensity produces zero indirect RGB and a constant incident-radiance case preserves the intended normalization.
- [ ] R2.2 Relocate embedded probes before final classification; use valid hit sentinels and an appropriate all-ray backface fraction. Validate a probe inside geometry can escape and valid triangle zero is shaded.
- [x] R2.3 Follow the engine depth range, reverse-Z and framebuffer orientation contracts in mono/stereo reconstruction. Validate standard/reverse depth in OpenGL and explicitly track Vulkan/stereo validation.
- [x] R2.4 Enforce bounded-volume sampling for one cascade and safe interpolation for singleton axes. Validate outside-volume contribution and smallest supported grids.
- [ ] R2.5 Bind scene material/emissive information and shadow visibility for hit lighting; preserve diffuse/specular energy separation. Validate a colored Cornell box, sealed/open room and moving light/occluder before claiming material-correct multi-bounce GI.
- [x] R2.6 Apply the authored tint and implement actual probe debug drawing. Validate the viewport controls through live captures.

### R3 — Repair scheduling, lifecycle and persistence

- [x] R3.1 Schedule SlowUpdate against an independently advancing frame counter, and advance only the cascade actually updated. Validate repeated periodic updates, invalidation, partial budgets and cascade round-robin coverage.
- [x] R3.2 Snap camera-scrolling grid origins before tracing and invalidate/reset or scroll the affected history coherently. Validate camera movement across cells and the single-cascade case.
- [ ] R3.3 Make baked capture include real GPU probe relocation/classification data plus atlas payloads. Explicitly upload buffers and texture arrays on both backends. Validate capture/save/load with relocated and inactive probes.
- [ ] R3.4 Track baked asset and resource-generation identity; invalidate on mode/asset changes. Validate dynamic-to-baked, asset replacement, resource regeneration and scene reload.
- [ ] R3.5 Refresh volume registration from activation/deactivation hooks and eliminate per-frame cascade-uniform allocations. Validate inactive-node activation and steady-state allocation behavior.
- [x] R3.6 Connect measured GPU update time to the actual cascade dispatch budget, or report unsupported fixed-time budgeting explicitly. Validate that the configured budget controls submitted probe work.

### R4 — Acceptance and honest documentation

Implementation progress is recorded below and in the verification note. The OpenGL bounded-volume, CPU-direct geometry, resize, dynamic lighting, bake/upload, selected cascade, scheduling, zero-intensity, tint, and singleton-grid checks have live evidence. Checked remediation items have completed their stated validation; other items retain explicit evidence gaps below.

### Corrective validation evidence — 2026-09-20

The following observations support the completed remediation items and record
partial validation for the remaining compound acceptance items.

- The isolated editor build completed with zero warnings and errors, and all 17
  expanded DDGI, geometry, debug, and composite shaders passed `glslangValidator`.
- Automatic DDGI startup on `CpuDirect` produced an aggregate world geometry BVH
  with 1,139 triangles and 2,277 nodes. An MCP resize to `8 x 6 x 8` produced
  384 probes, a `48 x 288` irradiance atlas, a `128 x 768` visibility atlas, and
  49,152 ray capacity.
- Dynamic captures with a blue emitter and a moved block showed coherent
  indirect-light changes from two camera views. These captures establish bounded
  dynamic behavior, not material-correct multi-bounce acceptance.
- Baked assets A (256 probes, 32 relocated, 1 inactive) and B (384 probes, 65
  relocated, 0 inactive) round-tripped with byte-verified payloads. Their GPU
  RGBA float atlas hashes matched saved payloads after the parent-direct upload
  correction. Dynamic→Baked→Dynamic and A→B→A replacement across sizes passed
  after settling, including ray-buffer regeneration while baked.
- `32 x 4 x 32` with three cascades, 256 rays, and a 256-probe budget allocated
  12,288 probes and 65,536 rays; all three 4,096-probe cascades updated, and
  all atlas layers remained finite. SlowUpdate interval 30 advanced the completed
  update counter from 10,639 to 10,647 with all three cursors advancing. Fixed budgets of 0.05 ms
  and 5 ms submitted one and two probes respectively with nonzero timings;
  this is adaptive behavior evidence, not a timing benchmark.
- `1 x 2 x 2` and `1 x 1 x 1` grids produced finite, correctly sized atlases.
  Zero intensity produced exact zero indirect RGB; a green tint produced pure
  green output; GPU probe billboards drew from the live probe buffer.
- Moving the bounded dynamic volume to `(100, 100, 100)` produced exact zero
  visible-scene DDGI RGB. Restoring its origin and toggling the node inactive
  then active regenerated 256-probe capacity; after warmup it completed update
  15,922 with `updatedProbeCounts[256]` and finite output (max 2.082, mean
  0.07657).
- Corrupt baked input leaves the editor responsive and DDGI uninitialized.
  Repairing the same file and toggling Dynamic→Baked restored both atlas hashes
  exactly. Baked layout edits restored the saved grid and origin without
  resetting live intensity; a Dynamic `4 x 4 x 4` grid returned to the same
  asset's `8 x 4 x 8` layout and exact atlas hashes on re-entry.
- With one camera-scrolling cascade and a 32-probe budget, moving the camera
  five world units in X snapped the origin from `(0, 2, 7.714286)` to
  `(3.857143, 2, 7.714286)`. The first observation after moving had 32 updated
  probes and warmup active; after settling all 256 probes were updated, warmup
  ended, and output remained finite.
- Cold-start OpenGL image allocation now creates target-typed texture objects
  before storage/image binding, retaining reserved names for texture views.
  The rebuilt session had no high-severity invalid-texture or illegal-map-read
  messages. Neutral debug exposure also produced a coherent final viewport
  with the camera's auto-exposure setting still enabled.
- Raw reverse-Z DDGI now matches normal depth after a two-second settle:
  mean RGB `0.0638661` versus `0.06386727`, with finite samples and identical
  alpha coverage. Both screen shaders use raw-depth unprojection because the
  camera inverse projection already contains the depth convention. A separate
  final-composite failure inherited scene depth testing; both DDGI composite
  factories now disable depth testing. The final images show the complete
  colored enclosure in both modes and from a second camera. Reverse-depth
  grid lines still draw through geometry with `LightProbesAndIbl` selected too;
  this separate editor-grid issue was isolated by hiding GridFloor for the
  final DDGI captures.
- Leaving debug mode restored normal lighting with authored `AutoExposure=true`
  and `Exposure=1` intact. The final cold session recorded zero invalid-texture,
  illegal-map-read, or DDGI failure messages. Startup multiview material errors
  remain a separate renderer limitation.
- The existing 54 targeted contracts reran after live validation: 46 passed,
  8 failed. Failures assert old geometry buffer names, fixed four-cascade
  allocation, old shader expressions without diffuse weighting, probe work
  above the configured cap, or incomplete/incorrect baked payloads. Source and
  live evidence explain these failures; refreshing those tests requires the
  explicit clearance specified by the repository testing policy.

- [ ] R4.1 Re-run the isolated editor build, shader compilation and existing targeted contracts after runtime correction; record what each check proves.
- [x] R4.2 Capture irradiance, visibility and final output from multiple camera positions in the shared validation scenes. Use RenderDoc for unresolved pass/resource failures.
- [ ] R4.3 Verify Vulkan, stereo and baked behavior before claiming support; validate Advanced's implemented resource/command integration through its live rendering path.
- [x] R4.4 Replace unmeasured production/performance/memory claims in stable docs with validated behavior, actual allocations and remaining limitations.
- [ ] R4.5 Update the investigation record with attempted fixes, observed results, remaining issues and user feedback. Add regression tests only after explicit user clearance under the repository testing policy.

### Remaining acceptance work

- After test clearance, replace the eight obsolete assertions/fixtures with
  valid complete bake data, active resource dimensions, the dedicated geometry
  bindings, diffuse material weighting and enforced update caps; rerun the
  targeted suite without weakening runtime guards.
- Exercise missing geometry and intentionally unavailable shader programs to
  verify update transactions never advance partially written history. Capture
  per-cascade ray origins and probe state during partial updates.
- Compare constant-radiance transport and sealed/open rooms, then animate a
  light and a skinned or blend-shape occluder. Confirm relocation can reactivate
  an embedded probe and measure steady-state managed allocations.
- Validate scene reload and multiple viewport ownership, then perform separate
  Vulkan and stereo runs including baked uploads. Advanced now contains the
  resource and command integration; its runtime acceptance remains open.
- Track the independently reproduced reverse-depth editor-grid occlusion issue
  separately from DDGI: six InfiniteGrid shader variants and five embedded
  fallback shaders invert already-reversed projected depth. Replace those
  writes with `gl_FragDepth = depth` and validate both depth modes. Review
  mutable 2D/3D first-image storage readiness for Landscape/MeshSDF as a
  separate existing OpenGL issue.

## Exit Conditions For This Work Item

- `EGlobalIlluminationMode.DDGI` produces visible, inspectable diffuse GI.
- DDGI works on the current OpenGL baseline through the GPU BVH path.
- DDGI diffuse coexists cleanly with reflection-probe and sky IBL specular without double-counting diffuse.
- The engine has a bounded-volume path first, then a documented large-scene scaling path.
- The work doc and stable GI docs describe DDGI honestly, including known limitations such as zero-thickness wall leaks and scene-scale bias tuning.

## Cross-Phase Validation Scenes

Keep these scenes alive across the whole implementation instead of inventing a new validation scene every phase:

- [ ] Cornell-box color-bleed scene
- [ ] sealed-room versus open-room leak test
- [ ] moving dynamic-geometry stress scene with objects passing through probe locations
- [ ] day-night or color-changing key light transition scene
- [ ] reflective interior scene that combines DDGI diffuse with current specular IBL or reflections

## Phase 0 - Honest Scaffolding And Pipeline Contracts

Outcome: the engine exposes DDGI as a real planned mode with honest stubs, provider interfaces, and author-facing scaffolding.

- [x] verify `DDGI = 8` in `XREngine.Data/Core/Enums/EGlobalIlluminationMode.cs` (already declared)
- [x] add `bool UsesDDGI { get; }` to `XREngine.Runtime.Rendering/Rendering/Pipelines/Contracts/IGlobalIlluminationPipelineProvider.cs`
- [x] implement `public bool UsesDDGI => _globalIlluminationMode == EGlobalIlluminationMode.DDGI;` in:
  - `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs`
  - `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs`
- [x] expose the new GI mode anywhere `UserSettings` or startup settings surface GI mode selection:
  - `XREngine.Data/Core/UserSettings.cs`
  - `XREngine.Runtime.Bootstrap/Settings/GameStartupSettings.cs`
  - `XREngine.Runtime.Bootstrap/Engine/Subclasses/Engine.EffectiveSettings.cs`
  - `XREngine.Runtime.Bootstrap/Engine/Subclasses/Rendering/EngineRenderingSettingsApplication.Preferences.cs`
- [x] add `DefaultPipelineResourceFeature.DdgiResourcesEnabled = 1UL << 31` to `DefaultRenderPipeline.Resources.cs`
- [x] update `GlobalIlluminationMode switch` in `DefaultRenderPipeline.Resources.cs` to set `DefaultPipelineResourceFeature.DdgiResourcesEnabled` when `EGlobalIlluminationMode.DDGI` is active
- [x] add neutral DDGI resource names to both pipeline texture-name declarations:
  - `DDGITextureName = "DDGITexture"`
  - `DDGICompositeFBOName = "DDGICompositeFBO"`
  - `DDGIIrradianceAtlasTextureName = "DDGIIrradianceAtlas"`
  - `DDGIVisibilityAtlasTextureName = "DDGIVisibilityAtlas"`
  - `DDGIProbeStateBufferName = "DDGIProbeStateBuffer"`
  - `DDGIRayBufferName = "DDGIRayBuffer"`
  - `DDGIHitBufferName = "DDGIHitBuffer"`
- [x] add a no-op or explicit-not-implemented DDGI branch in command-chain routing (`DefaultRenderPipeline.CommandChain.cs`) with honest logging and profiler labels (`VPRC_DDGICompositePass`)
- [x] add `DDGIVolumeComponent` scaffolding under `XREngine.Runtime.Rendering/Scene/Components/Lights/DDGIVolumeComponent.cs`:
  - volume bounds (`Vector3 HalfExtents`)
  - probe counts (`Vector3Int ProbeCounts`, default 16x8x16 for local bounded test)
  - ray budget per probe (`uint RaysPerProbe`, default 192)
  - update budget per frame (`uint MaxProbesUpdatedPerFrame`)
  - tuning parameters: `Hysteresis` (0.97f), `NormalBias` (0.1f), `ViewBias` (0.2f), `ChebyshevPower` (4.0f)
  - flags: `RelocationEnabled`, `ClassificationEnabled`, `BakedMode`, `DebugDrawProbes`
  - internal registry for lifecycle registration (`Register`, `Unregister`, `TryGetActive`)
- [x] add unit-testing world settings and bootstrap hook under `XREngine.Runtime.Bootstrap/Settings/UnitTestingWorldSettings.cs` and `BootstrapLightingBuilder.cs` for bounded DDGI volume bring-up
- [x] update stable GI docs only enough to describe DDGI as planned or experimental once the runtime selector exists

Acceptance criteria:

- selecting DDGI is a real code path and not an unhandled enum case
- the renderer states clearly that DDGI is stubbed until the real passes land
- a scene can contain a `DDGIVolumeComponent` without special-case hacks

## Phase 1 - Volume Owner And Persistent Resource Contract

Outcome: DDGI has a real runtime owner and persistent atlas-backed resource set with no steady-state allocations.

- [x] define the authoritative runtime DDGI state object owned by `DDGIVolumeComponent` (or rendering-side companion `DDGIVolumeRuntimeState`)
- [x] allocate the probe state buffer as a persistent SSBO:
  - stores base world position, current relocation offset, flags (active/sleeping/invalid), update priority/timestamp
- [x] allocate the irradiance atlas as `R11G11B10F` (6x6 texels per probe: 4x4 interior + 1-texel border for bilinear interpolation across octahedral edges)
- [x] allocate the visibility atlas as `RG16F` (16x16 texels per probe: 14x14 interior + 1-texel border storing mean distance and mean distance squared)
- [x] allocate the screen-space DDGI output texture (`DDGITextureName`) via `RenderPipelineResourceLayoutBuilder`:
  - `EPixelInternalFormat.Rgba16f`, sized to internal render resolution
  - for stereo, allocate with layer count matching declared eye count (`layers = 2`)
- [x] size persistent ray buffer and hit buffer SSBOs to the per-frame update budget:
  - capacity = `MaxUpdatedProbesPerFrame * RaysPerProbe` (prevents multi-megabyte buffer bloat)
  - ray buffer: origin (`vec4`, .w = tMin), direction (`vec4`, .w = tMax), probe index / cascade index
  - hit buffer: distance `t`, objectId, triangleIndex, barycentrics
- [x] lock atlas indexing conventions for probe tile addressing, octahedral layout, and cascade slices:
  - tile layout in atlas: $N_{\text{probesX}} \times (N_{\text{probesY}} \cdot N_{\text{probesZ}})$ grid or explicit 2D atlas packing
- [x] plumb resize, disposal, and stereo-safe lifetime handling for all DDGI resources in `DefaultRenderPipeline.Resources.cs`
- [x] add debug names and profiler labels for every DDGI resource

Acceptance criteria:

- DDGI resources are visible in renderer resource dumps and debug tooling
- resource lifetime is stable across resize, scene reload, and stereo paths
- no per-frame managed allocations are introduced by enabling the DDGI path

## Phase 2 - Probe-Ray Generation And GPU BVH Trace Backend

Outcome: DDGI generates probe rays and traces them entirely on the GPU against scene geometry with zero CPU readback.

- [x] define deterministic per-probe ray generation compute shader (`Build/CommonAssets/Shaders/Compute/DDGI/ddgi_raygen.comp`):
  - generate stratified ray directions using spherical Fibonacci or octahedral mapping with random frame rotation
  - generate ray origin as probe position + relocation offset
  - write `RayInput` into `DDGIRayBuffer` SSBO
- [x] execute trace pass synchronously on the main render context within `DefaultRenderPipeline.CommandChain.cs` (avoids multi-GPU / single-GPU fallback complexity of `SecondaryContext`)
- [x] ensure `VPRC_BuildAccelerationStructure` publishes `GpuBvhTree`, `Nodes`, and `Triangles` SSBOs prior to DDGI trace execution
- [x] integrate direct GPU BVH compute traversal:
  - dispatch specialized closest-hit compute shader (`Build/CommonAssets/Shaders/Compute/DDGI/ddgi_trace.comp` reusing `BvhRaycastCore.glsl` snippet)
  - read `DDGIRayBuffer` directly from binding 0; traverse scene BVH `Nodes` and `Triangles`
  - write `HitRecord` directly into `DDGIHitBuffer` SSBO (binding 3) on GPU
  - strict requirement: ZERO calls to `BvhRaycastDispatcher.Enqueue/ProcessCompletions`, ZERO CPU fence stalls, ZERO host readback
- [x] define the unified hit contract shared by future hardware RT / Vulkan backends:
  - hit distance $t$, object ID, triangle index, barycentric coordinates
- [x] add GPU probe billboard visualization (`VPRC_DDGIDebugVisualization.cs`); ray/hit-vector visualization remains unimplemented

Acceptance criteria:

- DDGI probe rays trace successfully on the current OpenGL baseline entirely on GPU
- probe billboards make live probe placement and state inspectable; ray/hit-vector validation remains open
- zero CPU readback occurs during the trace stage

## Phase 3 - Hit Shading, Irradiance Updates, And Visibility Updates

Outcome: traced rays become real probe data that converges over time with infinite-bounce indirect lighting.

- [x] implement GPU hit shading compute pass (`Build/CommonAssets/Shaders/Compute/DDGI/ddgi_hit_shade.comp`):
  - reconstruct world position: $\mathbf{x} = \mathbf{o} + t \cdot \mathbf{d}$
  - recover a surface normal from hit geometry; full per-vertex normal interpolation remains unvalidated
  - compute direct lighting from primary directional light
  - sample previous-frame DDGI irradiance at hit position ($\text{sampleDDGI}(\mathbf{x}, \mathbf{n})$) to achieve multi-bounce indirect convergence
  - write shaded hit radiance and hit distance into intermediate probe hit buffer (`DDGIRayRadianceBuffer`)
- [x] define warm-start behavior:
  - on frame zero or invalidated probes, set hysteresis to 0.0f for instantaneous convergence
  - during normal tracking, use hysteresis default of 0.97f (0.03 new + 0.97 old)
- [x] implement irradiance update compute pass (`Build/CommonAssets/Shaders/Compute/DDGI/ddgi_update_irradiance.comp`):
  - one workgroup per probe, matching tile texels (6x6)
  - gather cosine-weighted ray contributions over the hemisphere corresponding to each octahedral direction
  - blend with existing irradiance atlas texel using exponential moving average with hysteresis
- [x] implement visibility update compute pass (`Build/CommonAssets/Shaders/Compute/DDGI/ddgi_update_visibility.comp`):
  - one workgroup per probe, matching visibility tile texels (16x16)
  - accumulate distance moments: $E[r]$ (mean hit distance) and $E[r^2]$ (mean squared hit distance)
  - blend into visibility atlas with hysteresis
- [x] implement octahedral border replication compute pass (`Build/CommonAssets/Shaders/Compute/DDGI/ddgi_border_copy.comp`):
  - copy/mirror interior border texels across octahedral edges into the 1-pixel border to ensure seamless hardware bilinear filtering
- [x] add explicit invalidation paths for large scene/light edits
- [x] add debug views for raw irradiance atlas, visibility atlas, and per-probe update state

Acceptance criteria:

- DDGI probe atlases converge over multiple frames without high-frequency flicker
- sealed-room leak test materially outperforms classic light probes
- hit shading, irradiance update, and visibility update timings are independently visible in profiler traces

## Phase 4 - Screen Sampling And Specular IBL Decoupling

Outcome: DDGI diffuse indirect lighting becomes visible in the final frame while coexisting cleanly with reflection probes.

- [x] implement `sampleDDGI` as a reusable GLSL include (`Build/CommonAssets/Shaders/Snippets/DDGISampling.glsl`):
  - find the enclosing 8-probe regular-grid cell from shading position $\mathbf{x}$
  - apply normal bias and view bias: $\mathbf{x}' = \mathbf{x} + \mathbf{n} \cdot \text{normalBias} + \mathbf{v} \cdot \text{viewBias}$
  - for each of the 8 surrounding probes:
    - compute probe world position including relocation offset: $\mathbf{p}_i = \mathbf{p}_{\text{base}} + \mathbf{o}_i$
    - compute vector to probe: $\mathbf{v}_i = \mathbf{p}_i - \mathbf{x}'$, distance $d_i = \|\mathbf{v}_i\|$, direction $\mathbf{d}_i = \mathbf{v}_i / d_i$
    - compute cosine weight: $w_{\text{cosine}} = \max(0.0, \text{dot}(\mathbf{n}, \mathbf{d}_i))$
    - compute Chebyshev visibility weight from visibility atlas moments:
      - sample $(E[r], E[r^2])$ from visibility atlas using octahedral projection of $-\mathbf{d}_i$
      - if $d_i \le E[r]$, $w_{\text{vis}} = 1.0$
      - else $\sigma^2 = \max(E[r^2] - E[r]^2, \epsilon)$, $p_{\max} = \sigma^2 / (\sigma^2 + (d_i - E[r])^2)$
      - $w_{\text{vis}} = \text{clamp}((p_{\max} - \text{threshold}) / (1.0 - \text{threshold}), 0.0, 1.0)^k$
    - compute trilinear grid weight $w_{\text{grid}}$
    - combined probe weight: $w_i = w_{\text{grid}} \cdot w_{\text{cosine}} \cdot w_{\text{vis}}$
    - sample irradiance from irradiance atlas using octahedral direction $\mathbf{n}$
    - accumulate weighted irradiance: $\sum (w_i \cdot \text{Irradiance}_i) / \sum w_i$
- [x] implement `VPRC_DDGICompositePass.cs` and composite fragment shader (`Build/CommonAssets/Shaders/Scene3D/DDGIComposite.fs`):
  - reconstruct world position and normal from G-buffer depth and normal textures
  - sample DDGI diffuse via `sampleDDGI`
  - write resolved diffuse GI to `DDGITextureName`
  - blend additively into `ForwardPassFBOName` (via `XRQuadFrameBuffer` with `AdditiveBlend`)
  - support single-pass stereo (`DDGICompositeStereo.fs` with multiview layer selection)
- [x] **Decouple Specular IBL from Diffuse GI Selection**:
  - in `DefaultRenderPipeline.BindingPublishers.cs`:
    - decouple reflection probe synchronization (`SyncPbrLightingResourcesForFrame`) and array binding from `UsesLightProbeGI`; ensure probes sync whenever `UsesLightProbeGI || UsesDDGI`
  - in `DeferredLightCombine.fs` (and `DeferredLightCombineStereo.fs`):
    - when DDGI is active, suppress classical probe diffuse accumulation (`probeAmbient = vec3(0.0)`) to prevent double-counting diffuse lighting
    - retain reflection probe specular sampling (`prefilteredColor` from `PrefilterArray`) so glossy reflections remain fully active
- [x] add DDGI-only debug mode and probe-neighborhood debug mode

Acceptance criteria:

- enabling DDGI visibly changes the frame with stable diffuse GI
- DDGI sampling works in deferred rendering and single-pass stereo rendering
- diffuse DDGI and reflection-probe specular IBL coexist without double-counting or loss of reflections

## Phase 5 - Probe Relocation, Classification, And Scheduling

Outcome: DDGI behaves robustly around walls and moving dynamic geometry while respecting GPU execution budgets.

- [x] implement probe relocation compute pass (`Build/CommonAssets/Shaders/Compute/DDGI/ddgi_relocate.comp`):
  - analyze probe ray hit distances: if rays in certain directions hit backfaces or are at distance $< \text{threshold}$, push probe away along ray direction
  - strictly clamp relocation offsets to $[-\frac{1}{2}\Delta, +\frac{1}{2}\Delta]$ along each axis (dual-grid constraint)
  - update probe state buffer with new relocation offset
- [x] implement probe classification:
  - detect probes embedded inside solid geometry where all rays hit backfaces; flag as inactive so they are excluded from sampling
  - detect probes in open static space with stable lighting; flag as sleeping
- [x] implement budget-aware update scheduler:
  - maintain priority queue or round-robin index in probe state buffer
  - select at most `MaxProbesUpdatedPerFrame` (e.g. 256 or 512) to trace each frame
  - support fixed-time mode (target GPU budget in ms) and fixed-quality mode (fixed ray count per frame)
- [x] add profiler counters for relocated, sleeping, inactive, and active updated probes

Acceptance criteria:

- moving geometry through probe locations does not cause dark or bright light leaks
- lower-end budgets increase indirect convergence time rather than creating screen-space noise
- scheduling controls maintain consistent frame times

## Phase 6 - Cascades And Large-Scene Scaling

Outcome: DDGI scales beyond a single local volume.

- [x] implement camera-relative or scrolling cascade path after bounded volumes are validated:
  - cascade 0: fine grid (32x4x32 probes, tight spacing e.g. 1m)
  - cascade 1: medium grid (doubled spacing, e.g. 2m)
  - cascade 2: coarse grid (quadrupled spacing, e.g. 4m)
- [x] support per-cascade update frequency (near cascades update every frame; far cascades update every $N$ frames)
- [x] implement cascade-local indexing and atlas resource addressing:
  - store cascades in a Texture2DArray where layer index = cascade index
- [x] fade out and disable visibility storage on outermost coarse cascades where occlusion detail is minimal
- [ ] measure and validate a per-cascade memory budget; no fixed-memory production claim is accepted
- [x] add debug views for cascade coverage and active update budget per cascade

Acceptance criteria:

- large scenes no longer require every probe to update every frame
- DDGI cost is constrained by budget rather than exploding with world scale
- cascade transitions are smooth without visible seams

## Phase 7 - Baked And Infinite-Latency DDGI

Outcome: DDGI has a real fallback tier for budget or legacy platforms.

- [x] define the baked DDGI asset format for irradiance and visibility atlases
- [x] serialize and load baked probe atlases through the same runtime sampling path used by dynamic DDGI
- [x] expose per-volume baked, slow-update, or fully dynamic mode selection
- [x] ensure baked DDGI still uses the same visibility-based leak rejection path
- [x] add editor workflow notes for baking or regenerating DDGI volumes
- [x] document feature limits for baked DDGI: no dynamic light or dynamic geometry capture

Acceptance criteria:

- a DDGI volume can load and render without runtime tracing
- baked DDGI still benefits from the visibility atlas and avoids classic probe leak behavior
- the runtime does not fork into a second unrelated DDGI shading code path

## Phase 8 - Hybrid Integrations And Productization

Outcome: DDGI fits naturally into the rest of the renderer and docs.

- [x] evaluate whether ReSTIR hit shading should sample DDGI for diffuse fallback or secondary diffuse lighting
- [x] evaluate whether glossy RT and DDGI ray work should share a dispatch or packed buffer path
- [x] decide whether a short-range AO (GTAO/SSAO) or other near-field detail layer should complement DDGI
- [ ] finalize support language after backend and visual acceptance; current status is experimental
- [ ] update stable GI docs after backend and visual acceptance
- [ ] add or update focused unit tests only after explicit user clearance under the testing policy
- [ ] run targeted validation on stereo and unit-testing-world entry points

Acceptance criteria:

- DDGI is no longer an isolated experimental branch with unclear interaction against the rest of the lighting stack
- stable docs describe how DDGI relates to light probes, ReSTIR, Surfel GI, and specular IBL
- unit tests exist for the core math and atlas plumbing that can regress silently

## Key Files

### Existing Files to Modify

- `XREngine.Data/Core/Enums/EGlobalIlluminationMode.cs` (contains `DDGI = 8`)
- `XREngine.Data/Core/UserSettings.cs` (GI mode serialization & settings)
- `XREngine.Runtime.Bootstrap/Settings/GameStartupSettings.cs` (startup GI override)
- `XREngine.Runtime.Bootstrap/Settings/UnitTestingWorldSettings.cs` (test world GI toggles)
- `XREngine.Runtime.Bootstrap/Builders/BootstrapLightingBuilder.cs` (DDGI volume bootstrapping)
- `XREngine.Runtime.Bootstrap/Engine/Subclasses/Engine.EffectiveSettings.cs` (effective GI mode resolution)
- `XREngine.Runtime.Bootstrap/Engine/Subclasses/Rendering/EngineRenderingSettingsApplication.Preferences.cs` (pipeline GI preference application)
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Contracts/IGlobalIlluminationPipelineProvider.cs` (add `UsesDDGI`)
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs` (`UsesDDGI` helper)
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.Textures.cs` (texture name constants)
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.Resources.cs` (`DdgiResourcesEnabled` mask, `DeclareGlobalIlluminationResources`)
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.FBOs.cs` (composite FBO creation)
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.BindingPublishers.cs` (reflection probe specular IBL decoupling)
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs` (DDGI passes execution placement)
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs` (`UsesDDGI` helper)
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.Textures.cs` (texture name constants)
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.CommandChain.cs` (command chain integration)
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs` (suppress classical probe diffuse when DDGI active while retaining specular IBL)
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightCombineStereo.fs` (stereo counterpart)
- `Assets/UnitTestingWorldSettings.jsonc` (test settings schema)

### Expected New Files

- `XREngine.Runtime.Rendering/Scene/Components/Lights/DDGIVolumeComponent.cs` (scene volume component)
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/GI/VPRC_DDGITracePass.cs` (GPU BVH ray dispatch)
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/GI/VPRC_DDGIUpdatePass.cs` (hit shading + irradiance & visibility atlas updates)
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/GI/VPRC_DDGICompositePass.cs` (screen sampling & additive forward compositing)
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/GI/VPRC_DDGIDebugVisualization.cs` (probe visualization overlay)
- `Build/CommonAssets/Shaders/Compute/DDGI/ddgi_raygen.comp` (probe ray generation)
- `Build/CommonAssets/Shaders/Compute/DDGI/ddgi_trace.comp` (GPU BVH traversal)
- `Build/CommonAssets/Shaders/Compute/DDGI/ddgi_hit_shade.comp` (hit shading & direct light)
- `Build/CommonAssets/Shaders/Compute/DDGI/ddgi_update_irradiance.comp` (irradiance gather & hysteresis)
- `Build/CommonAssets/Shaders/Compute/DDGI/ddgi_update_visibility.comp` (distance moments & hysteresis)
- `Build/CommonAssets/Shaders/Compute/DDGI/ddgi_border_copy.comp` (octahedral border replication)
- `Build/CommonAssets/Shaders/Compute/DDGI/ddgi_relocate.comp` (dual-grid probe relocation)
- `Build/CommonAssets/Shaders/Snippets/DDGISampling.glsl` (reusable `sampleDDGI` function)
- `Build/CommonAssets/Shaders/Scene3D/DDGIComposite.fs` (screen sampling & composite)
- `Build/CommonAssets/Shaders/Scene3D/DDGICompositeStereo.fs` (multiview stereo composite)
- `XREngine.UnitTests/Rendering/DdgiComputeIntegrationTests.cs` (unit & contract tests)

## Suggested Next Step

Continue the completion-pass matrix and ordered actions near the top of this
document. Validate Vulkan stereo ownership next, then close probe escape,
remaining lifecycle/normalization repeats, specular coexistence and allocations.
The original phase checklist below the remediation record is implementation
history, not evidence that the expanded cross-backend acceptance is complete.
