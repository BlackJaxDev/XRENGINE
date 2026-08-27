using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XREngine.Animation
{
    /// <summary>
    /// Classifies the value type of an animation slot for typed storage.
    /// </summary>
    public enum EAnimValueType : byte
    {
        Float,
        Vector2,
        Vector3,
        Vector4,
        Quaternion,
        Bool,
        /// <summary>Fallback for non-numeric types (strings, objects, matrices, etc.).</summary>
        Discrete,
    }

    /// <summary>
    /// Identifies a single animation value within an <see cref="AnimationValueStore"/>.
    /// Assigned once during initialization; used for O(1) typed access at runtime.
    /// </summary>
    public struct AnimSlot
    {
        /// <summary>Which typed array the value lives in.</summary>
        public EAnimValueType Type;
        /// <summary>Index within the type-specific array.</summary>
        public int TypeIndex;

        public static readonly AnimSlot Invalid = new() { Type = 0, TypeIndex = -1 };

        public readonly bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => TypeIndex >= 0;
        }
    }

    /// <summary>
    /// Struct-of-arrays store for animation values, eliminating boxing and string-keyed dictionary lookups.
    /// All MotionBase, AnimLayer, and AnimStateMachine instances in the same state machine share a compatible
    /// slot layout so array indices are directly transferable.
    /// </summary>
    public sealed partial class AnimationValueStore
    {
        private float[] _floats = [];
        private Vector2[] _vectors2 = [];
        private Vector3[] _vectors3 = [];
        private Vector4[] _vectors4 = [];
        private Quaternion[] _quaternions = [];
        private bool[] _bools = [];
        private object?[] _discrete = [];
        private AnimationQuaternionFloatSlotGroup[] _quaternionFloatGroups = [];

        public int FloatCount => _floats.Length;
        public int Vector2Count => _vectors2.Length;
        public int Vector3Count => _vectors3.Length;
        public int Vector4Count => _vectors4.Length;
        public int QuaternionCount => _quaternions.Length;
        public int BoolCount => _bools.Length;
        public int DiscreteCount => _discrete.Length;
        public int TotalSlotCount => FloatCount + Vector2Count + Vector3Count + Vector4Count + QuaternionCount + BoolCount + DiscreteCount;

        /// <summary>
        /// Allocates (or re-allocates) the typed arrays to match the given slot layout.
        /// Existing values are discarded.
        /// </summary>
        public void Resize(AnimationSlotLayout layout)
        {
            _floats = layout.FloatCount > 0 ? new float[layout.FloatCount] : [];
            _vectors2 = layout.Vector2Count > 0 ? new Vector2[layout.Vector2Count] : [];
            _vectors3 = layout.Vector3Count > 0 ? new Vector3[layout.Vector3Count] : [];
            _vectors4 = layout.Vector4Count > 0 ? new Vector4[layout.Vector4Count] : [];
            _quaternions = layout.QuaternionCount > 0 ? new Quaternion[layout.QuaternionCount] : [];
            _bools = layout.BoolCount > 0 ? new bool[layout.BoolCount] : [];
            _discrete = layout.DiscreteCount > 0 ? new object?[layout.DiscreteCount] : [];
            _quaternionFloatGroups = layout.QuaternionFloatGroups;
            ResizeCoverage(layout);
            InitializeQuaternionsToIdentity();
            InitializeQuaternionFloatGroupsToIdentity();
        }

        // ── Typed getters ────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetFloat(int index) => _floats[index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2 GetVector2(int index) => _vectors2[index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 GetVector3(int index) => _vectors3[index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector4 GetVector4(int index) => _vectors4[index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Quaternion GetQuaternion(int index) => _quaternions[index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool GetBool(int index) => _bools[index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object? GetDiscrete(int index) => _discrete[index];

        // ── Typed setters ────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetFloat(int index, float value)
        {
            _floats[index] = value;
            _floatCoverage[index] = 1.0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVector2(int index, Vector2 value)
        {
            _vectors2[index] = value;
            _vector2Coverage[index] = 1.0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVector3(int index, Vector3 value)
        {
            _vectors3[index] = value;
            _vector3Coverage[index] = 1.0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVector4(int index, Vector4 value)
        {
            _vectors4[index] = value;
            _vector4Coverage[index] = 1.0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetQuaternion(int index, Quaternion value)
        {
            _quaternions[index] = NormalizeOrIdentity(value);
            _quaternionCoverage[index] = 1.0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBool(int index, bool value)
        {
            _bools[index] = value;
            _boolCoverage[index] = 1.0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetDiscrete(int index, object? value)
        {
            _discrete[index] = value;
            _discreteCoverage[index] = 1.0f;
        }

        internal Quaternion ReadQuaternionFloatGroup(AnimationQuaternionFloatSlotGroup group)
            => ReadNormalizedQuaternion(_floats, group);

        internal void WriteQuaternionFloatGroup(
            AnimationQuaternionFloatSlotGroup group,
            Quaternion value)
        {
            WriteQuaternion(_floats, group, NormalizeOrIdentity(value));
            SetQuaternionFloatGroupCoverage(group, 1.0f);
        }

        // ── Generic slot access (fallback paths) ─────────────────────────

        /// <summary>
        /// Sets a value by slot, boxing only for the Discrete path.
        /// For the typed paths, the value is unboxed and stored directly.
        /// </summary>
        public void SetValue(in AnimSlot slot, object? value)
        {
            switch (slot.Type)
            {
                case EAnimValueType.Float when value is float f:
                    _floats[slot.TypeIndex] = f;
                    _floatCoverage[slot.TypeIndex] = 1.0f;
                    break;
                case EAnimValueType.Vector2 when value is Vector2 v2:
                    _vectors2[slot.TypeIndex] = v2;
                    _vector2Coverage[slot.TypeIndex] = 1.0f;
                    break;
                case EAnimValueType.Vector3 when value is Vector3 v3:
                    _vectors3[slot.TypeIndex] = v3;
                    _vector3Coverage[slot.TypeIndex] = 1.0f;
                    break;
                case EAnimValueType.Vector4 when value is Vector4 v4:
                    _vectors4[slot.TypeIndex] = v4;
                    _vector4Coverage[slot.TypeIndex] = 1.0f;
                    break;
                case EAnimValueType.Quaternion when value is Quaternion q:
                    _quaternions[slot.TypeIndex] = NormalizeOrIdentity(q);
                    _quaternionCoverage[slot.TypeIndex] = 1.0f;
                    break;
                case EAnimValueType.Bool when value is bool b:
                    _bools[slot.TypeIndex] = b;
                    _boolCoverage[slot.TypeIndex] = 1.0f;
                    break;
                default:
                    _discrete[slot.TypeIndex] = value;
                    _discreteCoverage[slot.TypeIndex] = 1.0f;
                    break;
            }
        }

        /// <summary>
        /// Gets a value by slot. Typed values box on return — prefer the typed getters when type is known.
        /// </summary>
        public object? GetValue(in AnimSlot slot)
        {
            return slot.Type switch
            {
                EAnimValueType.Float => _floats[slot.TypeIndex],
                EAnimValueType.Vector2 => _vectors2[slot.TypeIndex],
                EAnimValueType.Vector3 => _vectors3[slot.TypeIndex],
                EAnimValueType.Vector4 => _vectors4[slot.TypeIndex],
                EAnimValueType.Quaternion => _quaternions[slot.TypeIndex],
                EAnimValueType.Bool => _bools[slot.TypeIndex],
                EAnimValueType.Discrete => _discrete[slot.TypeIndex],
                _ => null,
            };
        }

        // ── Bulk operations ──────────────────────────────────────────────

        /// <summary>
        /// Copies all typed arrays from <paramref name="source"/> into this store.
        /// Both stores must have the same layout.
        /// </summary>
        public void CopyFrom(AnimationValueStore source)
        {
            source._floats.AsSpan().CopyTo(_floats.AsSpan());
            source._vectors2.AsSpan().CopyTo(_vectors2.AsSpan());
            source._vectors3.AsSpan().CopyTo(_vectors3.AsSpan());
            source._vectors4.AsSpan().CopyTo(_vectors4.AsSpan());
            source._quaternions.AsSpan().CopyTo(_quaternions.AsSpan());
            source._bools.AsSpan().CopyTo(_bools.AsSpan());
            source._discrete.AsSpan().CopyTo(_discrete.AsSpan());
            CopyCoverageFrom(source);
        }

        /// <summary>
        /// Resets all values to their default (0 / false / null).
        /// </summary>
        public void Clear()
        {
            _floats.AsSpan().Clear();
            _vectors2.AsSpan().Clear();
            _vectors3.AsSpan().Clear();
            _vectors4.AsSpan().Clear();
            _quaternions.AsSpan().Clear();
            _bools.AsSpan().Clear();
            _discrete.AsSpan().Clear();
            ClearCoverage();
            InitializeQuaternionsToIdentity();
            InitializeQuaternionFloatGroupsToIdentity();
        }

        // ── Blend / Lerp ────────────────────────────────────────────────

        /// <summary>
        /// Linearly interpolates between <paramref name="a"/> and <paramref name="b"/> at time <paramref name="t"/>
        /// and writes the result into <paramref name="result"/>.
        /// Quaternions use Slerp; discrete values pick the closer side.
        /// </summary>
        public static void Lerp(AnimationValueStore a, AnimationValueStore b, float t, AnimationValueStore result)
            => BlendTwoPresenceAware(a, b, t, result);

        /// <summary>
        /// Weighted blend of three stores.
        /// </summary>
        public static void TriLerp(
            AnimationValueStore a, AnimationValueStore b, AnimationValueStore c,
            float w1, float w2, float w3,
            AnimationValueStore result)
            => BlendThreePresenceAware(a, b, c, w1, w2, w3, result);

        /// <summary>
        /// Weighted blend of four stores.
        /// </summary>
        public static void QuadLerp(
            AnimationValueStore a, AnimationValueStore b, AnimationValueStore c, AnimationValueStore d,
            float w1, float w2, float w3, float w4,
            AnimationValueStore result)
            => BlendFourPresenceAware(a, b, c, d, w1, w2, w3, w4, result);

        /// <summary>
        /// Blends an arbitrary preallocated set of stores without depending on source order.
        /// Direct trees use this for independently controlled child weights.
        /// </summary>
        public static void WeightedBlend(
            AnimationValueStore?[] sources,
            float[] weights,
            int count,
            bool normalizeWeights,
            AnimationValueStore result)
            => BlendManyPresenceAware(sources, weights, count, normalizeWeights, result);

        // ── Layer combine ────────────────────────────────────────────────

        /// <summary>
        /// Override-combine: copies all values from <paramref name="source"/> into this store.
        /// </summary>
        public void OverrideFrom(AnimationValueStore source)
            => OverrideFrom(source, 1.0f);

        /// <summary>
        /// Additive-combine: adds numeric values from <paramref name="source"/> to this store.
        /// Quaternions are multiplied; discrete values are overwritten.
        /// </summary>
        public void AddFrom(AnimationValueStore source)
            => AddFrom(source, 1.0f);

        // ── Private helpers ──────────────────────────────────────────────

        private void InitializeQuaternionFloatGroupsToIdentity()
        {
            for (int i = 0; i < _quaternionFloatGroups.Length; i++)
                WriteQuaternion(_floats, _quaternionFloatGroups[i], Quaternion.Identity);
        }

        private void InitializeQuaternionsToIdentity()
        {
            for (int i = 0; i < _quaternions.Length; i++)
                _quaternions[i] = Quaternion.Identity;
        }

        private static void SlerpQuaternionFloatGroups(
            AnimationValueStore a,
            AnimationValueStore b,
            float t,
            AnimationValueStore result)
        {
            for (int i = 0; i < result._quaternionFloatGroups.Length; i++)
            {
                AnimationQuaternionFloatSlotGroup group = result._quaternionFloatGroups[i];
                Quaternion blended = Quaternion.Slerp(
                    ReadNormalizedQuaternion(a._floats, group),
                    ReadNormalizedQuaternion(b._floats, group),
                    t);
                WriteQuaternion(result._floats, group, NormalizeOrIdentity(blended));
            }
        }

        private static void BlendQuaternionFloatGroups(
            AnimationValueStore a,
            AnimationValueStore b,
            AnimationValueStore c,
            float w1,
            float w2,
            float w3,
            AnimationValueStore result)
        {
            NormalizeWeights(ref w1, ref w2, ref w3);
            for (int i = 0; i < result._quaternionFloatGroups.Length; i++)
            {
                AnimationQuaternionFloatSlotGroup group = result._quaternionFloatGroups[i];
                Quaternion blended = WeightedQuaternionAverage(
                    ReadNormalizedQuaternion(a._floats, group),
                    w1,
                    ReadNormalizedQuaternion(b._floats, group),
                    w2,
                    ReadNormalizedQuaternion(c._floats, group),
                    w3);
                WriteQuaternion(result._floats, group, blended);
            }
        }

        private static void BlendQuaternionFloatGroups(
            AnimationValueStore a,
            AnimationValueStore b,
            AnimationValueStore c,
            AnimationValueStore d,
            float w1,
            float w2,
            float w3,
            float w4,
            AnimationValueStore result)
        {
            NormalizeWeights(ref w1, ref w2, ref w3, ref w4);
            for (int i = 0; i < result._quaternionFloatGroups.Length; i++)
            {
                AnimationQuaternionFloatSlotGroup group = result._quaternionFloatGroups[i];
                Quaternion blended = WeightedQuaternionAverage(
                    ReadNormalizedQuaternion(a._floats, group),
                    w1,
                    ReadNormalizedQuaternion(b._floats, group),
                    w2,
                    ReadNormalizedQuaternion(c._floats, group),
                    w3,
                    ReadNormalizedQuaternion(d._floats, group),
                    w4);
                WriteQuaternion(result._floats, group, blended);
            }
        }

        private void MultiplyQuaternionFloatGroupsAfterScalarAdd(AnimationValueStore source)
        {
            for (int i = 0; i < _quaternionFloatGroups.Length; i++)
            {
                AnimationQuaternionFloatSlotGroup group = _quaternionFloatGroups[i];
                Quaternion previous = NormalizeOrIdentity(new Quaternion(
                    _floats[group.XIndex] - source._floats[group.XIndex],
                    _floats[group.YIndex] - source._floats[group.YIndex],
                    _floats[group.ZIndex] - source._floats[group.ZIndex],
                    _floats[group.WIndex] - source._floats[group.WIndex]));
                Quaternion added = ReadNormalizedQuaternion(source._floats, group);
                WriteQuaternion(_floats, group, NormalizeOrIdentity(previous * added));
            }
        }

        private static Quaternion WeightedQuaternionAverage(
            Quaternion a,
            float aWeight,
            Quaternion b,
            float bWeight,
            Quaternion c,
            float cWeight)
            => BlendQuaternionFixed(
                a,
                Math.Max(0.0f, aWeight),
                b,
                Math.Max(0.0f, bWeight),
                c,
                Math.Max(0.0f, cWeight),
                Quaternion.Identity,
                0.0f);

        private static Quaternion WeightedQuaternionAverage(
            Quaternion a,
            float aWeight,
            Quaternion b,
            float bWeight,
            Quaternion c,
            float cWeight,
            Quaternion d,
            float dWeight)
            => BlendQuaternionFixed(
                a,
                Math.Max(0.0f, aWeight),
                b,
                Math.Max(0.0f, bWeight),
                c,
                Math.Max(0.0f, cWeight),
                d,
                Math.Max(0.0f, dWeight));

        private static Quaternion ReadNormalizedQuaternion(
            float[] values,
            AnimationQuaternionFloatSlotGroup group)
            => NormalizeOrIdentity(new Quaternion(
                values[group.XIndex],
                values[group.YIndex],
                values[group.ZIndex],
                values[group.WIndex]));

        private static Quaternion NormalizeOrIdentity(Quaternion value)
            => float.IsFinite(value.X)
                && float.IsFinite(value.Y)
                && float.IsFinite(value.Z)
                && float.IsFinite(value.W)
                && value.LengthSquared() > 1e-12f
                    ? Quaternion.Normalize(value)
                    : Quaternion.Identity;

        private static Quaternion CanonicalizeQuaternion(Quaternion value)
        {
            bool negate = value.W < 0.0f
                || (value.W == 0.0f && value.Z < 0.0f)
                || (value.W == 0.0f && value.Z == 0.0f && value.Y < 0.0f)
                || (value.W == 0.0f && value.Z == 0.0f && value.Y == 0.0f && value.X < 0.0f);
            return negate
                ? new Quaternion(-value.X, -value.Y, -value.Z, -value.W)
                : value;
        }

        private static Vector4 ToVector4(Quaternion value)
            => new(value.X, value.Y, value.Z, value.W);

        private static Quaternion NormalizeQuaternionVector(Vector4 value)
            => NormalizeOrIdentity(new Quaternion(value.X, value.Y, value.Z, value.W));

        private static float SanitizeWeight(AnimationValueStore? source, float weight)
            => source is not null && float.IsFinite(weight) && weight > 0.0f
                ? weight
                : 0.0f;

        private static void WriteQuaternion(
            float[] values,
            AnimationQuaternionFloatSlotGroup group,
            Quaternion value)
        {
            values[group.XIndex] = value.X;
            values[group.YIndex] = value.Y;
            values[group.ZIndex] = value.Z;
            values[group.WIndex] = value.W;
        }

        private static void NormalizeWeights(ref float w1, ref float w2, ref float w3)
        {
            float total = w1 + w2 + w3;
            if (!float.IsFinite(total) || MathF.Abs(total) <= float.Epsilon)
            {
                w1 = 1.0f;
                w2 = 0.0f;
                w3 = 0.0f;
                return;
            }

            w1 /= total;
            w2 /= total;
            w3 /= total;
        }

        private static void NormalizeWeights(ref float w1, ref float w2, ref float w3, ref float w4)
        {
            float total = w1 + w2 + w3 + w4;
            if (!float.IsFinite(total) || MathF.Abs(total) <= float.Epsilon)
            {
                w1 = 1.0f;
                w2 = 0.0f;
                w3 = 0.0f;
                w4 = 0.0f;
                return;
            }

            w1 /= total;
            w2 /= total;
            w3 /= total;
            w4 /= total;
        }

        private static void LerpFloats(float[] a, float[] b, float t, float[] result)
        {
            LerpFloatsSimd(a.AsSpan(), b.AsSpan(), t, result.AsSpan());
        }

        private static void LerpVectors2(Vector2[] a, Vector2[] b, float t, Vector2[] result)
        {
            // Vector2 = 2 contiguous floats — reinterpret for wide SIMD lerp
            LerpFloatsSimd(
                MemoryMarshal.Cast<Vector2, float>(a.AsSpan()),
                MemoryMarshal.Cast<Vector2, float>(b.AsSpan()),
                t,
                MemoryMarshal.Cast<Vector2, float>(result.AsSpan()));
        }

        private static void LerpVectors3(Vector3[] a, Vector3[] b, float t, Vector3[] result)
        {
            // Vector3 has 12-byte stride (not power-of-2), keep per-element to avoid alignment issues
            var sa = a.AsSpan(); var sb = b.AsSpan(); var sr = result.AsSpan();
            for (int i = 0; i < sr.Length; i++)
                sr[i] = Vector3.Lerp(sa[i], sb[i], t);
        }

        private static void LerpVectors4(Vector4[] a, Vector4[] b, float t, Vector4[] result)
        {
            // Vector4 = 4 contiguous floats — reinterpret for wide SIMD lerp
            LerpFloatsSimd(
                MemoryMarshal.Cast<Vector4, float>(a.AsSpan()),
                MemoryMarshal.Cast<Vector4, float>(b.AsSpan()),
                t,
                MemoryMarshal.Cast<Vector4, float>(result.AsSpan()));
        }

        private static void SlerpQuaternions(Quaternion[] a, Quaternion[] b, float t, Quaternion[] result)
        {
            var sa = a.AsSpan(); var sb = b.AsSpan(); var sr = result.AsSpan();
            for (int i = 0; i < sr.Length; i++)
                sr[i] = Quaternion.Slerp(sa[i], sb[i], t);
        }

        private static void LerpBools(bool[] a, bool[] b, float t, bool[] result)
        {
            var sa = a.AsSpan(); var sb = b.AsSpan(); var sr = result.AsSpan();
            for (int i = 0; i < sr.Length; i++)
                sr[i] = t > 0.5f ? sb[i] : sa[i];
        }

        private static void LerpDiscrete(object?[] a, object?[] b, float t, object?[] result)
        {
            var sa = a.AsSpan(); var sb = b.AsSpan(); var sr = result.AsSpan();
            for (int i = 0; i < sr.Length; i++)
                sr[i] = t > 0.5f ? sb[i] : sa[i];
        }

        // ── SIMD bulk helpers ────────────────────────────────────────────

        /// <summary>
        /// Lerps two float spans using <see cref="Vector{T}"/> for hardware-width SIMD.
        /// Falls back to scalar for the tail elements that don't fill a full vector.
        /// result[i] = a[i] + (b[i] - a[i]) * t
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void LerpFloatsSimd(ReadOnlySpan<float> a, ReadOnlySpan<float> b, float t, Span<float> result)
        {
            int count = result.Length;
            int vecSize = Vector<float>.Count;
            int simdEnd = count - (count % vecSize);
            var vt = new Vector<float>(t);

            for (int i = 0; i < simdEnd; i += vecSize)
            {
                var va = new Vector<float>(a.Slice(i, vecSize));
                var vb = new Vector<float>(b.Slice(i, vecSize));
                (va + (vb - va) * vt).CopyTo(result.Slice(i, vecSize));
            }

            // Scalar tail
            for (int i = simdEnd; i < count; i++)
                result[i] = a[i] + (b[i] - a[i]) * t;
        }

        /// <summary>
        /// Adds <paramref name="src"/> into <paramref name="dst"/> using <see cref="Vector{T}"/> SIMD.
        /// dst[i] += src[i]
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddFloatsSimd(ReadOnlySpan<float> src, Span<float> dst)
        {
            int count = dst.Length;
            int vecSize = Vector<float>.Count;
            int simdEnd = count - (count % vecSize);

            for (int i = 0; i < simdEnd; i += vecSize)
            {
                var vs = new Vector<float>(src.Slice(i, vecSize));
                var vd = new Vector<float>(dst.Slice(i, vecSize));
                (vd + vs).CopyTo(dst.Slice(i, vecSize));
            }

            // Scalar tail
            for (int i = simdEnd; i < count; i++)
                dst[i] += src[i];
        }

        /// <summary>
        /// Weighted-sum of three float spans using <see cref="Vector{T}"/> SIMD.
        /// result[i] = a[i]*w1 + b[i]*w2 + c[i]*w3
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WeightedSum3Simd(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<float> c,
            float w1, float w2, float w3, Span<float> result)
        {
            int count = result.Length;
            int vecSize = Vector<float>.Count;
            int simdEnd = count - (count % vecSize);
            var vw1 = new Vector<float>(w1);
            var vw2 = new Vector<float>(w2);
            var vw3 = new Vector<float>(w3);

            for (int i = 0; i < simdEnd; i += vecSize)
            {
                var va = new Vector<float>(a.Slice(i, vecSize));
                var vb = new Vector<float>(b.Slice(i, vecSize));
                var vc = new Vector<float>(c.Slice(i, vecSize));
                (va * vw1 + vb * vw2 + vc * vw3).CopyTo(result.Slice(i, vecSize));
            }

            for (int i = simdEnd; i < count; i++)
                result[i] = a[i] * w1 + b[i] * w2 + c[i] * w3;
        }

        /// <summary>
        /// Weighted-sum of four float spans using <see cref="Vector{T}"/> SIMD.
        /// result[i] = a[i]*w1 + b[i]*w2 + c[i]*w3 + d[i]*w4
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WeightedSum4Simd(ReadOnlySpan<float> a, ReadOnlySpan<float> b,
            ReadOnlySpan<float> c, ReadOnlySpan<float> d,
            float w1, float w2, float w3, float w4, Span<float> result)
        {
            int count = result.Length;
            int vecSize = Vector<float>.Count;
            int simdEnd = count - (count % vecSize);
            var vw1 = new Vector<float>(w1);
            var vw2 = new Vector<float>(w2);
            var vw3 = new Vector<float>(w3);
            var vw4 = new Vector<float>(w4);

            for (int i = 0; i < simdEnd; i += vecSize)
            {
                var va = new Vector<float>(a.Slice(i, vecSize));
                var vb = new Vector<float>(b.Slice(i, vecSize));
                var vc = new Vector<float>(c.Slice(i, vecSize));
                var vd = new Vector<float>(d.Slice(i, vecSize));
                (va * vw1 + vb * vw2 + vc * vw3 + vd * vw4).CopyTo(result.Slice(i, vecSize));
            }

            for (int i = simdEnd; i < count; i++)
                result[i] = a[i] * w1 + b[i] * w2 + c[i] * w3 + d[i] * w4;
        }
    }

    /// <summary>
    /// Describes the number of slots per type, used to size <see cref="AnimationValueStore"/> instances consistently.
    /// Built once during initialization; shared by all stores in the same state machine.
    /// </summary>
    public sealed class AnimationSlotLayout
    {
        public int FloatCount { get; set; }
        public int Vector2Count { get; set; }
        public int Vector3Count { get; set; }
        public int Vector4Count { get; set; }
        public int QuaternionCount { get; set; }
        public int BoolCount { get; set; }
        public int DiscreteCount { get; set; }

        /// <summary>
        /// Scalar slot quartets that blend as complete shortest-arc quaternions.
        /// </summary>
        public AnimationQuaternionFloatSlotGroup[] QuaternionFloatGroups { get; internal set; } = [];

        /// <summary>
        /// Creates a new <see cref="AnimationValueStore"/> sized to this layout.
        /// </summary>
        public AnimationValueStore CreateStore()
        {
            var store = new AnimationValueStore();
            store.Resize(this);
            return store;
        }

        /// <summary>
        /// Allocates the next slot index for the given value type and returns the assigned slot.
        /// </summary>
        public AnimSlot AllocateSlot(EAnimValueType type)
        {
            int index = type switch
            {
                EAnimValueType.Float => FloatCount++,
                EAnimValueType.Vector2 => Vector2Count++,
                EAnimValueType.Vector3 => Vector3Count++,
                EAnimValueType.Vector4 => Vector4Count++,
                EAnimValueType.Quaternion => QuaternionCount++,
                EAnimValueType.Bool => BoolCount++,
                EAnimValueType.Discrete => DiscreteCount++,
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
            return new AnimSlot { Type = type, TypeIndex = index };
        }
    }
}
