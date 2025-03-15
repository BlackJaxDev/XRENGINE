using Assimp;
using Extensions;
using SimpleScene.Util.ssBVH;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Triangle = XREngine.Data.Geometry.Triangle;

namespace XREngine.Rendering
{
    /// <summary>
    /// This class contains buffer-organized mesh data that can be rendered using an XRMeshRenderer.
    /// </summary>
    public partial class XRMesh : XRAsset
    {
        private delegate void DelVertexAction(XRMesh @this, int index, int remappedIndex, Vertex vtx, Matrix4x4? dataTransform);

        [YamlIgnore]
        public XREvent<XRMesh> DataChanged;

        private bool _interleaved = false;
        public bool Interleaved
        {
            get => _interleaved;
            set => SetField(ref _interleaved, value);
        }

        private uint _interleavedStride = 0u;
        public uint InterleavedStride
        {
            get => _interleavedStride;
            set => SetField(ref _interleavedStride, value);
        }

        private uint _positionOffset = 0;
        public uint PositionOffset
        {
            get => _positionOffset;
            set => SetField(ref _positionOffset, value);
        }

        private uint? _normalOffset = 0;
        public uint? NormalOffset
        {
            get => _normalOffset;
            set => SetField(ref _normalOffset, value);
        }

        private uint? _tangentOffset = null;
        public uint? TangentOffset
        {
            get => _tangentOffset;
            set => SetField(ref _tangentOffset, value);
        }

        private uint? _colorOffset = null;
        public uint? ColorOffset
        {
            get => _colorOffset;
            set => SetField(ref _colorOffset, value);
        }

        private uint? _texCoordOffset = null;
        public uint? TexCoordOffset
        {
            get => _texCoordOffset;
            set => SetField(ref _texCoordOffset, value);
        }

        private uint _colorCount = 0;
        public uint ColorCount
        {
            get => _colorCount;
            set => SetField(ref _colorCount, value);
        }

        private uint _texCoordCount = 0;
        public uint TexCoordCount
        {
            get => _texCoordCount;
            set => SetField(ref _texCoordCount, value);
        }

        private void PopulateVertexData(
            IEnumerable<DelVertexAction> vertexActions,
            List<Vertex> sourceList,
            int[]? firstAppearanceArray,
            Matrix4x4? dataTransform,
            bool parallel)
        {
            int len = firstAppearanceArray?.Length ?? sourceList.Count;
            using var t = Engine.Profiler.Start($"PopulateVertexData (remapped): {len} {(parallel ? "parallel" : "sequential")}");

            if (parallel)
                Parallel.For(0, len, i => SetVertexData(i, vertexActions, sourceList, firstAppearanceArray, dataTransform));
            else
                for (int i = 0; i < len; ++i)
                    SetVertexData(i, vertexActions, sourceList, firstAppearanceArray, dataTransform);
        }

        private void PopulateVertexData(
            IEnumerable<DelVertexAction> vertexActions,
            List<Vertex> sourceList,
            int count,
            Matrix4x4? dataTransform,
            bool parallel)
        {
            using var t = Engine.Profiler.Start($"PopulateVertexData: {count} {(parallel ? "parallel" : "sequential")}");

            if (parallel)
                Parallel.For(0, count, i => SetVertexData(i, vertexActions, sourceList, dataTransform));
            else
                for (int i = 0; i < count; ++i)
                    SetVertexData(i, vertexActions, sourceList, dataTransform);
        }

        private void SetVertexData(
            int i,
            IEnumerable<DelVertexAction> vertexActions,
            List<Vertex> sourceList,
            int[]? remapArray,
            Matrix4x4? dataTransform)
        {
            using var t = Engine.Profiler.Start();

            int x = remapArray?[i] ?? i;
            Vertex vtx = sourceList[x];
            foreach (var action in vertexActions)
                action.Invoke(this, i, x, vtx, dataTransform);
        }

        private void SetVertexData(
            int i,
            IEnumerable<DelVertexAction> vertexActions,
            List<Vertex> sourceList,
            Matrix4x4? dataTransform)
        {
            //using var t = Engine.Profiler.Start();

            Vertex vtx = sourceList[i];
            foreach (var action in vertexActions)
                action.Invoke(this, i, i, vtx, dataTransform);
        }

        private void InitMeshBuffers(
            bool hasNormals,
            bool hasTangents,
            int colorCount,
            int texCoordCount)
        {
            using var t = Engine.Profiler.Start();
            ColorCount = (uint)colorCount;
            TexCoordCount = (uint)texCoordCount;
            Interleaved = Engine.Rendering.Settings.UseInterleavedMeshBuffer;
            if (Interleaved)
            {
                InterleavedVertexBuffer = new XRDataBuffer(ECommonBufferType.InterleavedVertex.ToString(), EBufferTarget.ArrayBuffer, false)
                {
                    BindingIndexOverride = 0
                };
                List<InterleavedAttribute> attributes = [];
                attributes.Add((null, ECommonBufferType.Position.ToString(), 0u, EComponentType.Float, 3, false));
                uint stride = 12u; //Position
                PositionOffset = 0;
                //add position, normal, tangent, color, texcoord
                if (hasNormals)
                {
                    NormalOffset = stride;
                    attributes.Add((null, ECommonBufferType.Normal.ToString(), stride, EComponentType.Float, 3, false));
                    stride += 12u;
                }
                if (hasTangents)
                {
                    TangentOffset = stride;
                    attributes.Add((null, ECommonBufferType.Tangent.ToString(), stride, EComponentType.Float, 3, false));
                    stride += 12u;
                }
                if (colorCount > 0)
                {
                    ColorOffset = stride;
                    ColorCount = (uint)colorCount;
                    for (int i = 0; i < colorCount; ++i)
                    {
                        string binding = $"{ECommonBufferType.Color}{i}";
                        attributes.Add((null, binding, stride, EComponentType.Float, 4, false));
                        stride += 16u;
                    }
                }
                if (texCoordCount > 0)
                {
                    TexCoordOffset = stride;
                    TexCoordCount = (uint)texCoordCount;
                    for (int i = 0; i < texCoordCount; ++i)
                    {
                        string binding = $"{ECommonBufferType.TexCoord}{i}";
                        attributes.Add((null, binding, stride, EComponentType.Float, 2, false));
                        stride += 8u;
                    }
                }
                InterleavedStride = stride;
                InterleavedVertexBuffer.InterleavedAttributes = [.. attributes];
                InterleavedVertexBuffer.Allocate(stride, (uint)VertexCount);
                Buffers.Add(ECommonBufferType.InterleavedVertex.ToString(), InterleavedVertexBuffer);
            }
            else
            {
                PositionsBuffer = new XRDataBuffer(ECommonBufferType.Position.ToString(), EBufferTarget.ArrayBuffer, false);
                PositionsBuffer.Allocate<Vector3>((uint)VertexCount);
                Buffers.Add(ECommonBufferType.Position.ToString(), PositionsBuffer);

                if (hasNormals)
                {
                    NormalsBuffer = new XRDataBuffer(ECommonBufferType.Normal.ToString(), EBufferTarget.ArrayBuffer, false);
                    NormalsBuffer.Allocate<Vector3>((uint)VertexCount);
                    Buffers.Add(ECommonBufferType.Normal.ToString(), NormalsBuffer);
                }

                if (hasTangents)
                {
                    TangentsBuffer = new XRDataBuffer(ECommonBufferType.Tangent.ToString(), EBufferTarget.ArrayBuffer, false);
                    TangentsBuffer.Allocate<Vector3>((uint)VertexCount);
                    Buffers.Add(ECommonBufferType.Tangent.ToString(), TangentsBuffer);
                }

                if (colorCount > 0)
                {
                    ColorBuffers = new XRDataBuffer[colorCount];
                    for (int colorIndex = 0; colorIndex < colorCount; ++colorIndex)
                    {
                        string binding = $"{ECommonBufferType.Color}{colorIndex}";
                        ColorBuffers[colorIndex] = new XRDataBuffer(binding, EBufferTarget.ArrayBuffer, false);
                        ColorBuffers[colorIndex].Allocate<Vector4>((uint)VertexCount);
                        Buffers.Add(binding, ColorBuffers[colorIndex]);
                    }
                }

                if (texCoordCount > 0)
                {
                    TexCoordBuffers = new XRDataBuffer[texCoordCount];
                    for (int texCoordIndex = 0; texCoordIndex < texCoordCount; ++texCoordIndex)
                    {
                        string binding = $"{ECommonBufferType.TexCoord}{texCoordIndex}";
                        TexCoordBuffers[texCoordIndex] = new XRDataBuffer(binding, EBufferTarget.ArrayBuffer, false);
                        TexCoordBuffers[texCoordIndex].Allocate<Vector2>((uint)VertexCount);
                        Buffers.Add(binding, TexCoordBuffers[texCoordIndex]);
                    }
                }
            }
        }

        public int VertexCount { get; private set; } = 0;

        //private void MakeFaceIndices(ConcurrentDictionary<TransformBase, float>[]? weights, int vertexCount)
        //{
        //    _faceIndices = new VertexIndices[vertexCount];
        //    for (int i = 0; i < vertexCount; ++i)
        //    {
        //        Dictionary<string, uint> bufferBindings = [];
        //        foreach (string bindingName in Buffers.Keys)
        //            bufferBindings.Add(bindingName, (uint)i);
        //        _faceIndices[i] = new VertexIndices()
        //        {
        //            BufferBindings = bufferBindings,
        //            WeightIndex = weights is null || weights.Length == 0 ? -1 : i
        //        };
        //    }
        //}

        //private void UpdateFaceIndices(int dataCount, string bindingName, bool remap, uint instanceDivisor, Remapper? remapper)
        //{
        //    if (instanceDivisor != 0)
        //        return;

        //    Func<uint, uint> getter = remap && remapper is not null && remapper.RemapTable is not null && remapper.ImplementationTable is not null
        //        ? i => (uint)remapper.ImplementationTable[remapper.RemapTable[i]]
        //        : i => i;

        //    for (uint i = 0; i < dataCount; ++i)
        //    {
        //        var bindings = _faceIndices[(int)i].BufferBindings;
        //        if (bindings.ContainsKey(bindingName))
        //            bindings[bindingName] = getter(i);
        //        else
        //            bindings.Add(bindingName, getter(i));
        //    }
        //}

        public bool HasSkinning => _utilizedBones is not null && _utilizedBones.Length > 0;

        private List<Vertex> _vertices = [];
        public List<Vertex> Vertices
        {
            get => _vertices;
            private set => SetField(ref _vertices, value);
        }

        //Specific common buffers

        #region Per-Facepoint Buffers

        #region Data Buffers
        public XRDataBuffer? PositionsBuffer { get; private set; } //Required
        public XRDataBuffer? NormalsBuffer { get; private set; }
        public XRDataBuffer? TangentsBuffer { get; private set; }
        public XRDataBuffer[]? ColorBuffers { get; private set; } = [];
        public XRDataBuffer[]? TexCoordBuffers { get; private set; } = [];
        public XRDataBuffer? InterleavedVertexBuffer { get; private set; }
        #endregion

        //On-GPU transformations
        /// <summary>
        /// Offset into the bone index/weight buffers to find each bone weighted to this vertex.
        /// </summary>
        public XRDataBuffer? BoneWeightOffsets { get; private set; }
        /// <summary>
        /// Number of indices/weights in the bone index/weight buffers for each vertex.
        /// </summary>
        public XRDataBuffer? BoneWeightCounts { get; private set; }
        /// <summary>
        /// Number of indices/weights in the blendshape index/weight buffers for each vertex.
        /// </summary>
        public XRDataBuffer? BlendshapeCounts { get; private set; }

        #endregion

        public BufferCollection Buffers { get; private set; } = [];

        //[Browsable(false)]
        //public VertexWeightGroup[] WeightsPerVertex
        //{
        //    get => _weightsPerVertex;
        //    set => SetField(ref _weightsPerVertex, value);
        //}

        //[Browsable(false)]
        //public VertexIndices[] FaceIndices
        //{
        //    get => _faceIndices;
        //    set => SetField(ref _faceIndices, value);
        //}

        [Browsable(false)]
        public List<IndexTriangle>? Triangles
        {
            get => _triangles;
            set => SetField(ref _triangles, value);
        }

        [Browsable(false)]
        public List<IndexLine>? Lines
        {
            get => _lines;
            set => SetField(ref _lines, value);
        }

        [Browsable(false)]
        public List<int>? Points
        {
            get => _points;
            set => SetField(ref _points, value);
        }

        [Browsable(false)]
        public EPrimitiveType Type
        {
            get => _type;
            set => SetField(ref _type, value);
        }

        public (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] UtilizedBones
        {
            get => _utilizedBones;
            set => SetField(ref _utilizedBones, value);
        }

        public bool IsSingleBound => UtilizedBones.Length == 1;
        public bool IsUnskinned => UtilizedBones.Length == 0;
        public uint BlendshapeCount => (uint)(BlendshapeNames?.Length ?? 0);
        public string[] BlendshapeNames { get; set; } = [];
        public bool HasBlendshapes => BlendshapeCount > 0;

        private (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] _utilizedBones = [];
        //private VertexWeightGroup[] _weightsPerVertex = [];
        //This is the buffer data that will be passed to the shader.
        //Each buffer may have repeated values, as there must be a value for each remapped face point.
        //The key is the binding name and the value is the buffer.
        //private EventDictionary<string, XRDataBuffer> _buffers = [];
        //Face data last
        //Face points have indices that refer to each buffer.
        //These may contain repeat buffer indices but each point is unique.
        //private VertexIndices[] _faceIndices = [];

        //Each point, line and triangle has indices that refer to the face indices array.
        //These may contain repeat vertex indices but each primitive is unique.
        private List<int>? _points = null;
        private List<IndexLine>? _lines = null;
        private List<IndexTriangle>? _triangles = null;

        private EPrimitiveType _type = EPrimitiveType.Triangles;
        private AABB _bounds = new(Vector3.Zero, Vector3.Zero);

        /// <summary>
        /// The axis-aligned bounds of this mesh before any vertex transformations.
        /// </summary>
        public AABB Bounds
        {
            get => _bounds;
            private set => _bounds = value;
        }

        public static XRMesh Create<T>(params T[] prims) where T : VertexPrimitive
            => new(prims);
        public static XRMesh Create<T>(IEnumerable<T> prims) where T : VertexPrimitive
            => new(prims);
        public static XRMesh CreateTriangles(params Vector3[] positions)
            => new(positions.SelectEvery(3, x => new VertexTriangle(x[0], x[1], x[2])));
        public static XRMesh CreateTriangles(IEnumerable<Vector3> positions)
            => new(positions.SelectEvery(3, x => new VertexTriangle(x[0], x[1], x[2])));
        public static XRMesh CreateLines(params Vector3[] positions)
            => new(positions.SelectEvery(2, x => new VertexLine(x[0], x[1])));
        public static XRMesh CreateLinestrip(bool closed, params Vector3[] positions)
            => new(new VertexLineStrip(closed, positions.Select(x => new Vertex(x)).ToArray()));
        public static XRMesh CreateLines(IEnumerable<Vector3> positions)
            => new(positions.SelectEvery(2, x => new VertexLine(x[0], x[1])));
        public static XRMesh CreatePoints(params Vector3[] positions)
            => new(positions.Select(x => new Vertex(x)));
        public static XRMesh CreatePoints(IEnumerable<Vector3> positions)
            => new(positions.Select(x => new Vertex(x)));

        #region Non-Per-Facepoint Buffers

        #region Bone Weighting Buffers
        //Bone weights
        /// <summary>
        /// Indices into the UtilizedBones list for each bone that affects this vertex.
        /// Static read-only buffer.
        /// </summary>
        public XRDataBuffer? BoneWeightIndices { get; private set; }
        /// <summary>
        /// Weight values from 0.0 to 1.0 for each bone that affects this vertex.
        /// Static read-only buffer.
        /// </summary>
        public XRDataBuffer? BoneWeightValues { get; private set; }
        #endregion

        #region Blendshape Buffers
        //Deltas for each blendshape on this mesh
        public XRDataBuffer? BlendshapeDeltas { get; private set; }
        ///// <summary>
        ///// Remapped array of position deltas for all blendshapes on this mesh.
        ///// Static read-only buffer.
        ///// </summary>
        //public XRDataBuffer? BlendshapePositionDeltasBuffer { get; private set; }
        ///// <summary>
        ///// Remapped array of normal deltas for all blendshapes on this mesh.
        ///// Static read-only buffer.
        ///// </summary>
        //public XRDataBuffer? BlendshapeNormalDeltasBuffer { get; private set; }
        ///// <summary>
        ///// Remapped array of tangent deltas for all blendshapes on this mesh.
        ///// Static read-only buffer.
        ///// </summary>
        //public XRDataBuffer? BlendshapeTangentDeltasBuffer { get; private set; }
        ///// <summary>
        ///// Remapped array of color deltas for all blendshapes on this mesh.
        ///// Static read-only buffers.
        ///// </summary>
        //public XRDataBuffer[]? BlendshapeColorDeltaBuffers { get; private set; } = [];
        ///// <summary>
        ///// Remapped array of texture coordinate deltas for all blendshapes on this mesh.
        ///// Static read-only buffers.
        ///// </summary>
        //public XRDataBuffer[]? BlendshapeTexCoordDeltaBuffers { get; private set; } = [];
        //Weights for each blendshape on this mesh
        /// <summary>
        /// Indices into the blendshape delta buffers for each blendshape that affects each vertex.
        /// Static read-only buffer.
        /// </summary>
        public XRDataBuffer? BlendshapeIndices { get; private set; }
        #endregion

        #endregion

        #region Indices
        public int[]? GetIndices()
        {
            int[]? indices = _type switch
            {
                EPrimitiveType.Triangles => _triangles?.SelectMany(x => new int[] { x.Point0, x.Point1, x.Point2 }).ToArray(),
                EPrimitiveType.Lines => _lines?.SelectMany(x => new int[] { x.Point0, x.Point1 }).ToArray(),
                EPrimitiveType.Points => _points?.Select(x => (int)x).ToArray(),
                _ => null,
            };
            return indices;
        }

        public int[]? GetIndices(EPrimitiveType type)
        {
            int[]? indices = type switch
            {
                EPrimitiveType.Triangles => _triangles?.SelectMany(x => new int[] { x.Point0, x.Point1, x.Point2 }).ToArray(),
                EPrimitiveType.Lines => _lines?.SelectMany(x => new int[] { x.Point0, x.Point1 }).ToArray(),
                EPrimitiveType.Points => _points?.Select(x => (int)x).ToArray(),
                _ => null,
            };
            return indices;
        }

        private Remapper? SetTriangleIndices(List<Vertex> vertices, bool remap = true)
        {
            _triangles = [];

            while (vertices.Count % 3 != 0)
                vertices.RemoveAt(vertices.Count - 1);

            if (remap)
            {
                Remapper remapper = new();
                remapper.Remap(vertices, null);
                for (int i = 0; i < remapper.RemapTable?.Length;)
                {
                    _triangles.Add(new IndexTriangle(
                        remapper.RemapTable[i++],
                        remapper.RemapTable[i++],
                        remapper.RemapTable[i++]));
                }
                return remapper;
            }
            else
            {
                for (int i = 0; i < vertices.Count;)
                    _triangles.Add(new IndexTriangle(i++, i++, i++));
                return null;
            }
        }
        private Remapper? SetLineIndices(List<Vertex> vertices, bool remap = true)
        {
            if (vertices.Count % 2 != 0)
                vertices.RemoveAt(vertices.Count - 1);

            _lines = [];
            if (remap)
            {
                Remapper remapper = new();
                remapper.Remap(vertices, null);
                for (int i = 0; i < remapper.RemapTable?.Length;)
                {
                    _lines.Add(new IndexLine(
                        remapper.RemapTable[i++],
                        remapper.RemapTable[i++]));
                }
                return remapper;
            }
            else
            {
                for (int i = 0; i < vertices.Count;)
                    _lines.Add(new IndexLine(i++, i++));
                return null;
            }
        }
        private Remapper? SetPointIndices(List<Vertex> vertices, bool remap = true)
        {
            _points = [];
            if (remap)
            {
                Remapper remapper = new();
                remapper.Remap(vertices, null);
                for (int i = 0; i < remapper.RemapTable?.Length;)
                    _points.Add(remapper.RemapTable[i++]);
                return remapper;
            }
            else
            {
                for (int i = 0; i < vertices.Count;)
                    _points.Add(i++);
                return null;
            }
        }
        #endregion

        protected override void OnDestroying()
            => Buffers?.ForEach(x => x.Value.Dispose());

        public XRMesh()
        {
            //Buffers.UpdateFaceIndices += UpdateFaceIndices;
        }

        public void SetPosition(uint index, Vector3 value)
        {
            if (Interleaved)
                InterleavedVertexBuffer?.SetVector3AtOffset(index * InterleavedStride + PositionOffset, value);
            else
                PositionsBuffer?.SetVector3(index, value);
        }

        public Vector3 GetPosition(uint index)
            => Interleaved
                ? InterleavedVertexBuffer?.GetVector3AtOffset(index * InterleavedStride + PositionOffset) ?? Vector3.Zero
                : PositionsBuffer?.GetVector3(index) ?? Vector3.Zero;

        public void SetNormal(uint index, Vector3 value)
        {
            if (Interleaved)
            {
                if (NormalOffset.HasValue)
                    InterleavedVertexBuffer?.SetVector3AtOffset(index * InterleavedStride + NormalOffset.Value, value);
            }
            else
                NormalsBuffer?.SetVector3(index, value);
        }

        public Vector3 GetNormal(uint index)
        {
            if (Interleaved)
            {
                if (NormalOffset.HasValue)
                    return InterleavedVertexBuffer?.GetVector3AtOffset(index * InterleavedStride + NormalOffset.Value) ?? Vector3.Zero;
                else
                    return Vector3.Zero;
            }
            else
                return NormalsBuffer?.GetVector3(index) ?? Vector3.Zero;
        }

        public void SetTangent(uint index, Vector3 value)
        {
            if (Interleaved)
            {
                if (TangentOffset.HasValue)
                    InterleavedVertexBuffer?.SetVector3AtOffset(index * InterleavedStride + TangentOffset.Value, value);
            }
            else
                TangentsBuffer?.SetVector3(index, value);
        }

        public Vector3 GetTangent(uint index)
        {
            if (Interleaved)
            {
                if (TangentOffset.HasValue)
                    return InterleavedVertexBuffer?.GetVector3AtOffset(index * InterleavedStride + TangentOffset.Value) ?? Vector3.Zero;
                else
                    return Vector3.Zero;
            }
            else
                return TangentsBuffer?.GetVector3(index) ?? Vector3.Zero;
        }
        
        public void SetColor(uint index, Vector4 value, uint colorIndex)
        {
            if (Interleaved) 
            {
                if (ColorOffset is not null && ColorCount > colorIndex)
                {
                    uint offset = ColorOffset.Value + colorIndex * 16u;
                    InterleavedVertexBuffer?.SetVector4AtOffset(index * InterleavedStride + offset, value);
                }
            }
            else
                ColorBuffers?[colorIndex].SetVector4(index, value);
        }

        public Vector4 GetColor(uint index, uint colorIndex)
        {
            if (Interleaved)
            {
                if (ColorOffset is not null && ColorCount > colorIndex)
                {
                    uint offset = ColorOffset.Value + colorIndex * 16u;
                    return InterleavedVertexBuffer?.GetVector4AtOffset(index * InterleavedStride + offset) ?? Vector4.Zero;
                }
                else
                    return Vector4.Zero;
            }
            else
                return ColorBuffers?[colorIndex].GetVector4(index) ?? Vector4.Zero;
        }

        public void SetTexCoord(uint index, Vector2 value, uint texCoordIndex)
        {
            if (Interleaved)
            {
                if (TexCoordOffset is not null && TexCoordCount > texCoordIndex)
                {
                    uint offset = TexCoordOffset.Value + texCoordIndex * 8u;
                    InterleavedVertexBuffer?.SetVector2AtOffset(index * InterleavedStride + offset, value);
                }
            }
            else
                TexCoordBuffers?[texCoordIndex].SetVector2(index, value);
        }

        public Vector2 GetTexCoord(uint index, uint texCoordIndex)
        {
            if (Interleaved)
            {
                if (TexCoordOffset is not null && TexCoordCount > texCoordIndex)
                {
                    uint offset = TexCoordOffset.Value + texCoordIndex * 8u;
                    return InterleavedVertexBuffer?.GetVector2AtOffset(index * InterleavedStride + offset) ?? Vector2.Zero;
                }
                else
                    return Vector2.Zero;
            }
            else
                return TexCoordBuffers?[texCoordIndex].GetVector2(index) ?? Vector2.Zero;
        }

        private static bool AddPositionsAction(ConcurrentDictionary<int, DelVertexAction> vertexActions)
        {
            static void Action(XRMesh @this, int i, int x, Vertex vtx, Matrix4x4? dataTransform)
            {
                Vector3 value = vtx?.Position ?? Vector3.Zero;

                if (dataTransform.HasValue)
                    value = Vector3.Transform(value, dataTransform.Value);

                @this.SetPosition((uint)i, value);
            }
            return vertexActions.TryAdd(6, Action);
        }
        private static bool AddColorAction(ConcurrentDictionary<int, DelVertexAction> vertexActions)
        {
            static void Action(XRMesh @this, int i, int x, Vertex vtx, Matrix4x4? dataTransform)
            {
                int count = vtx.ColorSets?.Count ?? 0;
                for (int colorIndex = 0; colorIndex < count; ++colorIndex)
                {
                    Vector4 value = vtx?.ColorSets != null
                        ? vtx!.ColorSets[colorIndex]
                        : Vector4.Zero;

                    @this.SetColor((uint)i, value, (uint)colorIndex);
                }
            }
            return vertexActions.TryAdd(4, Action);
        }
        private static bool AddTexCoordAction(ConcurrentDictionary<int, DelVertexAction> vertexActions)
        {
            static void Action(XRMesh @this, int i, int x, Vertex vtx, Matrix4x4? dataTransform)
            {
                int count = vtx.TextureCoordinateSets?.Count ?? 0;
                for (int texCoordIndex = 0; texCoordIndex < count; ++texCoordIndex)
                {
                    Vector2 value = vtx?.TextureCoordinateSets != null
                        ? vtx!.TextureCoordinateSets[texCoordIndex]
                        : Vector2.Zero;

                    @this.SetTexCoord((uint)i, value, (uint)texCoordIndex);
                }
            }
            return vertexActions.TryAdd(3, Action);
        }
        private static bool AddTangentAction(ConcurrentDictionary<int, DelVertexAction> vertexActions)
        {
            static void Action(XRMesh @this, int i, int x, Vertex vtx, Matrix4x4? dataTransform)
            {
                Vector3 value = vtx?.Tangent ?? Vector3.Zero;

                if (dataTransform.HasValue)
                    value = Vector3.TransformNormal(value, dataTransform.Value);

                @this.SetTangent((uint)i, value);
            }
            return vertexActions.TryAdd(2, Action);
        }
        private static bool AddNormalAction(ConcurrentDictionary<int, DelVertexAction> vertexActions)
        {
            static void Action(XRMesh @this, int i, int x, Vertex vtx, Matrix4x4? dataTransform)
            {
                Vector3 value = vtx?.Normal ?? Vector3.Zero;

                if (dataTransform.HasValue)
                    value = Vector3.TransformNormal(value, dataTransform.Value);

                @this.SetNormal((uint)i, value);
            }
            return vertexActions.TryAdd(1, Action);
        }

        public XRMesh(IEnumerable<Vertex> vertices, List<ushort> triangleIndices)
        {
            using var r = Engine.Profiler.Start("XRMesh Triangles Constructor");

            List<Vertex> triVertices = [];

            //Create an action for each vertex attribute to set the buffer data
            //This lets us avoid redundant LINQ code by looping through the vertices only once
            ConcurrentDictionary<int, DelVertexAction> vertexActions = [];

            int maxColorCount = 0;
            int maxTexCoordCount = 0;
            AABB? bounds = null;
            Matrix4x4? dataTransform = null;
            bool hasNormalAction = false;
            bool hasTangentAction = false;
            bool hasTexCoordAction = false;
            bool hasColorAction = false;

            //Convert all primitives to simple primitives
            //While doing this, compile a command list of actions to set buffer data
            void Add(Vertex v)
            {
                AddVertex(
                    triVertices,
                    v,
                    vertexActions,
                    ref maxTexCoordCount,
                    ref maxColorCount,
                    ref hasNormalAction,
                    ref hasTangentAction,
                    ref hasTexCoordAction,
                    ref hasColorAction);
            }
            vertices.ForEach(Add);

            _bounds = bounds ?? new AABB(Vector3.Zero, Vector3.Zero);
            _triangles = [.. triangleIndices.SelectEvery(3, x => new IndexTriangle(x[0], x[1], x[2]))];
            _type = EPrimitiveType.Triangles;
            VertexCount = triVertices.Count;

            InitMeshBuffers(
                vertexActions.ContainsKey(1),
                vertexActions.ContainsKey(2),
                maxColorCount,
                maxTexCoordCount);

            AddPositionsAction(vertexActions);

            //Fill the buffers with the vertex data using the command list
            //We can do this in parallel since each vertex is independent
            PopulateVertexData(
                vertexActions.Values,
                triVertices,
                VertexCount,
                dataTransform,
                Engine.Rendering.Settings.PopulateVertexDataInParallel);

            Vertices = [.. vertices];
        }

        /// <summary>
        /// This constructor converts a simple list of vertices into a mesh optimized for rendering.
        /// </summary>
        /// <param name="info"></param>
        /// <param name="primitives"></param>
        /// <param name="type"></param>
        public XRMesh(IEnumerable<VertexPrimitive> primitives) : this()
        {
            using var r = Engine.Profiler.Start("XRMesh Constructor");
            
            //Convert all primitives to simple primitives
            List<Vertex> points = [];
            List<Vertex> lines = [];
            List<Vertex> triangles = [];

            //Create an action for each vertex attribute to set the buffer data
            //This lets us avoid redundant LINQ code by looping through the vertices only once
            ConcurrentDictionary<int, DelVertexAction> vertexActions = [];

            int maxColorCount = 0;
            int maxTexCoordCount = 0;
            AABB? bounds = null;
            Matrix4x4? dataTransform = null;

            bool hasNormalAction = false;
            bool hasTangentAction = false;
            bool hasTexCoordAction = false;
            bool hasColorAction = false;

            //Convert all primitives to simple primitives
            //While doing this, compile a command list of actions to set buffer data
            foreach (VertexPrimitive prim in primitives)
            {
                switch (prim)
                {
                    case Vertex v:
                        AddVertex(
                            points,
                            v,
                            vertexActions,
                            ref maxTexCoordCount,
                            ref maxColorCount,
                            ref hasNormalAction,
                            ref hasTangentAction,
                            ref hasTexCoordAction,
                            ref hasColorAction);
                        break;
                    case VertexLinePrimitive l:
                        {
                            var asLines = l.ToLines();
                            foreach (VertexLine line in asLines)
                                foreach (Vertex v in line.Vertices)
                                    AddVertex(
                                        lines,
                                        v,
                                        vertexActions,
                                        ref maxTexCoordCount,
                                        ref maxColorCount,
                                        ref hasNormalAction,
                                        ref hasTangentAction,
                                        ref hasTexCoordAction,
                                        ref hasColorAction);
                        }
                        break;
                    case VertexLine line:
                        foreach (Vertex v in line.Vertices)
                            AddVertex(
                                lines,
                                v,
                                vertexActions,
                                ref maxTexCoordCount,
                                ref maxColorCount,
                                ref hasNormalAction,
                                ref hasTangentAction,
                                ref hasTexCoordAction,
                                ref hasColorAction);
                        break;
                    case VertexPolygon t:
                        {
                            var asTris = t.ToTriangles();
                            foreach (VertexTriangle tri in asTris)
                                foreach (Vertex v in tri.Vertices)
                                    AddVertex(
                                        triangles,
                                        v,
                                        vertexActions,
                                        ref maxTexCoordCount,
                                        ref maxColorCount,
                                        ref hasNormalAction,
                                        ref hasTangentAction,
                                        ref hasTexCoordAction,
                                        ref hasColorAction);
                        }
                        break;
                }
            }

            _bounds = bounds ?? new AABB(Vector3.Zero, Vector3.Zero);

            //Remap vertices to unique indices for each type of simple primitive
            Remapper? triRemap = SetTriangleIndices(triangles);
            Remapper? lineRemap = SetLineIndices(lines);
            Remapper? pointRemap = SetPointIndices(points);

            //Determine which type of primitive has the most data and use that as the primary type
            int count;
            Remapper? remapper;
            List<Vertex> sourceList;
            if (triangles.Count > lines.Count && triangles.Count > points.Count)
            {
                _type = EPrimitiveType.Triangles;
                count = triangles.Count;
                remapper = triRemap;
                sourceList = triangles;
            }
            else if (lines.Count > triangles.Count && lines.Count > points.Count)
            {
                _type = EPrimitiveType.Lines;
                count = lines.Count;
                remapper = lineRemap;
                sourceList = lines;
            }
            else
            {
                _type = EPrimitiveType.Points;
                count = points.Count;
                remapper = pointRemap;
                sourceList = points;
            }

            int[] firstAppearanceArray;
            if (remapper?.ImplementationTable is null)
            {
                firstAppearanceArray = new int[count];
                firstAppearanceArray.Fill(x => x);
            }
            else
                firstAppearanceArray = remapper.ImplementationTable!;
            VertexCount = firstAppearanceArray.Length;

            InitMeshBuffers(
                vertexActions.ContainsKey(1),
                vertexActions.ContainsKey(2),
                maxColorCount,
                maxTexCoordCount);

            AddPositionsAction(vertexActions);

            //MakeFaceIndices(weights, firstAppearanceArray.Length);

            //Fill the buffers with the vertex data using the command list
            //We can do this in parallel since each vertex is independent
            PopulateVertexData(
                vertexActions.Values,
                sourceList,
                firstAppearanceArray,
                dataTransform,
                Engine.Rendering.Settings.PopulateVertexDataInParallel);

            //if (weights is not null)
            //    SetBoneWeights(weights, );

            Vertices = sourceList;
        }

        public unsafe XRMesh(
            Mesh mesh,
            AssimpContext assimp,
            Dictionary<string, List<SceneNode>> nodeCache,
            Matrix4x4 dataTransform) : this()
        {
            using var t = Engine.Profiler.Start("Assimp XRMesh Constructor");

            ArgumentNullException.ThrowIfNull(mesh);
            ArgumentNullException.ThrowIfNull(assimp);
            ArgumentNullException.ThrowIfNull(nodeCache);

            //Convert all primitives to simple primitives
            List<Vertex> points = [];
            List<Vertex> lines = [];
            List<Vertex> triangles = [];

            //Create an action for each vertex attribute to set the buffer data
            //This lets us avoid redundant LINQ code by looping through the vertices only once
            ConcurrentDictionary<int, DelVertexAction> vertexActions = [];

            int maxColorCount = 0;
            int maxTexCoordCount = 0;

            //Convert all primitives to simple primitives
            //While doing this, compile a command list of actions to set buffer data
            ConcurrentDictionary<int, Vertex> vertexCache = new();
            PrimitiveType primType = mesh.PrimitiveType;

            //TODO: pre-allocate points, lines, and triangles to the correct size and populate in parallel? this is already pretty fast anyways

            //This remap contains a list of new vertex indices for each original vertex index.
            PopulateVerticesAssimp(
                mesh,
                points,
                lines,
                triangles,
                vertexActions,
                ref maxColorCount,
                ref maxTexCoordCount,
                vertexCache,
                out var faceRemap);

            SetTriangleIndices(triangles, false);
            SetLineIndices(lines, false);
            SetPointIndices(points, false);

            //Determine which type of primitive has the most data and use that as the primary type
            int count;
            List<Vertex> sourceList;
            if (triangles.Count > lines.Count && triangles.Count > points.Count)
            {
                _type = EPrimitiveType.Triangles;
                count = triangles.Count;
                sourceList = triangles;
            }
            else if (lines.Count > triangles.Count && lines.Count > points.Count)
            {
                _type = EPrimitiveType.Lines;
                count = lines.Count;
                sourceList = lines;
            }
            else
            {
                _type = EPrimitiveType.Points;
                count = points.Count;
                sourceList = points;
            }

            VertexCount = count;

            InitializeSkinning(
                mesh,
                nodeCache,
                faceRemap,
                sourceList);

            InitMeshBuffers(
                vertexActions.ContainsKey(1),
                vertexActions.ContainsKey(2),
                maxColorCount,
                maxTexCoordCount);

            AddPositionsAction(vertexActions);

            //Fill the buffers with the vertex data using the command list
            //We can do this in parallel since each vertex is independent
            PopulateVertexData(
                vertexActions.Values,
                sourceList,
                count,
                dataTransform,
                Engine.Rendering.Settings.PopulateVertexDataInParallel);

            PopulateAssimpBlendshapeData(mesh, dataTransform, sourceList);

            mesh.BoundingBox.Deconstruct(out Vector3 min, out Vector3 max);
            _bounds = new AABB(min, max);

            Vertices = sourceList;
        }

        private unsafe void PopulateAssimpBlendshapeData(Mesh mesh, Matrix4x4 dataTransform, List<Vertex> sourceList)
        {
            if (!Engine.Rendering.Settings.AllowBlendshapes || !mesh.HasMeshAnimationAttachments)
                return;
            
            BlendshapeNames = new string[mesh.MeshAnimationAttachmentCount];
            for (int i = 0; i < mesh.MeshAnimationAttachmentCount; i++)
                BlendshapeNames[i] = mesh.MeshAnimationAttachments[i].Name;

            PopulateBlendshapeBuffers(sourceList, dataTransform);
        }
        private static void PopulateVerticesAssimp(
            Mesh mesh,
            List<Vertex> points,
            List<Vertex> lines,
            List<Vertex> triangles,
            ConcurrentDictionary<int, DelVertexAction> vertexActions,
            ref int maxColorCount,
            ref int maxTexCoordCount,
            ConcurrentDictionary<int, Vertex> vertexCache,
            out Dictionary<int, List<int>> faceRemap)
        {
            using var t = Engine.Profiler.Start();

            faceRemap = [];

            // Cache flags to avoid repeated dictionary lookups for vertexActions keys
            bool hasNormalAction = vertexActions.ContainsKey(1);
            bool hasTangentAction = vertexActions.ContainsKey(2);
            bool hasTexCoordAction = vertexActions.ContainsKey(3);
            bool hasColorAction = vertexActions.ContainsKey(4);

            int faceCount = mesh.FaceCount;
            for (int i = 0; i < faceCount; i++)
            {
                Face face = mesh.Faces[i];
                int numInd = face.IndexCount;
                List<Vertex> targetList = numInd switch
                {
                    1 => points,
                    2 => lines,
                    _ => triangles,
                };

                if (numInd > 3)
                {
                    // Use fan triangulation without array allocation
                    int index0 = face.Indices[0];
                    for (int ind = 0; ind < numInd - 2; ind++)
                    {
                        int index1 = face.Indices[ind + 1];
                        int index2 = face.Indices[ind + 2];

                        ProcessAssimpVertex(
                            index0,
                            targetList,
                            mesh,
                            vertexCache,
                            vertexActions,
                            ref maxTexCoordCount,
                            ref maxColorCount,
                            faceRemap,
                            ref hasNormalAction,
                            ref hasTangentAction,
                            ref hasTexCoordAction,
                            ref hasColorAction);
                        ProcessAssimpVertex(
                            index1,
                            targetList,
                            mesh,
                            vertexCache,
                            vertexActions,
                            ref maxTexCoordCount,
                            ref maxColorCount,
                            faceRemap,
                            ref hasNormalAction,
                            ref hasTangentAction,
                            ref hasTexCoordAction,
                            ref hasColorAction);
                        ProcessAssimpVertex(
                            index2,
                            targetList,
                            mesh,
                            vertexCache,
                            vertexActions,
                            ref maxTexCoordCount,
                            ref maxColorCount,
                            faceRemap,
                            ref hasNormalAction,
                            ref hasTangentAction,
                            ref hasTexCoordAction,
                            ref hasColorAction);
                    }
                }
                else
                {
                    for (int ind = 0; ind < numInd; ind++)
                    {
                        int origIndex = face.Indices[ind];
                        ProcessAssimpVertex(
                            origIndex,
                            targetList,
                            mesh,
                            vertexCache,
                            vertexActions,
                            ref maxTexCoordCount,
                            ref maxColorCount,
                            faceRemap,
                            ref hasNormalAction,
                            ref hasTangentAction,
                            ref hasTexCoordAction,
                            ref hasColorAction);
                    }
                }
            }
        }

        private static void ProcessAssimpVertex(
            int originalIndex,
            List<Vertex> targetList,
            Mesh mesh,
            ConcurrentDictionary<int, Vertex> vertexCache,
            ConcurrentDictionary<int, DelVertexAction> vertexActions,
            ref int maxTexCoordCount,
            ref int maxColorCount,
            Dictionary<int, List<int>> faceRemap,
            ref bool hasNormalAction,
            ref bool hasTangentAction,
            ref bool hasTexCoordAction,
            ref bool hasColorAction)
        {
            int newIndex = targetList.Count;
            // Reuse cached vertex or create if needed
            Vertex v = vertexCache.GetOrAdd(originalIndex, x => Vertex.FromAssimp(mesh, x));
            AddVertex(
                targetList,
                v,
                vertexActions,
                ref maxTexCoordCount,
                ref maxColorCount,
                ref hasNormalAction,
                ref hasTangentAction,
                ref hasTexCoordAction,
                ref hasColorAction);

            // Update faceRemap without extra allocations
            if (!faceRemap.TryGetValue(originalIndex, out List<int>? list))
                faceRemap[originalIndex] = [newIndex];
            else
                list.Add(newIndex);
        }
        /// <summary>
        /// Adds a vertex to the list and updates the vertex buffer command list based on the vertex's attributes.
        /// </summary>
        /// <param name="vertices"></param>
        /// <param name="v"></param>
        /// <param name="vertexActions"></param>
        /// <param name="maxTexCoordCount"></param>
        /// <param name="maxColorCount"></param>
        /// <param name="hasNormalAction"></param>
        /// <param name="hasTangentAction"></param>
        /// <param name="hasTexCoordAction"></param>
        /// <param name="hasColorAction"></param>
        private static void AddVertex(
            List<Vertex> vertices,
            Vertex v,
            ConcurrentDictionary<int, DelVertexAction> vertexActions,
            ref int maxTexCoordCount,
            ref int maxColorCount,
            ref bool hasNormalAction,
            ref bool hasTangentAction,
            ref bool hasTexCoordAction,
            ref bool hasColorAction)
        {
            if (v == null)
                return;

            vertices.Add(v);

            // Setup vertex actions once based on first vertex having the attribute
            if (v.Normal != null && !hasNormalAction)
                hasNormalAction |= AddNormalAction(vertexActions);
            
            if (v.Tangent != null && !hasTangentAction)
                hasTangentAction |= AddTangentAction(vertexActions);
            
            if (v.TextureCoordinateSets != null && v.TextureCoordinateSets.Count > 0 && !hasTexCoordAction)
            {
                Interlocked.Exchange(ref maxTexCoordCount, Math.Max(maxTexCoordCount, v.TextureCoordinateSets.Count));
                hasTexCoordAction |= AddTexCoordAction(vertexActions);
            }

            if (v.ColorSets != null && v.ColorSets.Count > 0 && !hasColorAction)
            {
                Interlocked.Exchange(ref maxColorCount, Math.Max(maxColorCount, v.ColorSets.Count));
                hasColorAction |= AddColorAction(vertexActions);
            }
        }

        private unsafe void PopulateBlendshapeBuffers(List<Vertex> sourceList, Matrix4x4 dataTransform)
        {
            using var t = Engine.Profiler.Start();

            bool intVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders;

            BlendshapeCounts = new XRDataBuffer(ECommonBufferType.BlendshapeCount.ToString(), EBufferTarget.ArrayBuffer, (uint)sourceList.Count, intVarType ? EComponentType.Int : EComponentType.Float, 2, false, intVarType);

            List<Vector3> deltas = [Vector3.Zero]; //0 index is reserved for 0 delta
            List<IVector4> blendshapeIndices = [];

            bool remapDeltas = Engine.Rendering.Settings.RemapBlendshapeDeltas;

            int blendshapeDeltaIndicesIndex = 0;
            int sourceCount = sourceList.Count;
            int blendshapeCount = (int)BlendshapeCount;
            int* blendshapeCounts = (int*)BlendshapeCounts.Address;
            float* blendshapeCountsFloat = (float*)BlendshapeCounts.Address;
            for (int i = 0; i < sourceCount; i++)
            {
                int activeBlendshapeCountForThisVertex = 0;
                Vertex vtx = sourceList[i];

                if (vtx.Blendshapes is null)
                {
                    if (intVarType)
                    {
                        *blendshapeCounts++ = 0;
                        *blendshapeCounts++ = 0;
                    }
                    else
                    {
                        *blendshapeCountsFloat++ = 0;
                        *blendshapeCountsFloat++ = 0;
                    }
                    continue;
                }

                //Can't use vtx.XXX data here because it's not transformed
                Vector3 vtxPos = GetPosition((uint)i); //vtx.Position;
                Vector3 vtxNrm = GetNormal((uint)i); //vtx.Normal;
                Vector3 vtxTan = GetTangent((uint)i); //vtx.Tangent;
                for (int bsInd = 0; bsInd < blendshapeCount; bsInd++)
                {
                    var (_, bsData) = vtx.Blendshapes[bsInd];
                    bool anyData = false;
                    int posInd = 0;
                    int nrmInd = 0;
                    int tanInd = 0;

                    Vector3 posDt = Vector3.Transform(bsData.Position, dataTransform) - vtxPos;
                    Vector3 nrmDt = Vector3.TransformNormal(bsData.Normal ?? Vector3.Zero, dataTransform).Normalized() - vtxNrm;
                    Vector3 tanDt = Vector3.TransformNormal(bsData.Tangent ?? Vector3.Zero, dataTransform).Normalized() - vtxTan;

                    if (posDt.LengthSquared() > 0.0f)
                    {
                        posInd = deltas.Count;
                        deltas.Add(posDt);
                        anyData = true;
                    }
                    if (nrmDt.LengthSquared() > 0.0f)
                    {
                        nrmInd = deltas.Count;
                        deltas.Add(nrmDt);
                        anyData = true;
                    }
                    if (tanDt.LengthSquared() > 0.0f)
                    {
                        tanInd = deltas.Count;
                        deltas.Add(tanDt);
                        anyData = true;
                    }
                    if (anyData)
                    {
                        ++activeBlendshapeCountForThisVertex;
                        blendshapeIndices.Add(new IVector4(bsInd, posInd, nrmInd, tanInd));
                    }
                }
                if (intVarType)
                {
                    *blendshapeCounts++ = blendshapeDeltaIndicesIndex;
                    *blendshapeCounts++ = activeBlendshapeCountForThisVertex;
                }
                else
                {
                    *blendshapeCountsFloat++ = blendshapeDeltaIndicesIndex;
                    *blendshapeCountsFloat++ = activeBlendshapeCountForThisVertex;
                }
                blendshapeDeltaIndicesIndex += activeBlendshapeCountForThisVertex;
            }

            BlendshapeIndices = new XRDataBuffer($"{ECommonBufferType.BlendshapeIndices}Buffer", EBufferTarget.ShaderStorageBuffer, (uint)blendshapeIndices.Count, intVarType ? EComponentType.Int : EComponentType.Float, 4, false, intVarType);
            
            if (remapDeltas)
            {
                Remapper deltaRemap = new();
                deltaRemap.Remap(deltas, null);
                BlendshapeDeltas = new XRDataBuffer($"{ECommonBufferType.BlendshapeDeltas}Buffer", EBufferTarget.ShaderStorageBuffer, deltaRemap.ImplementationLength, EComponentType.Float, 4, false, false);

                float* deltaData = (float*)BlendshapeDeltas.Address;
                for (int i = 0; i < deltaRemap.ImplementationLength; i++)
                {
                    Vector3 delta = deltas[deltaRemap.ImplementationTable![i]];
                    *deltaData++ = delta.X;
                    *deltaData++ = delta.Y;
                    *deltaData++ = delta.Z;
                    *deltaData++ = 0.0f;
                }

                //Update the blendshape indices buffer with remapped delta indices
                var remap = deltaRemap!.RemapTable!;
                int* indicesDataInt = (int*)BlendshapeIndices.Address;
                float* indicesDataFloat = (float*)BlendshapeIndices.Address;
                for (int i = 0; i < blendshapeIndices.Count; i++)
                {
                    IVector4 indices = blendshapeIndices[i];
                    if (intVarType)
                    {
                        *indicesDataInt++ = indices.X;
                        *indicesDataInt++ = remap[indices.Y];
                        *indicesDataInt++ = remap[indices.Z];
                        *indicesDataInt++ = remap[indices.W];
                    }
                    else
                    {
                        Vector4 newIndices = new(indices.X, remap[indices.Y], remap[indices.Z], remap[indices.W]);
                        *indicesDataFloat++ = indices.X;
                        *indicesDataFloat++ = remap[indices.Y];
                        *indicesDataFloat++ = remap[indices.Z];
                        *indicesDataFloat++ = remap[indices.W];
                    }
                }
            }
            else
            {
                BlendshapeDeltas = new XRDataBuffer($"{ECommonBufferType.BlendshapeDeltas}Buffer", EBufferTarget.ShaderStorageBuffer, (uint)deltas.Count, EComponentType.Float, 4, false, false);
                float* deltaData = (float*)BlendshapeDeltas.Address;
                for (int i = 0; i < deltas.Count; i++)
                {
                    Vector3 delta = deltas[i];
                    *deltaData++ = delta.X;
                    *deltaData++ = delta.Y;
                    *deltaData++ = delta.Z;
                    *deltaData++ = 0.0f;
                }
                int* indicesDataInt = (int*)BlendshapeIndices.Address;
                float* indicesDataFloat = (float*)BlendshapeIndices.Address;
                for (int i = 0; i < blendshapeIndices.Count; i++)
                {
                    IVector4 indices = blendshapeIndices[i];
                    if (intVarType)
                    {
                        *indicesDataInt++ = indices.X;
                        *indicesDataInt++ = indices.Y;
                        *indicesDataInt++ = indices.Z;
                        *indicesDataInt++ = indices.W;
                    }
                    else
                    {
                        *indicesDataFloat++ = indices.X;
                        *indicesDataFloat++ = indices.Y;
                        *indicesDataFloat++ = indices.Z;
                        *indicesDataFloat++ = indices.W;
                    }
                }
            }

            Buffers.Add(BlendshapeCounts.AttributeName, BlendshapeCounts);
            Buffers.Add(BlendshapeIndices.AttributeName, BlendshapeIndices);
            Buffers.Add(BlendshapeDeltas.AttributeName, BlendshapeDeltas);
        }

        private void InitializeSkinning(
            Mesh mesh,
            Dictionary<string, List<SceneNode>> nodeCache,
            Dictionary<int, List<int>>? faceRemap,
            List<Vertex> sourceList)
        {
            using var t = Engine.Profiler.Start();

            //Debug.Out($"Collecting bone weights for {mesh.Name}.");

            int boneCount = Engine.Rendering.Settings.AllowSkinning ? mesh.BoneCount : 0;
            Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[]? weightsPerVertex = new Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[VertexCount];
            Dictionary<TransformBase, Matrix4x4> invBindMatrices = [];

            int boneIndex = 0;
            Dictionary<TransformBase, int> boneToIndexTable = [];
            MaxWeightCount = 0;
            for (int i = 0; i < boneCount; i++)
            {
                Bone bone = mesh.Bones[i];
                if (!bone.HasVertexWeights)
                    continue;

                string name = bone.Name;
                //Debug.Out($"Bone {name} has {bone.VertexWeightCount} weights.");

                if (!TryGetTransform(nodeCache, name, out var transform) || transform is null)
                {
                    Debug.Out($"Bone {name} has no corresponding node in the heirarchy.");
                    continue;
                }

                //Dispose of the imported offset matrix and just use the initially-calculated inverse bind matrix (bind pose)
                Matrix4x4 invBind = transform.InverseBindMatrix; //bone.OffsetMatrix.Transposed();

                invBindMatrices.Add(transform!, invBind);

                int weightCount = bone.VertexWeightCount;
                for (int j = 0; j < weightCount; j++)
                {
                    var vw = bone.VertexWeights[j];
                    var id = vw.VertexID;
                    var weight = vw.Weight;

                    var list = faceRemap?[id] ?? [id];
                    foreach (var newId in list)
                    {
                        var wpv = weightsPerVertex![newId] ??= [];

                        if (!wpv.TryGetValue(transform!, out (float weight, Matrix4x4 invBindMatrix) existingPair))
                            wpv.Add(transform!, (weight, invBind));
                        else if (existingPair.weight != weight)
                        {
                            Debug.Out($"Vertex {newId} has multiple different weights for bone {name}.");
                            wpv[transform] = ((existingPair.weight + weight) / 2.0f, existingPair.invBindMatrix);
                        }

                        sourceList[newId].Weights ??= wpv;
                        MaxWeightCount = Math.Max(MaxWeightCount, wpv.Count);
                    }
                }

                if (!boneToIndexTable.ContainsKey(transform!))
                    boneToIndexTable.Add(transform!, boneIndex++);
            }

            var utilizedBones = new (TransformBase, Matrix4x4)[boneToIndexTable.Count];
            foreach (var pair in boneToIndexTable)
                utilizedBones[pair.Value] = (pair.Key, invBindMatrices[pair.Key]);
            UtilizedBones = utilizedBones;

            //if (boneToIndexTable.Count < boneCount)
            //    Debug.Out($"{boneCount - boneToIndexTable.Count} unweighted bones were removed.");

            if (weightsPerVertex is not null && weightsPerVertex.Length > 0 && boneCount > 0)
                PopulateSkinningBuffers(boneToIndexTable, weightsPerVertex);
        }

        /// <summary>
        /// This is the maximum number of weights used for one or more vertices.
        /// </summary>
        public int MaxWeightCount { get; private set; } = 0;

        private void PopulateSkinningBuffers(Dictionary<TransformBase, int> boneToIndexTable, Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[] weightsPerVertex)
        {
            using var timer = Engine.Profiler.Start();

            uint vertCount = (uint)VertexCount;
            bool intVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders;
            EComponentType indexVarType = intVarType ? EComponentType.Int : EComponentType.Float;

            bool optimizeTo4Weights = Engine.Rendering.Settings.OptimizeSkinningTo4Weights || (Engine.Rendering.Settings.OptimizeSkinningWeightsIfPossible && MaxWeightCount <= 4);
            if (optimizeTo4Weights)
            {
                //4 bone indices
                BoneWeightOffsets = new XRDataBuffer(ECommonBufferType.BoneMatrixOffset.ToString(), EBufferTarget.ArrayBuffer, vertCount, indexVarType, 4, false, intVarType);
                //4 bone weights
                BoneWeightCounts = new XRDataBuffer(ECommonBufferType.BoneMatrixCount.ToString(), EBufferTarget.ArrayBuffer, vertCount, EComponentType.Float, 4, false, false);
            }
            else
            {
                BoneWeightOffsets = new XRDataBuffer(ECommonBufferType.BoneMatrixOffset.ToString(), EBufferTarget.ArrayBuffer, vertCount, indexVarType, 1, false, intVarType);
                BoneWeightCounts = new XRDataBuffer(ECommonBufferType.BoneMatrixCount.ToString(), EBufferTarget.ArrayBuffer, vertCount, indexVarType, 1, false, intVarType);
            }

            PopulateWeightBuffers(boneToIndexTable, weightsPerVertex, optimizeTo4Weights, out List<int> boneIndices, out List<float> boneWeights);

            Buffers.Add(BoneWeightOffsets.AttributeName, BoneWeightOffsets);
            Buffers.Add(BoneWeightCounts.AttributeName, BoneWeightCounts);

            if (!optimizeTo4Weights)
            {
                if (intVarType)
                    BoneWeightIndices = Buffers.SetBufferRaw(boneIndices, $"{ECommonBufferType.BoneMatrixIndices}Buffer", false, true, false, 0, EBufferTarget.ShaderStorageBuffer);
                else
                    BoneWeightIndices = Buffers.SetBufferRaw(boneIndices.Select(x => (float)x).ToArray(), $"{ECommonBufferType.BoneMatrixIndices}Buffer", false, false, false, 0, EBufferTarget.ShaderStorageBuffer);
                BoneWeightValues = Buffers.SetBufferRaw(boneWeights, $"{ECommonBufferType.BoneMatrixWeights}Buffer", false, false, false, 0, EBufferTarget.ShaderStorageBuffer);
            }
        }

        private void PopulateWeightBuffers(
            Dictionary<TransformBase, int> boneToIndexTable,
            Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[] weightsPerVertex,
            bool optimizeTo4Weights,
            out List<int> boneIndices,
            out List<float> boneWeights)
        {
            using var timer = Engine.Profiler.Start();

            MaxWeightCount = 0;
            boneIndices = [];
            boneWeights = [];
            int offset = 0;
            bool intVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders;
            for (uint vertexIndex = 0; vertexIndex < VertexCount; ++vertexIndex)
            {
                var weightGroup = weightsPerVertex[vertexIndex];
                if (weightGroup is null)
                {
                    Debug.Out($"Vertex {vertexIndex} has no weights.");
                }

                if (weightGroup is null)
                {
                    if (intVarType)
                    {
                        if (optimizeTo4Weights)
                        {
                            BoneWeightOffsets?.Set(vertexIndex, new IVector4());
                            BoneWeightCounts?.Set(vertexIndex, new Vector4());
                        }
                        else
                        {
                            BoneWeightOffsets?.Set(vertexIndex, offset);
                            BoneWeightCounts?.Set(vertexIndex, 0);
                        }
                    }
                    else
                    {
                        if (optimizeTo4Weights)
                        {
                            BoneWeightOffsets?.Set(vertexIndex, new Vector4());
                            BoneWeightCounts?.Set(vertexIndex, new Vector4());
                        }
                        else
                        {
                            BoneWeightOffsets?.Set(vertexIndex, (float)offset);
                            BoneWeightCounts?.Set(vertexIndex, 0.0f);
                        }
                    }
                }
                else if (optimizeTo4Weights)
                {
                    VertexWeightGroup.Optimize(weightGroup, 4);
                    int count = weightGroup.Count;
                    MaxWeightCount = Math.Max(MaxWeightCount, count);

                    IVector4 indices = new();
                    Vector4 weights = new();
                    int i = 0;
                    foreach (var pair in weightGroup)
                    {
                        int boneIndex = boneToIndexTable[pair.Key];
                        float boneWeight = pair.Value.weight;
                        if (boneIndex < 0)
                        {
                            boneIndex = -1;
                            boneWeight = 0.0f;
                        }
                        indices[i] = boneIndex + 1; //+1 because 0 is reserved for the identity matrix
                        weights[i] = boneWeight;
                        i++;
                    }

                    if (intVarType)
                        BoneWeightOffsets?.Set(vertexIndex, indices);
                    else
                        BoneWeightOffsets?.Set(vertexIndex, new Vector4(indices.X, indices.Y, indices.Z, indices.W));
                    BoneWeightCounts?.Set(vertexIndex, weights);
                }
                else
                {
                    VertexWeightGroup.Normalize(weightGroup);
                    int count = weightGroup.Count;
                    MaxWeightCount = Math.Max(MaxWeightCount, count);

                    foreach (var pair in weightGroup)
                    {
                        int boneIndex = boneToIndexTable[pair.Key];
                        float boneWeight = pair.Value.weight;
                        if (boneIndex < 0)
                        {
                            boneIndex = -1;
                            boneWeight = 0.0f;
                        }

                        boneIndices.Add(boneIndex + 1); //+1 because 0 is reserved for the identity matrix
                        boneWeights.Add(boneWeight);
                    }
                    if (intVarType)
                    {
                        BoneWeightOffsets?.Set(vertexIndex, offset);
                        BoneWeightCounts?.Set(vertexIndex, count);
                    }
                    else
                    {
                        BoneWeightOffsets?.Set(vertexIndex, (float)offset);
                        BoneWeightCounts?.Set(vertexIndex, (float)count);
                    }
                    offset += count;
                }
            }

            //if (MaxWeightCount > 4)
            //    Debug.Out($"Max weight count: {MaxWeightCount}");

            //while (boneIndices.Count % 4 != 0)
            //{
            //    boneIndices.Add(-1);
            //    boneWeights.Add(0.0f);
            //}
        }

        private static unsafe bool TryGetTransform(Dictionary<string, List<SceneNode>> nodeCache, string name, out TransformBase? transform)
        {
            if (!nodeCache.TryGetValue(name, out var matchList) || matchList is null || matchList.Count == 0)
            {
                Debug.Out($"{name} has no corresponding node in the heirarchy.");
                transform = null;
                return false;
            }

            if (matchList.Count > 1)
                Debug.Out($"{name} has multiple corresponding nodes in the heirarchy. Using the first one.");

            transform = matchList[0].Transform;
            return true;
        }

        /// <summary>
        /// OpenGL has an inverted Y axis for UV coordinates.
        /// </summary>
        /// <param name="uv"></param>
        /// <returns></returns>
        private static Vector2 FlipYCoord(Vector2 uv)
        {
            uv.Y = 1.0f - uv.Y;
            return uv;
        }

        [RequiresDynamicCode("")]
        public float? Intersect(Segment localSpaceSegment, out Triangle? triangle)
        {
            using var t = Engine.Profiler.Start();

            triangle = null;

            if (BVHTree is null)
                return null;

            var matches = BVHTree.Traverse(x => GeoUtil.SegmentIntersectsAABB(localSpaceSegment.Start, localSpaceSegment.End, x.Min, x.Max, out _, out _));
            if (matches is null)
                return null;

            var triangles = matches.Select(x =>
            {
                Triangle? tri = null;
                if (x.gobjects is not null && x.gobjects.Count != 0)
                    tri = x.gobjects[0];
                return tri;
            });
            float? minDist = null;
            foreach (var tri in triangles)
            {
                if (tri is null)
                    continue;

                GeoUtil.RayIntersectsTriangle(localSpaceSegment.Start, localSpaceSegment.End, tri.Value.A, tri.Value.B, tri.Value.C, out float dist);
                if (dist < minDist || minDist is null)
                {
                    minDist = dist;
                    triangle = tri;
                }
            }

            return minDist;
        }

        private BVH<Triangle>? _bvhTree = null;
        private bool _generating = false;
        public BVH<Triangle>? BVHTree
        {
            [RequiresDynamicCode("")]
            get
            {
                if (_bvhTree is null && !_generating)
                {
                    _generating = true;
                    Task.Run(GenerateBVH);
                }
                return _bvhTree;
            }
        }

        [RequiresDynamicCode("")]
        public void GenerateBVH()
        {
            if (Triangles is null)
                return;

            _bvhTree = new(new TriangleAdapter(), [.. Triangles.Select(GetTriangle)]);
            _generating = false;
        }

        [RequiresDynamicCode("Calls XREngine.Rendering.XRDataBuffer.Get<T>(UInt32)")]
        private Triangle GetTriangle(IndexTriangle indices)
        {
            Vector3 pos0 = GetPosition((uint)indices.Point0);
            Vector3 pos1 = GetPosition((uint)indices.Point1);
            Vector3 pos2 = GetPosition((uint)indices.Point2);
            return new Triangle(pos0, pos1, pos2);
        }

        public XRTexture3D? SignedDistanceField { get; internal set; } = null;

        public void GenerateSDF(IVector3 resolution)
        {
            //Each pixel in the 3D texture is a distance to the nearest triangle
            SignedDistanceField = new();
            XRShader shader = ShaderHelper.LoadEngineShader("Compute//sdfgen.comp");
            XRRenderProgram program = new(true, shader);
            XRDataBuffer verticesBuffer = Buffers[ECommonBufferType.Position.ToString()].Clone(false, EBufferTarget.ShaderStorageBuffer);
            verticesBuffer.AttributeName = "Vertices";
            XRDataBuffer indicesBuffer = GetIndexBuffer(EPrimitiveType.Triangles, out _, EBufferTarget.ShaderStorageBuffer)!;
            indicesBuffer.AttributeName = "Indices";
            program.BindImageTexture(0, SignedDistanceField, 0, false, 0, XRRenderProgram.EImageAccess.ReadWrite, XRRenderProgram.EImageFormat.RGB8);
            program.Uniform("sdfMinBounds", Bounds.Min);
            program.Uniform("sdfMaxBounds", Bounds.Max);
            program.Uniform("sdfResolution", resolution);
            Engine.EnqueueMainThreadTask(() =>
            {
                int local_size_x = 8;
                int local_size_y = 8;
                int local_size_z = 8;
                AbstractRenderer.Current?.DispatchCompute(
                    program,
                    (resolution.X + local_size_x - 1) / local_size_x,
                    (resolution.Y + local_size_y - 1) / local_size_y,
                    (resolution.Z + local_size_z - 1) / local_size_z);
            });
        }

        public XRDataBuffer? GetIndexBuffer(EPrimitiveType type, out IndexSize bufferElementSize, EBufferTarget target = EBufferTarget.ElementArrayBuffer)
        {
            bufferElementSize = IndexSize.Byte;

            var indices = GetIndices(type);
            if (indices is null || indices.Length == 0)
                return null;

            var data = new XRDataBuffer(target, true) { AttributeName = type.ToString() };
            //TODO: primitive restart will use MaxValue for restart id
            if (VertexCount < byte.MaxValue)
            {
                bufferElementSize = IndexSize.Byte;
                data.SetDataRaw(indices?.Select(x => (byte)x)?.ToList() ?? []);
            }
            else if (VertexCount < short.MaxValue)
            {
                bufferElementSize = IndexSize.TwoBytes;
                data.SetDataRaw(indices?.Select(x => (ushort)x)?.ToList() ?? []);
            }
            else
            {
                bufferElementSize = IndexSize.FourBytes;
                data.SetDataRaw(indices);
            }
            return data;
        }
    }
}
