# Gaussian-Splat Distant-Crowd LOD TODO

Last Updated: 2026-05-29
Owner: Rendering / Assets
Status: Active
Target Branch: `avatar-gaussian-splat-distant-crowd`

Design source:

- [Avatar Optimization And Virtualized Avatar Rendering Design](../../design/rendering/avatar-optimization-and-virtualized-rendering-design.md)
- [Avatar Optimization Roadmap](avatar-optimization-roadmap.md)
- [Animated Gaussian Capture And Streaming TODO](../animated-gaussian-cloud-capture-and-streaming-todo.md)
- [VR Rendering Performance Contract TODO](../rendering/optimization/vr-rendering-performance-contract-todo.md)

## Goal

Render distant crowds of unique avatars by baking each user's appearance into a
distant LOD representation. The first production target is a measured 50+
unique-avatar distant crowd budget with graceful degradation to octahedral
impostors where splat bake/runtime support is unavailable.

## Scope

- Avatar-to-splat bake.
- Representative view and pose capture.
- Offline 3DGS training/fitting or generated splat fitting.
- Skeleton-bound animation for distant splats.
- GPU sort/binning and tile composite.
- Depth, color, and velocity output.
- Cross-fade with triangle/cluster LOD.
- Identity preservation and customization invalidation.
- Octahedral impostor fallback.

## Non-Goals

- Do not replace close-up triangle, meshlet, or cluster rendering.
- Do not promise runtime splat generation time; first version treats bake as
  offline and reports duration.
- Do not support facial blendshapes at splat distance by default.
- Do not replace every far avatar with a generic mannequin.

## Phase 0 - Branch, Corpus, And Feature Gate

- [ ] Create dedicated branch `avatar-gaussian-splat-distant-crowd`.
- [ ] Add feature flag for Gaussian-splat avatar LOD.
- [ ] Select distant crowd corpus with at least 50 unique avatars or reusable
  stand-ins with unique materials/accessories.
- [ ] Capture triangle/impostor baseline cost and identity quality.
- [ ] Define target distance bands and target splat count ranges.
- [ ] Define fallback to octahedral impostor payloads.

Acceptance criteria:

- [ ] Splat LOD can be disabled without breaking existing LOD/impostor paths.

## Phase 1 - Bake Capture

- [ ] Render optimized avatar from N viewpoints, initially 64-128 where quality
  profile requests it.
- [ ] Capture concentric rings around the avatar.
- [ ] Capture representative poses from the optimizer animation sample set.
- [ ] Capture albedo/color, depth, normal, mask/alpha, and optional material ID
  buffers as bake supervision.
- [ ] Capture customization slot values used during bake.
- [ ] Store bake manifest with source variant hash, profile hash, capture pose
  set, view count, resolution, lighting assumptions, and bake time.

Acceptance criteria:

- [ ] Bake inputs are deterministic and invalidated when source appearance or
  significant customization changes.

## Phase 2 - Splat Fitting And Compression

- [ ] Fit or train 3D Gaussian representation from captured views.
- [ ] Store anisotropic Gaussian position, scale, rotation, opacity, and color
  representation.
- [ ] Support spherical harmonics or simpler view-dependent color where profile
  allows.
- [ ] Optionally apply anti-aliasing strategy such as Mip-Splatting where
  available.
- [ ] Prune low-opacity or low-contribution Gaussians.
- [ ] Quantize and pack coefficients into cooked binary payload.
- [ ] Report bake duration, final splat count, compressed bytes, and validation
  error.
- [ ] Bound visible splat count by distance band and profile.

Acceptance criteria:

- [ ] Bake output has measured quality and size; no fixed training-time promise
  is baked into the design.

## Phase 3 - Animation Binding

- [ ] Bind each Gaussian to one or more bones or to barycentric coordinates on a
  source bind-pose triangle.
- [ ] Store local frame needed to update position and orientation.
- [ ] Compute runtime splat position from skinned bind primitive or weighted
  bones.
- [ ] Store previous-frame splat position for motion vectors.
- [ ] Treat pose-conditioned splats as optional research path.
- [ ] Validate identity only within sampled pose envelope.
- [ ] Add warnings for extreme unseen poses that may artifact.

Acceptance criteria:

- [ ] Distant avatars still move believably without close-up facial expression
  requirements.

## Phase 4 - Runtime Culling And Sorting

- [ ] Cull splat-avatar instances against frustum and far Hi-Z.
- [ ] Skin active splat lists on GPU.
- [ ] Choose sort strategy: GPU radix sort, merge sort, tile-local approximate
  order, or hybrid.
- [ ] Report chosen sort strategy in profiler output.
- [ ] Bin visible splats into tiles for composite.
- [ ] Add overflow behavior for splat bins and active splat lists.
- [ ] Add counters for visible splat avatars, visible splats, cull time,
  skinning time, sort/bin time, and overflow.

Acceptance criteria:

- [ ] Runtime cost scales with visible splat count and active avatars, not with
  source triangle count.

## Phase 5 - Tile Composite And Outputs

- [ ] Composite splats back-to-front or approximate order into a splat
  framebuffer.
- [ ] Write color.
- [ ] Write depth compatible with main scene depth.
- [ ] Write velocity from current vs previous skinned splat position.
- [ ] Composite splat framebuffer into the main framebuffer with depth test and
  alpha blend.
- [ ] Ensure output is reprojection-friendly.
- [ ] Add fallback when GPU sort/bin/composite path is unavailable.

Acceptance criteria:

- [ ] Splats participate in depth and motion-vector based reprojection instead
  of appearing as disconnected overlays.

## Phase 6 - LOD Transition

- [ ] Cross-fade between deepest triangle/cluster LOD and splat LOD over N
  frames.
- [ ] Evaluate transition distance per instance from head position, not per-eye.
- [ ] Match splat depth to triangle depth within tolerance at transition band.
- [ ] Write velocity from both representations consistently during transition.
- [ ] Add transition debug view.
- [ ] Validate under VR head motion.

Acceptance criteria:

- [ ] One eye cannot see splats while the other sees triangles due to per-eye
  transition disagreement.

## Phase 7 - Identity And Customization Preservation

- [ ] Bake each splat representation from the user's own avatar.
- [ ] Preserve outfit, hair color, accessories, body proportions, and skin tone.
- [ ] Allow customization slots to modulate splat color in composite shader
  where safe.
- [ ] Invalidate and rebake when significant customization changes.
- [ ] Report whether splat payload is exact-to-current-customization or using a
  modulated older bake.
- [ ] Add identity comparison thumbnails at target distance.

Acceptance criteria:

- [ ] Distant crowds preserve who each avatar is, not just that "a person" is
  present.

## Phase 8 - Cost Model And Degradation

- [ ] Target a 50+ unique-avatar distant crowd at about 1 ms of the 90 Hz stereo
  frame on target desktop hardware after foveation/VRS, culling, and splat
  pruning.
- [ ] Treat target as engineering validation, not guarantee.
- [ ] Add degradation steps:
  - [ ] reduce splat count by distance
  - [ ] reduce SH/order or color complexity
  - [ ] reduce update frequency for distant animation
  - [ ] switch to octahedral impostor fallback
  - [ ] hide sub-threshold accessories only where profile allows
- [ ] Report active degradation rung per avatar or crowd batch.

Acceptance criteria:

- [ ] Crowds degrade gracefully rather than causing frame spikes or generic
  placeholder swaps.

## Final Validation And Merge

- [ ] Run bake determinism tests.
- [ ] Run runtime cull/sort/composite tests where practical.
- [ ] Run visual transition validation against triangle/cluster LOD.
- [ ] Run VR/stereo smoke when hardware/runtime is available.
- [ ] Update animated Gaussian and avatar design docs if payload contracts
  change.
- [ ] Merge branch `avatar-gaussian-splat-distant-crowd` back into `main` after
  implementation, validation, and documentation updates are complete.
