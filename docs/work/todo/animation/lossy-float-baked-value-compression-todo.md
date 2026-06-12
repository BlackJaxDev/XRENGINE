# Lossy Float Baked Value Compression TODO

Last Updated: 2026-06-12
Owner: Animation
Status: Proposed
Target Branch: `animation-lossy-float-baked-compression`

Design source:

- [Baked Animation Value Compression](../../../architecture/animation/baked-value-compression.md)
- [Baked Animation Value Compression Follow-Ups TODO](baked-value-compression-followups-todo.md)
- [Animation System](../../../developer-guides/animation/animation-api.md)

## Goal

Add measured, opt-in lossy compression formats for baked float-family property
animation values. The first useful targets are `float`, `Vector2`, `Vector3`,
`Vector4`, `Quaternion`, and transform-like `Matrix4x4` tracks whose users can
accept bounded reconstruction error in exchange for smaller baked payloads.

## Non-Goals

- Do not apply lossy compression by default.
- Do not use lossy formats for bool, string, object, or method-backed reference
  tracks.
- Do not quantize values without storing enough metadata to reconstruct them
  deterministically.
- Do not use one global tolerance for every property type.
- Do not change authored keyframes; lossy compression applies only to baked
  sample stores.
- Do not add GPU-only decode paths before CPU decode and tests are complete.

## Core Contract

- Lossy formats must be explicit in the requested compression settings.
- Every lossy format must define its value domain, metadata, decode formula,
  and error metric before implementation.
- Error budgets must be per track or per asset profile, not hidden constants.
- Endpoints and marked exact frames should be optionally preservable for loops,
  gameplay events, and authored poses.
- Runtime decode must allocate zero heap memory in steady-state playback.
- Cooked lossy payloads must carry schema and codec versions.

## Phase 0 - Branch, Corpus, And Metrics

- [ ] Create dedicated branch `animation-lossy-float-baked-compression`.
- [ ] Select a corpus of float-family baked tracks:
  - [ ] scalar curves with small and large ranges,
  - [ ] normalized blendshape weights,
  - [ ] positions in world-like units,
  - [ ] rotations as quaternions,
  - [ ] colors or normalized vectors,
  - [ ] transform matrices,
  - [ ] noisy mocap-style data,
  - [ ] smooth authored curves.
- [ ] Capture current raw bytes, existing lossless encoded bytes, bake time,
  decode time, and visual/semantic error for each track.
- [ ] Decide the first platform-neutral binary layout assumptions: little
  endian, alignment, `Half` handling, and integer bit packing.
- [ ] Record where editor profiles or import settings will store lossy error
  budgets.

Acceptance criteria:

- [ ] The team can compare lossy candidates against raw and existing lossless
  stores using the same corpus and metrics.

## Phase 1 - Error Budget And API Design

- [ ] Add an explicit lossy compression profile object or equivalent settings
  structure.
- [ ] Decide whether lossy codecs extend `EAnimationValueCompressionAlgorithm`
  directly or use a second field such as compression quality/profile.
- [ ] Define scalar float error metrics:
  - [ ] absolute error,
  - [ ] relative error,
  - [ ] normalized-range error,
  - [ ] endpoint exactness.
- [ ] Define vector error metrics:
  - [ ] per-component max error,
  - [ ] Euclidean distance,
  - [ ] normalized-vector angular error when applicable.
- [ ] Define quaternion error metrics:
  - [ ] angular error in degrees,
  - [ ] normalization drift,
  - [ ] sign-equivalent rotation handling.
- [ ] Define matrix error metrics:
  - [ ] per-element error for general matrices,
  - [ ] transformed-point error for transform matrices,
  - [ ] decomposed translation/rotation/scale error when decomposition is used.
- [ ] Add tests that reject invalid tolerances, negative ranges, and lossy
  settings on unsupported value types.
- [ ] Document the API contract before adding codec enum values.

Acceptance criteria:

- [ ] A lossy bake request states exactly what kind of error is allowed and how
  it will be measured.

## Phase 2 - Scalar Float Codecs

- [ ] Add `Float16` or equivalent binary16 scalar storage using deterministic
  conversion rules.
- [ ] Add per-track range quantization to unsigned integer payloads, starting
  with 8-bit and 16-bit variants.
- [ ] Add signed normalized quantization for symmetric ranges around zero.
- [ ] Add block quantization with per-block min/max for tracks with changing
  dynamic range.
- [ ] Add delta-predictive quantization: previous decoded value plus quantized
  residual.
- [ ] Preserve exact endpoints when the profile requests loop or endpoint
  stability.
- [ ] Add scalar tests for NaN, infinities, negative zero, denormals, constant
  values, tiny ranges, huge ranges, and monotonic curves.
- [ ] Decide and document whether NaN/infinity values are rejected, preserved
  raw, or handled by side tables.

Acceptance criteria:

- [ ] Scalar float codecs meet requested error budgets and beat raw FP32 payload
  size on representative tracks.

## Phase 3 - Vector Float Codecs

- [ ] Add per-component min/max range quantization for `Vector2`, `Vector3`,
  and `Vector4`.
- [ ] Add shared-range quantization for vectors whose components share a unit or
  normalized domain.
- [ ] Add unorm/snorm specializations for known normalized tracks such as
  weights, colors, and directions.
- [ ] Add lane masks for constant vector components so unchanged lanes do not
  consume quantized payload bits.
- [ ] Add block-based vector quantization for position curves with local motion
  ranges.
- [ ] Add decode tests for component error, vector distance error, and endpoint
  preservation.
- [ ] Add benchmark coverage against existing `DeltaRunLength` on smooth and
  noisy vector tracks.

Acceptance criteria:

- [ ] Vector codecs reduce payload size while staying within both component and
  vector-level error budgets.

## Phase 4 - Quaternion Codecs

- [ ] Add quaternion normalization before quantization only when the profile
  explicitly allows semantic rotation equivalence.
- [ ] Add sign canonicalization so equivalent rotations choose deterministic
  storage.
- [ ] Evaluate smallest-three quaternion encoding with 10, 12, and 16 bits per
  stored component.
- [ ] Evaluate normalized `Half` component storage as a simpler baseline.
- [ ] Preserve exact identity rotations when requested.
- [ ] Add angular-error tests over representative rotations, including near
  180-degree rotations and sign-flipped equivalent quaternions.
- [ ] Verify slerp between decoded adjacent baked samples does not exceed the
  track's angular error budget by more than the documented interpolation margin.

Acceptance criteria:

- [ ] Quaternion codecs report angular error and never rely on raw component
  error as the only correctness metric.

## Phase 5 - Matrix And Transform-Aware Lossy Codecs

- [ ] Split general matrix handling from transform-matrix handling.
- [ ] For general matrices, support only per-element float-family codecs until
  a semantic metric is known.
- [ ] For transform matrices, add an opt-in decomposition path into translation,
  rotation, scale, and optional shear only when decomposition succeeds inside
  the requested error budget.
- [ ] Quantize translation using world/unit-aware ranges.
- [ ] Quantize rotation through the quaternion codec selected by the profile.
- [ ] Quantize scale with absolute or relative error, depending on profile.
- [ ] Reject or store raw fallback blocks for matrices that cannot be safely
  decomposed.
- [ ] Add transformed-point error tests using representative local bounds, not
  only per-element matrix comparisons.
- [ ] Add tests for identity, translation-only, rotation-only, non-uniform
  scale, negative scale, shear, perspective-like, and non-invertible matrices.

Acceptance criteria:

- [ ] Transform-like matrix compression is only used when decoded transforms
  satisfy translation, angular, scale, and transformed-bounds error budgets.

## Phase 6 - Adaptive Codec Selection

- [ ] Add an offline estimator that tries candidate lossy codecs against the
  requested error budget.
- [ ] Choose the smallest payload that passes validation, or keep the lossless
  requested store when no lossy candidate passes.
- [ ] Record the selected effective lossy codec and its measured max/average
  error.
- [ ] Add an option to require a specific lossy codec and fail if it exceeds the
  error budget.
- [ ] Add editor UI for "best under budget" versus "specific codec".
- [ ] Add tests proving adaptive selection does not pick a codec that violates
  the budget.

Acceptance criteria:

- [ ] Users can request a quality target without manually guessing the best
  codec for each track.

## Phase 7 - Cooked Payload And Versioning

- [ ] Add cooked payload layouts for each accepted lossy codec.
- [ ] Store quantization metadata such as min/max, scale/bias, block size,
  bit width, endpoint exact tables, and codec version.
- [ ] Add rejection reasons for unsupported lossy codec version, invalid
  quantization range, corrupt bitstream, and metadata/value-type mismatch.
- [ ] Ensure cooked payload schema bumps are documented.
- [ ] Add deterministic payload tests for repeated bakes with identical source
  data and settings.
- [ ] Add cross-version rejection tests.

Acceptance criteria:

- [ ] Cooked lossy payloads can be read deterministically and rejected safely.

## Phase 8 - Editor UX And Reporting

- [ ] Show estimated raw, lossless, and lossy bytes before baking.
- [ ] Show requested error budget and measured max/average error after baking.
- [ ] Show warnings for unsupported value types or values such as NaN if the
  selected codec rejects them.
- [ ] Add visual diff or curve overlay support for scalar and vector tracks.
- [ ] Add angular-error reporting for quaternion tracks.
- [ ] Add transformed-bounds error reporting for matrix transform tracks.
- [ ] Add batch reporting for clips with many property animations.

Acceptance criteria:

- [ ] Artists and technical animators can see what memory was saved and what
  error was introduced before accepting a lossy bake.

## Phase 9 - Validation And Documentation

- [ ] Add unit tests for every codec and every supported value family.
- [ ] Add property-based or generated tests for random float/vector/quaternion
  streams within valid ranges.
- [ ] Add golden tests for deterministic encoded bytes.
- [ ] Add allocation tests for playback decode.
- [ ] Run `dotnet build .\XREngine.Animation\XREngine.Animation.csproj`.
- [ ] Run targeted animation tests.
- [ ] Run any editor tests added for lossy compression reporting.
- [ ] Update [Baked Animation Value Compression](../../../architecture/animation/baked-value-compression.md)
  with the accepted lossy architecture.
- [ ] Update [Animation System](../../../developer-guides/animation/animation-api.md)
  with user-facing lossy compression settings.
- [ ] Report unrelated validation failures instead of hiding them in this
  tracker.
- [ ] Merge branch `animation-lossy-float-baked-compression` back into `main`
  after implementation, validation, and documentation updates are complete.

## Suggested Test Names

- [ ] `LossyFloatCompression_RejectsUnsupportedValueType`
- [ ] `LossyFloatCompression_Float16_RespectsAbsoluteError`
- [ ] `LossyFloatCompression_RangeUInt16_RespectsRelativeError`
- [ ] `LossyFloatCompression_BlockQuantizedFloat_DecodesEndpointsExactly`
- [ ] `LossyVectorCompression_RangeQuantized_RespectsVectorDistance`
- [ ] `LossyVectorCompression_LaneMask_OmitsConstantComponents`
- [ ] `LossyQuaternionCompression_SmallestThree_RespectsAngularError`
- [ ] `LossyQuaternionCompression_SignCanonicalization_IsDeterministic`
- [ ] `LossyMatrixCompression_TransformDecompose_RespectsPointError`
- [ ] `LossyMatrixCompression_RejectsUnsafeDecomposition`
- [ ] `LossyCompressionAdaptiveSelector_PicksSmallestPassingCodec`
- [ ] `LossyCookedPayload_RejectsUnsupportedCodecVersion`
