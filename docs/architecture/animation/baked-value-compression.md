# Baked Animation Value Compression

[<- Architecture index](../README.md)

Property animations can run directly from keyframes, or they can be baked into a dense sequence of sampled values. Baking trades authoring-time interpolation work for runtime array-style lookup: the spline, tangent, method-call, or discrete-key evaluation happens once during `Bake`, then playback reads the sampled value for the requested frame.

Baked value compression keeps that runtime path available without requiring every baked track to keep an uncompressed value for every frame.

## Goals

- Keep baked playback simple for animation evaluators: tracks ask for a value at a baked frame and receive the decoded value.
- Preserve exact values for the initial compression set. All current codecs are lossless.
- Let authoring tools choose the memory/runtime tradeoff per property animation.
- Keep codec selection serializable while keeping encoded runtime stores in memory only.
- Avoid heap allocation during baked playback.

## Core Types

- `EAnimationValueCompressionAlgorithm` selects the requested baked-value codec. Current options are `None`, `Constant`, `RunLength`, `Delta`, and `DeltaRunLength`.
- `BasePropAnimBakeable` owns the bake cadence, requested compression algorithm, and effective encoded algorithm. It exposes `Bake(framesPerSecond, algorithm)` for explicit bake requests and re-bakes when `BakedValueCompressionAlgorithm` changes on an already baked track.
- `BakedValueStore<T>` is the in-memory encoded store used by baked tracks. It hides the codec behind `GetValue(frameIndex)`, so track evaluators do not need to know how the samples are stored.
- Concrete property animation classes sample their source data into a temporary dense array, call the base encoding helper, and then decode through `BakedValueStore<T>` during baked evaluation.

## Bake Lifecycle

1. A tool or runtime caller sets `BakedValueCompressionAlgorithm`, or calls `Bake(framesPerSecond, EAnimationValueCompressionAlgorithm)`.
2. The track records the bake cadence and samples its source animation into a dense temporary value array.
3. The track passes that array to `EncodeBakedValues` or `EncodeUnmanagedBakedValues`.
4. `BakedValueStore<T>` builds the selected store and `BasePropAnimBakeable` records `EncodedBakedValueCompressionAlgorithm`.
5. During playback, baked evaluation maps time to one or two baked frame indices, decodes those samples, and applies the track's normal value policy. Continuous tracks can still interpolate between adjacent baked samples; discrete tracks return the decoded frame value.

The requested algorithm and effective encoded algorithm are intentionally separate. `BakedValueCompressionAlgorithm` is the serialized authoring/runtime preference. `EncodedBakedValueCompressionAlgorithm` reports what the current in-memory baked store actually uses, which can differ when an encoder chooses a smaller equivalent representation such as collapsing a single run into `Constant`.

## Codec Behavior

| Algorithm | Storage Shape | Type Support | Decode Cost | Notes |
| --- | --- | --- | --- | --- |
| `None` | Dense value array | All baked value types | O(1) | Fastest random access, largest memory footprint. |
| `Constant` | One value plus frame count | All baked value types | O(1) | Requires every baked sample to be equal; non-constant input fails during bake. |
| `RunLength` | Run values plus starting frame indices | All baked value types | O(log runs) | Best for repeated discrete values or stepped tracks. A single run is stored as `Constant`. |
| `Delta` | Initial value plus XOR byte deltas | Unmanaged value types | O(frame) | Lossless binary delta stream. Best when memory matters more than random seek cost. |
| `DeltaRunLength` | Initial value plus run-length encoded XOR deltas | Unmanaged value types | O(runs + repeats to target) | Compresses repeated binary deltas, including long unchanged ranges. |

The delta codecs operate on unmanaged values as bytes and XOR each sample against the previous sample. This makes them lossless and type-agnostic for structs such as vectors, quaternions, matrices, numeric primitives, and booleans. Managed tracks such as strings, objects, and method-returned reference values support `None`, `Constant`, and `RunLength`; selecting a delta codec for those tracks throws during bake.

## Runtime Evaluation

Baked evaluation remains owned by each property animation family:

- Vector, lerpable, and quaternion tracks decode baked samples and then apply their existing interpolation behavior.
- Boolean, string, object, method, and matrix tracks decode the frame sample directly.
- Time-based baked reads clamp or wrap through the existing animation timing rules before the frame index is decoded.

The stores are optimized for no heap allocation in the playback path. Raw and constant stores return values directly. Run-length stores binary-search their start-frame table. Delta stores reconstruct the requested frame using stack memory for unmanaged byte buffers.

## Serialization Boundary

The compression preference is part of the property animation asset data. Encoded baked stores are currently runtime-only:

- `BakedValueCompressionAlgorithm` is serialized so a clip can be re-baked with the same requested codec.
- `EncodedBakedValueCompressionAlgorithm` is ignored by MemoryPack and is only meaningful after an in-memory bake.
- The encoded sample payload is not currently serialized as cooked animation data.

This keeps authoring assets simple while leaving room for a future cooked-animation path that serializes encoded baked stores directly.

## Failure Modes

Codec failures are visible during bake:

- `Constant` fails if any sample differs from the first sample.
- Delta codecs fail for managed tracks because they require unmanaged value bytes.
- Invalid frame requests fail through the store's normal index validation.

The engine does not silently fall back to a different compression algorithm when the requested algorithm is invalid for the value stream. That keeps authoring mistakes discoverable.

## Testing

The baked compression behavior is covered by animation unit tests:

- Serialization tests verify the requested compression algorithm round-trips with animation clips.
- Timing tests verify decoded values for raw, run-length, constant, delta, and delta-run-length baked tracks.
- Managed-value tests verify delta compression is rejected for non-unmanaged value streams.

## Future Work

- Track cooked asset support, delta seek checkpoints, editor estimates, and type-specific lossless stores in the [baked value compression follow-ups TODO](../../work/todo/animation/baked-value-compression-followups-todo.md).
- Track lossy quantized codecs, especially float-family formats, in the [lossy float baked value compression TODO](../../work/todo/animation/lossy-float-baked-value-compression-todo.md). Lossy formats should land only after the engine has a per-type error-budget contract and clear reconstruction rules.
