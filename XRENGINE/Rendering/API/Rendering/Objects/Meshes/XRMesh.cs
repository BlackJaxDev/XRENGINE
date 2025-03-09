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
        [YamlIgnore]
        public XREvent<XRMesh> DataChanged;

        private static void PopulateVertexData(IEnumerable<Action<int, int, Vertex>> vertexActions, List<Vertex> sourceList, int[]? firstAppearanceArray, bool parallel = true)
        {
            int len = firstAppearanceArray?.Length ?? sourceList.Count;
            if (parallel)
                Parallel.For(0, len, i => SetVertexData(i, vertexActions, sourceList, firstAppearanceArray));
            else
                for (int i = 0; i < len; ++i)
                    SetVertexData(i, vertexActions, sourceList, firstAppearanceArray);
        }

        private static void PopulateVertexData(IEnumerable<Action<int, int, Vertex>> vertexActions, List<Vertex> sourceList, int count, bool parallel = true)
        {
            if (parallel)
                Parallel.For(0, count, i => SetVertexData(i, vertexActions, sourceList));
            else
                for (int i = 0; i < count; ++i)
                    SetVertexData(i, vertexActions, sourceList);
        }

        private static void SetVertexData(int i, IEnumerable<Action<int, int, Vertex>> vertexActions, List<Vertex> sourceList, int[]? remapArray)
        {
            int x = remapArray?[i] ?? i;
            Vertex vtx = sourceList[x];
            foreach (var action in vertexActions)
                action.Invoke(i, x, vtx);
        }

        private static void SetVertexData(int i, IEnumerable<Action<int, int, Vertex>> vertexActions, List<Vertex> sourceList)
        {
            Vertex vtx = sourceList[i];
            foreach (var action in vertexActions)
                action.Invoke(i, i, vtx);
        }

        private void InitMeshBuffers(
            bool hasNormals,
            bool hasTangents,
            int colorCount,
            int texCoordCount)
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

        //Specific common buffers

        #region Per-Facepoint Buffers

        #region Data Buffers
        public XRDataBuffer? PositionsBuffer { get; private set; } //Required
        public XRDataBuffer? NormalsBuffer { get; private set; }
        public XRDataBuffer? TangentsBuffer { get; private set; }
        public XRDataBuffer[]? ColorBuffers { get; private set; } = [];
        public XRDataBuffer[]? TexCoordBuffers { get; private set; } = [];
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

        //public void GetTriangle(int index, out VertexIndices point0, out VertexIndices point1, out VertexIndices point2)
        //{
        //    if (_triangles is null)
        //        throw new InvalidOperationException();

        //    if (index < 0 || index >= _triangles.Count)
        //        throw new IndexOutOfRangeException();

        //    IndexTriangle face = _triangles[index];
        //    point0 = _faceIndices[face.Point0];
        //    point1 = _faceIndices[face.Point1];
        //    point2 = _faceIndices[face.Point2];
        //}
        //public void SetTriangle(int index, VertexIndices point0, VertexIndices point1, VertexIndices point2)
        //{
        //    if (_triangles is null)
        //        throw new InvalidOperationException();

        //    if (index < 0 || index >= _triangles.Count)
        //        throw new IndexOutOfRangeException();

        //    IndexTriangle face = _triangles[index];
        //    _faceIndices[face.Point0] = point0;
        //    _faceIndices[face.Point1] = point1;
        //    _faceIndices[face.Point2] = point2;
        //}
        //public void GetLine(int index, out VertexIndices point0, out VertexIndices point1)
        //{
        //    if (_lines is null)
        //        throw new InvalidOperationException();

        //    if (index < 0 || index >= _lines.Count)
        //        throw new IndexOutOfRangeException();

        //    IndexLine line = _lines[index];
        //    point0 = _faceIndices[line.Point0];
        //    point1 = _faceIndices[line.Point1];
        //}
        //public void SetLine(int index, VertexIndices point0, VertexIndices point1)
        //{
        //    if (_lines is null)
        //        throw new InvalidOperationException();

        //    if (index < 0 || index >= _lines.Count)
        //        throw new IndexOutOfRangeException();

        //    IndexLine line = _lines[index];
        //    _faceIndices[line.Point0] = point0;
        //    _faceIndices[line.Point1] = point1;
        //}
        //public void GetPoint(int index, out VertexIndices point)
        //{
        //    if (_points is null)
        //        throw new InvalidOperationException();

        //    if (index < 0 || index >= _points.Count)
        //        throw new IndexOutOfRangeException();

        //    point = _faceIndices[_points[index]];
        //}
        //public void SetPoint(int index, VertexIndices point)
        //{
        //    if (_points is null)
        //        throw new InvalidOperationException();

        //    if (index < 0 || index >= _points.Count)
        //        throw new IndexOutOfRangeException();

        //    _faceIndices[_points[index]] = point;
        //}

        //private static Dictionary<Type, EPrimitiveType> PrimTypeDic { get; }
        //    = new Dictionary<Type, EPrimitiveType>()
        //    {
        //        { typeof(VertexQuad), EPrimitiveType.Quads },
        //        { typeof(VertexTriangle), EPrimitiveType.Triangles },
        //        { typeof(VertexTriangleFan), EPrimitiveType.TriangleFan },
        //        { typeof(VertexTriangleStrip), EPrimitiveType.TriangleStrip },
        //        { typeof(VertexLine), EPrimitiveType.Lines },
        //        { typeof(VertexLineStrip), EPrimitiveType.LineStrip },
        //        { typeof(Vertex), EPrimitiveType.Points },
        //    };

        //private static Dictionary<EPrimitiveType, Func<IEnumerable<VertexPrimitive>, IEnumerable<Vertex>>> PrimConvDic { get; }
        //    = new Dictionary<EPrimitiveType, Func<IEnumerable<VertexPrimitive>, IEnumerable<Vertex>>>()
        //    {
        //        { EPrimitiveType.Quads, p => p.SelectMany(x => ((VertexQuad)x).ToTriangles()).SelectMany(x => x.Vertices) },
        //        { EPrimitiveType.Triangles, p => p.SelectMany(x => x.Vertices) },
        //        { EPrimitiveType.TriangleFan, p => p.SelectMany(x => ((VertexTriangleFan)x).ToTriangles()).SelectMany(x => x.Vertices) },
        //        { EPrimitiveType.TriangleStrip, p => p.SelectMany(x => ((VertexTriangleStrip)x).ToTriangles()).SelectMany(x => x.Vertices) },
        //        { EPrimitiveType.Lines, p => p.SelectMany(x => x.Vertices) },
        //        { EPrimitiveType.LineStrip, p => p.SelectMany(x => ((VertexLineStrip)x).ToLines()).SelectMany(x => x.Vertices) },
        //        { EPrimitiveType.Points, p => p.Select(x => (Vertex)x) },
        //    };

        /// <summary>
        /// The axis-aligned bounds of this mesh before any vertex transformations.
        /// </summary>
        public AABB Bounds
        {
            get => _bounds;
            private set => _bounds = value;
        }
        //private static EPrimitiveType ConvertType(EPrimitiveType type)
        //    => type switch
        //    {
        //        EPrimitiveType.Quads or
        //        EPrimitiveType.QuadStrip or
        //        EPrimitiveType.Triangles or
        //        EPrimitiveType.TriangleFan or
        //        EPrimitiveType.TriangleStrip => EPrimitiveType.Triangles,

        //        EPrimitiveType.Lines or
        //        EPrimitiveType.LineLoop or
        //        EPrimitiveType.LineStrip => EPrimitiveType.Lines,

        //        _ => EPrimitiveType.Points,
        //    };

        public static XRMesh Create<T>(params T[] prims) where T : VertexPrimitive
        {
            //No more need to convert types, this is handled by the constructor now
            //if (!GetPrimType<T>(out EPrimitiveType type))
            //    type = EPrimitiveType.Triangles;

            //return new(PrimConvDic[type](prims));
            return new(prims);
        }

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

        //private static bool GetPrimType<T>(out EPrimitiveType type) where T : VertexPrimitive
        //    => PrimTypeDic.TryGetValue(typeof(T), out type);

        //public VertexTriangle? GetFace(int index)
        //{
        //    IndexTriangle? face = _triangles?[index];
        //    if (face is null)
        //        return null;

        //    return new VertexTriangle(
        //        BuffersToVertex(_faceIndices[face.Point0]),
        //        BuffersToVertex(_faceIndices[face.Point1]),
        //        BuffersToVertex(_faceIndices[face.Point2]));
        //}

        //public VertexLine? GetLine(int index)
        //{
        //    IndexLine? line = _lines?[index];
        //    if (line is null)
        //        return null;

        //    return new VertexLine(
        //        BuffersToVertex(_faceIndices[line.Value.Point0]),
        //        BuffersToVertex(_faceIndices[line.Value.Point1]));
        //}

        //public Vertex? GetPoint(int index)
        //{
        //    IndexPoint? point = _points?[index];
        //    if (point is null)
        //        return null;

        //    return BuffersToVertex(_faceIndices[point.Value]);
        //}

        //public void VertexToBuffers(Vertex v, VertexIndices facepoint)
        //{
        //    if (facepoint.BufferBindings is null)
        //        return;

        //    for (int i = 0; i < facepoint.BufferBindings.Count; ++i)
        //    {
        //        XRDataBuffer b = Buffers[i];
        //        uint index = facepoint.BufferBindings[i];
        //        var binding = b.Binding;
        //        switch (binding)
        //        {
        //            case EBufferType.Position:
        //                b.Set(index, v.Position);
        //                break;
        //            case EBufferType.Normal:
        //                b.Set(index, v.Normal ?? Vector3.Zero);
        //                break;
        //            //case EBufferType.Binormal:
        //            //    b.Set(index, Binormal);
        //            //    break;
        //            case EBufferType.Tangent:
        //                b.Set(index, v.Tangent ?? Vector3.Zero);
        //                break;
        //            case EBufferType.Color:
        //                //b.Set(index, Color);
        //                break;
        //            case EBufferType.TexCoord:
        //                //b.Set(index, TexCoord);
        //                break;
        //        }
        //    }
        //}

        /// <summary>
        /// Sets the vertex data from the buffers into the vertex using the facepoint to match indices.
        /// </summary>
        /// <param name="v"></param>
        /// <param name="facepoint"></param>
        /// <param name="buffers"></param>
        //public void BuffersToVertex(Vertex v, VertexIndices facepoint)
        //{
        //    if (facepoint.BufferBindings is null)
        //        return;

        //    for (int i = 0; i < facepoint.BufferBindings.Count; ++i)
        //    {
        //        XRDataBuffer b = Buffers[i];
        //        uint index = facepoint.BufferBindings[i];
        //        EBufferType type = b.Binding;
        //        switch (type)
        //        {
        //            case EBufferType.Position:
        //                v.Position = b.Get<Vector3>(index * 12) ?? Vector3.Zero;
        //                break;
        //            case EBufferType.Normal:
        //                v.Normal = b.Get<Vector3>(index * 12);
        //                break;
        //            //case EBufferType.Binormal:
        //            //    Binormal = b.Get<Vector3>(index * 12);
        //            //    break;
        //            case EBufferType.Tangent:
        //                v.Tangent = b.Get<Vector3>(index * 12);
        //                break;
        //            case EBufferType.Color:
        //                //TexCoord = b.Get<Vector2>(index << 4);
        //                break;
        //            case EBufferType.TexCoord:
        //                //TexCoord = b.Get<Vector2>(index << 3);
        //                break;
        //        }
        //    }
        //}
        /// <summary>
        /// Sets the vertex data from the buffers into the vertex using the facepoint to match indices.
        /// </summary>
        /// <param name="v"></param>
        /// <param name="facepoint"></param>
        /// <param name="buffers"></param>
        //public Vertex BuffersToVertex(VertexIndices facepoint)
        //{
        //    Vertex v = new();
        //    if (facepoint.BufferBindings is null)
        //        return v;

        //    for (int i = 0; i < facepoint.BufferBindings.Count; ++i)
        //    {
        //        XRDataBuffer b = Buffers[i];
        //        uint index = facepoint.BufferBindings[i];
        //        EBufferType type = b.Binding;
        //        switch (type)
        //        {
        //            case EBufferType.Position:
        //                v.Position = b.Get<Vector3>(index * 12) ?? Vector3.Zero;
        //                break;
        //            case EBufferType.Normal:
        //                v.Normal = b.Get<Vector3>(index * 12);
        //                break;
        //            //case EBufferType.Binormal:
        //            //    Binormal = b.Get<Vector3>(index * 12);
        //            //    break;
        //            case EBufferType.Tangent:
        //                v.Tangent = b.Get<Vector3>(index * 12);
        //                break;
        //            case EBufferType.Color:
        //                //TexCoord = b.Get<Vector2>(index << 4);
        //                break;
        //            case EBufferType.TexCoord:
        //                //TexCoord = b.Get<Vector2>(index << 3);
        //                break;
        //        }
        //    }
        //    return v;
        //}

        //public void GenerateTangents(int positionIndex, int normalIndex, int uvIndex, bool addTangents)
        //{
        //    if (_triangles is null)
        //        return;

        //    XRDataBuffer[] pBuffs = GetAllBuffersOfType(EBufferType.Position);
        //    if (pBuffs.Length == 0)
        //    {
        //        //Engine.LogWarning("No position buffers found.");
        //        return;
        //    }
        //    if (!pBuffs.IndexInRangeArrayT(positionIndex))
        //    {
        //        //Engine.LogWarning("Position index out of range of available position buffers.");
        //        return;
        //    }
        //    XRDataBuffer[] nBuffs = GetAllBuffersOfType(EBufferType.Normal);
        //    if (nBuffs.Length == 0)
        //    {
        //        //Engine.LogWarning("No normal buffers found.");
        //        return;
        //    }
        //    if (!nBuffs.IndexInRangeArrayT(normalIndex))
        //    {
        //        //Engine.LogWarning("Normal index out of range of available normal buffers.");
        //        return;
        //    }
        //    XRDataBuffer[] tBuffs = GetAllBuffersOfType(EBufferType.TexCoord);
        //    if (tBuffs.Length == 0)
        //    {
        //        //Engine.LogWarning("No texcoord buffers found.");
        //        return;
        //    }
        //    if (!tBuffs.IndexInRangeArrayT(uvIndex))
        //    {
        //        //Engine.LogWarning("UV index out of range of available texcoord buffers.");
        //        return;
        //    }

        //    Vector3 pos1, pos2, pos3;
        //    //Vector3 n0, n1, n2;
        //    Vector2 uv1, uv2, uv3;

        //    XRDataBuffer pBuff = pBuffs[positionIndex];
        //    //VertexBuffer nBuff = pBuffs[normalIndex];
        //    XRDataBuffer tBuff = pBuffs[uvIndex];
        //    int pointCount = _triangles.Count * 3;
        //    //List<Vector3> binormals = new(pointCount);
        //    List<Vector3> tangents = new(pointCount);

        //    for (int i = 0; i < _triangles.Count; ++i)
        //    {
        //        IndexTriangle t = _triangles[i];

        //        VertexIndices fp0 = _faceIndices[t.Point0];
        //        VertexIndices fp1 = _faceIndices[t.Point1];
        //        VertexIndices fp2 = _faceIndices[t.Point2];

        //        pos1 = pBuff.Get<Vector3>(fp0.BufferBindings[pBuff.Index] * 12) ?? Vector3.Zero;
        //        pos2 = pBuff.Get<Vector3>(fp1.BufferBindings[pBuff.Index] * 12) ?? Vector3.Zero;
        //        pos3 = pBuff.Get<Vector3>(fp2.BufferBindings[pBuff.Index] * 12) ?? Vector3.Zero;

        //        uv1 = tBuff.Get<Vector2>(fp0.BufferBindings[tBuff.Index] * 8) ?? Vector2.Zero;
        //        uv2 = tBuff.Get<Vector2>(fp1.BufferBindings[tBuff.Index] * 8) ?? Vector2.Zero;
        //        uv3 = tBuff.Get<Vector2>(fp2.BufferBindings[tBuff.Index] * 8) ?? Vector2.Zero;

        //        Vector3 deltaPos1 = pos2 - pos1;
        //        Vector3 deltaPos2 = pos3 - pos1;

        //        Vector2 deltaUV1 = uv2 - uv1;
        //        Vector2 deltaUV2 = uv3 - uv1;

        //        Vector3 tangent;
        //        //Vector3 binormal;

        //        float m = deltaUV1.X * deltaUV2.Y - deltaUV1.Y * deltaUV2.X;
        //        if (m == 0.0f)
        //        {
        //            tangent = Globals.Up;
        //            //binormal = Globals.Up;
        //        }
        //        else
        //        {
        //            float r = 1.0f / m;
        //            tangent = (deltaPos1 * deltaUV2.Y - deltaPos2 * deltaUV1.Y) * r;
        //            //binormal = (deltaPos2 * deltaUV1.X - deltaPos1 * deltaUV2.X) * r;
        //        }

        //        //3 for each triangle
        //        //binormals.Add(binormal);
        //        //binormals.Add(binormal);
        //        //binormals.Add(binormal);

        //        //3 for each triangle
        //        tangents.Add(tangent);
        //        tangents.Add(tangent);
        //        tangents.Add(tangent);
        //    }

        //    //AddBuffer(binormals, new VertexAttribInfo(EBufferType.Binormal));
        //    //_bufferInfo.HasBinormals = true;

        //    if (addTangents)
        //    {
        //        AddBuffer(tangents, new VertexAttribInfo(EBufferType.Tangent));
        //        _bufferInfo.HasTangents = true;
        //    }

        //    OnBufferInfoChanged();
        //}

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

        public XRMesh(IEnumerable<Vertex> vertices, List<ushort> triangleIndices)
        {
            //using var t = Engine.Profiler.Start(null, true, "XRMesh Constructor");

            List<Vertex> triVertices = [];

            //Create an action for each vertex attribute to set the buffer data
            //This lets us avoid redundant LINQ code by looping through the vertices only once
            ConcurrentDictionary<int, Action<int, int, Vertex>> vertexActions = [];

            int maxColorCount = 0;
            int maxTexCoordCount = 0;
            AABB? bounds = null;

            //For each vertex, we double check what data it has and add verify that the corresponding add-to-buffer action is added
            void AddVertex(Vertex v)
            {
                if (v is null)
                    return;

                triVertices.Add(v);

                bounds = bounds is null ? new AABB(v.Position, v.Position) : bounds.Value.ExpandedToInclude(v.Position);

                if (v.Normal is not null && !vertexActions.ContainsKey(1))
                    vertexActions.TryAdd(1, (i, x, vtx) => NormalsBuffer!.SetVector3((uint)i, vtx?.Normal ?? Vector3.Zero));

                if (v.Tangent is not null && !vertexActions.ContainsKey(2))
                    vertexActions.TryAdd(2, (i, x, vtx) => TangentsBuffer!.SetVector3((uint)i, vtx?.Tangent ?? Vector3.Zero));

                Interlocked.Exchange(ref maxTexCoordCount, Math.Max(maxTexCoordCount, v.TextureCoordinateSets.Count));
                if (v.TextureCoordinateSets is not null && v.TextureCoordinateSets.Count > 0 && !vertexActions.ContainsKey(3))
                {
                    vertexActions.TryAdd(3, (i, x, vtx) =>
                    {
                        for (int texCoordIndex = 0; texCoordIndex < v.TextureCoordinateSets.Count; ++texCoordIndex)
                            TexCoordBuffers![texCoordIndex].SetVector2((uint)i, vtx?.TextureCoordinateSets != null && texCoordIndex < (vtx?.TextureCoordinateSets?.Count ?? 0)
                                ? vtx!.TextureCoordinateSets[texCoordIndex]
                                : Vector2.Zero);
                    });
                }

                Interlocked.Exchange(ref maxColorCount, Math.Max(maxColorCount, v.ColorSets.Count));
                if (v.ColorSets is not null && v.ColorSets.Count > 0 && !vertexActions.ContainsKey(4))
                {
                    vertexActions.TryAdd(4, (i, x, vtx) =>
                    {
                        for (int colorIndex = 0; colorIndex < v.ColorSets.Count; ++colorIndex)
                            ColorBuffers![colorIndex].SetVector4((uint)i, vtx?.ColorSets != null && colorIndex < (vtx?.ColorSets?.Count ?? 0)
                                ? vtx!.ColorSets[colorIndex]
                                : Vector4.Zero);
                    });
                }

                //if (v.Blendshapes is not null && v.Blendshapes.Count > 0 && !vertexActions.ContainsKey(5))
                //{
                //    vertexActions.TryAdd(5, (i, x, vtx) =>
                //    {
                //        if (vtx?.Blendshapes is null)
                //            return;

                //        foreach (var pair in vtx.Blendshapes!)
                //        {
                //            string name = pair.Key;
                //            var data = pair.Value;
                //            Vector3 deltaPos = data.Position - vtx.Position;
                //            Vector3? deltaNorm = data.Normal - vtx.Normal;
                //            Vector3? deltaTan = data.Tangent - vtx.Tangent;
                //            //List<Vector4> colors = data.ColorSets;
                //            //List<Vector2> texCoords = data.TextureCoordinateSets;

                //        }
                //    });
                //}
            }

            //Convert all primitives to simple primitives
            //While doing this, compile a command list of actions to set buffer data
            vertices.ForEach(AddVertex);

            _bounds = bounds ?? new AABB(Vector3.Zero, Vector3.Zero);
            _triangles = triangleIndices.SelectEvery(3, x => new IndexTriangle(x[0], x[1], x[2])).ToList();
            _type = EPrimitiveType.Triangles;
            VertexCount = triVertices.Count;

            InitMeshBuffers(
                vertexActions.ContainsKey(1),
                vertexActions.ContainsKey(2),
                maxColorCount,
                maxTexCoordCount);

            vertexActions.TryAdd(6, (i, x, vtx) => PositionsBuffer!.SetVector3((uint)i, vtx?.Position ?? Vector3.Zero));

            //Fill the buffers with the vertex data using the command list
            //We can do this in parallel since each vertex is independent
            PopulateVertexData(vertexActions.Values, triVertices, VertexCount, true);

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
            //Engine.Profiler.Start(null, true, "XRMesh Constructor");

            //TODO: convert triangles to tristrips and use primitive restart to render them all in one call? is this more efficient?

            //Convert all primitives to simple primitives
            List<Vertex> points = [];
            List<Vertex> lines = [];
            List<Vertex> triangles = [];

            //Create an action for each vertex attribute to set the buffer data
            //This lets us avoid redundant LINQ code by looping through the vertices only once
            ConcurrentDictionary<int, Action<int, int, Vertex>> vertexActions = [];

            int maxColorCount = 0;
            int maxTexCoordCount = 0;
            AABB? bounds = null;

            //For each vertex, we double check what data it has and add verify that the corresponding add-to-buffer action is added
            void AddVertex(List<Vertex> vertices, Vertex v)
            {
                if (v is null)
                    return;

                vertices.Add(v);

                bounds = bounds is null ? new AABB(v.Position, v.Position) : bounds.Value.ExpandedToInclude(v.Position);

                if (v.Normal is not null && !vertexActions.ContainsKey(1))
                    vertexActions.TryAdd(1, (i, x, vtx) => NormalsBuffer!.SetVector3((uint)i, vtx?.Normal ?? Vector3.Zero));

                if (v.Tangent is not null && !vertexActions.ContainsKey(2))
                    vertexActions.TryAdd(2, (i, x, vtx) => TangentsBuffer!.SetVector3((uint)i, vtx?.Tangent ?? Vector3.Zero));

                Interlocked.Exchange(ref maxTexCoordCount, Math.Max(maxTexCoordCount, v.TextureCoordinateSets.Count));
                if (v.TextureCoordinateSets is not null && v.TextureCoordinateSets.Count > 0 && !vertexActions.ContainsKey(3))
                {
                    vertexActions.TryAdd(3, (i, x, vtx) =>
                    {
                        for (int texCoordIndex = 0; texCoordIndex < v.TextureCoordinateSets.Count; ++texCoordIndex)
                            TexCoordBuffers![texCoordIndex].SetVector2((uint)i, vtx?.TextureCoordinateSets != null && texCoordIndex < (vtx?.TextureCoordinateSets?.Count ?? 0)
                                ? vtx!.TextureCoordinateSets[texCoordIndex]
                                : Vector2.Zero);
                    });
                }

                Interlocked.Exchange(ref maxColorCount, Math.Max(maxColorCount, v.ColorSets.Count));
                if (v.ColorSets is not null && v.ColorSets.Count > 0 && !vertexActions.ContainsKey(4))
                {
                    vertexActions.TryAdd(4, (i, x, vtx) =>
                    {
                        for (int colorIndex = 0; colorIndex < v.ColorSets.Count; ++colorIndex)
                            ColorBuffers![colorIndex].SetVector4((uint)i, vtx?.ColorSets != null && colorIndex < (vtx?.ColorSets?.Count ?? 0)
                                ? vtx!.ColorSets[colorIndex]
                                : Vector4.Zero);
                    });
                }

                //if (v.Blendshapes is not null && v.Blendshapes.Count > 0 && !vertexActions.ContainsKey(5))
                //{
                //    vertexActions.TryAdd(5, (i, x, vtx) =>
                //    {
                //        if (vtx?.Blendshapes is null)
                //            return;

                //        foreach (var pair in vtx.Blendshapes!)
                //        {
                //            string name = pair.Key;
                //            var data = pair.Value;
                //            Vector3 deltaPos = data.Position - vtx.Position;
                //            Vector3? deltaNorm = data.Normal - vtx.Normal;
                //            Vector3? deltaTan = data.Tangent - vtx.Tangent;
                //            //List<Vector4> colors = data.ColorSets;
                //            //List<Vector2> texCoords = data.TextureCoordinateSets;

                //        }
                //    });
                //}
            }

            //Convert all primitives to simple primitives
            //While doing this, compile a command list of actions to set buffer data
            foreach (VertexPrimitive prim in primitives)
            {
                switch (prim)
                {
                    case Vertex v:
                        AddVertex(points, v);
                        break;
                    case VertexLinePrimitive l:
                        {
                            var asLines = l.ToLines();
                            foreach (VertexLine line in asLines)
                                foreach (Vertex v in line.Vertices)
                                    AddVertex(lines, v);
                        }
                        break;
                    case VertexLine line:
                        foreach (Vertex v in line.Vertices)
                            AddVertex(lines, v);
                        break;
                    case VertexPolygon t:
                        {
                            var asTris = t.ToTriangles();
                            foreach (VertexTriangle tri in asTris)
                                foreach (Vertex v in tri.Vertices)
                                    AddVertex(triangles, v);
                        }
                        break;
                }
            }

            //Dictionary<TransformBase, float>?[]? weights = null;

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
            
            vertexActions.TryAdd(6, (i, x, vtx) => PositionsBuffer!.SetVector3((uint)i, vtx?.Position ?? Vector3.Zero));

            //MakeFaceIndices(weights, firstAppearanceArray.Length);

            //Fill the buffers with the vertex data using the command list
            //We can do this in parallel since each vertex is independent
            PopulateVertexData(vertexActions.Values, sourceList, firstAppearanceArray, true);

            //if (weights is not null)
            //    SetBoneWeights(weights, );

            Vertices = sourceList;
        }

        public unsafe XRMesh(
            TransformBase parentTransform,
            Mesh mesh,
            AssimpContext assimp,
            Dictionary<string, List<SceneNode>> nodeCache,
            Matrix4x4 invRootMatrix) : this()
        {
            //using var t = Engine.Profiler.Start(null, true, "XRMesh Assimp Constructor");

            Matrix4x4 dataTransform = Matrix4x4.Identity;//parentTransform.InverseWorldMatrix;

            ArgumentNullException.ThrowIfNull(mesh);
            ArgumentNullException.ThrowIfNull(assimp);
            ArgumentNullException.ThrowIfNull(nodeCache);

            //Convert all primitives to simple primitives
            List<Vertex> points = [];
            List<Vertex> lines = [];
            List<Vertex> triangles = [];

            //Vector3[][]? positionDeltas = null;
            //Vector3[][]? normalDeltas = null;
            //Vector3[][]? tangentDeltas = null;

            //Create an action for each vertex attribute to set the buffer data
            //This lets us avoid redundant LINQ code by looping through the vertices only once
            ConcurrentDictionary<int, Action<int, int, Vertex>> vertexActions = [];

            int maxColorCount = 0;
            int maxTexCoordCount = 0;
            AABB? bounds = null;

            //For each vertex, we double check what data it has and add verify that the corresponding add-to-buffer action is added
            void AddVertex(List<Vertex> vertices, Vertex v)
            {
                if (v is null)
                    return;

                vertices.Add(v);

                if (!vertexActions.ContainsKey(0))
                    vertexActions.TryAdd(0, (i, x, vtx) =>
                    {
                        Vector3 worldPos = vtx.GetWorldPosition();
                        bounds = bounds is null ? new AABB(worldPos, worldPos) : bounds.Value.ExpandedToInclude(worldPos);
                    });

                if (v.Normal is not null && !vertexActions.ContainsKey(1))
                    vertexActions.TryAdd(1, (i, x, vtx) => NormalsBuffer!.SetVector3((uint)i, Vector3.TransformNormal(vtx?.Normal ?? Vector3.Zero, dataTransform).Normalized()));

                if (v.Tangent is not null && !vertexActions.ContainsKey(2))
                    vertexActions.TryAdd(2, (i, x, vtx) => TangentsBuffer!.SetVector3((uint)i, Vector3.TransformNormal(vtx?.Tangent ?? Vector3.Zero, dataTransform).Normalized()));

                Interlocked.Exchange(ref maxTexCoordCount, Math.Max(maxTexCoordCount, v.TextureCoordinateSets.Count));
                if (v.TextureCoordinateSets is not null && v.TextureCoordinateSets.Count > 0 && !vertexActions.ContainsKey(3))
                {
                    vertexActions.TryAdd(3, (i, x, vtx) =>
                    {
                        for (int texCoordIndex = 0; texCoordIndex < v.TextureCoordinateSets.Count; ++texCoordIndex)
                            TexCoordBuffers![texCoordIndex].SetVector2((uint)i, vtx?.TextureCoordinateSets != null && texCoordIndex < (vtx?.TextureCoordinateSets?.Count ?? 0)
                                ? FlipYCoord(vtx!.TextureCoordinateSets[texCoordIndex])
                                : Vector2.Zero);
                    });
                }

                Interlocked.Exchange(ref maxColorCount, Math.Max(maxColorCount, v.ColorSets.Count));
                if (v.ColorSets is not null && v.ColorSets.Count > 0 && !vertexActions.ContainsKey(4))
                {
                    vertexActions.TryAdd(4, (i, x, vtx) =>
                    {
                        for (int colorIndex = maxColorCount; colorIndex < v.ColorSets.Count; ++colorIndex)
                            ColorBuffers![colorIndex].SetVector4((uint)i, vtx?.ColorSets != null && colorIndex < (vtx?.ColorSets?.Count ?? 0)
                                ? vtx!.ColorSets[colorIndex]
                                : Vector4.Zero);
                    });
                }

                //if (v.Blendshapes is not null && v.Blendshapes.Count > 0 && !vertexActions.ContainsKey(5) && Engine.Rendering.Settings.AllowBlendshapes)
                //{
                //    bool absolute = Engine.Rendering.Settings.UseAbsoluteBlendshapePositions;
                //    if (absolute)
                //    {
                //        vertexActions.TryAdd(5, (i, x, vtx) =>
                //        {
                //            if (vtx?.Blendshapes is null)
                //                return;

                //            for (int r = 0; r < vtx.Blendshapes.Count; r++)
                //            {
                //                (string _, VertexData data) = vtx.Blendshapes[r];
                //                if (data is null)
                //                    continue;

                //                if (positionDeltas is not null)
                //                    positionDeltas[r][i] = data.Position;
                //                if (normalDeltas is not null && data.Normal is not null)
                //                    normalDeltas[r][i] = data.Normal.Value;
                //                if (tangentDeltas is not null && data.Tangent is not null)
                //                    tangentDeltas[r][i] = data.Tangent.Value;
                //            }
                //        });
                //    }
                //    else
                //    {
                //        vertexActions.TryAdd(5, (i, x, vtx) =>
                //        {
                //            if (vtx?.Blendshapes is null)
                //                return;

                //            for (int r = 0; r < vtx.Blendshapes.Count; r++)
                //            {
                //                (string _, VertexData data) = vtx.Blendshapes[r];
                //                if (data is null)
                //                    continue;

                //                if (positionDeltas is not null)
                //                    positionDeltas[r][i] = data.Position - vtx.Position;
                //                if (normalDeltas is not null && data.Normal is not null && vtx.Normal is not null)
                //                    normalDeltas[r][i] = data.Normal.Value - vtx.Normal.Value;
                //                if (tangentDeltas is not null && data.Tangent is not null && vtx.Tangent is not null)
                //                    tangentDeltas[r][i] = data.Tangent.Value - vtx.Tangent.Value;
                //            }
                //        });
                //    }
                //}
            }

            //Convert all primitives to simple primitives
            //While doing this, compile a command list of actions to set buffer data
            ConcurrentDictionary<int, Vertex> vertexCache = new();
            PrimitiveType primType = mesh.PrimitiveType;

            //TODO: pre-allocate points, lines, and triangles to the correct size and populate in parallel? this is already pretty fast anyways

            //This remap contains a list of new vertex indices for each original vertex index.
            Dictionary<int, List<int>> faceRemap = [];

            for (int i = 0; i < mesh.FaceCount; i++)
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
                    // Convert n-gon to triangles using fan triangulation
                    for (int ind = 0; ind < numInd - 2; ind++)
                    {
                        int[] originalIndices =
                        [
                            face.Indices[0],       // First vertex of the face
                            face.Indices[ind + 1], // Current vertex
                            face.Indices[ind + 2]  // Next vertex
                        ];

                        for (int j = 0; j < 3; j++)
                        {
                            int originalIndex = originalIndices[j];
                            int newIndex = targetList.Count;

                            // Add the vertex to the target list
                            AddVertex(targetList, vertexCache.GetOrAdd(originalIndex, x => Vertex.FromAssimp(mesh, x)));

                            // Update faceRemap
                            if (!faceRemap.TryGetValue(originalIndex, out List<int>? value))
                            {
                                value = [newIndex];
                                faceRemap[originalIndex] = value;
                            }
                            else
                                value.Add(newIndex);
                        }
                    }
                }
                else
                {
                    for (int ind = 0; ind < numInd; ind++)
                    {
                        int originalIndex = face.Indices[ind];
                        int newIndex = targetList.Count;

                        // Add the vertex to the target list
                        AddVertex(targetList, vertexCache.GetOrAdd(originalIndex, x => Vertex.FromAssimp(mesh, x)));

                        // Update faceRemap
                        if (!faceRemap.TryGetValue(originalIndex, out List<int>? value))
                        {
                            value = [newIndex];
                            faceRemap[originalIndex] = value;
                        }
                        else
                            value.Add(newIndex);
                    }
                }
            }

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

            //if (hasBlendshapes)
            //{
            //    positionDeltas = new Vector3[mesh.MeshAnimationAttachmentCount][];
            //    normalDeltas = new Vector3[mesh.MeshAnimationAttachmentCount][];
            //    tangentDeltas = new Vector3[mesh.MeshAnimationAttachmentCount][];
            //    for (int i = 0; i < mesh.MeshAnimationAttachmentCount; i++)
            //    {
            //        positionDeltas[i] = new Vector3[count];
            //        normalDeltas[i] = new Vector3[count];
            //        tangentDeltas[i] = new Vector3[count];
            //    }
            //}

            vertexActions.TryAdd(6, (i, x, vtx) => PositionsBuffer!.SetVector3((uint)i, Vector3.Transform(vtx?.Position ?? Vector3.Zero, dataTransform)));

            //MakeFaceIndices(weights, count);

            //Fill the buffers with the vertex data using the command list
            //We can do this in parallel since each vertex is independent
            PopulateVertexData(vertexActions.Values, sourceList, count, true);

            if (Engine.Rendering.Settings.AllowBlendshapes && mesh.HasMeshAnimationAttachments)
                PopulateBlendshapeBuffers(sourceList, mesh/*, positionDeltas, normalDeltas, tangentDeltas*/);

            _bounds = bounds ?? new AABB(Vector3.Zero, Vector3.Zero);

            Vertices = sourceList;
        }

        private List<Vertex> _vertices = [];
        public List<Vertex> Vertices
        {
            get => _vertices;
            private set => SetField(ref _vertices, value);
        }

        private unsafe void PopulateBlendshapeBuffers(
            List<Vertex> sourceList,
            Mesh mesh)//,
            //Vector3[][]? positionDeltas,
            //Vector3[][]? normalDeltas,
            //Vector3[][]? tangentDeltas)
        {
            bool intVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders;

            BlendshapeCounts = new XRDataBuffer(ECommonBufferType.BlendshapeCount.ToString(), EBufferTarget.ArrayBuffer, (uint)sourceList.Count, intVarType ? EComponentType.Int : EComponentType.Float, 2, false, intVarType);

            BlendshapeNames = new string[mesh.MeshAnimationAttachmentCount];
            for (int i = 0; i < mesh.MeshAnimationAttachmentCount; i++)
                BlendshapeNames[i] = mesh.MeshAnimationAttachments[i].Name;

            List<Vector3> deltas = [Vector3.Zero]; //0 index is reserved for 0 delta
            List<IVector4> blendshapeIndices = [];

            bool remapDeltas = Engine.Rendering.Settings.RemapBlendshapeDeltas;

            int blendshapeDeltaIndicesIndex = 0;
            int sourceCount = sourceList.Count;
            int blendshapeCount = mesh.MeshAnimationAttachmentCount;
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
                for (int bsInd = 0; bsInd < blendshapeCount; bsInd++)
                {
                    var (_, data) = vtx.Blendshapes[bsInd];
                    bool anyData = false;
                    int posInd = 0;
                    int nrmInd = 0;
                    int tanInd = 0;

                    Vector3 posDt = data.Position - vtx.Position;
                    Vector3 nrmDt = (data.Normal ?? Vector3.Zero) - (vtx.Normal ?? Vector3.Zero);
                    Vector3 tanDt = (data.Tangent ?? Vector3.Zero) - (vtx.Tangent ?? Vector3.Zero);

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

            Buffers.Add(BlendshapeCounts.BindingName, BlendshapeCounts);
            Buffers.Add(BlendshapeIndices.BindingName, BlendshapeIndices);
            Buffers.Add(BlendshapeDeltas.BindingName, BlendshapeDeltas);
        }

        //private unsafe Matrix4x4 GetSingleBind(Mesh mesh, Dictionary<string, List<SceneNode>> nodeCache)
        //{
        //    Matrix4x4 dataTransform;
        //    var bone = mesh.Bones[0];
        //    dataTransform = bone->MOffsetMatrix.Transposed();
        //    string name = bone->MName.ToString();
        //    TransformBase? transform = null;
        //    if (!nodeCache.TryGetValue(name, out var matchList) || matchList is null || matchList.Count == 0)
        //    {
        //        Debug.Out($"{name} has no corresponding node in the heirarchy.");
        //        _utilizedBones = [];
        //    }
        //    else
        //    {
        //        if (matchList.Count > 1)
        //            Debug.Out($"{name} has multiple corresponding nodes in the heirarchy. Using the first one.");
        //        transform = matchList[0].Transform;
        //        _utilizedBones = [(transform, dataTransform)];
        //    }

        //    return dataTransform;
        //}

        private void InitializeSkinning(
            Mesh mesh,
            Dictionary<string, List<SceneNode>> nodeCache,
            Dictionary<int, List<int>> faceRemap,
            List<Vertex> sourceList)
        {
            //using var time = Engine.Profiler.Start();

            //Debug.Out($"Collecting bone weights for {mesh.Name}.");

            int boneCount = Engine.Rendering.Settings.AllowSkinning ? mesh.BoneCount : 0;
            Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[]? weightsPerVertex = new Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[VertexCount];
            Dictionary<TransformBase, Matrix4x4> invBindMatrices = [];

            int boneIndex = 0;
            Dictionary<TransformBase, int> boneToIndexTable = [];
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

                Matrix4x4 invBind = bone.OffsetMatrix.Transposed();
                invBindMatrices.Add(transform!, invBind);

                int weightCount = bone.VertexWeightCount;
                for (int j = 0; j < weightCount; j++)
                {
                    var vw = bone.VertexWeights[j];
                    var id = vw.VertexID;
                    var weight = vw.Weight;

                    var list = faceRemap[id];
                    foreach (var newId in list)
                    {
                        weightsPerVertex![newId] ??= [];
                        if (!weightsPerVertex[newId]!.TryGetValue(transform!, out (float weight, Matrix4x4 invBindMatrix) existingPair))
                            weightsPerVertex[newId]!.Add(transform!, (weight, invBind));
                        else if (existingPair.weight != weight)
                        {
                            Debug.Out($"Vertex {newId} has multiple different weights for bone {name}.");
                            weightsPerVertex[newId]![transform] = ((existingPair.weight + weight) / 2.0f, existingPair.invBindMatrix);
                        }
                        sourceList[newId].Weights ??= weightsPerVertex[newId];
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
            }

            PopulateWeightBuffers(boneToIndexTable, weightsPerVertex, optimizeTo4Weights, out List<int> boneIndices, out List<float> boneWeights);

            Buffers.Add(BoneWeightOffsets.BindingName, BoneWeightOffsets);
            Buffers.Add(BoneWeightCounts.BindingName, BoneWeightCounts);

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

            if (MaxWeightCount > 4)
                Debug.Out($"Max weight count: {MaxWeightCount}");

            //while (boneIndices.Count % 4 != 0)
            //{
            //    boneIndices.Add(-1);
            //    boneWeights.Add(0.0f);
            //}
        }

        //private static unsafe uint ResolveBone(Mesh* mesh, uint boneCount, int i, ref Bone* boneResolve)
        //{
        //    uint boneIndex = 0;
        //    uint weightStartIndex = 0;
        //    for (uint j = 0; j < boneCount; ++j)
        //    {
        //        Silk.NET.Assimp.Bone* bone = mesh->MBones[j];
        //        if (i < bone->MNumWeights + weightStartIndex)
        //        {
        //            boneResolve = bone;
        //            boneIndex = j;
        //            break;
        //        }
        //        weightStartIndex += bone->MNumWeights;
        //    }

        //    return weightStartIndex;
        //}

        //private static unsafe uint GetBones(
        //    Mesh* mesh,
        //    Dictionary<string, List<SceneNode>> nodeCache,
        //    Matrix4x4 invFullConv,
        //    Dictionary<TransformBase, Matrix4x4> invBindMatrices,
        //    uint boneCount,
        //    out Dictionary<string, TransformBase> boneRefs)
        //{
        //    boneRefs = [];
        //    uint totalWeights = 0u;
        //    for (uint i = 0; i < boneCount; ++i)
        //    {
        //        var bone = mesh->MBones[i];
        //        totalWeights += bone->MNumWeights;
        //        string name = bone->MName.ToString();

        //        if (!TryGetTransform(nodeCache, name, out TransformBase? transform))
        //            continue;

        //        boneRefs.Add(name, transform!);
        //        invBindMatrices.Add(transform!, invFullConv * bone->MOffsetMatrix.Transposed());
        //    }
        //    return totalWeights;
        //}

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
            if (PositionsBuffer is null || Triangles is null)
                return;

            _bvhTree = new(new TriangleAdapter(), Triangles.Select(GetTriangle).ToList());
            _generating = false;
        }

        [RequiresDynamicCode("Calls XREngine.Rendering.XRDataBuffer.Get<T>(UInt32)")]
        private Triangle GetTriangle(IndexTriangle indices)
        {
            Vector3 pos0 = PositionsBuffer!.Get<Vector3>((uint)indices.Point0 * 12u)!.Value;
            Vector3 pos1 = PositionsBuffer!.Get<Vector3>((uint)indices.Point1 * 12u)!.Value;
            Vector3 pos2 = PositionsBuffer!.Get<Vector3>((uint)indices.Point2 * 12u)!.Value;
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
            verticesBuffer.BindingName = "Vertices";
            XRDataBuffer indicesBuffer = GetIndexBuffer(EPrimitiveType.Triangles, out _, EBufferTarget.ShaderStorageBuffer)!;
            indicesBuffer.BindingName = "Indices";
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

            var data = new XRDataBuffer(target, true) { BindingName = type.ToString() };
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

    public class TriangleAdapter : ISSBVHNodeAdaptor<Triangle>
    {
        public BVH<Triangle>? BVH { get; private set; }

        public void SetBVH(BVH<Triangle> bvh)
            => BVH = bvh;

        private readonly Dictionary<Triangle, BVHNode<Triangle>> _triangleToLeaf = [];

        public void UnmapObject(Triangle obj)
            => _triangleToLeaf.Remove(obj);

        public void CheckMap(Triangle obj)
        {
            if (!_triangleToLeaf.ContainsKey(obj))
                throw new Exception("missing map for a shuffled child");
        }

        public BVHNode<Triangle>? GetLeaf(Triangle obj)
            => _triangleToLeaf.TryGetValue(obj, out BVHNode<Triangle>? leaf) ? leaf : null;

        public void MapObjectToBVHLeaf(Triangle obj, BVHNode<Triangle> leaf)
            => _triangleToLeaf.TryAdd(obj, leaf);

        public Vector3 ObjectPos(Triangle obj)
            => (obj.A + obj.B + obj.C) / 3.0f;

        public float Radius(Triangle obj)
        {
            //Calc center of triangle
            Vector3 center = ObjectPos(obj);
            //Calc distance to each vertex
            float distA = (center - obj.A).Length();
            float distB = (center - obj.B).Length();
            float distC = (center - obj.C).Length();
            //Return the max distance
            return Math.Max(distA, Math.Max(distB, distC));
        }
    }
}
