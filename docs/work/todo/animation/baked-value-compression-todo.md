# Baked Animation Value Compression TODO

Last Updated: 2026-10-06
Status: Planned
Architecture: [Baked Animation Value Compression](../../../architecture/animation/baked-value-compression.md)
Validation: [Animation Validation](../../testing/animation/animation-validation.md#baked-value-compression)

## Current State

`EAnimationValueCompressionAlgorithm` has the lossless values `None`, `Constant`, `RunLength`, `Delta`, and `DeltaRunLength`. `BakedValueStore<T>` holds encoded values in memory only. `BasePropAnimBakeable` stores the requested and the effective algorithm. No cooked encoded payload, delta checkpoint, bool or matrix store, editor estimate, or lossy codec exists in `XREngine.Animation`.

Rules for all items: lossless stores decode exact values. Lossy formats are opt-in only, never default, and apply only to float-family baked samples (`float`, `Vector2`, `Vector3`, `Vector4`, `Quaternion`, transform `Matrix4x4`). A codec that cannot encode a track fails visibly. It never substitutes another codec. Playback decode allocates no heap memory.

## Open Code Items

### Cooked encoded payloads

- [ ] Add schema and payload version constants and a compact header (value type, frame count, requested and effective algorithm, codec version, byte order, optional checksum). Done when: the header type and constants exist.
- [ ] Define payload layouts for `None`, `Constant`, `RunLength`, `Delta`, and `DeltaRunLength`. Done when: a cooked store loads without building a dense temporary array.
- [ ] Add rejection reasons for unsupported codec version, wrong value type, invalid frame count, corrupt run table, truncated delta stream, and checksum mismatch. Re-bake from source keyframes when a payload is missing or rejected. Done when: a rejected payload logs one diagnostic and re-bakes.
- [ ] Add unit tests `BakedValueCookedPayload_RoundTripsRawStore`, `BakedValueCookedPayload_RejectsWrongValueType`, `BakedValueCookedPayload_RejectsTruncatedDeltaStream`, deterministic-byte tests, and tests for truncated headers and bad run starts. Done when: the tests exist.

### Delta seek checkpoints

- [ ] Add an optional checkpoint interval (fixed or adaptive by run density) to `Delta` and `DeltaRunLength` stores. Decode random frames from the nearest earlier checkpoint. Keep a sequential cursor path without changing the public evaluation API. Done when: random decode cost is bounded by the interval.
- [ ] Add unit tests `DeltaStore_WithCheckpoints_DecodesRandomSeek` and `DeltaRunLengthStore_WithCheckpoints_DecodesLoopedSeek`, plus seeks before, at, and after checkpoints and reverse seeks. Done when: the tests exist.

### Type-specific lossless stores

- [ ] Add a `BoolBitSet` store (one bit per frame, O(1) decode) and a bool chooser that picks `Constant`, bitset, or run-length by size. Done when: unit tests `BoolBitSetStore_DecodesAlternatingFrames` and `BoolCompressionChooser_SelectsSmallestLosslessStore` pass for all-false, all-true, alternating, sparse, and random tracks, and a bool playback allocation test passes.
- [ ] Add matrix stores: identity and constant fast paths, an affine store that omits invariant elements only when every sample is exactly affine, a lane-mask store, and per-lane run-length or delta-run-length. Confirm the `Matrix4x4` translation and affine-invariant convention first. Done when: unit tests `MatrixAffineStore_DecodesExactElements` and `MatrixLaneMaskStore_DecodesSparseAnimatedLanes` compare every element exactly for identity, translation-only, rotation-only, affine, non-affine, sparse-lane, and fully animated matrices.
- [ ] Add numeric primitive stores with the narrowest exact integer backing type, and vector lane-mask stores for tracks where only some components change. Keep each behind `BakedValueStore<T>`. Done when: property animation classes have no new codec branches.
- [ ] Add run-length diagnostics for nullable, object, string, and method-backed tracks. Done when: the bake report shows run counts for these tracks.

### Lossy float-family codecs

- [ ] Add a lossy compression profile with per-track or per-asset error budgets. Define scalar (absolute, relative, normalized-range, endpoint exactness), vector (per-component, Euclidean, angular), quaternion (angular degrees, normalization drift, sign equivalence), and matrix (per-element, transformed-point, decomposed TRS) metrics. Done when: the profile type exists and unit test `LossyFloatCompression_RejectsUnsupportedValueType` plus invalid-tolerance tests pass.
- [ ] Add scalar codecs: binary16, 8-bit and 16-bit range quantization, signed normalized quantization, block min and max quantization, and delta-predictive quantization, with optional exact endpoints. Done when: unit tests `LossyFloatCompression_Float16_RespectsAbsoluteError`, `LossyFloatCompression_RangeUInt16_RespectsRelativeError`, and `LossyFloatCompression_BlockQuantizedFloat_DecodesEndpointsExactly` pass, plus NaN, infinity, negative zero, denormal, constant, tiny-range, huge-range, and monotonic cases.
- [ ] Add vector codecs: per-component and shared-range quantization, unorm and snorm for normalized tracks, constant-lane masks, and block quantization for positions. Done when: unit tests `LossyVectorCompression_RangeQuantized_RespectsVectorDistance` and `LossyVectorCompression_LaneMask_OmitsConstantComponents` pass.
- [ ] Add quaternion codecs: sign canonicalization, opt-in normalization, smallest-three at 10, 12, and 16 bits, and normalized half storage, with exact identity when requested. Done when: unit tests `LossyQuaternionCompression_SmallestThree_RespectsAngularError` and `LossyQuaternionCompression_SignCanonicalization_IsDeterministic` pass, including near-180-degree and sign-flipped rotations and slerp between decoded samples.
- [ ] Add matrix codecs: per-element codecs for general matrices, and opt-in TRS (optional shear) decomposition for transform matrices that succeeds within budget. Store raw blocks or reject when decomposition is not safe. Done when: unit tests `LossyMatrixCompression_TransformDecompose_RespectsPointError` and `LossyMatrixCompression_RejectsUnsafeDecomposition` pass for identity, translation, rotation, non-uniform and negative scale, shear, perspective-like, and non-invertible matrices.
- [ ] Add an offline adaptive selector that picks the smallest codec within budget, keeps the lossless store when none passes, and records the selected codec and measured maximum and average error. Add a mode that requires one codec and fails over budget. Done when: unit test `LossyCompressionAdaptiveSelector_PicksSmallestPassingCodec` passes.
- [ ] Add cooked lossy payload layouts with quantization metadata (range, scale and bias, block size, bit width, exact-endpoint tables, codec version) and rejection reasons. Done when: unit test `LossyCookedPayload_RejectsUnsupportedCodecVersion` and deterministic repeated-bake tests pass.
- [ ] Add generated tests for random float, vector, and quaternion streams, golden byte tests, and playback allocation tests for each codec. Done when: the tests exist.

### Editor estimates and reporting

- [ ] Show raw, lossless, and lossy byte estimates, random-access and sequential decode cost estimates, and requested versus effective algorithm. Show measured bake time, encoded bytes, error budget, and measured error after bake. Done when: the bake inspector shows the values.
- [ ] Show warnings for invalid algorithm and type pairs (for example delta on managed tracks), the reason `Constant` cannot encode a stream, and unsupported values such as NaN. Done when: unit test `BakedCompressionEditorEstimate_ReportsRawAndEncodedBytes` and warning view-model tests pass.
- [ ] Add curve overlays for scalar and vector tracks, angular-error reports for quaternions, transformed-bounds error reports for matrices, and batch reports for clips. Add a "best under budget" versus "specific codec" selector. Done when: the editor shows them.

### Documentation

- [ ] Update `docs/architecture/animation/baked-value-compression.md` and `docs/developer-guides/animation/animation-api.md` as each store, payload, or lossy codec lands. Done when: the docs match the code.

## Decisions Needed

- [ ] Decide where cooked baked values live: in clip assets, in a cooked clip cache asset, or in a separate cooked animation payload. Owner: animation.
- [ ] Decide whether bool-specific stores get public enum values or stay effective choices under existing algorithms. Owner: animation.
- [ ] Decide whether lossy codecs extend `EAnimationValueCompressionAlgorithm` or use a separate quality or profile field. Owner: animation.
- [ ] Decide the binary layout: little endian, alignment, `Half` handling, and integer bit packing. Owner: animation.
- [ ] Decide where editor profiles or import settings store lossy error budgets. Owner: animation.
- [ ] Decide whether lossy codecs reject NaN and infinity, keep them raw, or use side tables. Owner: animation.
- [ ] Decide whether a `QuaternionConstantOrRaw` store or a lossless transform-specialized matrix store is worth adding. Owner: animation.

## Out Of Scope

- Changing authored keyframe semantics.
- Lossy formats for bool, string, object, or method-backed tracks.
- GPU-only decode paths before CPU decode and tests are complete.
- One global tolerance for all property types.

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/animation/baked-value-compression-followups-todo.md`

- [ ] Add tests for seeking before, at, and after checkpoints.
- [ ] Add tests for looped playback and reverse playback when a caller seeks
  non-monotonically.
- [ ] Checkpoints improve random seek time on long tracks without erasing the
  memory benefit that justified delta compression.
- [ ] Add a bool codec chooser that can select `Constant`, bitset, or RLE based
  on measured payload size.

### From `todo/animation/lossy-float-baked-value-compression-todo.md`

- [ ] noisy mocap-style data,
- [ ] Add `Float16` or equivalent binary16 scalar storage using deterministic
  conversion rules.
- [ ] Add delta-predictive quantization: previous decoded value plus quantized
  residual.
- [ ] Add scalar tests for NaN, infinities, negative zero, denormals, constant
  values, tiny ranges, huge ranges, and monotonic curves.
- [ ] Add unorm/snorm specializations for known normalized tracks such as
  weights, colors, and directions.
- [ ] Vector codecs reduce payload size while staying within both component and
  vector-level error budgets.
- [ ] Verify slerp between decoded adjacent baked samples does not exceed the
  track's angular error budget by more than the documented interpolation margin.
- [ ] Add an offline estimator that tries candidate lossy codecs against the
  requested error budget.
- [ ] Add tests proving adaptive selection does not pick a codec that violates
  the budget.
- [ ] Users can request a quality target without manually guessing the best
  codec for each track.
- [ ] Artists and technical animators can see what memory was saved and what
  error was introduced before accepting a lossy bake.
