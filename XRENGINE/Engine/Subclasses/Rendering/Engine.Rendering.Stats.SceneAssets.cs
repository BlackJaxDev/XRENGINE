using System;
using System.Threading;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static class SceneAssets
                {
                    private const int MaxAssetRows = 32;
                    private static int _visibleRendererCount;
                    private static int _visibleSubmeshCount;
                    private static long _visibleTriangleCount;
                    private static int _materialSlotCount;
                    private static int _activeMaterialCount;
                    private static int _textureCount;
                    private static long _residentTextureMemoryBytes;
                    private static int _textureUploadJobs;
                    private static long _textureUploadBytes;
                    private static long _textureUploadTicks;
                    private static int _shaderVariantsRequested;
                    private static int _shaderVariantsWarming;
                    private static int _shaderVariantsLinked;
                    private static int _shaderVariantsFailed;
                    private static int _shaderVariantsLoadedFromDiskCache;
                    private static int _shaderVariantsGeneratedThisRun;
                    private static int _skinnedRendererCount;
                    private static long _boneMatrixUploadBytes;
                    private static long _blendshapeWeightUploadBytes;
                    private static int _skinningComputeDispatchCount;
                    private static int _blendshapeComputeDispatchCount;
                    private static int _avatarSourceMeshCount;
                    private static int _avatarOptimizedLodCount;
                    private static int _avatarMeshletCount;
                    private static int _avatarVisibilityBufferCount;
                    private static int _avatarClusterVirtualizedCount;
                    private static int _avatarOctahedralImpostorCount;
                    private static int _avatarGaussianSplatCount;

                    private static int _lastFrameVisibleRendererCount;
                    private static int _lastFrameVisibleSubmeshCount;
                    private static long _lastFrameVisibleTriangleCount;
                    private static int _lastFrameMaterialSlotCount;
                    private static int _lastFrameActiveMaterialCount;
                    private static int _lastFrameTextureCount;
                    private static long _lastFrameResidentTextureMemoryBytes;
                    private static int _lastFrameTextureUploadJobs;
                    private static long _lastFrameTextureUploadBytes;
                    private static long _lastFrameTextureUploadTicks;
                    private static int _lastFrameShaderVariantsRequested;
                    private static int _lastFrameShaderVariantsWarming;
                    private static int _lastFrameShaderVariantsLinked;
                    private static int _lastFrameShaderVariantsFailed;
                    private static int _lastFrameShaderVariantsLoadedFromDiskCache;
                    private static int _lastFrameShaderVariantsGeneratedThisRun;
                    private static int _lastFrameSkinnedRendererCount;
                    private static long _lastFrameBoneMatrixUploadBytes;
                    private static long _lastFrameBlendshapeWeightUploadBytes;
                    private static int _lastFrameSkinningComputeDispatchCount;
                    private static int _lastFrameBlendshapeComputeDispatchCount;
                    private static int _lastFrameAvatarSourceMeshCount;
                    private static int _lastFrameAvatarOptimizedLodCount;
                    private static int _lastFrameAvatarMeshletCount;
                    private static int _lastFrameAvatarVisibilityBufferCount;
                    private static int _lastFrameAvatarClusterVirtualizedCount;
                    private static int _lastFrameAvatarOctahedralImpostorCount;
                    private static int _lastFrameAvatarGaussianSplatCount;

                    private static readonly object _assetRowsLock = new();
                    private static readonly AssetCostAccumulator[] _assetRows = new AssetCostAccumulator[MaxAssetRows];
                    private static int _assetRowCount;
                    private static RenderAssetCostRow[] _lastFrameAssetRows = [];

                    public static int VisibleRendererCount => _lastFrameVisibleRendererCount;
                    public static int VisibleSubmeshCount => _lastFrameVisibleSubmeshCount;
                    public static long VisibleTriangleCount => _lastFrameVisibleTriangleCount;
                    public static int MaterialSlotCount => _lastFrameMaterialSlotCount;
                    public static int ActiveMaterialCount => _lastFrameActiveMaterialCount;
                    public static int TextureCount => _lastFrameTextureCount;
                    public static long ResidentTextureMemoryBytes => _lastFrameResidentTextureMemoryBytes;
                    public static int TextureUploadJobs => _lastFrameTextureUploadJobs;
                    public static long TextureUploadBytes => _lastFrameTextureUploadBytes;
                    public static double TextureUploadMs => TimeSpan.FromTicks(_lastFrameTextureUploadTicks).TotalMilliseconds;
                    public static int ShaderVariantsRequested => _lastFrameShaderVariantsRequested;
                    public static int ShaderVariantsWarming => _lastFrameShaderVariantsWarming;
                    public static int ShaderVariantsLinked => _lastFrameShaderVariantsLinked;
                    public static int ShaderVariantsFailed => _lastFrameShaderVariantsFailed;
                    public static int ShaderVariantsLoadedFromDiskCache => _lastFrameShaderVariantsLoadedFromDiskCache;
                    public static int ShaderVariantsGeneratedThisRun => _lastFrameShaderVariantsGeneratedThisRun;
                    public static int SkinnedRendererCount => _lastFrameSkinnedRendererCount;
                    public static long BoneMatrixUploadBytes => _lastFrameBoneMatrixUploadBytes;
                    public static long BlendshapeWeightUploadBytes => _lastFrameBlendshapeWeightUploadBytes;
                    public static int SkinningComputeDispatchCount => _lastFrameSkinningComputeDispatchCount;
                    public static int BlendshapeComputeDispatchCount => _lastFrameBlendshapeComputeDispatchCount;
                    public static int AvatarSourceMeshCount => _lastFrameAvatarSourceMeshCount;
                    public static int AvatarOptimizedLodCount => _lastFrameAvatarOptimizedLodCount;
                    public static int AvatarMeshletCount => _lastFrameAvatarMeshletCount;
                    public static int AvatarVisibilityBufferCount => _lastFrameAvatarVisibilityBufferCount;
                    public static int AvatarClusterVirtualizedCount => _lastFrameAvatarClusterVirtualizedCount;
                    public static int AvatarOctahedralImpostorCount => _lastFrameAvatarOctahedralImpostorCount;
                    public static int AvatarGaussianSplatCount => _lastFrameAvatarGaussianSplatCount;

                    internal static void SnapshotAndReset()
                    {
                        _lastFrameVisibleRendererCount = Interlocked.Exchange(ref _visibleRendererCount, 0);
                        _lastFrameVisibleSubmeshCount = Interlocked.Exchange(ref _visibleSubmeshCount, 0);
                        _lastFrameVisibleTriangleCount = Interlocked.Exchange(ref _visibleTriangleCount, 0);
                        _lastFrameMaterialSlotCount = Interlocked.Exchange(ref _materialSlotCount, 0);
                        _lastFrameActiveMaterialCount = Interlocked.Exchange(ref _activeMaterialCount, 0);
                        _lastFrameTextureCount = Interlocked.Exchange(ref _textureCount, 0);
                        long residentTextureMemoryBytes = Interlocked.Exchange(ref _residentTextureMemoryBytes, 0);
                        _lastFrameResidentTextureMemoryBytes = residentTextureMemoryBytes > 0
                            ? residentTextureMemoryBytes
                            : Vram.AllocatedTextureBytes;
                        _lastFrameTextureUploadJobs = Interlocked.Exchange(ref _textureUploadJobs, 0);
                        _lastFrameTextureUploadBytes = Interlocked.Exchange(ref _textureUploadBytes, 0);
                        _lastFrameTextureUploadTicks = Interlocked.Exchange(ref _textureUploadTicks, 0);
                        _lastFrameShaderVariantsRequested = Interlocked.Exchange(ref _shaderVariantsRequested, 0);
                        _lastFrameShaderVariantsWarming = Interlocked.Exchange(ref _shaderVariantsWarming, 0);
                        _lastFrameShaderVariantsLinked = Interlocked.Exchange(ref _shaderVariantsLinked, 0);
                        _lastFrameShaderVariantsFailed = Interlocked.Exchange(ref _shaderVariantsFailed, 0);
                        _lastFrameShaderVariantsLoadedFromDiskCache = Interlocked.Exchange(ref _shaderVariantsLoadedFromDiskCache, 0);
                        _lastFrameShaderVariantsGeneratedThisRun = Interlocked.Exchange(ref _shaderVariantsGeneratedThisRun, 0);
                        _lastFrameSkinnedRendererCount = Interlocked.Exchange(ref _skinnedRendererCount, 0);
                        _lastFrameBoneMatrixUploadBytes = Interlocked.Exchange(ref _boneMatrixUploadBytes, 0);
                        _lastFrameBlendshapeWeightUploadBytes = Interlocked.Exchange(ref _blendshapeWeightUploadBytes, 0);
                        _lastFrameSkinningComputeDispatchCount = Interlocked.Exchange(ref _skinningComputeDispatchCount, 0);
                        _lastFrameBlendshapeComputeDispatchCount = Interlocked.Exchange(ref _blendshapeComputeDispatchCount, 0);
                        _lastFrameAvatarSourceMeshCount = Interlocked.Exchange(ref _avatarSourceMeshCount, 0);
                        _lastFrameAvatarOptimizedLodCount = Interlocked.Exchange(ref _avatarOptimizedLodCount, 0);
                        _lastFrameAvatarMeshletCount = Interlocked.Exchange(ref _avatarMeshletCount, 0);
                        _lastFrameAvatarVisibilityBufferCount = Interlocked.Exchange(ref _avatarVisibilityBufferCount, 0);
                        _lastFrameAvatarClusterVirtualizedCount = Interlocked.Exchange(ref _avatarClusterVirtualizedCount, 0);
                        _lastFrameAvatarOctahedralImpostorCount = Interlocked.Exchange(ref _avatarOctahedralImpostorCount, 0);
                        _lastFrameAvatarGaussianSplatCount = Interlocked.Exchange(ref _avatarGaussianSplatCount, 0);

                        lock (_assetRowsLock)
                        {
                            if (_assetRowCount == 0)
                            {
                                _lastFrameAssetRows = [];
                            }
                            else
                            {
                                RenderAssetCostRow[] rows = new RenderAssetCostRow[_assetRowCount];
                                for (int i = 0; i < _assetRowCount; i++)
                                    rows[i] = _assetRows[i].ToRow();
                                _lastFrameAssetRows = rows;
                            }

                            _assetRowCount = 0;
                        }
                    }

                    public static RenderAssetCostRow[] GetAssetCostRows()
                    {
                        RenderAssetCostRow[] rows = _lastFrameAssetRows;
                        if (rows.Length == 0)
                            return [];

                        RenderAssetCostRow[] copy = new RenderAssetCostRow[rows.Length];
                        Array.Copy(rows, copy, rows.Length);
                        return copy;
                    }

                    public static void RecordVisibleRenderer(
                        string? sourceAssetIdentity,
                        string? cookedVariantIdentity,
                        string? meshName,
                        string? materialName,
                        int materialSlots,
                        int textureCount,
                        long triangleCount,
                        bool skinned,
                        string? representation)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Increment(ref _visibleRendererCount);
                        Interlocked.Increment(ref _visibleSubmeshCount);
                        if (triangleCount > 0)
                            Interlocked.Add(ref _visibleTriangleCount, triangleCount);
                        if (materialSlots > 0)
                            Interlocked.Add(ref _materialSlotCount, materialSlots);
                        if (!string.IsNullOrWhiteSpace(materialName))
                            Interlocked.Increment(ref _activeMaterialCount);
                        if (textureCount > 0)
                            Interlocked.Add(ref _textureCount, textureCount);
                        if (skinned)
                            Interlocked.Increment(ref _skinnedRendererCount);
                        RecordAvatarRepresentation(representation, 1);

                        lock (_assetRowsLock)
                            AddOrUpdateAssetRowNoLock(sourceAssetIdentity, cookedVariantIdentity, meshName, materialName, representation, materialSlots, textureCount, triangleCount, skinned);
                    }

                    public static void RecordResidentTextureMemory(long bytes)
                    {
                        if (!EnableTracking || bytes <= 0)
                            return;

                        Interlocked.Add(ref _residentTextureMemoryBytes, bytes);
                    }

                    public static void RecordTextureUpload(long bytes, TimeSpan elapsed)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Increment(ref _textureUploadJobs);
                        if (bytes > 0)
                            Interlocked.Add(ref _textureUploadBytes, bytes);
                        if (elapsed.Ticks > 0)
                            Interlocked.Add(ref _textureUploadTicks, elapsed.Ticks);
                    }

                    public static void RecordShaderVariant(bool requested = false, bool warming = false, bool linked = false, bool failed = false, bool loadedFromDiskCache = false, bool generatedThisRun = false)
                    {
                        if (!EnableTracking)
                            return;

                        if (requested)
                            Interlocked.Increment(ref _shaderVariantsRequested);
                        if (warming)
                            Interlocked.Increment(ref _shaderVariantsWarming);
                        if (linked)
                            Interlocked.Increment(ref _shaderVariantsLinked);
                        if (failed)
                            Interlocked.Increment(ref _shaderVariantsFailed);
                        if (loadedFromDiskCache)
                            Interlocked.Increment(ref _shaderVariantsLoadedFromDiskCache);
                        if (generatedThisRun)
                            Interlocked.Increment(ref _shaderVariantsGeneratedThisRun);
                    }

                    public static void RecordSkinningUpload(long boneMatrixBytes, long blendshapeWeightBytes, int skinningDispatches = 0, int blendshapeDispatches = 0)
                    {
                        if (!EnableTracking)
                            return;

                        if (boneMatrixBytes > 0)
                            Interlocked.Add(ref _boneMatrixUploadBytes, boneMatrixBytes);
                        if (blendshapeWeightBytes > 0)
                            Interlocked.Add(ref _blendshapeWeightUploadBytes, blendshapeWeightBytes);
                        if (skinningDispatches > 0)
                            Interlocked.Add(ref _skinningComputeDispatchCount, skinningDispatches);
                        if (blendshapeDispatches > 0)
                            Interlocked.Add(ref _blendshapeComputeDispatchCount, blendshapeDispatches);
                    }

                    public static void RecordAvatarRepresentation(string? representation, int count)
                    {
                        if (!EnableTracking || count <= 0 || string.IsNullOrWhiteSpace(representation))
                            return;

                        if (representation!.Equals("optimized_lod", StringComparison.OrdinalIgnoreCase))
                            Interlocked.Add(ref _avatarOptimizedLodCount, count);
                        else if (representation.Equals("meshlet", StringComparison.OrdinalIgnoreCase))
                            Interlocked.Add(ref _avatarMeshletCount, count);
                        else if (representation.Equals("visibility_buffer", StringComparison.OrdinalIgnoreCase))
                            Interlocked.Add(ref _avatarVisibilityBufferCount, count);
                        else if (representation.Equals("cluster_virtualized", StringComparison.OrdinalIgnoreCase))
                            Interlocked.Add(ref _avatarClusterVirtualizedCount, count);
                        else if (representation.Equals("octahedral_impostor", StringComparison.OrdinalIgnoreCase))
                            Interlocked.Add(ref _avatarOctahedralImpostorCount, count);
                        else if (representation.Equals("gaussian_splat", StringComparison.OrdinalIgnoreCase))
                            Interlocked.Add(ref _avatarGaussianSplatCount, count);
                        else
                            Interlocked.Add(ref _avatarSourceMeshCount, count);
                    }

                    private static void AddOrUpdateAssetRowNoLock(
                        string? sourceAssetIdentity,
                        string? cookedVariantIdentity,
                        string? meshName,
                        string? materialName,
                        string? representation,
                        int materialSlots,
                        int textureCount,
                        long triangles,
                        bool skinned)
                    {
                        string source = NormalizeIdentity(sourceAssetIdentity, meshName);
                        string cooked = string.IsNullOrWhiteSpace(cookedVariantIdentity) ? source : cookedVariantIdentity!;
                        string material = string.IsNullOrWhiteSpace(materialName) ? "<none>" : materialName!;

                        for (int i = 0; i < _assetRowCount; i++)
                        {
                            if (_assetRows[i].Matches(source, cooked, material))
                            {
                                _assetRows[i].Add(materialSlots, textureCount, triangles, skinned);
                                return;
                            }
                        }

                        if (_assetRowCount >= MaxAssetRows)
                        {
                            _assetRows[MaxAssetRows - 1].Add(materialSlots, textureCount, triangles, skinned);
                            return;
                        }

                        _assetRows[_assetRowCount++] = new AssetCostAccumulator(
                            source,
                            cooked,
                            string.IsNullOrWhiteSpace(meshName) ? "<unknown mesh>" : meshName!,
                            material,
                            string.IsNullOrWhiteSpace(representation) ? "source_mesh" : representation!,
                            materialSlots,
                            textureCount,
                            triangles,
                            skinned);
                    }

                    private static string NormalizeIdentity(string? identity, string? fallback)
                        => string.IsNullOrWhiteSpace(identity)
                            ? string.IsNullOrWhiteSpace(fallback) ? "<unknown asset>" : fallback!
                            : identity!;

                    private struct AssetCostAccumulator
                    {
                        private readonly string _source;
                        private readonly string _cooked;
                        private readonly string _mesh;
                        private readonly string _material;
                        private readonly string _representation;
                        private int _draws;
                        private long _triangles;
                        private int _materialSlots;
                        private int _textures;
                        private int _skinnedDraws;

                        public AssetCostAccumulator(
                            string source,
                            string cooked,
                            string mesh,
                            string material,
                            string representation,
                            int materialSlots,
                            int textures,
                            long triangles,
                            bool skinned)
                        {
                            _source = source;
                            _cooked = cooked;
                            _mesh = mesh;
                            _material = material;
                            _representation = representation;
                            _draws = 1;
                            _triangles = Math.Max(0, triangles);
                            _materialSlots = Math.Max(0, materialSlots);
                            _textures = Math.Max(0, textures);
                            _skinnedDraws = skinned ? 1 : 0;
                        }

                        public bool Matches(string source, string cooked, string material)
                            => string.Equals(_source, source, StringComparison.Ordinal) &&
                               string.Equals(_cooked, cooked, StringComparison.Ordinal) &&
                               string.Equals(_material, material, StringComparison.Ordinal);

                        public void Add(int materialSlots, int textures, long triangles, bool skinned)
                        {
                            _draws++;
                            _triangles += Math.Max(0, triangles);
                            _materialSlots += Math.Max(0, materialSlots);
                            _textures += Math.Max(0, textures);
                            if (skinned)
                                _skinnedDraws++;
                        }

                        public RenderAssetCostRow ToRow()
                            => new(
                                _source,
                                _cooked,
                                _mesh,
                                _material,
                                _representation,
                                _draws,
                                _triangles,
                                _materialSlots,
                                _textures,
                                _skinnedDraws);
                    }
                }

                public readonly record struct RenderAssetCostRow(
                    string SourceAssetIdentity,
                    string CookedVariantIdentity,
                    string MeshName,
                    string MaterialName,
                    string Representation,
                    int DrawCalls,
                    long Triangles,
                    int MaterialSlots,
                    int TextureCount,
                    int SkinnedDraws);
            }
        }
    }
}
