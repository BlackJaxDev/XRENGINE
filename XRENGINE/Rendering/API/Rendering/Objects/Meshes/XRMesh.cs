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
        public XREvent<XRMesh>? DataChanged;

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
            Vertex[] sourceList,
            int[]? firstAppearanceArray,
            Matrix4x4? dataTransform,
            bool parallel)
        {
            int count = firstAppearanceArray?.Length ?? sourceList.Length;
            using var t = Engine.Profiler.Start($"PopulateVertexData (remapped): {count} {(parallel ? "parallel" : "sequential")}");
            var actions = vertexActions as DelVertexAction[] ?? [.. vertexActions];

            if (parallel)
            {
                Parallel.For(0, count, i =>
                {
                    int x = firstAppearanceArray?[i] ?? i;
                    Vertex vtx = sourceList[x];
                    for (int j = 0, len = actions.Length; j < len; j++)
                    {
                        actions[j].Invoke(this, i, x, vtx, dataTransform);
                    }
                });
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    int x = firstAppearanceArray?[i] ?? i;
                    Vertex vtx = sourceList[x];
                    for (int j = 0, len = actions.Length; j < len; j++)
                    {
                        actions[j].Invoke(this, i, x, vtx, dataTransform);
                    }
                }
            }
        }

        private void PopulateVertexData(
            IEnumerable<DelVertexAction> vertexActions,
            Vertex[] sourceList,
            int count,
            Matrix4x4? dataTransform,
            bool parallel)
        {
            using var t = Engine.Profiler.Start($"PopulateVertexData: {count} {(parallel ? "parallel" : "sequential")}");
            // Cache the vertex actions array so we don't enumerate repeatedly
            var actions = vertexActions as DelVertexAction[] ?? [.. vertexActions];

            if (parallel)
            {
                Parallel.For(0, count, i =>
                {
                    var vtx = sourceList[i];
                    // Inline loop over cached actions
                    for (int j = 0, len = actions.Length; j < len; j++)
                    {
                        actions[j].Invoke(this, i, i, vtx, dataTransform);
                    }
                });
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    var vtx = sourceList[i];
                    for (int j = 0, len = actions.Length; j < len; j++)
                    {
                        actions[j].Invoke(this, i, i, vtx, dataTransform);
                    }
                }
            }
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

        private Vertex[] _vertices = [];
        public Vertex[] Vertices
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

        private string[] _blendshapeNames = [];
        public string[] BlendshapeNames
        {
            get => _blendshapeNames;
            set => SetField(ref _blendshapeNames, value);
        }

        private readonly Dictionary<string, int> _blendshapeNameToIndex = [];

        public bool HasBlendshapes => BlendshapeCount > 0;

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(BlendshapeNames):
                    if (field is string[] names)
                    {
                        _blendshapeNameToIndex.Clear();
                        for (int i = 0; i < names.Length; ++i)
                            if (!string.IsNullOrEmpty(names[i]) && !_blendshapeNameToIndex.ContainsKey(names[i]))
                                _blendshapeNameToIndex.Add(names[i], i);
                            else
                                Debug.LogWarning($"Duplicate or empty blendshape name '{names[i]}' found in mesh {Name}");
                    }
                    break;
            }
        }

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

        private Remapper? SetTriangleIndices(Vertex[] vertices, bool remap = true)
        {
            _triangles = [];

            //while (vertices.Count % 3 != 0)
            //    vertices.RemoveAt(vertices.Count - 1);

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
                for (int i = 0; i < vertices.Length;)
                    _triangles.Add(new IndexTriangle(i++, i++, i++));
                return null;
            }
        }
        private Remapper? SetLineIndices(Vertex[] vertices, bool remap = true)
        {
            //if (vertices.Count % 2 != 0)
            //    vertices.RemoveAt(vertices.Count - 1);

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
                for (int i = 0; i < vertices.Length;)
                    _lines.Add(new IndexLine(i++, i++));
                return null;
            }
        }
        private Remapper? SetPointIndices(Vertex[] vertices, bool remap = true)
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
                for (int i = 0; i < vertices.Length;)
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
                bounds = bounds?.ExpandedToInclude(v.Position) ?? new AABB(v.Position, v.Position);
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
                [.. triVertices],
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
                        bounds = bounds?.ExpandedToInclude(v.Position) ?? new AABB(v.Position, v.Position);
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
                                {
                                    bounds = bounds?.ExpandedToInclude(v.Position) ?? new AABB(v.Position, v.Position);
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
                        }
                        break;
                    case VertexLine line:
                        foreach (Vertex v in line.Vertices)
                        {
                            bounds = bounds?.ExpandedToInclude(v.Position) ?? new AABB(v.Position, v.Position);
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
                    case VertexPolygon t:
                        {
                            var asTris = t.ToTriangles();
                            foreach (VertexTriangle tri in asTris)
                                foreach (Vertex v in tri.Vertices)
                                {
                                    bounds = bounds?.ExpandedToInclude(v.Position) ?? new AABB(v.Position, v.Position);
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
                        }
                        break;
                }
            }

            _bounds = bounds ?? new AABB(Vector3.Zero, Vector3.Zero);

            //Determine which type of primitive has the most data and use that as the primary type
            int count;
            Remapper? remapper;
            Vertex[] sourceList;
            if (triangles.Count > lines.Count && triangles.Count > points.Count)
            {
                _type = EPrimitiveType.Triangles;
                count = triangles.Count;
                sourceList = [.. triangles];
                remapper = SetTriangleIndices(sourceList);
            }
            else if (lines.Count > triangles.Count && lines.Count > points.Count)
            {
                _type = EPrimitiveType.Lines;
                count = lines.Count;
                sourceList = [.. lines];
                remapper = SetLineIndices(sourceList);
            }
            else
            {
                _type = EPrimitiveType.Points;
                count = points.Count;
                sourceList = [.. points];
                remapper = SetPointIndices(sourceList);
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
            Vertex[] points = [];
            Vertex[] lines = [];
            Vertex[] triangles = [];

            //Create an action for each vertex attribute to set the buffer data
            //This lets us avoid redundant LINQ code by looping through the vertices only once
            ConcurrentDictionary<int, DelVertexAction> vertexActions = [];

            int maxColorCount = 0;
            int maxTexCoordCount = 0;

            //Convert all primitives to simple primitives
            //While doing this, compile a command list of actions to set buffer data
            ConcurrentDictionary<int, Vertex> vertexCache = new();
            PrimitiveType primType = mesh.PrimitiveType;

            //This remap contains a list of new vertex indices for each original vertex index.
            PopulateVerticesAssimpParallelPrecomputed(
                mesh,
                vertexActions,
                ref maxColorCount,
                ref maxTexCoordCount,
                vertexCache,
                out points,
                out lines,
                out triangles,
                out var faceRemap);

            SetTriangleIndices(triangles, false);
            SetLineIndices(lines, false);
            SetPointIndices(points, false);

            //Determine which type of primitive has the most data and use that as the primary type
            int count;
            Vertex[] sourceList;
            if (triangles.Length > lines.Length && triangles.Length > points.Length)
            {
                _type = EPrimitiveType.Triangles;
                count = triangles.Length;
                sourceList = triangles;
            }
            else if (lines.Length > triangles.Length && lines.Length > points.Length)
            {
                _type = EPrimitiveType.Lines;
                count = lines.Length;
                sourceList = lines;
            }
            else
            {
                _type = EPrimitiveType.Points;
                count = points.Length;
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

        private unsafe void PopulateAssimpBlendshapeData(Mesh mesh, Matrix4x4 dataTransform, Vertex[] sourceList)
        {
            if (!Engine.Rendering.Settings.AllowBlendshapes || !mesh.HasMeshAnimationAttachments)
                return;

            string[] names = new string[mesh.MeshAnimationAttachmentCount];
            for (int i = 0; i < mesh.MeshAnimationAttachmentCount; i++)
                names[i] = mesh.MeshAnimationAttachments[i].Name;
            BlendshapeNames = names;

            PopulateBlendshapeBuffers(sourceList, dataTransform);
        }
        private static void PopulateVerticesAssimpParallelPrecomputed(
            Mesh mesh,
            ConcurrentDictionary<int, DelVertexAction> vertexActions,
            ref int mcc,
            ref int mtc,
            ConcurrentDictionary<int, Vertex> vertexCache,
            out Vertex[] points,
            out Vertex[] lines,
            out Vertex[] triangles,
            out Dictionary<int, List<int>> faceRemap)
        {
            int faceCount = mesh.FaceCount;

            using var t = Engine.Profiler.Start($"PopulateVerticesAssimpParallelPrecomputed with {faceCount} faces");

            int maxColorCount = 0;
            int maxTexCoordCount = 0;

            // Precompute per-face vertex counts and offsets for each target type.
            // For a face:
            //  • 1 index → points (1 vertex)
            //  • 2 indices → lines (2 vertices)
            //  • ≥3 indices → triangles (fan triangulation: (IndexCount - 2) * 3 vertices)
            int[] offsetPoints = new int[faceCount];
            int[] offsetLines = new int[faceCount];
            int[] offsetTriangles = new int[faceCount];

            int totalPoints = 0, totalLines = 0, totalTriangles = 0;

            using (var t1 = Engine.Profiler.Start("Precompute offsets"))
            {
                for (int i = 0; i < faceCount; i++)
                {
                    Face face = mesh.Faces[i];
                    if (face.IndexCount == 1)
                    {
                        offsetPoints[i] = totalPoints;
                        totalPoints += 1;
                    }
                    else if (face.IndexCount == 2)
                    {
                        offsetLines[i] = totalLines;
                        totalLines += 2;
                    }
                    else
                    {
                        offsetTriangles[i] = totalTriangles;
                        int numTriangles = face.IndexCount - 2; // fan triangulation
                        totalTriangles += numTriangles * 3;
                    }
                }
            }

            // Preallocate arrays to hold the vertices in correct order.
            Vertex[] pointsArray = new Vertex[totalPoints];
            Vertex[] linesArray = new Vertex[totalLines];
            Vertex[] trianglesArray = new Vertex[totalTriangles];

            // Use a concurrent dictionary for face remapping.
            var concurrentFaceRemap = new ConcurrentDictionary<int, List<int>>();

            // Global flags for vertex actions.
            bool hasNormalAction = vertexActions.ContainsKey(1);
            bool hasTangentAction = vertexActions.ContainsKey(2);
            bool hasTexCoordAction = vertexActions.ContainsKey(3);
            bool hasColorAction = vertexActions.ContainsKey(4);

            using (var t2 = Engine.Profiler.Start("Populate vertices"))
            {
                // Process each face in parallel.
                Parallel.For(0, faceCount, i =>
                {
                    Face face = mesh.Faces[i];
                    int numInd = face.IndexCount;

                    if (numInd == 1)
                    {
                        int baseOffset = offsetPoints[i];
                        ProcessAssimpVertexPrecomputed(face.Indices[0], pointsArray, baseOffset, mesh, vertexCache, vertexActions,
                            ref maxTexCoordCount, ref maxColorCount,
                            concurrentFaceRemap,
                            ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
                    }
                    else if (numInd == 2)
                    {
                        int baseOffset = offsetLines[i];
                        ProcessAssimpVertexPrecomputed(face.Indices[0], linesArray, baseOffset, mesh, vertexCache, vertexActions,
                            ref maxTexCoordCount, ref maxColorCount,
                            concurrentFaceRemap,
                            ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
                        ProcessAssimpVertexPrecomputed(face.Indices[1], linesArray, baseOffset + 1, mesh, vertexCache, vertexActions,
                            ref maxTexCoordCount, ref maxColorCount,
                            concurrentFaceRemap,
                            ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
                    }
                    else
                    {
                        int baseOffset = offsetTriangles[i];
                        int localOffset = 0;
                        int index0 = face.Indices[0];
                        // Fan triangulation: (i0, i1, i2), (i0, i2, i3), ...
                        for (int j = 0; j < numInd - 2; j++)
                        {
                            int index1 = face.Indices[j + 1];
                            int index2 = face.Indices[j + 2];
                            ProcessAssimpVertexPrecomputed(index0, trianglesArray, baseOffset + localOffset, mesh, vertexCache, vertexActions,
                                ref maxTexCoordCount, ref maxColorCount,
                                concurrentFaceRemap,
                                ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
                            localOffset++;
                            ProcessAssimpVertexPrecomputed(index1, trianglesArray, baseOffset + localOffset, mesh, vertexCache, vertexActions,
                                ref maxTexCoordCount, ref maxColorCount,
                                concurrentFaceRemap,
                                ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
                            localOffset++;
                            ProcessAssimpVertexPrecomputed(index2, trianglesArray, baseOffset + localOffset, mesh, vertexCache, vertexActions,
                                ref maxTexCoordCount, ref maxColorCount,
                                concurrentFaceRemap,
                                ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
                            localOffset++;
                        }
                    }
                });
            }

            points = pointsArray;
            lines = linesArray;
            triangles = trianglesArray;

            // Convert the concurrent face remap to a regular dictionary.
            faceRemap = new Dictionary<int, List<int>>(concurrentFaceRemap);
            mtc = maxTexCoordCount;
            mcc = maxColorCount;
        }

        private static void ProcessAssimpVertexPrecomputed(
            int originalIndex,
            Vertex[] targetArray,
            int targetIndex,
            Mesh mesh,
            ConcurrentDictionary<int, Vertex> vertexCache,
            ConcurrentDictionary<int, DelVertexAction> vertexActions,
            ref int maxTexCoordCount,
            ref int maxColorCount,
            ConcurrentDictionary<int, List<int>> faceRemap,
            ref bool hasNormalAction,
            ref bool hasTangentAction,
            ref bool hasTexCoordAction,
            ref bool hasColorAction)
        {
            // Get or create the vertex (thread-safe via the concurrent dictionary)
            Vertex v = vertexCache.GetOrAdd(originalIndex, x => Vertex.FromAssimp(mesh, x));
            if (v == null)
                return;

            // Write the vertex into the preallocated array at its computed index.
            targetArray[targetIndex] = v;

            // Update vertex actions based on the vertex's attributes.
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

            // Update the face remap: map the original index to its new global index.
            faceRemap.AddOrUpdate(originalIndex,
                key => [targetIndex],
                (key, list) =>
                {
                    lock (list)
                    {
                        list.Add(targetIndex);
                    }
                    return list;
                });
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

        private unsafe void PopulateBlendshapeBuffers(Vertex[] sourceList, Matrix4x4 dataTransform)
        {
            using var t = Engine.Profiler.Start();

            bool intVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders;

            BlendshapeCounts = new XRDataBuffer(ECommonBufferType.BlendshapeCount.ToString(), EBufferTarget.ArrayBuffer, (uint)sourceList.Length, intVarType ? EComponentType.Int : EComponentType.Float, 2, false, intVarType);
            Buffers.Add(BlendshapeCounts.AttributeName, BlendshapeCounts);

            List<Vector3> deltas = [Vector3.Zero]; //0 index is reserved for 0 delta
            List<IVector4> blendshapeIndices = [];

            bool remapDeltas = Engine.Rendering.Settings.RemapBlendshapeDeltas;

            int blendshapeDeltaIndicesIndex = 0;
            int sourceCount = sourceList.Length;
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
            Buffers.Add(BlendshapeIndices.AttributeName, BlendshapeIndices);

            if (remapDeltas)
                PopulateRemappedBlendshapeDeltas(intVarType, deltas, blendshapeIndices);
            else
                PopulateBlendshapeDeltas(intVarType, deltas, blendshapeIndices);
        }

        private unsafe void PopulateBlendshapeDeltas(bool intVarType, List<Vector3> deltas, List<IVector4> blendshapeIndices)
        {
            using var t = Engine.Profiler.Start();

            BlendshapeDeltas = new XRDataBuffer($"{ECommonBufferType.BlendshapeDeltas}Buffer", EBufferTarget.ShaderStorageBuffer, (uint)deltas.Count, EComponentType.Float, 4, false, false);
            Buffers.Add(BlendshapeDeltas.AttributeName, BlendshapeDeltas);

            float* deltaData = (float*)BlendshapeDeltas.Address;
            for (int i = 0; i < deltas.Count; i++)
            {
                Vector3 delta = deltas[i];
                *deltaData++ = delta.X;
                *deltaData++ = delta.Y;
                *deltaData++ = delta.Z;
                *deltaData++ = 0.0f;
            }
            int* indicesDataInt = (int*)BlendshapeIndices!.Address;
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

        private unsafe void PopulateRemappedBlendshapeDeltas(bool intVarType, List<Vector3> deltas, List<IVector4> blendshapeIndices)
        {
            using var t = Engine.Profiler.Start();

            Remapper deltaRemap = new();
            deltaRemap.Remap(deltas, null);
            BlendshapeDeltas = new XRDataBuffer($"{ECommonBufferType.BlendshapeDeltas}Buffer", EBufferTarget.ShaderStorageBuffer, deltaRemap.ImplementationLength, EComponentType.Float, 4, false, false);
            Buffers.Add(BlendshapeDeltas.AttributeName, BlendshapeDeltas);

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
            int* indicesDataInt = (int*)BlendshapeIndices!.Address;
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

        private void InitializeSkinning(
            Mesh mesh,
            Dictionary<string, List<SceneNode>> nodeCache,
            Dictionary<int, List<int>>? faceRemap,
            Vertex[] sourceList)
        {
            using var t = Engine.Profiler.Start();

            CollectBoneWeights(mesh, nodeCache, faceRemap, sourceList, out int boneCount, out Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[]? weightsPerVertex, out Dictionary<TransformBase, int> boneToIndexTable);

            //if (boneToIndexTable.Count < boneCount)
            //    Debug.Out($"{boneCount - boneToIndexTable.Count} unweighted bones were removed.");

            if (weightsPerVertex is not null && weightsPerVertex.Length > 0 && boneCount > 0)
                PopulateSkinningBuffers(boneToIndexTable, weightsPerVertex);
        }

        private void CollectBoneWeights(
            Mesh mesh,
            Dictionary<string, List<SceneNode>> nodeCache,
            Dictionary<int, List<int>>? faceRemap,
            Vertex[] sourceList,
            out int boneCount,
            out Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[]? weightsPerVertex,
            out Dictionary<TransformBase, int> boneToIndexTable)
        {
            using var t = Engine.Profiler.Start();

            boneCount = Engine.Rendering.Settings.AllowSkinning ? mesh.BoneCount : 0;
            int vertexCount = VertexCount;
            var weightsPerVertex2 = new Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[vertexCount];

            // Use concurrent dictionaries for thread-safe updates.
            var concurrentInvBindMatrices = new ConcurrentDictionary<TransformBase, Matrix4x4>();
            var concurrentBoneToIndexTable = new ConcurrentDictionary<TransformBase, int>();
            _maxWeightCount = 0;
            int boneIndex = 0;

            // Create per-vertex locks to synchronize updates to weightsPerVertex.
            object[] vertexLocks = new object[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                vertexLocks[i] = new object();

            Parallel.For(0, boneCount, i =>
            {
                Bone bone = mesh.Bones[i];
                if (!bone.HasVertexWeights)
                    return;

                string name = bone.Name;
                if (!TryGetTransform(nodeCache, name, out var transform) || transform is null)
                {
                    Debug.Out($"Bone {name} has no corresponding node in the heirarchy.");
                    return;
                }

                Matrix4x4 invBind = transform.InverseBindMatrix;
                concurrentInvBindMatrices[transform] = invBind;

                int weightCount = bone.VertexWeightCount;
                for (int j = 0; j < weightCount; j++)
                {
                    var vw = bone.VertexWeights[j];
                    int origId = vw.VertexID;
                    float weight = vw.Weight;
                    List<int> targetIndices = (faceRemap != null && faceRemap.TryGetValue(origId, out var remapped))
                        ? remapped
                        : [origId];

                    foreach (int newId in targetIndices)
                    {
                        lock (vertexLocks[newId])
                        {
                            var wpv = weightsPerVertex2[newId];
                            if (wpv == null)
                            {
                                wpv = [];
                                weightsPerVertex2[newId] = wpv;
                            }

                            if (!wpv.TryGetValue(transform, out var existing))
                                wpv[transform] = (weight, invBind);
                            else if (existing.weight != weight)
                            {
                                wpv[transform] = ((existing.weight + weight) * 0.5f, existing.invBindMatrix);
                                Debug.Out($"Vertex {newId} has multiple weights for bone {name}.");
                            }
                            if (sourceList[newId].Weights == null)
                                sourceList[newId].Weights = wpv;

                            int currentMax, origMax;
                            do
                            {
                                origMax = _maxWeightCount;
                                currentMax = Math.Max(origMax, wpv.Count);
                            }
                            while (Interlocked.CompareExchange(ref _maxWeightCount, currentMax, origMax) != origMax);
                        }
                    }
                }

                int idx = Interlocked.Increment(ref boneIndex) - 1;
                concurrentBoneToIndexTable.TryAdd(transform, idx);
            });

            boneToIndexTable = concurrentBoneToIndexTable.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            // Set UtilizedBones from boneToIndexTable and the collected inverse bind matrices.
            var utilizedBones = new (TransformBase tfm, Matrix4x4 invBindWorldMtx)[boneToIndexTable.Count];
            foreach (var pair in boneToIndexTable)
                if (concurrentInvBindMatrices.TryGetValue(pair.Key, out Matrix4x4 cachedInvBind))
                    utilizedBones[pair.Value] = (pair.Key, cachedInvBind);
            UtilizedBones = utilizedBones;

            weightsPerVertex = weightsPerVertex2;
        }

        private int _maxWeightCount = 0;
        /// <summary>
        /// This is the maximum number of weights used for one or more vertices.
        /// </summary>
        public int MaxWeightCount => _maxWeightCount;

        private void PopulateSkinningBuffers(Dictionary<TransformBase, int> boneToIndexTable, Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[] weightsPerVertex)
        {
            //using var timer = Engine.Profiler.Start();

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
    
                BoneWeightIndices = new XRDataBuffer($"{ECommonBufferType.BoneMatrixIndices}Buffer", EBufferTarget.ShaderStorageBuffer, true);
                Buffers.Add(BoneWeightIndices.AttributeName, BoneWeightIndices);

                BoneWeightValues = new XRDataBuffer($"{ECommonBufferType.BoneMatrixWeights}Buffer", EBufferTarget.ShaderStorageBuffer, false);
                Buffers.Add(BoneWeightValues.AttributeName, BoneWeightValues);
            }

            Buffers.Add(BoneWeightOffsets.AttributeName, BoneWeightOffsets);
            Buffers.Add(BoneWeightCounts.AttributeName, BoneWeightCounts);

            PopulateWeightBuffers(boneToIndexTable, weightsPerVertex, optimizeTo4Weights);
        }

        /// <summary>
        /// Populates bone weight buffers in parallel for skinning with more than 4 weights per vertex.
        /// </summary>
        /// <param name="boneToIndexTable"></param>
        /// <param name="weightsPerVertex"></param>
        private unsafe void PopulateUnoptWeightsParallel(
            Dictionary<TransformBase, int> boneToIndexTable,
            Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[] weightsPerVertex)
        {
            using var t = Engine.Profiler.Start();

            int vertexCount = VertexCount;
            // Arrays to hold per-vertex count and temporary storage per vertex.
            uint[] counts = new uint[vertexCount];
            // Temporary storage for indices and weights computed per vertex.
            List<int>[] localBoneIndices = new List<int>[vertexCount];
            List<float>[] localBoneWeights = new List<float>[vertexCount];
            bool intVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders;

            // Process each vertex in parallel.
            void Fill(int vertexIndex)
            {
                var weightGroup = weightsPerVertex[vertexIndex];
                if (weightGroup is null)
                {
                    counts[vertexIndex] = 0;
                    localBoneIndices[vertexIndex] = [];
                    localBoneWeights[vertexIndex] = [];
                }
                else
                {
                    // Normalize weights before processing.
                    VertexWeightGroup.Normalize(weightGroup);
                    int count = weightGroup.Count;
                    counts[vertexIndex] = (uint)count;
                    List<int> indicesList = new(count);
                    List<float> weightsList = new(count);
                    foreach (var pair in weightGroup)
                    {
                        int bIndex = boneToIndexTable[pair.Key];
                        float bWeight = pair.Value.weight;
                        if (bIndex < 0)
                        {
                            bIndex = -1;
                            bWeight = 0.0f;
                        }
                        // +1 because 0 is reserved for identity.
                        indicesList.Add(bIndex + 1);
                        weightsList.Add(bWeight);
                    }
                    localBoneIndices[vertexIndex] = indicesList;
                    localBoneWeights[vertexIndex] = weightsList;
                    // Thread-safe update of maximum weight count.
                    Interlocked.Exchange(ref _maxWeightCount, Math.Max(MaxWeightCount, count));
                }
            }
            Parallel.For(0, vertexCount, Fill);

            // Compute prefix-sum offsets sequentially.
            uint offset = 0u;
            XRDataBuffer offsetsBuf = BoneWeightOffsets!;
            XRDataBuffer countsBuf = BoneWeightCounts!;

            // Write the computed offsets and counts to the weight buffers.
            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                uint count = counts[vertexIndex];
                if (intVarType)
                {
                    ((uint*)offsetsBuf.Address)[vertexIndex] = offset;
                    ((uint*)countsBuf.Address)[vertexIndex] = count;
                }
                else
                {
                    ((float*)offsetsBuf.Address)[vertexIndex] = offset;
                    ((float*)countsBuf.Address)[vertexIndex] = count;
                }
                offset += count;
            }

            // Assemble global boneIndices and boneWeights lists sequentially.
            BoneWeightIndices!.Allocate<int>(offset);
            BoneWeightValues!.Allocate<int>(offset);
            offset = 0u;

            for (int i = 0; i < vertexCount; i++)
            {
                uint count = counts[i];
                if (intVarType)
                {
                    for (int j = 0; j < count; j++)
                    {
                        ((int*)BoneWeightIndices.Address)[offset] = localBoneIndices[i][j];
                        ((float*)BoneWeightValues.Address)[offset] = localBoneWeights[i][j];
                        offset++;
                    }
                }
                else
                {
                    for (int j = 0; j < count; j++)
                    {
                        ((float*)BoneWeightIndices.Address)[offset] = localBoneIndices[i][j];
                        ((float*)BoneWeightValues.Address)[offset] = localBoneWeights[i][j];
                        offset++;
                    }
                }
            }
        }
        private unsafe void PopulateWeightBuffers(
            Dictionary<TransformBase, int> boneToIndexTable,
            Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[] weightsPerVertex,
            bool optimizeTo4Weights)
        {
            _maxWeightCount = 0;
            if (optimizeTo4Weights)
                PopulateOptWeightsParallel(boneToIndexTable, weightsPerVertex);
            else
                PopulateUnoptWeightsParallel(boneToIndexTable, weightsPerVertex);
        }

        private unsafe void PopulateUnoptWeightsSequential(
            Dictionary<TransformBase, int> boneToIndexTable,
            Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[] weightsPerVertex)
        {
            using var t = Engine.Profiler.Start();

            bool intVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders;
            int vertexCount = VertexCount;
            var weightOffsets = BoneWeightOffsets;
            var weightCounts = BoneWeightCounts;
            List<int> boneIndices = [];
            List<float> boneWeights = [];
            int offset = 0;
            float* offsetData = (float*)weightOffsets!.Address;
            float* countData = (float*)weightCounts!.Address;
            int* offsetDataInt = (int*)weightOffsets!.Address;
            int* countDataInt = (int*)weightCounts!.Address;

            // Sequential path when not optimizing to 4 weights.
            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                var weightGroup = weightsPerVertex[vertexIndex];
                if (weightGroup is null)
                {
                    if (intVarType)
                    {
                        offsetDataInt[vertexIndex] = offset;
                        countDataInt[vertexIndex] = 0;
                    }
                    else
                    {
                        offsetData[vertexIndex] = (float)offset;
                        countData[vertexIndex] = 0.0f;
                    }
                }
                else
                {
                    VertexWeightGroup.Normalize(weightGroup);
                    int count = weightGroup.Count;
                    _maxWeightCount = Math.Max(MaxWeightCount, count);
                    foreach (var pair in weightGroup)
                    {
                        int bIndex = boneToIndexTable[pair.Key];
                        float bWeight = pair.Value.weight;
                        if (bIndex < 0)
                        {
                            bIndex = -1;
                            bWeight = 0.0f;
                        }
                        boneIndices.Add(bIndex + 1); // +1 because 0 is reserved for identity.
                        boneWeights.Add(bWeight);
                    }
                    if (intVarType)
                    {
                        offsetDataInt[vertexIndex] = offset;
                        countDataInt[vertexIndex] = count;
                    }
                    else
                    {
                        offsetData[vertexIndex] = (float)offset;
                        countData[vertexIndex] = (float)count;
                    }
                    offset += count;
                }
            }

            BoneWeightIndices!.SetDataRaw(boneIndices.ToArray());
            BoneWeightValues!.SetDataRaw(boneWeights.ToArray());
        }

        private unsafe void PopulateOptWeightsParallel(
            Dictionary<TransformBase, int> boneToIndexTable,
            Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[] weightsPerVertex)
        {
            using var t = Engine.Profiler.Start();

            bool intVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders;
            int vertexCount = VertexCount;
            var weightOffsets = BoneWeightOffsets!;
            var weightCounts = BoneWeightCounts!;

            // Parallelize since each vertex is independent in the optimized path.
            void Fill(int vertexIndex)
            {
                var weightGroup = weightsPerVertex[vertexIndex];
                int* offsetInts = (int*)weightOffsets.Address;
                float* countFloats = (float*)weightCounts.Address;
                // Each vertex uses 4 ints (for indices) and 4 floats (for weights)
                int baseIndex = vertexIndex * 4;

                if (weightGroup is null)
                {
                    // Set default values: all zeros.
                    offsetInts[baseIndex + 0] = 0;
                    offsetInts[baseIndex + 1] = 0;
                    offsetInts[baseIndex + 2] = 0;
                    offsetInts[baseIndex + 3] = 0;
                    countFloats[baseIndex + 0] = 0.0f;
                    countFloats[baseIndex + 1] = 0.0f;
                    countFloats[baseIndex + 2] = 0.0f;
                    countFloats[baseIndex + 3] = 0.0f;
                }
                else
                {
                    // Optimize weight group to at most 4 weights.
                    VertexWeightGroup.Optimize(weightGroup, 4);
                    int count = weightGroup.Count;
                    int current, computed;
                    do
                    {
                        current = _maxWeightCount;
                        computed = Math.Max(current, count);
                    }
                    while (Interlocked.CompareExchange(ref _maxWeightCount, computed, current) != current);

                    // Prepare local storage for up to four indices and weights.
                    int i = 0;
                    foreach (var pair in weightGroup)
                    {
                        int bIndex = boneToIndexTable[pair.Key];
                        float bWeight = pair.Value.weight;
                        if (bIndex < 0)
                        {
                            bIndex = -1;
                            bWeight = 0.0f;
                        }
                        // +1 because 0 is reserved for identity.
                        int value = bIndex + 1;
                        offsetInts[baseIndex + i] = value;
                        countFloats[baseIndex + i] = bWeight;
                        i++;
                    }
                    // Fill remaining slots with zeros.
                    while (i < 4)
                    {
                        offsetInts[baseIndex + i] = 0;
                        countFloats[baseIndex + i] = 0.0f;
                        i++;
                    }
                }
            }
            Parallel.For(0, vertexCount, Fill);
        }

        private static unsafe bool TryGetTransform(Dictionary<string, List<SceneNode>> nodeCache, string name, out TransformBase? transform)
        {
            if (!nodeCache.TryGetValue(name, out var matchList) || matchList is null || matchList.Count == 0)
            {
                Debug.Out($"{name} has no corresponding node in the heirarchy.");
                transform = null;
                return false;
            }

            //if (matchList.Count > 1)
            //    Debug.Out($"{name} has multiple corresponding nodes in the heirarchy. Using the first one.");

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
            //using var t = Engine.Profiler.Start();

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
            XRRenderProgram program = new(true, true, shader);
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

        /// <summary>
        /// Creates a deep copy of this mesh including all vertex data, indices, and buffers.
        /// </summary>
        /// <returns>A new XRMesh instance containing copied data</returns>
        public XRMesh Clone()
        {
            using var t = Engine.Profiler.Start("XRMesh Clone");

            // Create new mesh
            XRMesh clone = new()
            {
                // Copy basic properties
                _interleaved = Interleaved,
                _interleavedStride = InterleavedStride,
                _positionOffset = PositionOffset,
                _normalOffset = NormalOffset,
                _tangentOffset = TangentOffset,
                _colorOffset = ColorOffset,
                _texCoordOffset = TexCoordOffset,
                _colorCount = ColorCount,
                _texCoordCount = TexCoordCount,
                VertexCount = VertexCount,
                _type = Type,
                _bounds = Bounds,
                _maxWeightCount = MaxWeightCount,
                BlendshapeNames = [.. BlendshapeNames],
                // Deep copy vertices
                _vertices = new Vertex[Vertices.Length]
            };

            for (int i = 0; i < Vertices.Length; i++)
                clone._vertices[i] = Vertices[i].HardCopy();

            // Deep copy primitive indices
            if (_points != null)
                clone._points = [.. _points];
            if (_lines != null)
                clone._lines = [.. _lines];
            if (_triangles != null)
                clone._triangles = [.. _triangles];

            // Deep copy buffers
            clone.Buffers = Buffers.Clone();

            // Copy specific buffer references
            clone.PositionsBuffer = clone.Buffers.GetValueOrDefault(ECommonBufferType.Position.ToString());
            clone.NormalsBuffer = clone.Buffers.GetValueOrDefault(ECommonBufferType.Normal.ToString());
            clone.TangentsBuffer = clone.Buffers.GetValueOrDefault(ECommonBufferType.Tangent.ToString());

            if (ColorBuffers != null)
            {
                clone.ColorBuffers = new XRDataBuffer[ColorBuffers.Length];
                for (int i = 0; i < ColorBuffers.Length; i++)
                    clone.ColorBuffers[i] = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.Color}{i}")!;
            }

            if (TexCoordBuffers != null)
            {
                clone.TexCoordBuffers = new XRDataBuffer[TexCoordBuffers.Length];
                for (int i = 0; i < TexCoordBuffers.Length; i++)
                    clone.TexCoordBuffers[i] = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.TexCoord}{i}")!;
            }

            clone.InterleavedVertexBuffer = clone.Buffers.GetValueOrDefault(ECommonBufferType.InterleavedVertex.ToString());

            // Copy skinning data
            if (HasSkinning)
            {
                clone.UtilizedBones = new (TransformBase tfm, Matrix4x4 invBindWorldMtx)[UtilizedBones.Length];
                Array.Copy(UtilizedBones, clone.UtilizedBones, UtilizedBones.Length);

                clone.BoneWeightOffsets = clone.Buffers.GetValueOrDefault(ECommonBufferType.BoneMatrixOffset.ToString());
                clone.BoneWeightCounts = clone.Buffers.GetValueOrDefault(ECommonBufferType.BoneMatrixCount.ToString());
                clone.BoneWeightIndices = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.BoneMatrixIndices}Buffer");
                clone.BoneWeightValues = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.BoneMatrixWeights}Buffer");
            }

            // Copy blendshape data
            if (HasBlendshapes)
            {
                clone.BlendshapeCounts = clone.Buffers.GetValueOrDefault(ECommonBufferType.BlendshapeCount.ToString());
                clone.BlendshapeIndices = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeIndices}Buffer");
                clone.BlendshapeDeltas = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeDeltas}Buffer");
            }

            //// Copy SDF if exists
            //if (SignedDistanceField != null)
            //    clone.SignedDistanceField = SignedDistanceField.Clone();

            //// Copy events
            //if (DataChanged != null)
            //    clone.DataChanged = new XREvent<XRMesh>(DataChanged);

            return clone;
        }
    }
}
