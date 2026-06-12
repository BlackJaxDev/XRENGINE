using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XREngine.Animation
{
    internal abstract class BakedValueStore<T>
    {
        public abstract int Count { get; }
        public abstract EAnimationValueCompressionAlgorithm Algorithm { get; }
        public abstract T GetValue(int frameIndex);

        public static BakedValueStore<T> Empty { get; } = new RawBakedValueStore<T>([]);

        public static BakedValueStore<T> Encode(T[] values, EAnimationValueCompressionAlgorithm algorithm)
        {
            if (values.Length == 0)
                return Empty;

            return algorithm switch
            {
                EAnimationValueCompressionAlgorithm.None => new RawBakedValueStore<T>(values),
                EAnimationValueCompressionAlgorithm.Constant => ConstantBakedValueStore<T>.Create(values),
                EAnimationValueCompressionAlgorithm.RunLength => RunLengthBakedValueStore<T>.Create(values),
                EAnimationValueCompressionAlgorithm.Delta or
                EAnimationValueCompressionAlgorithm.DeltaRunLength => throw new NotSupportedException(
                    $"{algorithm} baked value compression requires unmanaged values. Value type '{typeof(T).FullName}' is not supported."),
                _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown baked value compression algorithm.")
            };
        }

        public static BakedValueStore<TValue> EncodeUnmanaged<TValue>(TValue[] values, EAnimationValueCompressionAlgorithm algorithm)
            where TValue : unmanaged
        {
            if (values.Length == 0)
                return BakedValueStore<TValue>.Empty;

            return algorithm switch
            {
                EAnimationValueCompressionAlgorithm.None => new RawBakedValueStore<TValue>(values),
                EAnimationValueCompressionAlgorithm.Constant => ConstantBakedValueStore<TValue>.Create(values),
                EAnimationValueCompressionAlgorithm.RunLength => RunLengthBakedValueStore<TValue>.Create(values),
                EAnimationValueCompressionAlgorithm.Delta => DeltaBakedValueStore<TValue>.Create(values),
                EAnimationValueCompressionAlgorithm.DeltaRunLength => DeltaRunLengthBakedValueStore<TValue>.Create(values),
                _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown baked value compression algorithm.")
            };
        }
    }

    internal sealed class RawBakedValueStore<T>(T[] values) : BakedValueStore<T>
    {
        public override int Count => values.Length;
        public override EAnimationValueCompressionAlgorithm Algorithm => EAnimationValueCompressionAlgorithm.None;

        public override T GetValue(int frameIndex)
            => values[frameIndex];
    }

    internal sealed class ConstantBakedValueStore<T> : BakedValueStore<T>
    {
        private readonly T _value;

        internal ConstantBakedValueStore(T value, int count)
        {
            _value = value;
            Count = count;
        }

        public override int Count { get; }
        public override EAnimationValueCompressionAlgorithm Algorithm => EAnimationValueCompressionAlgorithm.Constant;

        public static ConstantBakedValueStore<T> Create(T[] values)
        {
            T value = values[0];
            EqualityComparer<T> comparer = EqualityComparer<T>.Default;
            for (int i = 1; i < values.Length; i++)
            {
                if (!comparer.Equals(value, values[i]))
                    throw new InvalidOperationException("Cannot encode non-constant baked samples with Constant compression.");
            }

            return new ConstantBakedValueStore<T>(value, values.Length);
        }

        public override T GetValue(int frameIndex)
            => _value;
    }

    internal sealed class RunLengthBakedValueStore<T> : BakedValueStore<T>
    {
        private readonly T[] _values;
        private readonly int[] _startFrames;

        private RunLengthBakedValueStore(T[] values, int[] startFrames, int count)
        {
            _values = values;
            _startFrames = startFrames;
            Count = count;
        }

        public override int Count { get; }
        public override EAnimationValueCompressionAlgorithm Algorithm => EAnimationValueCompressionAlgorithm.RunLength;

        public static BakedValueStore<T> Create(T[] values)
        {
            List<T> runValues = [];
            List<int> startFrames = [];
            EqualityComparer<T> comparer = EqualityComparer<T>.Default;

            runValues.Add(values[0]);
            startFrames.Add(0);

            T last = values[0];
            for (int i = 1; i < values.Length; i++)
            {
                T current = values[i];
                if (comparer.Equals(last, current))
                    continue;

                runValues.Add(current);
                startFrames.Add(i);
                last = current;
            }

            return runValues.Count == 1
                ? new ConstantBakedValueStore<T>(runValues[0], values.Length)
                : new RunLengthBakedValueStore<T>([.. runValues], [.. startFrames], values.Length);
        }

        public override T GetValue(int frameIndex)
        {
            int runIndex = Array.BinarySearch(_startFrames, frameIndex);
            if (runIndex < 0)
                runIndex = ~runIndex - 1;

            return _values[Math.Max(0, runIndex)];
        }
    }

    internal sealed class DeltaBakedValueStore<T> : BakedValueStore<T>
        where T : unmanaged
    {
        private readonly byte[] _deltas;
        private readonly int _stride;

        private DeltaBakedValueStore(byte[] deltas, int count, int stride)
        {
            _deltas = deltas;
            Count = count;
            _stride = stride;
        }

        public override int Count { get; }
        public override EAnimationValueCompressionAlgorithm Algorithm => EAnimationValueCompressionAlgorithm.Delta;

        public static DeltaBakedValueStore<T> Create(T[] values)
        {
            int stride = Unsafe.SizeOf<T>();
            byte[] deltas = new byte[values.Length * stride];
            WriteDeltas(MemoryMarshal.AsBytes(values.AsSpan()), deltas, stride);
            return new DeltaBakedValueStore<T>(deltas, values.Length, stride);
        }

        public override T GetValue(int frameIndex)
        {
            Span<byte> valueBytes = stackalloc byte[_stride];
            for (int frame = 0; frame <= frameIndex; frame++)
            {
                int sourceOffset = frame * _stride;
                for (int byteIndex = 0; byteIndex < _stride; byteIndex++)
                    valueBytes[byteIndex] ^= _deltas[sourceOffset + byteIndex];
            }

            return MemoryMarshal.Read<T>(valueBytes);
        }

        internal static void WriteDeltas(ReadOnlySpan<byte> source, Span<byte> destination, int stride)
        {
            Span<byte> previous = stackalloc byte[stride];
            for (int offset = 0; offset < source.Length; offset += stride)
            {
                ReadOnlySpan<byte> current = source.Slice(offset, stride);
                Span<byte> delta = destination.Slice(offset, stride);
                for (int byteIndex = 0; byteIndex < stride; byteIndex++)
                {
                    byte currentByte = current[byteIndex];
                    delta[byteIndex] = (byte)(currentByte ^ previous[byteIndex]);
                    previous[byteIndex] = currentByte;
                }
            }
        }
    }

    internal sealed class DeltaRunLengthBakedValueStore<T> : BakedValueStore<T>
        where T : unmanaged
    {
        private readonly byte[] _deltaRuns;
        private readonly int[] _startFrames;
        private readonly int _stride;

        private DeltaRunLengthBakedValueStore(byte[] deltaRuns, int[] startFrames, int count, int stride)
        {
            _deltaRuns = deltaRuns;
            _startFrames = startFrames;
            Count = count;
            _stride = stride;
        }

        public override int Count { get; }
        public override EAnimationValueCompressionAlgorithm Algorithm => EAnimationValueCompressionAlgorithm.DeltaRunLength;

        public static BakedValueStore<T> Create(T[] values)
        {
            int stride = Unsafe.SizeOf<T>();
            byte[] deltas = new byte[values.Length * stride];
            DeltaBakedValueStore<T>.WriteDeltas(MemoryMarshal.AsBytes(values.AsSpan()), deltas, stride);

            List<byte> deltaRuns = [];
            List<int> startFrames = [];

            startFrames.Add(0);
            deltaRuns.AddRange(deltas.AsSpan(0, stride).ToArray());

            ReadOnlySpan<byte> previousDelta = deltas.AsSpan(0, stride);
            for (int frame = 1; frame < values.Length; frame++)
            {
                ReadOnlySpan<byte> currentDelta = deltas.AsSpan(frame * stride, stride);
                if (currentDelta.SequenceEqual(previousDelta))
                    continue;

                startFrames.Add(frame);
                deltaRuns.AddRange(currentDelta.ToArray());
                previousDelta = currentDelta;
            }

            return new DeltaRunLengthBakedValueStore<T>([.. deltaRuns], [.. startFrames], values.Length, stride);
        }

        public override T GetValue(int frameIndex)
        {
            Span<byte> valueBytes = stackalloc byte[_stride];
            for (int runIndex = 0; runIndex < _startFrames.Length; runIndex++)
            {
                int runStart = _startFrames[runIndex];
                if (runStart > frameIndex)
                    break;

                int nextRunStart = runIndex == _startFrames.Length - 1
                    ? Count
                    : _startFrames[runIndex + 1];
                int runEnd = Math.Min(nextRunStart, frameIndex + 1);
                int repeatCount = runEnd - runStart;
                int deltaOffset = runIndex * _stride;

                for (int repeat = 0; repeat < repeatCount; repeat++)
                {
                    for (int byteIndex = 0; byteIndex < _stride; byteIndex++)
                        valueBytes[byteIndex] ^= _deltaRuns[deltaOffset + byteIndex];
                }
            }

            return MemoryMarshal.Read<T>(valueBytes);
        }
    }
}
