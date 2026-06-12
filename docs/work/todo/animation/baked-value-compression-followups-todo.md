# Baked Animation Value Compression Follow-Ups TODO

Last Updated: 2026-06-12
Owner: Animation
Status: Proposed
Target Branch: `animation-baked-value-compression-followups`

Design source:

- [Baked Animation Value Compression](../../../architecture/animation/baked-value-compression.md)
- [Animation System](../../../developer-guides/animation/animation-api.md)
- [Lossy Float Baked Value Compression TODO](lossy-float-baked-value-compression-todo.md)

## Goal

Close the known follow-up work for lossless baked property animation value
compression. This tracker owns cooked encoded payloads, faster delta seeking,
editor memory/cost estimates, and type-specific exact storage improvements.
Lossy and approximate float-family formats are tracked separately in the lossy
compression TODO.

## Non-Goals

- Do not add lossy quantized formats in this work item.
- Do not silently substitute another compression algorithm when the requested
  algorithm is invalid for a track.
- Do not serialize encoded stores without a cooked payload version and rejection
  path.
- Do not add per-frame heap allocations to baked playback.
- Do not change keyframe authoring semantics.

## Invariants

- All formats in this tracker are lossless at the value level.
- `BakedValueCompressionAlgorithm` remains the serialized requested algorithm.
- `EncodedBakedValueCompressionAlgorithm` remains the runtime effective
  algorithm unless a cooked payload contract explicitly stores the same fact.
- Runtime decode must validate frame bounds.
- Hot playback paths must use direct arrays, spans, stack memory, or prebuilt
  tables rather than LINQ, closures, boxing, or temporary heap buffers.
- `XRBase` mutation paths added near animation asset types must use
  `SetField(...)`.

## Phase 0 - Branch, Baseline, And Corpus

- [ ] Create dedicated branch `animation-baked-value-compression-followups`.
- [ ] Select a baked animation corpus:
  - [ ] continuous float/vector/quaternion tracks,
  - [ ] boolean visibility or event-like tracks,
  - [ ] matrix transform tracks,
  - [ ] string/object/method-backed discrete tracks,
  - [ ] clips with long unchanged ranges,
  - [ ] clips with frequent random seeking.
- [ ] Capture current raw sample counts, encoded bytes, bake time, and decode
  time for `None`, `Constant`, `RunLength`, `Delta`, and `DeltaRunLength`.
- [ ] Capture current allocation profile for baked playback.
- [ ] Record the existing serialization behavior for requested algorithm,
  effective algorithm, and runtime-only stores.

Acceptance criteria:

- [ ] Later phases have byte, time, and allocation baselines for each value
  family before format changes land.

## Phase 1 - Cooked Encoded Store Contract

- [ ] Decide whether cooked baked values live inside animation clip assets,
  cooked clip cache assets, or a separate cooked animation payload.
- [ ] Add schema and payload version constants for encoded baked stores.
- [ ] Define a compact payload header containing value type, frame count,
  requested algorithm, effective algorithm, codec version, byte order, and
  optional payload checksum.
- [ ] Define payload layouts for `None`, `Constant`, `RunLength`, `Delta`, and
  `DeltaRunLength`.
- [ ] Add rejection reasons for unsupported codec version, mismatched value
  type, invalid frame count, corrupt run table, truncated delta stream, and
  checksum mismatch.
- [ ] Keep authoring assets able to re-bake from source keyframes when cooked
  payloads are missing or rejected.
- [ ] Add deterministic write tests for encoded payload bytes.
- [ ] Add corrupted payload tests for truncated headers, bad run starts, bad
  delta lengths, and wrong value type.
- [ ] Update architecture and developer docs after the cooked boundary is
  implemented.

Acceptance criteria:

- [ ] Cooked encoded stores load without rebuilding dense temporary arrays.
- [ ] Rejected cooked payloads produce one clear diagnostic and can re-bake from
  authoring data when source data is available.

## Phase 2 - Delta Seek Checkpoints

- [ ] Add an optional checkpoint interval for `Delta` and `DeltaRunLength`
  stores.
- [ ] Store full decoded values at fixed frame intervals or adaptive intervals
  chosen by run density.
- [ ] Decode random frame requests from the nearest previous checkpoint instead
  of from frame zero.
- [ ] Keep sequential playback able to use a cursor/cache path without changing
  public baked evaluation APIs.
- [ ] Benchmark intervals such as 8, 16, 32, and 64 frames against the Phase 0
  corpus.
- [ ] Add tests for seeking before, at, and after checkpoints.
- [ ] Add tests for looped playback and reverse playback when a caller seeks
  non-monotonically.

Acceptance criteria:

- [ ] Delta random access cost is bounded by the checkpoint interval.
- [ ] Checkpoints improve random seek time on long tracks without erasing the
  memory benefit that justified delta compression.

## Phase 3 - Editor Estimates And Diagnostics

- [ ] Add bake preview estimates for raw dense bytes.
- [ ] Add encoded byte estimates for each supported lossless algorithm before
  committing a bake.
- [ ] Add estimated random-access decode cost and sequential decode cost.
- [ ] Show requested algorithm versus effective encoded algorithm after bake.
- [ ] Show warnings for invalid algorithm/type combinations, such as delta on
  managed tracks.
- [ ] Show why `Constant` cannot encode a non-constant stream instead of only
  surfacing the exception text.
- [ ] Expose measured bake time and encoded byte count after a bake.
- [ ] Add editor tests or view-model tests for estimate text and invalid
  algorithm warnings.

Acceptance criteria:

- [ ] An author can compare memory and decode tradeoffs before changing a
  property's baked compression algorithm.

## Phase 4 - Bool-Specific Lossless Stores

- [ ] Add a `BoolBitSet` store that packs one frame per bit with O(1) random
  decode.
- [ ] Add a `BoolRunLength` or reuse general RLE when run count is smaller than
  the bitset payload.
- [ ] Add a bool codec chooser that can select `Constant`, bitset, or RLE based
  on measured payload size.
- [ ] Decide whether bool-specific stores need public enum values or stay as
  effective store choices under existing algorithms.
- [ ] Add serialization layout support for bit-packed bool stores if cooked
  encoded payloads have landed.
- [ ] Add tests for all-false, all-true, alternating, sparse true, sparse false,
  and random bool tracks.
- [ ] Add allocation tests for bool baked playback.

Acceptance criteria:

- [ ] Bool tracks no longer need one byte or one unmanaged delta record per
  frame when a bitset or run table is smaller.

## Phase 5 - Matrix-Specific Lossless Stores

- [ ] Audit XRENGINE's `Matrix4x4` convention before adding structural matrix
  stores, including translation position and affine invariant row/column.
- [ ] Add exact identity and constant matrix fast paths.
- [ ] Add an affine matrix store that omits invariant affine elements only when
  every sample exactly satisfies the affine invariant.
- [ ] Add a lane mask store for matrices where only a subset of float lanes
  changes over the track.
- [ ] Add per-lane RLE or delta-RLE for matrix tracks with sparse animated
  components.
- [ ] Evaluate a transform-specialized store only for matrices proven to be
  transform matrices, and keep it lossless in this tracker.
- [ ] Add tests for identity, translation-only, rotation-only, affine,
  non-affine, sparse-lane, and fully animated matrices.
- [ ] Add decode correctness tests that compare every matrix element exactly.

Acceptance criteria:

- [ ] Matrix tracks with common transform shapes use less memory without
  changing any matrix element on decode.

## Phase 6 - Other Exact Type Optimizations

- [ ] Add numeric primitive stores that choose the narrowest exact integer
  backing type when all values fit, such as byte, short, or int.
- [ ] Add nullable/reference-value RLE diagnostics for object, string, and
  method-backed tracks.
- [ ] Evaluate a `QuaternionConstantOrRaw` fast path only if it beats existing
  constant/raw stores in memory or decode simplicity.
- [ ] Add vector lane-mask stores for tracks where only X, Y, Z, or W changes.
- [ ] Keep any type-specific optimization behind the same `BakedValueStore<T>`
  abstraction so property animation classes do not gain codec branches.

Acceptance criteria:

- [ ] Type-specific stores improve measured payload size or decode cost without
  widening the public evaluation surface.

## Phase 7 - Validation And Documentation

- [ ] Run targeted animation unit tests.
- [ ] Add cooked payload round-trip tests when Phase 1 lands.
- [ ] Add codec selection tests for bool and matrix stores.
- [ ] Run `dotnet build .\XREngine.Animation\XREngine.Animation.csproj`.
- [ ] Run the narrowest useful editor or unit-test validation for any editor UX
  changes.
- [ ] Run a focused allocation audit for baked playback paths.
- [ ] Update [Baked Animation Value Compression](../../../architecture/animation/baked-value-compression.md)
  with implemented payload and store details.
- [ ] Update [Animation System](../../../developer-guides/animation/animation-api.md)
  with user-visible codec behavior.
- [ ] Report unrelated validation failures instead of hiding them in this
  tracker.
- [ ] Merge branch `animation-baked-value-compression-followups` back into
  `main` after implementation, validation, and documentation updates are
  complete.

## Suggested Test Names

- [ ] `BakedValueCookedPayload_RoundTripsRawStore`
- [ ] `BakedValueCookedPayload_RejectsWrongValueType`
- [ ] `BakedValueCookedPayload_RejectsTruncatedDeltaStream`
- [ ] `DeltaStore_WithCheckpoints_DecodesRandomSeek`
- [ ] `DeltaRunLengthStore_WithCheckpoints_DecodesLoopedSeek`
- [ ] `BoolBitSetStore_DecodesAlternatingFrames`
- [ ] `BoolCompressionChooser_SelectsSmallestLosslessStore`
- [ ] `MatrixAffineStore_DecodesExactElements`
- [ ] `MatrixLaneMaskStore_DecodesSparseAnimatedLanes`
- [ ] `BakedCompressionEditorEstimate_ReportsRawAndEncodedBytes`
