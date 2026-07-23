using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Prefabs;

namespace XREngine;

internal enum SnapshotAssetSerializationMode
{
    Inline,
    Reference,
}

internal static class SnapshotDiagnostics
{
    private const string AuxiliaryLogName = "playmode-snapshot-diagnostics.log";
    private static long _nextScopeId;
    private static readonly AsyncLocal<Session?> CurrentSession = new();

    public static IDisposable BeginScope(string operation, XRWorld? world)
    {
        Session? previous = CurrentSession.Value;
        Session session = new(Interlocked.Increment(ref _nextScopeId), operation, world?.Name);
        CurrentSession.Value = session;

        Log($"Begin {operation}. World={world?.Name ?? "<null>"} Scenes={world?.Scenes.Count ?? 0}");
        return new Scope(previous, session);
    }

    public static void Log(string message, ELogCategory category = ELogCategory.Rendering, bool mirrorToCategoryLog = true)
    {
        string prefix = GetPrefix();
        string line = $"{prefix} {message}";

        if (mirrorToCategoryLog)
            Debug.Log(category, EOutputVerbosity.Verbose, debugOnly: false, line);

        Debug.WriteAuxiliaryLog(AuxiliaryLogName, $"{DateTimeOffset.Now:O} {line}");
    }

    public static void Warning(string message, ELogCategory category = ELogCategory.Rendering)
    {
        string line = $"{GetPrefix()} [WARN] {message}";
        Debug.LogWarning(category, line);
        Debug.WriteAuxiliaryLog(AuxiliaryLogName, $"{DateTimeOffset.Now:O} {line}");
    }

    public static void LogScenePayload(string action, XRScene scene, int payloadLength)
        => Log(
            $"{action} scene '{scene.Name ?? "<unnamed>"}' key='{GetSceneDisplayKey(scene)}' payloadBytes={payloadLength} roots={scene.RootNodes?.Count ?? 0}",
            ELogCategory.Rendering);

    public static void LogAssetSerializationDecision(XRAsset asset, SnapshotAssetSerializationMode mode, string reason)
    {
        Session? session = CurrentSession.Value;
        string key = $"{mode}:{asset.ID}:{RuntimeHelpers.GetHashCode(asset)}";
        if (session is not null && !session.AssetSerializationDecisions.Add(key))
            return;

        if (session is not null)
        {
            if (mode == SnapshotAssetSerializationMode.Inline)
                session.InlinedAssetCount++;
            else
                session.ReferencedAssetCount++;
        }

        Log(
            $"Asset serialize {mode}: reason='{reason}' {DescribeAsset(asset)} cache=({DescribeAssetCacheState(asset)}) data=({DescribeAssetData(asset)})",
            ClassifyAssetCategory(asset),
            mirrorToCategoryLog: mode == SnapshotAssetSerializationMode.Reference || IsRenderAsset(asset));
    }

    public static void LogAssetReferenceCreated(SnapshotAssetReference reference, XRAsset sourceAsset)
    {
        Log(
            $"Asset reference created: {DescribeReference(reference)} from {DescribeAsset(sourceAsset)} data=({DescribeAssetData(sourceAsset)})",
            ClassifyAssetCategory(sourceAsset),
            mirrorToCategoryLog: IsRenderAsset(sourceAsset));
    }

    public static void LogAssetResolveStart(SnapshotAssetReference reference)
    {
        if (CurrentSession.Value is { } session)
            session.AssetResolveAttempts++;
        Log($"Asset resolve start: {DescribeReference(reference)}", ELogCategory.Assets);
    }

    public static void LogAssetResolveAttempt(SnapshotAssetReference reference, string route, XRAsset? resolved, string? detail = null)
    {
        if (resolved is not null && CurrentSession.Value is { } session)
            session.ResolvedAssetCount++;

        string result = resolved is null
            ? "miss"
            : $"hit {DescribeAsset(resolved)} data=({DescribeAssetData(resolved)})";

        Log(
            $"Asset resolve {route}: {result}; ref=({DescribeReference(reference)}){(string.IsNullOrWhiteSpace(detail) ? string.Empty : $" detail='{detail}'")}",
            resolved is null ? ELogCategory.Assets : ClassifyAssetCategory(resolved),
            mirrorToCategoryLog: resolved is null || IsRenderAsset(resolved));
    }

    public static void LogAssetResolveFailure(SnapshotAssetReference reference, string reason)
    {
        if (CurrentSession.Value is { } session)
            session.FailedAssetResolveCount++;
        Warning($"Asset resolve failed: reason='{reason}' ref=({DescribeReference(reference)})", ELogCategory.Assets);
    }

    public static void LogWorldAssetSummary(XRWorld world, string phase)
    {
        WorldAssetSummary summary = new();
        Log($"Asset summary begin ({phase}). World={world.Name ?? "<unnamed>"} Scenes={world.Scenes.Count}", mirrorToCategoryLog: false);

        for (int sceneIndex = 0; sceneIndex < world.Scenes.Count; sceneIndex++)
        {
            XRScene scene = world.Scenes[sceneIndex];
            if (scene.RootNodes is null)
                continue;

            for (int rootIndex = 0; rootIndex < scene.RootNodes.Count; rootIndex++)
            {
                SceneNode? root = scene.RootNodes[rootIndex];
                if (root is null)
                    continue;

                foreach (SceneNode node in SceneNodePrefabUtility.EnumerateHierarchy(root))
                {
                    summary.NodeCount++;
                    SummarizeNodeAssets(phase, sceneIndex, rootIndex, node, summary);
                }
            }
        }

        Log(
            $"Asset summary ({phase}): nodes={summary.NodeCount} modelComponents={summary.ModelComponentCount} " +
            $"missingModels={summary.MissingModelCount} sourceModels={summary.ModelCount} subMeshes={summary.SubMeshCount} lods={summary.LodCount} " +
            $"sourceMeshes={summary.MeshCount} sourceMeshBuffers={summary.MeshBufferCount} sourceMeshBufferBytes={summary.MeshBufferBytes} " +
            $"materials={summary.MaterialCount} textures={summary.TextureCount} textureMipBytes={summary.TextureMipmapBytes} " +
            $"runtimeRenderableComponents={summary.RenderableComponentCount} runtimeRenderableMeshes={summary.RuntimeRenderableMeshCount} " +
            $"runtimeRenderInfos={summary.RuntimeRenderInfoCount} runtimeMeshes={summary.RuntimeMeshCount} runtimeMeshBuffers={summary.RuntimeMeshBufferCount} " +
            $"runtimeMeshBufferBytes={summary.RuntimeMeshBufferBytes}",
            ELogCategory.Rendering);
    }

    public static void LogWorldInstanceAssetSummary(XRWorldInstance worldInstance, string phase)
    {
        WorldAssetSummary summary = new();
        Log(
            $"WorldInstance asset summary begin ({phase}). World={worldInstance.TargetWorld?.Name ?? "<unknown>"} Roots={worldInstance.RootNodes.Count}",
            mirrorToCategoryLog: false);

        for (int rootIndex = 0; rootIndex < worldInstance.RootNodes.Count; rootIndex++)
        {
            SceneNode? root = worldInstance.RootNodes[rootIndex];
            if (root is null)
                continue;

            foreach (SceneNode node in SceneNodePrefabUtility.EnumerateHierarchy(root))
            {
                summary.NodeCount++;
                SummarizeNodeAssets(phase, sceneIndex: -1, rootIndex, node, summary);
            }
        }

        Log(
            $"WorldInstance asset summary ({phase}): nodes={summary.NodeCount} modelComponents={summary.ModelComponentCount} " +
            $"missingModels={summary.MissingModelCount} sourceModels={summary.ModelCount} subMeshes={summary.SubMeshCount} lods={summary.LodCount} " +
            $"sourceMeshes={summary.MeshCount} sourceMeshBuffers={summary.MeshBufferCount} sourceMeshBufferBytes={summary.MeshBufferBytes} " +
            $"materials={summary.MaterialCount} textures={summary.TextureCount} textureMipBytes={summary.TextureMipmapBytes} " +
            $"runtimeRenderableComponents={summary.RenderableComponentCount} runtimeRenderableMeshes={summary.RuntimeRenderableMeshCount} " +
            $"runtimeRenderInfos={summary.RuntimeRenderInfoCount} runtimeMeshes={summary.RuntimeMeshCount} runtimeMeshBuffers={summary.RuntimeMeshBufferCount} " +
            $"runtimeMeshBufferBytes={summary.RuntimeMeshBufferBytes}",
            ELogCategory.Rendering);
    }

    public static string DescribeAsset(XRAsset asset)
    {
        return $"type={asset.GetType().FullName ?? asset.GetType().Name} name='{asset.Name ?? "<unnamed>"}' " +
            $"id={asset.ID} hash={RuntimeHelpers.GetHashCode(asset)} file='{ShortPath(asset.FilePath)}' original='{ShortPath(asset.OriginalPath)}'";
    }

    public static string DescribeReference(SnapshotAssetReference reference)
        => $"id={reference.AssetId} name='{reference.AssetName ?? "<unnamed>"}' type='{reference.AssetType ?? "<null>"}' path='{ShortPath(reference.AssetPath)}'";

    private static void SummarizeNodeAssets(string phase, int sceneIndex, int rootIndex, SceneNode node, WorldAssetSummary summary)
    {
        string nodePath = BuildNodePath(node);

        foreach (var component in node.Components)
        {
            if (component is RenderableComponent renderableComponent)
            {
                summary.RenderableComponentCount++;
                summary.RuntimeRenderableMeshCount += renderableComponent.Meshes.Count;
                summary.RuntimeRenderInfoCount += renderableComponent.RenderedObjects.Length;

                for (int i = 0; i < renderableComponent.Meshes.Count; i++)
                {
                    RenderableMesh renderable = renderableComponent.Meshes[i];
                    XRMesh? runtimeMesh = renderable.CurrentLODMesh;
                    if (runtimeMesh is null)
                        continue;

                    summary.RuntimeMeshCount++;
                    BufferStats stats = GetMeshBufferStats(runtimeMesh);
                    summary.RuntimeMeshBufferCount += stats.Count;
                    summary.RuntimeMeshBufferBytes += stats.Bytes;
                }
            }

            if (component is not ModelComponent modelComponent)
                continue;

            summary.ModelComponentCount++;
            string location = sceneIndex >= 0
                ? $"scene={sceneIndex} root={rootIndex} node='{nodePath}'"
                : $"worldRoot={rootIndex} node='{nodePath}'";

            if (modelComponent.Model is null)
            {
                summary.MissingModelCount++;
                Warning($"ModelComponent missing Model ({phase}). {location} component='{modelComponent.Name ?? modelComponent.GetType().Name}'");
                continue;
            }

            Model model = modelComponent.Model;
            summary.ModelCount++;
            Log(
                $"ModelComponent ({phase}) {location} component='{modelComponent.Name ?? modelComponent.GetType().Name}' " +
                $"runtimeMeshes={modelComponent.Meshes.Count} renderInfos={modelComponent.RenderedObjects.Length} model=({DescribeAsset(model)}) data=({DescribeAssetData(model)})",
                ELogCategory.Meshes,
                mirrorToCategoryLog: true);

            int subMeshIndex = 0;
            foreach (SubMesh subMesh in model.Meshes)
            {
                summary.SubMeshCount++;
                Log(
                    $"  SubMesh[{subMeshIndex}] ({phase}) {location} asset=({DescribeAsset(subMesh)}) lods={subMesh.LODs.Count} bounds={FormatBounds(subMesh.Bounds)}",
                    ELogCategory.Meshes,
                    mirrorToCategoryLog: false);

                int lodIndex = 0;
                foreach (SubMeshLOD lod in subMesh.LODs)
                {
                    summary.LodCount++;
                    XRMesh? mesh = lod.Mesh;
                    XRMaterial? material = lod.Material;
                    if (mesh is not null)
                    {
                        summary.MeshCount++;
                        BufferStats stats = GetMeshBufferStats(mesh);
                        summary.MeshBufferCount += stats.Count;
                        summary.MeshBufferBytes += stats.Bytes;
                    }

                    if (material is not null)
                    {
                        summary.MaterialCount++;
                        summary.TextureCount += material.Textures.Count(texture => texture is not null);
                        foreach (XRTexture? texture in material.Textures)
                            if (texture is XRTexture2D texture2D)
                                summary.TextureMipmapBytes += GetTextureMipBytes(texture2D);
                    }

                    Log(
                        $"    LOD[{lodIndex}] maxDistance={lod.MaxVisibleDistance:G5} minProjectedRadius={lod.MinProjectedScreenRadiusPixels:G5} " +
                        $"mesh=({(mesh is null ? "<null>" : DescribeAsset(mesh))}) meshData=({(mesh is null ? "<null>" : DescribeAssetData(mesh))}) " +
                        $"material=({(material is null ? "<null>" : DescribeAsset(material))}) materialData=({(material is null ? "<null>" : DescribeAssetData(material))})",
                        ELogCategory.Meshes,
                        mirrorToCategoryLog: mesh is null || material is null);

                    lodIndex++;
                }

                subMeshIndex++;
            }
        }
    }

    private static string DescribeAssetData(XRAsset asset)
        => asset switch
        {
            Model model => DescribeModelData(model),
            SubMesh subMesh => DescribeSubMeshData(subMesh),
            XRMesh mesh => DescribeMeshData(mesh),
            XRMaterial material => DescribeMaterialData(material),
            XRTexture2D texture => DescribeTexture2DData(texture),
            XRTexture texture => $"textureType={texture.GetType().Name} size={texture.WidthHeightDepth}",
            _ => $"embeddedAssets={asset.EmbeddedAssets.Count} dirty={asset.IsDirty}"
        };

    private static string DescribeModelData(Model model)
    {
        int lodCount = 0;
        int meshCount = 0;
        int materialCount = 0;
        int textureCount = 0;
        long meshBytes = 0;
        long textureBytes = 0;

        foreach (SubMesh subMesh in model.Meshes)
        {
            lodCount += subMesh.LODs.Count;
            foreach (SubMeshLOD lod in subMesh.LODs)
            {
                if (lod.Mesh is not null)
                {
                    meshCount++;
                    meshBytes += GetMeshBufferStats(lod.Mesh).Bytes;
                }

                if (lod.Material is not null)
                {
                    materialCount++;
                    textureCount += lod.Material.Textures.Count(texture => texture is not null);
                    foreach (XRTexture? texture in lod.Material.Textures)
                        if (texture is XRTexture2D texture2D)
                            textureBytes += GetTextureMipBytes(texture2D);
                }
            }
        }

        return $"subMeshes={model.Meshes.Count} lods={lodCount} meshes={meshCount} materials={materialCount} textures={textureCount} meshBufferBytes={meshBytes} textureMipBytes={textureBytes}";
    }

    private static string DescribeSubMeshData(SubMesh subMesh)
        => $"lods={subMesh.LODs.Count} bounds={FormatBounds(subMesh.Bounds)} culling={subMesh.CullingBounds?.ToString() ?? "<null>"}";

    private static string DescribeMeshData(XRMesh mesh)
    {
        BufferStats stats = GetMeshBufferStats(mesh);
        return $"vertices={mesh.VertexCount} indices={mesh.IndexCount} primitive={mesh.Type} interleaved={mesh.Interleaved} " +
            $"buffers={stats.Count} bufferBytes={stats.Bytes} missingClientBuffers={stats.MissingClientSources} " +
            $"skinning={mesh.HasSkinning} bones={mesh.UtilizedBones.Length} blendshapes={mesh.BlendshapeCount} bounds={FormatBounds(mesh.Bounds)}";
    }

    private static string DescribeMaterialData(XRMaterial material)
    {
        int texture2DCount = material.Textures.Count(texture => texture is XRTexture2D);
        long textureBytes = 0;
        foreach (XRTexture? texture in material.Textures)
            if (texture is XRTexture2D texture2D)
                textureBytes += GetTextureMipBytes(texture2D);

        return $"textures={material.Textures.Count} texture2D={texture2DCount} textureMipBytes={textureBytes} renderPass={material.RenderPass}";
    }

    private static string DescribeTexture2DData(XRTexture2D texture)
        => $"size={texture.Width}x{texture.Height} mipmaps={texture.Mipmaps.Length} mipBytes={GetTextureMipBytes(texture)} " +
            $"autoMips={texture.AutoGenerateMipmaps} minFilter={texture.MinFilter} magFilter={texture.MagFilter} sizedFormat={texture.SizedInternalFormat}";

    private static BufferStats GetMeshBufferStats(XRMesh mesh)
    {
        int count = 0;
        int missingClientSources = 0;
        long bytes = 0;

        // BufferCollection exposes the legacy non-generic IDictionary enumerator
        // for pattern-based foreach, which yields DictionaryEntry values. Iterate
        // the strongly typed value collection so diagnostics cannot abort a
        // play-mode snapshot restore with an InvalidCastException.
        foreach (XRDataBuffer buffer in mesh.Buffers.Values)
        {
            count++;
            bytes += buffer.ClientSideSource?.Length ?? 0;
            if (buffer.ClientSideSource is null)
                missingClientSources++;
        }

        return new BufferStats(count, bytes, missingClientSources);
    }

    private static long GetTextureMipBytes(XRTexture2D texture)
    {
        long bytes = 0;
        foreach (Mipmap2D? mipmap in texture.Mipmaps)
            if (mipmap is not null)
                bytes += mipmap.Data?.Length ?? 0;
        return bytes;
    }

    private static string DescribeAssetCacheState(XRAsset asset)
    {
        bool byId = asset.ID != Guid.Empty
            && Engine.Assets.TryGetAssetByID(asset.ID, out XRAsset? idAsset)
            && ReferenceEquals(idAsset, asset);
        bool byPath = !string.IsNullOrWhiteSpace(asset.FilePath)
            && Engine.Assets.TryGetAssetByPath(asset.FilePath, out XRAsset? pathAsset)
            && ReferenceEquals(pathAsset, asset);
        bool byOriginal = !string.IsNullOrWhiteSpace(asset.OriginalPath)
            && Engine.Assets.TryGetAssetByOriginalPath(asset.OriginalPath, out XRAsset? originalAsset)
            && ReferenceEquals(originalAsset, asset);

        return $"byId={byId} byPath={byPath} byOriginal={byOriginal}";
    }

    private static ELogCategory ClassifyAssetCategory(XRAsset asset)
        => asset switch
        {
            Model or SubMesh or XRMesh => ELogCategory.Meshes,
            XRTexture => ELogCategory.Textures,
            XRMaterial => ELogCategory.Rendering,
            _ => ELogCategory.Assets,
        };

    private static bool IsRenderAsset(XRAsset asset)
        => asset is Model or SubMesh or XRMesh or XRTexture or XRMaterial;

    private static string BuildNodePath(SceneNode node)
    {
        Stack<string> names = new();
        SceneNode? current = node;
        while (current is not null)
        {
            names.Push(current.Name ?? SceneNode.DefaultName);
            current = current.Parent;
        }

        return string.Join("/", names);
    }

    private static string GetPrefix()
    {
        Session? session = CurrentSession.Value;
        return session is null
            ? "[SnapshotDiag]"
            : $"[SnapshotDiag:{session.Operation}#{session.Id}]";
    }

    private static string GetSceneDisplayKey(XRScene scene)
        => scene.Name ?? RuntimeHelpers.GetHashCode(scene).ToString();

    private static string ShortPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "<null>";

        try
        {
            string full = Path.GetFullPath(path);
            string current = Environment.CurrentDirectory;
            string relative = Path.GetRelativePath(current, full);
            return relative.StartsWith("..", StringComparison.Ordinal) ? full : relative;
        }
        catch
        {
            return path;
        }
    }

    private static string FormatBounds(XREngine.Data.Geometry.AABB bounds)
        => $"min=<{bounds.Min.X:G5},{bounds.Min.Y:G5},{bounds.Min.Z:G5}> max=<{bounds.Max.X:G5},{bounds.Max.Y:G5},{bounds.Max.Z:G5}>";

    private sealed class Scope(Session? previous, Session session) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            CurrentSession.Value = session;
            Log(
                $"End {session.Operation}. World={session.WorldName ?? "<null>"} " +
                $"assetDecisions={session.AssetSerializationDecisions.Count} inlined={session.InlinedAssetCount} referenced={session.ReferencedAssetCount} " +
                $"resolveAttempts={session.AssetResolveAttempts} resolved={session.ResolvedAssetCount} failed={session.FailedAssetResolveCount}");
            CurrentSession.Value = previous;
        }
    }

    private sealed class Session(long id, string operation, string? worldName)
    {
        public long Id { get; } = id;
        public string Operation { get; } = operation;
        public string? WorldName { get; } = worldName;
        public HashSet<string> AssetSerializationDecisions { get; } = new(StringComparer.Ordinal);
        public int InlinedAssetCount;
        public int ReferencedAssetCount;
        public int AssetResolveAttempts;
        public int ResolvedAssetCount;
        public int FailedAssetResolveCount;
    }

    private readonly record struct BufferStats(int Count, long Bytes, int MissingClientSources);

    private sealed class WorldAssetSummary
    {
        public int NodeCount;
        public int ModelComponentCount;
        public int MissingModelCount;
        public int ModelCount;
        public int SubMeshCount;
        public int LodCount;
        public int MeshCount;
        public int MeshBufferCount;
        public long MeshBufferBytes;
        public int MaterialCount;
        public int TextureCount;
        public long TextureMipmapBytes;
        public int RenderableComponentCount;
        public int RuntimeRenderableMeshCount;
        public int RuntimeRenderInfoCount;
        public int RuntimeMeshCount;
        public int RuntimeMeshBufferCount;
        public long RuntimeMeshBufferBytes;
    }
}
