// =====================================================================================
// GPUScene.MeshAtlas.cs - Mesh atlas state, nested types, and read-only properties.
// Part of the GPUScene partial class. See GPUScene.cs for the canonical class summary.
// =====================================================================================

using XREngine.Extensions;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Transforms;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands
{
    public partial class GPUScene
    {

        // -------------------------------------------------------------------------
        // Mesh Atlas: Consolidated vertex + index data for all meshes referenced by commands.
        // Meshes are linearly appended; future optimization could bin by vertex format/material.
        // -------------------------------------------------------------------------

        public const uint MeshDataFlagAtlasTierMask = 0x3u;
        public const int MaxLogicalMeshLodCount = 4;
        public const int LODTableEntryFloatCount = 12;
        private const uint MinLodTableEntries = 16;
        private const uint MinLodRequestEntries = 16;

        [StructLayout(LayoutKind.Sequential)]
        public struct LODTableEntry
        {
            public uint LODCount;
            public uint LOD0_MeshDataID;
            public uint LOD1_MeshDataID;
            public uint LOD2_MeshDataID;
            public uint LOD3_MeshDataID;
            public float LOD0_MinProjectedRadiusPixels;
            public float LOD1_MinProjectedRadiusPixels;
            public float LOD2_MinProjectedRadiusPixels;
            public float LOD3_MinProjectedRadiusPixels;
            public float Padding0;
            public float Padding1;
            public float Padding2;

            public readonly uint GetMeshDataId(int lodLevel)
                => lodLevel switch
                {
                    0 => LOD0_MeshDataID,
                    1 => LOD1_MeshDataID,
                    2 => LOD2_MeshDataID,
                    3 => LOD3_MeshDataID,
                    _ => 0,
                };

            public readonly float GetMinProjectedRadiusPixels(int lodLevel)
                => lodLevel switch
                {
                    0 => LOD0_MinProjectedRadiusPixels,
                    1 => LOD1_MinProjectedRadiusPixels,
                    2 => LOD2_MinProjectedRadiusPixels,
                    3 => LOD3_MinProjectedRadiusPixels,
                    _ => 0.0f,
                };
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GpuMeshletRange
        {
            public uint MeshletOffset;
            public uint MeshletCount;
            public uint VertexIndexOffset;
            public uint TriangleIndexOffset;

            public readonly bool HasMeshlets => MeshletCount != 0u;

            public readonly bool RequiresTraditionalIndirectFallback => MeshletCount == 0u;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GpuMeshletDescriptor
        {
            public Vector4 BoundsSphere;
            public uint VertexOffset;
            public uint TriangleByteOffset;
            public uint VertexCount;
            public uint TriangleCount;
            public Vector4 Cone;
            public Vector4 ConeApex;
            public uint PackedCone;
            public uint Reserved0;
            public uint Reserved1;
            public uint Reserved2;
        }

        private readonly struct AtlasAllocation(int firstVertex, int firstIndex, int vertexCount, int indexCount, int reservedVertexCount, int reservedIndexCount)
        {
            public int FirstVertex { get; } = firstVertex;
            public int FirstIndex { get; } = firstIndex;
            public int VertexCount { get; } = vertexCount;
            public int IndexCount { get; } = indexCount;
            public int ReservedVertexCount { get; } = reservedVertexCount;
            public int ReservedIndexCount { get; } = reservedIndexCount;
        }

        private sealed class LogicalMeshState(uint logicalMeshId, uint submeshIndex)
        {
            public uint LogicalMeshId { get; } = logicalMeshId;
            public uint SubmeshIndex { get; } = submeshIndex;
            public string DebugLabel { get; set; } = $"LogicalMesh_{logicalMeshId}";
            public int ReferenceCount;
            public readonly uint[] MeshIds = new uint[MaxLogicalMeshLodCount];
            public readonly XRMesh?[] Meshes = new XRMesh?[MaxLogicalMeshLodCount];
            public readonly float[] MinProjectedRadiusPixels = new float[MaxLogicalMeshLodCount];
            public uint LODCount;

            public LODTableEntry ToEntry()
                => new()
                {
                    LODCount = LODCount,
                    LOD0_MeshDataID = MeshIds[0],
                    LOD1_MeshDataID = MeshIds[1],
                    LOD2_MeshDataID = MeshIds[2],
                    LOD3_MeshDataID = MeshIds[3],
                    LOD0_MinProjectedRadiusPixels = MinProjectedRadiusPixels[0],
                    LOD1_MinProjectedRadiusPixels = MinProjectedRadiusPixels[1],
                    LOD2_MinProjectedRadiusPixels = MinProjectedRadiusPixels[2],
                    LOD3_MinProjectedRadiusPixels = MinProjectedRadiusPixels[3],
                };
        }

        public readonly struct StreamingWritePointers(
            VoidPtr positions,
            VoidPtr normals,
            VoidPtr tangents,
            VoidPtr uv0,
            VoidPtr indices,
            int maxVertexCount,
            int maxIndexCount)
        {
            public VoidPtr Positions { get; } = positions;
            public VoidPtr Normals { get; } = normals;
            public VoidPtr Tangents { get; } = tangents;
            public VoidPtr UV0 { get; } = uv0;
            public VoidPtr Indices { get; } = indices;
            public int MaxVertexCount { get; } = maxVertexCount;
            public int MaxIndexCount { get; } = maxIndexCount;
        }

        private sealed class AtlasTierState(EAtlasTier tier)
        {
            public EAtlasTier Tier { get; } = tier;
            public XRDataBuffer? Positions;
            public XRDataBuffer? Normals;
            public XRDataBuffer? Tangents;
            public XRDataBuffer? UV0;
            public XRDataBuffer? Indices;
            public bool Dirty;
            public int VertexCount;
            public int IndexCount;
            public uint Version;
            public IndexSize IndexElementSize = IndexSize.FourBytes;
            public readonly Dictionary<XRMesh, AtlasAllocation> MeshOffsets = [];
            public readonly List<IndexTriangle> IndirectFaceIndices = [];

            // Tracks how much of the client-side atlas has been pushed to the GPU,
            // so RebuildAtlasIfDirty can issue subrange PushSubData calls instead of
            // re-uploading the entire SoA arrays on every rebuild.
            // Invalidated (reset to 0) whenever the underlying GPU buffer must be
            // fully re-allocated (Resize beyond current GPU capacity).
            public int LastUploadedVertexCount;
            public int LastUploadedIndexCount;
        }

        private readonly AtlasTierState _staticAtlas = new(EAtlasTier.Static);
        private readonly AtlasTierState _dynamicAtlas = new(EAtlasTier.Dynamic);
        private readonly AtlasTierState[] _streamingAtlases =
        [
            new AtlasTierState(EAtlasTier.Streaming),
            new AtlasTierState(EAtlasTier.Streaming),
            new AtlasTierState(EAtlasTier.Streaming),
        ];

        private readonly Dictionary<XRMesh, EAtlasTier> _activeAtlasTiers = [];
        private readonly Dictionary<XRMesh, (int maxVertexCount, int maxIndexCount)> _streamingReservations = [];
        private int _streamingWriteSlot;
        private int _streamingRenderSlot;

        /// <summary>Atlas buffer containing position vec3 data for all meshes.</summary>
        private XRDataBuffer? _atlasPositions;

        /// <summary>Atlas buffer containing normal vec3 data for all meshes.</summary>
        private XRDataBuffer? _atlasNormals;

        /// <summary>Atlas buffer containing tangent vec4 data for all meshes.</summary>
        private XRDataBuffer? _atlasTangents;

        /// <summary>Atlas buffer containing UV0 vec2 data for all meshes.</summary>
        private XRDataBuffer? _atlasUV0;

        /// <summary>Atlas buffer containing element indices.</summary>
        private XRDataBuffer? _atlasIndices;

        /// <summary>Flag indicating atlas needs rebuild after adds/removes.</summary>
        private bool _atlasDirty = false;

        /// <summary>Running vertex count for atlas packing.</summary>
        private int _atlasVertexCount = 0;

        /// <summary>Running index count for atlas packing.</summary>
        private int _atlasIndexCount = 0;

        /// <summary>Maps each mesh to its offsets within the atlas (firstVertex, firstIndex, indexCount).</summary>
        private readonly Dictionary<XRMesh, (int firstVertex, int firstIndex, int indexCount)> _atlasMeshOffsets = [];

        /// <summary>
        /// Tracks how many active GPU commands reference each mesh resident in the atlas.
        /// Atlas geometry is only removed when this reaches zero.
        /// </summary>
        private readonly Dictionary<XRMesh, int> _atlasMeshRefCounts = [];

        /// <summary>Version number incremented on each atlas rebuild for change detection.</summary>
        private uint _atlasVersion = 0;

        /// <summary>Current index element type (u8/u16/u32) used in atlas index buffer.</summary>
        private IndexSize _atlasIndexElementSize = IndexSize.FourBytes;

        /// <summary>List of triangle indices for indirect rendering.</summary>
        private readonly List<IndexTriangle> _indirectFaceIndices = [];

        /// <summary>
        /// Raised after the atlas GPU buffers have been rebuilt (EBO updated).
        /// Listeners should re-bind their VAO index buffers.
        /// </summary>
        public event Action<GPUScene>? AtlasRebuilt;

        /// <summary>Gets the list of triangle indices for indirect rendering.</summary>
        public List<IndexTriangle> IndirectFaceIndices => _indirectFaceIndices;

        public IReadOnlyList<IndexTriangle> GetIndirectFaceIndices(EAtlasTier tier)
            => GetTierState(tier).IndirectFaceIndices;

        /// <summary>Gets the current total vertex count in the atlas.</summary>
        public int AtlasVertexCount => _atlasVertexCount;

        public int GetAtlasVertexCount(EAtlasTier tier)
            => GetTierState(tier).VertexCount;

        /// <summary>Gets the current total index count in the atlas.</summary>
        public int AtlasIndexCount => _atlasIndexCount;

        public int GetAtlasIndexCount(EAtlasTier tier)
            => GetTierState(tier).IndexCount;

        /// <summary>Gets the version number incremented on each atlas rebuild. Use for change detection.</summary>
        public uint AtlasVersion => _atlasVersion;

        public uint GetAtlasVersion(EAtlasTier tier)
            => GetTierState(tier).Version;

        /// <summary>Gets the index element size used in the atlas index buffer (u8, u16, or u32).</summary>
        public IndexSize AtlasIndexElementSize => _atlasIndexElementSize;

        public IndexSize GetAtlasIndexElementSize(EAtlasTier tier)
            => GetTierState(tier).IndexElementSize;

        /// <summary>Gets the atlas buffer containing position data.</summary>
        public XRDataBuffer? AtlasPositions => _atlasPositions;

        public XRDataBuffer? GetAtlasPositions(EAtlasTier tier)
            => GetTierState(tier).Positions;

        /// <summary>Gets the atlas buffer containing normal data.</summary>
        public XRDataBuffer? AtlasNormals => _atlasNormals;

        public XRDataBuffer? GetAtlasNormals(EAtlasTier tier)
            => GetTierState(tier).Normals;

        /// <summary>Gets the atlas buffer containing tangent data.</summary>
        public XRDataBuffer? AtlasTangents => _atlasTangents;

        public XRDataBuffer? GetAtlasTangents(EAtlasTier tier)
            => GetTierState(tier).Tangents;

        /// <summary>Gets the atlas buffer containing UV0 data.</summary>
        public XRDataBuffer? AtlasUV0 => _atlasUV0;

        public XRDataBuffer? GetAtlasUV0(EAtlasTier tier)
            => GetTierState(tier).UV0;

        /// <summary>Gets the atlas buffer containing index data.</summary>
        public XRDataBuffer? AtlasIndices => _atlasIndices;

        public XRDataBuffer? GetAtlasIndices(EAtlasTier tier)
            => GetTierState(tier).Indices;

        public EAtlasTier GetActiveAtlasTier(uint meshID)
        {
            if (meshID != 0 && _idToMesh.TryGetValue(meshID, out var mesh) && mesh is not null && _activeAtlasTiers.TryGetValue(mesh, out var tier))
                return tier;

            return EAtlasTier.Dynamic;
        }

        private AtlasTierState GetTierState(EAtlasTier tier)
            => tier switch
            {
                EAtlasTier.Static => _staticAtlas,
                EAtlasTier.Dynamic => _dynamicAtlas,
                EAtlasTier.Streaming => _streamingAtlases[_streamingRenderSlot],
                _ => _dynamicAtlas,
            };

        private AtlasTierState GetStreamingWriteTierState()
            => _streamingAtlases[_streamingWriteSlot];

        private static uint ComposeMeshDataFlags(EAtlasTier tier)
            => (uint)tier & MeshDataFlagAtlasTierMask;

    }
}
