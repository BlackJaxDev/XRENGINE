using System.Collections;
using XREngine.Extensions;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Scene.Transforms;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace XREngine.Data.Rendering
{
    public class Vertex : VertexData, IEquatable<Vertex>, IEnumerable<Vertex>
    {
        private Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? _weights;
        /// <summary>
        /// Contains weights for each bone that influences the position of this vertex.
        /// </summary>
        public Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? Weights
        {
            get => _weights;
            set => SetField(ref _weights, value);
        }

        private List<(string name, VertexData data)>? _blendshapes;
        /// <summary>
        /// Data this vertex can morph to, indexed by blendshape name.
        /// Data here is absolute, not deltas, for simplicity.
        /// </summary>
        public List<(string name, VertexData data)>? Blendshapes
        {
            get => _blendshapes;
            set => SetField(ref _blendshapes, value);
        }

        public Vertex()
        {
        }

        public Vertex(Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? weights)
            : this() => Weights = weights;

        public Vertex(Vector3 position)
            : this() => Position = position;

        public Vertex(Vector3 position, Vector4 color)
            : this(position) => ColorSets = [color];

        public Vertex(Vector3 position, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? weights)
            : this(position) => Weights = weights;

        public Vertex(Vector3 position, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? weights, Vector3 normal)
            : this(position, weights) => Normal = normal;

        public Vertex(Vector3 position, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? inf, Vector3 normal, Vector2 texCoord)
            : this(position, inf, normal) => TextureCoordinateSets = [texCoord];

        public Vertex(Vector3 position, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? inf, Vector3 normal, Vector2 texCoord, Vector4 color)
            : this(position, inf, normal, texCoord) => ColorSets = [color];

        public Vertex(Vector3 position, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? inf, Vector3 normal, Vector3 tangent, Vector2 texCoord, Vector4 color)
            : this(position, inf, normal, texCoord, color) => Tangent = tangent;

        public Vertex(Vector3 position, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? inf, Vector2 texCoord)
            : this(position, inf) => TextureCoordinateSets = [texCoord];

        public Vertex(Vector3 position, Vector2 texCoord)
            : this(position) => TextureCoordinateSets = [texCoord];

        public Vertex(Vector3 position, Vector3 normal)
            : this(position, null, normal) { }

        public Vertex(Vector3 position, Vector3 normal, Vector2 texCoord)
            : this(position, null, normal) => TextureCoordinateSets = [texCoord];

        public Vertex(Vector3 position, Vector3 normal, Vector2 texCoord, Vector4 color)
            : this(position, null, normal, texCoord) => ColorSets = [color];

        public Vertex(Vector3 position, Vector3 normal, Vector3 tangent, Vector2 texCoord, Vector4 color)
            : this(position, null, normal, texCoord, color) => Tangent = tangent;

        public override bool Equals(object? obj) 
            => obj is Vertex vertex && Equals(vertex);

        public bool Equals(Vertex? other)
        {
            if (ReferenceEquals(this, other))
                return true;

            if (other is null)
                return false;

            return VertexDataEquals(this, other)
                && WeightsEqual(Weights, other.Weights)
                && BlendshapesEqual(Blendshapes, other.Blendshapes);
        }

        public static implicit operator Vertex(Vector3 pos) => new(pos);

        public IEnumerator<Vertex> GetEnumerator()
        {
            yield return this;
        }

        IEnumerator IEnumerable.GetEnumerator()
            => GetEnumerator();

        public override int GetHashCode()
        {
            var hash = new HashCode();
            AddVertexDataHash(ref hash, this);
            AddWeightsHash(ref hash, Weights);
            AddBlendshapeHash(ref hash, Blendshapes);
            return hash.ToHashCode();
        }

        private static bool VertexDataEquals(VertexData left, VertexData right)
            => left.Position == right.Position
            && Nullable.Equals(left.Normal, right.Normal)
            && Nullable.Equals(left.Tangent, right.Tangent)
            && left.BitangentSign == right.BitangentSign
            && SequenceEqual(left.TextureCoordinateSets, right.TextureCoordinateSets)
            && SequenceEqual(left.ColorSets, right.ColorSets);

        private static bool SequenceEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
        {
            if (ReferenceEquals(left, right))
                return true;

            if (left is null || right is null || left.Count != right.Count)
                return false;

            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < left.Count; i++)
            {
                if (!comparer.Equals(left[i], right[i]))
                    return false;
            }

            return true;
        }

        private static bool WeightsEqual(
            Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? left,
            Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? right)
        {
            if (ReferenceEquals(left, right))
                return true;

            if (left is null || right is null || left.Count != right.Count)
                return false;

            foreach (var pair in left)
            {
                if (!TryGetWeight(right, pair.Key, out var otherWeight) || otherWeight != pair.Value)
                    return false;
            }

            return true;
        }

        private static bool TryGetWeight(
            Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> weights,
            TransformBase key,
            out (float weight, Matrix4x4 bindInvWorldMatrix) value)
        {
            foreach (var pair in weights)
            {
                if (ReferenceEquals(pair.Key, key))
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static bool BlendshapesEqual(List<(string name, VertexData data)>? left, List<(string name, VertexData data)>? right)
        {
            if (ReferenceEquals(left, right))
                return true;

            if (left is null || right is null || left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; i++)
            {
                var leftBlendshape = left[i];
                var rightBlendshape = right[i];
                if (leftBlendshape.name != rightBlendshape.name || !VertexDataEquals(leftBlendshape.data, rightBlendshape.data))
                    return false;
            }

            return true;
        }

        private static void AddVertexDataHash(ref HashCode hash, VertexData data)
        {
            hash.Add(data.Position);
            hash.Add(data.Normal);
            hash.Add(data.Tangent);
            hash.Add(data.BitangentSign);
            AddSequenceHash(ref hash, data.TextureCoordinateSets);
            AddSequenceHash(ref hash, data.ColorSets);
        }

        private static void AddSequenceHash<T>(ref HashCode hash, IReadOnlyList<T>? values)
        {
            if (values is null)
            {
                hash.Add(0);
                return;
            }

            hash.Add(values.Count);
            foreach (var value in values)
                hash.Add(value);
        }

        private static void AddWeightsHash(
            ref HashCode hash,
            Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? weights)
        {
            if (weights is null)
            {
                hash.Add(0);
                return;
            }

            int combinedHash = 0;
            foreach (var pair in weights)
            {
                combinedHash ^= HashCode.Combine(
                    RuntimeHelpers.GetHashCode(pair.Key),
                    pair.Value.weight,
                    pair.Value.bindInvWorldMatrix);
            }

            hash.Add(weights.Count);
            hash.Add(combinedHash);
        }

        private static void AddBlendshapeHash(ref HashCode hash, List<(string name, VertexData data)>? blendshapes)
        {
            if (blendshapes is null)
            {
                hash.Add(0);
                return;
            }

            hash.Add(blendshapes.Count);
            foreach (var blendshape in blendshapes)
            {
                hash.Add(blendshape.name);
                AddVertexDataHash(ref hash, blendshape.data);
            }
        }

        public Vertex HardCopy()
            => new()
            {
                Weights = Weights is null ? null : new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>(Weights),
                Position = Position,
                Normal = Normal,
                Tangent = Tangent,
                BitangentSign = BitangentSign,
                TextureCoordinateSets = TextureCoordinateSets is null ? null : new(TextureCoordinateSets),
                ColorSets = ColorSets is null ? null : new(ColorSets),
                Blendshapes = Blendshapes is null ? null : new(Blendshapes),
            };

        public Vector3 GetWorldPosition()
        {
            if (Weights is null || Weights.Count == 0)
                return Position;

            Vector3 pos = Vector3.Zero;
            foreach ((TransformBase bone, (float weight, Matrix4x4 bindInvWorldMatrix) pair) in Weights)
                pos += Vector3.Transform(Position, pair.bindInvWorldMatrix * bone.WorldMatrix) * pair.weight;

            return pos;
        }

        public Vector3 GetWorldBindPosition()
        {
            if (Weights is null || Weights.Count == 0)
                return Position;
            
            Vector3 pos = Vector3.Zero;
            foreach ((TransformBase bone, (float weight, Matrix4x4 bindInvWorldMatrix) pair) in Weights)
                pos += Vector3.Transform(Position, pair.bindInvWorldMatrix * bone.BindMatrix) * pair.weight;

            return pos;
        }

        public Matrix4x4 GetBoneTransformMatrix()
        {
            if (Weights is null || Weights.Count == 0)
                return Matrix4x4.Identity;

            Matrix4x4 matrix = new();
            foreach ((TransformBase bone, (float weight, Matrix4x4 bindInvWorldMatrix) pair) in Weights)
                matrix += (pair.bindInvWorldMatrix * bone.WorldMatrix) * pair.weight;

            return matrix;
        }

        public Matrix4x4 GetInverseBoneTransformMatrix()
        {
            if (Weights is null || Weights.Count == 0)
                return Matrix4x4.Identity;

            Matrix4x4 matrix = new();
            foreach ((TransformBase bone, (float weight, Matrix4x4 bindInvWorldMatrix) pair) in Weights)
                matrix += (bone.InverseWorldMatrix * pair.bindInvWorldMatrix.Inverted()) * pair.weight;

            return matrix;
        }

    }
}
