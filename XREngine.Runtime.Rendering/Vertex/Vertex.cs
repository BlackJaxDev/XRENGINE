using System.Collections;
using Assimp;
using Extensions;
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

        public static unsafe Vertex FromAssimp(Mesh mesh, int vertexIndex, Matrix4x4 dataTransform)
        {
            Vector3 pos = Vector3.Transform(mesh.Vertices[vertexIndex], dataTransform);
            Vector3? normal = (mesh.Normals?.TryGet(vertexIndex, out var normalValue) ?? false) ? Vector3.TransformNormal(normalValue, dataTransform) : null;
            Vector3? tangent = (mesh.Tangents?.TryGet(vertexIndex, out var tangentValue) ?? false) ? Vector3.TransformNormal(tangentValue, dataTransform) : null;
            Vector3? bitangent = (mesh.BiTangents?.TryGet(vertexIndex, out var bitangentValue) ?? false) ? Vector3.TransformNormal(bitangentValue, dataTransform) : null;

            //If two of the three vectors are zero, the normal is calculated from the cross product of the other two.
            if (normal == null)
            {
                if (tangent != null && bitangent != null)
                    normal = Vector3.Cross(tangent.Value, bitangent.Value);
            }
            if (tangent == null)
            {
                if (normal != null && bitangent != null)
                    tangent = Vector3.Cross(normal.Value, bitangent.Value);
            }

            // Compute bitangent handedness from the Assimp bitangent before discarding it.
            // sign(dot(cross(normal, tangent), bitangent)) tells us whether the tangent space
            // is right-handed (+1) or left-handed/mirrored (-1).
            float bitangentSign = 1.0f;
            if (normal != null && tangent != null && bitangent != null)
            {
                Vector3 computed = Vector3.Cross(normal.Value, tangent.Value);
                bitangentSign = Vector3.Dot(computed, bitangent.Value) < 0 ? -1.0f : 1.0f;
            }

            Vertex v = new()
            {
                Position = pos,
                Normal = normal,
                Tangent = tangent,
                BitangentSign = bitangentSign
            };

            for (int i = 0; i < mesh.TextureCoordinateChannelCount; ++i)
            {
                var channel = mesh.TextureCoordinateChannels[i];
                if (channel is null || vertexIndex >= channel.Count)
                    break;

                Vector3 uv = channel[vertexIndex];

                if (v.TextureCoordinateSets is null)
                    v.TextureCoordinateSets = [new Vector2(uv.X, uv.Y)];
                else
                    v.TextureCoordinateSets.Add(new Vector2(uv.X, uv.Y));
            }

            for (int i = 0; i < mesh.VertexColorChannelCount; ++i)
            {
                var channel = mesh.VertexColorChannels[i];
                if (channel is null || vertexIndex >= channel.Count)
                    break;

                if (v.ColorSets is null)
                    v.ColorSets = [channel[vertexIndex]];
                else
                    v.ColorSets.Add(channel[vertexIndex]);
            }

            //Blendshapes
            int blendshapeCount = mesh.MeshAnimationAttachmentCount;
            if (blendshapeCount > 0)
            {
                v.Blendshapes = [];
                for (int i = 0; i < blendshapeCount; ++i)
                {
                    var blendshape = mesh.MeshAnimationAttachments[i];

                    VertexData data = new()
                    {
                        Position = Vector3.Transform(blendshape.Vertices[vertexIndex], dataTransform)
                    };

                    if (blendshape.Normals != null && blendshape.Normals.Count > vertexIndex)
                        data.Normal = Vector3.TransformNormal(blendshape.Normals[vertexIndex], dataTransform);

                    if (blendshape.Tangents != null && blendshape.Tangents.Count > vertexIndex)
                        data.Tangent = Vector3.TransformNormal(blendshape.Tangents[vertexIndex], dataTransform);

                    for (int j = 0; j < blendshape.TextureCoordinateChannelCount; ++j)
                    {
                        if (blendshape.TextureCoordinateChannels[j] is null || vertexIndex >= blendshape.TextureCoordinateChannels[j].Count)
                            break;

                        Vector3 uv = blendshape.TextureCoordinateChannels[j][vertexIndex];

                        if (data.TextureCoordinateSets is null)
                            data.TextureCoordinateSets = [new Vector2(uv.X, uv.Y)];
                        else
                            data.TextureCoordinateSets.Add(new Vector2(uv.X, uv.Y));
                    }
                    for (int j = 0; j < blendshape.VertexColorChannelCount; ++j)
                    {
                        if (blendshape.VertexColorChannels[j] is null || vertexIndex >= blendshape.VertexColorChannels[j].Count)
                            break;

                        Vector4 color = blendshape.VertexColorChannels[j][vertexIndex];

                        if (data.ColorSets is null)
                            data.ColorSets = [color];
                        else
                            data.ColorSets.Add(color);
                    }

                    v.Blendshapes.Add((blendshape.Name, data));
                }
            }

            return v;
        }
    }
}