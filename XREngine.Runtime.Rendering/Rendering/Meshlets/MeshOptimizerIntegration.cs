using System.Numerics;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Meshlets;

public sealed class MeshletBuildResult
{
    public required Meshlet[] Meshlets { get; init; }
    public required uint[] VertexIndices { get; init; }
    public required byte[] TriangleIndices { get; init; }
    public required MeshletVertex[] Vertices { get; init; }
    public required MeshOptimizerMeshletStats Stats { get; init; }
    public required MeshletPayload Payload { get; init; }
}

public static class MeshOptimizerIntegration
{
    private static long s_meshletBuildInvocationCount;

    public static long MeshletBuildInvocationCount
        => Interlocked.Read(ref s_meshletBuildInvocationCount);

    public static string MeshOptimizerVersionKey
        => MeshOptimizerNative.VersionKey;

    public static void ResetMeshletBuildDiagnosticsForTests()
        => Interlocked.Exchange(ref s_meshletBuildInvocationCount, 0);

    /// <summary>
    /// Builds the meshlet payload for exactly one already-finalized mesh.
    /// This method never generates or reconciles model LODs.
    /// </summary>
    public static MeshletBuildResult BuildMeshletPayloadForMesh(XRMesh mesh, MeshletGenerationSettings settings)
        => BuildMeshletPayloadForMesh(mesh, settings, null, null);

    /// <summary>
    /// Builds the meshlet payload for exactly one already-finalized mesh while
    /// recording the effective LOD policy as cook provenance. The caller owns
    /// LOD generation and must invoke this once for every renderable LOD.
    /// </summary>
    public static MeshletBuildResult BuildMeshletPayloadForMesh(
        XRMesh mesh,
        MeshletGenerationSettings settings,
        MeshLodGenerationSettings? lodSettings,
        string? sourceMeshIdentity)
    {
        if (mesh is null)
            throw new ArgumentNullException(nameof(mesh));
        if (settings is null)
            throw new ArgumentNullException(nameof(settings));

        if (!settings.Enabled)
        {
            MeshletPayload disabledPayload = MeshletPayload.CreateDisabled(mesh, settings, lodSettings, sourceMeshIdentity);
            return new MeshletBuildResult
            {
                Meshlets = [],
                VertexIndices = [],
                TriangleIndices = [],
                Vertices = [],
                Stats = disabledPayload.Stats,
                Payload = disabledPayload,
            };
        }

        int[] indices = mesh.GetIndices(EPrimitiveType.Triangles) ?? [];
        if (indices.Length == 0 || mesh.VertexCount == 0)
        {
            MeshletPayload emptyPayload = CreatePayload(
                mesh,
                settings,
                lodSettings,
                sourceMeshIdentity,
                [],
                [],
                [],
                [],
                new MeshOptimizerMeshletStats(0, 0, 0, 0));

            return new MeshletBuildResult
            {
                Meshlets = [],
                VertexIndices = [],
                TriangleIndices = [],
                Vertices = [],
                Stats = emptyPayload.Stats,
                Payload = emptyPayload,
            };
        }

        uint[] sourceIndices = new uint[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            int sourceIndex = indices[i];
            if ((uint)sourceIndex >= (uint)mesh.VertexCount)
                throw new InvalidDataException($"Meshlet source index {sourceIndex} at element {i} is outside vertex range [0, {mesh.VertexCount}).");

            sourceIndices[i] = (uint)sourceIndex;
        }

        ValidateNativeBuildSettings(settings, sourceIndices.Length);

        MeshletVertex[] vertices = BuildMeshletVertices(mesh);

        uint minTriangles = settings.BuildMode is MeshletBuildMode.Flex or MeshletBuildMode.Spatial
            ? Math.Clamp(settings.MinTriangles, 1u, settings.MaxTriangles)
            : settings.MaxTriangles;

        nuint maxMeshlets = MeshOptimizerNative.BuildMeshletsBound((nuint)sourceIndices.Length, settings.MaxVertices, minTriangles);
        if (maxMeshlets == 0)
        {
            MeshletPayload emptyPayload = CreatePayload(
                mesh,
                settings,
                lodSettings,
                sourceMeshIdentity,
                [],
                [],
                [],
                vertices,
                new MeshOptimizerMeshletStats(0, 0, 0, 0));

            return new MeshletBuildResult
            {
                Meshlets = [],
                VertexIndices = [],
                TriangleIndices = [],
                Vertices = vertices,
                Stats = emptyPayload.Stats,
                Payload = emptyPayload,
            };
        }

        MeshOptimizerMeshlet[] meshoptMeshlets = new MeshOptimizerMeshlet[CheckedMeshletCount(maxMeshlets)];
        uint[] meshletVertices = new uint[CheckedScratchElementCount(maxMeshlets, settings.MaxVertices, "meshletVertices")];
        byte[] meshletTriangles = new byte[CheckedTriangleScratchByteCount(maxMeshlets, settings.MaxTriangles)];

        // Count at the only real meshoptimizer builder entry. Import and
        // cache-repair callers must not infer this from payload outcomes.
        Interlocked.Increment(ref s_meshletBuildInvocationCount);
        RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordMeshletNativeBuilderEntry();
        nuint meshletCount = MeshOptimizerNative.BuildNativeMeshletClusters(
            settings.BuildMode,
            meshoptMeshlets,
            meshletVertices,
            meshletTriangles,
            sourceIndices,
            GetPositionArray(mesh),
            (nuint)mesh.VertexCount,
            settings.MaxVertices,
            minTriangles,
            settings.MaxTriangles,
            settings.ConeWeight,
            settings.SplitFactor,
            settings.FillWeight);

        if (meshletCount == 0)
        {
            MeshletPayload emptyPayload = CreatePayload(
                mesh,
                settings,
                lodSettings,
                sourceMeshIdentity,
                [],
                [],
                [],
                vertices,
                new MeshOptimizerMeshletStats(0, 0, 0, 0));

            return new MeshletBuildResult
            {
                Meshlets = [],
                VertexIndices = [],
                TriangleIndices = [],
                Vertices = vertices,
                Stats = emptyPayload.Stats,
                Payload = emptyPayload,
            };
        }

        ValidateNativeMeshletBuildOutput(
            meshoptMeshlets,
            meshletVertices,
            meshletTriangles,
            meshletCount,
            mesh.VertexCount,
            settings);

        int finalMeshletCount = (int)meshletCount;
        if (settings.OptimizeMeshlets)
        {
            for (int i = 0; i < finalMeshletCount; i++)
            {
                MeshOptimizerMeshlet meshlet = meshoptMeshlets[i];
                int triangleByteCountForMeshlet = GetTriangleByteCount(meshlet.TriangleCount);
                MeshOptimizerNative.OptimizeMeshletLevel(
                    meshletVertices.AsSpan((int)meshlet.VertexOffset, (int)meshlet.VertexCount),
                    meshletTriangles.AsSpan((int)meshlet.TriangleOffset, triangleByteCountForMeshlet),
                    settings.OptimizeLevel);
            }
        }

        MeshOptimizerMeshlet last = meshoptMeshlets[finalMeshletCount - 1];
        int vertexReferenceCount = (int)(last.VertexOffset + last.VertexCount);
        // meshoptimizer's per-meshlet spans are tightly sized. The persisted GPU stream,
        // however, retains the final four-byte padding required by descriptor offsets and
        // the shader's byte-addressed loads.
        int triangleByteCount = checked((int)AlignToFour((ulong)last.TriangleOffset + (last.TriangleCount * 3UL)));

        Array.Resize(ref meshletVertices, vertexReferenceCount);
        Array.Resize(ref meshletTriangles, triangleByteCount);
        Array.Resize(ref meshoptMeshlets, finalMeshletCount);

        int encodedByteCount = 0;
        Meshlet[] results = new Meshlet[finalMeshletCount];
        CpuMeshletDescriptor[] descriptors = new CpuMeshletDescriptor[finalMeshletCount];
        float[] positionArray = GetPositionArray(mesh);
        for (int i = 0; i < finalMeshletCount; i++)
        {
            MeshOptimizerMeshlet meshlet = meshoptMeshlets[i];
            CpuMeshletDescriptor descriptor = settings.ComputeBounds
                ? ComputeMeshletDescriptor(meshletVertices, meshletTriangles, meshlet, positionArray, mesh.VertexCount)
                : ComputeFallbackDescriptor(mesh, meshletVertices, meshlet);

            if (settings.EncodeMeshlets)
            {
                uint[]? encodedVertices = settings.EncodeVertexReferences
                    ? meshletVertices.AsSpan((int)meshlet.VertexOffset, (int)meshlet.VertexCount).ToArray()
                    : null;
                byte[] encodedTriangles = meshletTriangles.AsSpan((int)meshlet.TriangleOffset, GetTriangleByteCount(meshlet.TriangleCount)).ToArray();
                encodedByteCount += MeshOptimizerNative.EncodeMeshlet(encodedVertices, encodedTriangles, (int)settings.MaxVertices, (int)settings.MaxTriangles);
            }

            descriptors[i] = descriptor;
            results[i] = descriptor.ToGpuMeshlet();
        }

        MeshOptimizerMeshletStats stats = new(finalMeshletCount, vertexReferenceCount, triangleByteCount, encodedByteCount);
        MeshletPayload payload = CreatePayload(
            mesh,
            settings,
            lodSettings,
            sourceMeshIdentity,
            descriptors,
            meshletVertices,
            meshletTriangles,
            vertices,
            stats);

        return new MeshletBuildResult
        {
            Meshlets = results,
            VertexIndices = meshletVertices,
            TriangleIndices = meshletTriangles,
            Vertices = vertices,
            Stats = stats,
            Payload = payload,
        };
    }

    private static MeshletPayload CreatePayload(
        XRMesh mesh,
        MeshletGenerationSettings settings,
        MeshLodGenerationSettings? lodSettings,
        string? sourceMeshIdentity,
        CpuMeshletDescriptor[] descriptors,
        uint[] vertexIndices,
        byte[] triangleIndices,
        MeshletVertex[] vertices,
        MeshOptimizerMeshletStats stats)
    {
        MeshletGenerationSettingsSnapshot meshletSnapshot = MeshletGenerationSettingsSnapshot.From(settings);
        MeshLodGenerationSettingsSnapshot lodSnapshot = MeshLodGenerationSettingsSnapshot.From(lodSettings);
        string identity = MeshletPayloadUtility.ResolveSourceMeshIdentity(mesh, sourceMeshIdentity);
        ulong sourceHash = MeshletPayloadUtility.ComputeSourceMeshHash(mesh);
        ulong meshletSettingsHash = MeshletPayloadUtility.ComputeHash(meshletSnapshot);
        ulong lodSettingsHash = MeshletPayloadUtility.ComputeHash(lodSnapshot);
        string versionKey = MeshOptimizerVersionKey;
        string provenanceKey = MeshletPayloadUtility.CurrentCookProvenanceKey;

        MeshletPayload payload = new()
        {
            GenerationEnabled = settings.Enabled,
            State = descriptors.Length == 0 ? MeshletPayloadState.Empty : MeshletPayloadState.Present,
            MeshOptimizerVersionKey = versionKey,
            CookProvenanceKey = provenanceKey,
            RuntimeCompatibilityToken = MeshletPayloadUtility.ComputeRuntimeCompatibilityToken(meshletSnapshot),
            SourceMeshIdentity = identity,
            SourceVertexCount = mesh.VertexCount,
            SourceTriangleCount = mesh.Triangles?.Count ?? 0,
            SourceMeshHash = sourceHash,
            MeshletSettingsHash = meshletSettingsHash,
            LodSettingsHash = lodSettingsHash,
            FreshnessHash = MeshletPayloadUtility.ComputeFreshnessHash(identity, sourceHash, meshletSettingsHash, lodSettingsHash, provenanceKey),
            MeshletSettings = meshletSnapshot,
            LodSettings = lodSnapshot,
            Meshlets = descriptors.ToImmutableArray(),
            VertexIndices = vertexIndices.ToImmutableArray(),
            TriangleIndices = triangleIndices.ToImmutableArray(),
            Vertices = vertices.ToImmutableArray(),
            Stats = stats,
        };
        payload.ValidatePortablePayload();
        return payload;
    }

    private static int CheckedMeshletCount(nuint maxMeshlets)
    {
        if (maxMeshlets > (nuint)int.MaxValue)
            throw new InvalidOperationException($"Meshlet build bound exceeds supported array length: {maxMeshlets}.");

        return (int)maxMeshlets;
    }

    private static int CheckedScratchElementCount(nuint maxMeshlets, uint maxElementsPerMeshlet, string bufferName)
    {
        ulong count = checked((ulong)maxMeshlets * maxElementsPerMeshlet);
        if (count > int.MaxValue)
            throw new InvalidOperationException($"Meshlet scratch buffer '{bufferName}' exceeds supported array length: {count}.");

        return (int)count;
    }

    private static void ValidateNativeBuildSettings(MeshletGenerationSettings settings, int indexCount)
    {
        if (indexCount < 3 || indexCount % 3 != 0)
            throw new InvalidDataException($"Meshlet source index count must be a non-empty triangle list; received {indexCount} indices.");

        if (settings.MaxVertices is < 3u or > 256u)
            throw new InvalidDataException($"meshoptimizer requires MaxVertices in [3, 256]; received {settings.MaxVertices}.");

        if (settings.MaxTriangles is 0u or > 512u || settings.MaxTriangles % 4u != 0u)
            throw new InvalidDataException($"The bundled meshoptimizer ABI requires MaxTriangles in [4, 512] and divisible by four; received {settings.MaxTriangles}.");

        if (settings.BuildMode is MeshletBuildMode.Flex or MeshletBuildMode.Spatial)
        {
            if (settings.MinTriangles > settings.MaxTriangles || settings.MinTriangles % 4u != 0u)
                throw new InvalidDataException($"The bundled meshoptimizer ABI requires MinTriangles <= MaxTriangles and divisible by four for {settings.BuildMode}; received {settings.MinTriangles}/{settings.MaxTriangles}.");
        }
    }

    private static void ValidateNativeMeshletBuildOutput(
        MeshOptimizerMeshlet[] meshlets,
        uint[] vertexReferences,
        byte[] triangleReferences,
        nuint meshletCount,
        int sourceVertexCount,
        MeshletGenerationSettings settings)
    {
        if (meshletCount > (nuint)meshlets.Length)
            throw new InvalidDataException($"meshoptimizer returned {meshletCount} meshlets for a bound of {meshlets.Length}.");

        ulong priorVertexEnd = 0UL;
        ulong priorTriangleEnd = 0UL;
        for (nuint meshletIndex = 0; meshletIndex < meshletCount; meshletIndex++)
        {
            MeshOptimizerMeshlet meshlet = meshlets[(int)meshletIndex];
            ulong vertexEnd = checked((ulong)meshlet.VertexOffset + meshlet.VertexCount);
            ulong triangleEnd = checked((ulong)meshlet.TriangleOffset + ((ulong)meshlet.TriangleCount * 3UL));
            ulong paddedTriangleEnd = AlignToFour(triangleEnd);
            if (meshlet.VertexCount is 0u || meshlet.VertexCount > settings.MaxVertices ||
                meshlet.TriangleCount is 0u || meshlet.TriangleCount > settings.MaxTriangles)
            {
                throw new InvalidDataException(
                    $"meshoptimizer returned invalid meshlet {meshletIndex} counts: vertices={meshlet.VertexCount}/{settings.MaxVertices}, triangles={meshlet.TriangleCount}/{settings.MaxTriangles}.");
            }

            if (vertexEnd > (ulong)vertexReferences.Length || paddedTriangleEnd > (ulong)triangleReferences.Length)
            {
                throw new InvalidDataException(
                    $"meshoptimizer returned out-of-range meshlet {meshletIndex}: vertexEnd={vertexEnd}/{vertexReferences.Length}, paddedTriangleEnd={paddedTriangleEnd}/{triangleReferences.Length}.");
            }

            if ((meshlet.TriangleOffset & 3u) != 0u ||
                (ulong)meshlet.VertexOffset < priorVertexEnd ||
                (ulong)meshlet.TriangleOffset < priorTriangleEnd)
                throw new InvalidDataException($"meshoptimizer returned overlapping or non-monotonic ranges at meshlet {meshletIndex}.");

            int vertexOffset = (int)meshlet.VertexOffset;
            for (uint vertexIndex = 0; vertexIndex < meshlet.VertexCount; vertexIndex++)
                if (vertexReferences[vertexOffset + (int)vertexIndex] >= (uint)sourceVertexCount)
                    throw new InvalidDataException($"Meshlet {meshletIndex} references source vertex {vertexReferences[vertexOffset + (int)vertexIndex]} outside [0, {sourceVertexCount}).");

            int triangleOffset = (int)meshlet.TriangleOffset;
            int triangleByteCount = GetTriangleByteCount(meshlet.TriangleCount);
            for (int triangleByteIndex = 0; triangleByteIndex < triangleByteCount; triangleByteIndex++)
                if (triangleReferences[triangleOffset + triangleByteIndex] >= meshlet.VertexCount)
                    throw new InvalidDataException($"Meshlet {meshletIndex} local triangle index {triangleReferences[triangleOffset + triangleByteIndex]} exceeds vertex count {meshlet.VertexCount}.");

            priorVertexEnd = vertexEnd;
            priorTriangleEnd = paddedTriangleEnd;
        }
    }

    private static int CheckedTriangleScratchByteCount(nuint maxMeshlets, uint maxTriangles)
    {
        ulong bytesPerMeshlet = AlignToFour(checked((ulong)maxTriangles * 3UL));
        ulong byteCount = checked((ulong)maxMeshlets * bytesPerMeshlet);
        if (byteCount > int.MaxValue)
            throw new InvalidOperationException($"Meshlet triangle scratch buffer exceeds supported array length: {byteCount}.");

        return (int)byteCount;
    }

    private static int GetTriangleByteCount(uint triangleCount)
    {
        ulong byteCount = checked((ulong)triangleCount * 3UL);
        if (byteCount > int.MaxValue)
            throw new InvalidOperationException($"Meshlet triangle byte count exceeds supported span length: {byteCount}.");

        return (int)byteCount;
    }

    private static ulong AlignToFour(ulong value)
        => (value + 3UL) & ~3UL;

    public static void RemoveAutoGeneratedLods(SubMesh subMesh)
    {
        ArgumentNullException.ThrowIfNull(subMesh);

        List<SubMeshLOD> manualLods = [.. subMesh.LODs.Where(static lod => !lod.IsAutoGenerated).OrderBy(static lod => lod.MaxVisibleDistance)];
        subMesh.LODs = new SortedSet<SubMeshLOD>(manualLods, new LODSorter());
        subMesh.Bounds = subMesh.CalculateBoundingBox();
    }

    public static IReadOnlyList<(SubMeshLOD Lod, MeshOptimizerLodStats Stats)> RegenerateAutoLods(SubMesh subMesh)
    {
        ArgumentNullException.ThrowIfNull(subMesh);

        MeshLodGenerationSettings settings = subMesh.MeshOptimizer.Lods;
        RemoveAutoGeneratedLods(subMesh);

        if (!settings.Enabled || settings.AdditionalLodCount <= 0)
            return [];

        SubMeshLOD? baseLod = subMesh.LODs.FirstOrDefault(static lod => lod.Mesh is not null);
        XRMesh? baseMesh = baseLod?.Mesh;
        if (baseLod is null || baseMesh is null)
            return [];

        List<SubMeshLOD> orderedLods = [.. subMesh.LODs.OrderBy(static lod => lod.MaxVisibleDistance)];
        List<(SubMeshLOD Lod, MeshOptimizerLodStats Stats)> generated = [];
        XRMesh currentSourceMesh = baseMesh;

        for (int lodIndex = 0; lodIndex < settings.AdditionalLodCount; lodIndex++)
        {
            XRMesh sourceMesh = settings.ReusePreviousLodAsSource ? currentSourceMesh : baseMesh;
            MeshOptimizerLodStats? stats = TryBuildLod(sourceMesh, settings, lodIndex, out XRMesh? generatedMesh);
            if (generatedMesh is null || stats is null)
                break;

            float distance = settings.FirstLodDistance * MathF.Pow(settings.LodDistanceScale, lodIndex);
            SubMeshLOD lod = new(baseLod.Material, generatedMesh, distance)
            {
                GenerateAsync = baseLod.GenerateAsync,
                IsAutoGenerated = true,
                GeneratedTargetIndexRatio = stats.Value.TargetIndexRatio,
                GeneratedNormalizedError = stats.Value.NormalizedError,
                GeneratedObjectSpaceError = stats.Value.ObjectSpaceError,
            };

            orderedLods.Add(lod);
            generated.Add((lod, stats.Value));
            currentSourceMesh = generatedMesh;
        }

        if (orderedLods.Count > 0)
        {
            orderedLods.Sort(static (left, right) => left.MaxVisibleDistance.CompareTo(right.MaxVisibleDistance));
            orderedLods[^1].MaxVisibleDistance = float.MaxValue;
        }

        subMesh.LODs = new SortedSet<SubMeshLOD>(orderedLods, new LODSorter());
        subMesh.Bounds = subMesh.CalculateBoundingBox();
        subMesh.DetermineRootBone();
        return generated;
    }

    public static MeshletMaterial CreateMeshletMaterial(XRMaterial? material)
    {
        MeshletMaterial result = new()
        {
            DiffuseTextureID = 32u,
            NormalTextureID = 32u,
            MetallicRoughnessTextureID = 32u,
        };

        if (material is null)
            return result;

        result.Albedo = Vector4.One;
        result.Metallic = 0.0f;
        result.Roughness = 1.0f;
        result.AO = 1.0f;
        return result;
    }

    private static MeshOptimizerLodStats? TryBuildLod(XRMesh sourceMesh, MeshLodGenerationSettings settings, int lodIndex, out XRMesh? generatedMesh)
    {
        generatedMesh = null;

        int[] sourceIndices = sourceMesh.GetIndices(EPrimitiveType.Triangles) ?? [];
        if (sourceIndices.Length < 3 || sourceMesh.VertexCount == 0)
            return null;

        float targetRatio = Math.Clamp(settings.FirstLodIndexRatio * MathF.Pow(settings.LodRatioScale, lodIndex), 0.0f, 1.0f);
        int targetIndexCount = AlignIndexCount((int)(sourceIndices.Length * targetRatio));
        if (targetIndexCount == 0)
            targetIndexCount = sourceIndices.Length;
        if (targetIndexCount >= sourceIndices.Length)
            return null;

        uint[] workingIndices = new uint[sourceIndices.Length];
        for (int i = 0; i < sourceIndices.Length; i++)
            workingIndices[i] = (uint)sourceIndices[i];

        Vertex[] vertices = [.. sourceMesh.Vertices.Select(static vertex => vertex.HardCopy())];
        float[] positions = GetPositionArray(vertices);
        AttributeBuffer attributes = BuildAttributeBuffer(sourceMesh, vertices, settings);
        byte[]? vertexLock = BuildVertexLockBuffer(sourceMesh, vertices, settings);

        float resultError;
        int resultIndexCount;
        switch (settings.Mode)
        {
            case MeshOptimizerLodMode.Simplify:
                resultIndexCount = MeshOptimizerNative.Simplify(workingIndices, positions, sourceMesh.VertexCount, targetIndexCount, settings.TargetError, settings.Options, out resultError);
                break;
            case MeshOptimizerLodMode.SimplifyWithUpdate:
                resultIndexCount = MeshOptimizerNative.SimplifyWithUpdate(workingIndices, positions, sourceMesh.VertexCount, attributes.Buffer, attributes.Stride, attributes.Weights, targetIndexCount, settings.TargetError, settings.Options, vertexLock, out resultError);
                ApplyUpdatedAttributes(vertices, sourceMesh, positions, attributes);
                break;
            case MeshOptimizerLodMode.SimplifySloppy:
                resultIndexCount = MeshOptimizerNative.SimplifySloppy(workingIndices, positions, sourceMesh.VertexCount, targetIndexCount, settings.TargetError, vertexLock, out resultError);
                break;
            default:
                resultIndexCount = MeshOptimizerNative.SimplifyWithAttributes(workingIndices, positions, sourceMesh.VertexCount, attributes.Buffer, attributes.Stride, attributes.Weights, targetIndexCount, settings.TargetError, settings.Options, vertexLock, out resultError);
                break;
        }

        if (resultIndexCount < 3)
            return null;

        generatedMesh = CreateMeshFromIndexedVertices(
            vertices,
            workingIndices.AsSpan(0, resultIndexCount),
            sourceMesh,
            $"{(string.IsNullOrWhiteSpace(sourceMesh.Name) ? "Mesh" : sourceMesh.Name)}_LOD{lodIndex + 1}_Meshopt");

        if (generatedMesh is null)
            return null;

        float objectSpaceError = resultError * MeshOptimizerNative.SimplifyScale(GetPositionArray(sourceMesh), sourceMesh.VertexCount);
        return new MeshOptimizerLodStats(sourceIndices.Length / 3, resultIndexCount / 3, targetRatio, resultError, objectSpaceError);
    }

    private static XRMesh? CreateMeshFromIndexedVertices(Vertex[] vertices, ReadOnlySpan<uint> indices, XRMesh sourceMesh, string meshName)
    {
        if (indices.Length < 3)
            return null;

        List<VertexTriangle> triangles = new(indices.Length / 3);
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            uint a = indices[i];
            uint b = indices[i + 1];
            uint c = indices[i + 2];
            if (a >= vertices.Length || b >= vertices.Length || c >= vertices.Length)
                continue;

            triangles.Add(new VertexTriangle(vertices[(int)a].HardCopy(), vertices[(int)b].HardCopy(), vertices[(int)c].HardCopy()));
        }

        if (triangles.Count == 0)
            return null;

        XRMesh mesh = XRMesh.Create([.. triangles]);
        mesh.Name = meshName;
        if (sourceMesh.HasBlendshapes)
        {
            mesh.BlendshapeNames = [.. sourceMesh.BlendshapeNames];
            mesh.RebuildBlendshapeBuffersFromVertices();
        }

        if (sourceMesh.HasSkinning)
            mesh.RebuildSkinningBuffersFromVertices();

        return mesh;
    }

    private static void ApplyUpdatedAttributes(Vertex[] vertices, XRMesh sourceMesh, float[] positions, AttributeBuffer attributes)
    {
        int attributeOffset = 0;
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].Position = new Vector3(positions[i * 3 + 0], positions[i * 3 + 1], positions[i * 3 + 2]);

            int baseOffset = i * attributes.Stride;
            int cursor = 0;
            if (attributes.IncludeNormals)
            {
                vertices[i].Normal = new Vector3(attributes.Buffer[baseOffset + cursor + 0], attributes.Buffer[baseOffset + cursor + 1], attributes.Buffer[baseOffset + cursor + 2]);
                cursor += 3;
            }

            if (attributes.IncludeTangents)
            {
                vertices[i].Tangent = new Vector3(attributes.Buffer[baseOffset + cursor + 0], attributes.Buffer[baseOffset + cursor + 1], attributes.Buffer[baseOffset + cursor + 2]);
                vertices[i].BitangentSign = attributes.Buffer[baseOffset + cursor + 3];
                cursor += 4;
            }

            if (attributes.IncludeTexCoords)
            {
                List<Vector2> texCoords = vertices[i].TextureCoordinateSets ??= new List<Vector2>((int)sourceMesh.TexCoordCount);
                texCoords.Clear();
                for (uint setIndex = 0; setIndex < sourceMesh.TexCoordCount; setIndex++)
                {
                    texCoords.Add(new Vector2(attributes.Buffer[baseOffset + cursor + 0], attributes.Buffer[baseOffset + cursor + 1]));
                    cursor += 2;
                }
            }

            if (attributes.IncludeColors)
            {
                List<Vector4> colors = vertices[i].ColorSets ??= new List<Vector4>((int)sourceMesh.ColorCount);
                colors.Clear();
                for (uint setIndex = 0; setIndex < sourceMesh.ColorCount; setIndex++)
                {
                    colors.Add(new Vector4(
                        attributes.Buffer[baseOffset + cursor + 0],
                        attributes.Buffer[baseOffset + cursor + 1],
                        attributes.Buffer[baseOffset + cursor + 2],
                        attributes.Buffer[baseOffset + cursor + 3]));
                    cursor += 4;
                }
            }

            attributeOffset += attributes.Stride;
        }
    }

    private static MeshletVertex[] BuildMeshletVertices(XRMesh mesh)
    {
        MeshletVertex[] vertices = new MeshletVertex[mesh.VertexCount];
        for (uint i = 0; i < mesh.VertexCount; i++)
        {
            Vector3 position = mesh.GetPosition(i);
            Vector3 normal = mesh.HasNormals ? mesh.GetNormal(i) : Vector3.UnitY;
            Vector4 tangent = mesh.HasTangents ? mesh.GetTangentWithSign(i) : new Vector4(Vector3.UnitX, 1.0f);
            Vector2 texCoord = mesh.HasTexCoords ? mesh.GetTexCoord(i, 0u) : Vector2.Zero;
            vertices[i] = new MeshletVertex
            {
                Position = new Vector4(position, 1.0f),
                Normal = new Vector4(Vector3.Normalize(normal == Vector3.Zero ? Vector3.UnitY : normal), 0.0f),
                TexCoord = texCoord,
                Padding = Vector2.Zero,
                Tangent = tangent,
            };
        }

        return vertices;
    }

    public static MeshletVertex[] BuildMeshletVerticesForPayload(XRMesh mesh)
        => BuildMeshletVertices(mesh);

    private static CpuMeshletDescriptor ComputeMeshletDescriptor(uint[] meshletVertices, byte[] meshletTriangles, MeshOptimizerMeshlet meshlet, float[] positions, int vertexCount)
    {
        MeshOptimizerBounds bounds = MeshOptimizerNative.ComputeMeshletBounds(
            meshletVertices.AsSpan((int)meshlet.VertexOffset, (int)meshlet.VertexCount),
            meshletTriangles.AsSpan((int)meshlet.TriangleOffset, GetTriangleByteCount(meshlet.TriangleCount)),
            positions,
            vertexCount);
        return CreateDescriptor(meshlet, bounds);
    }

    private static CpuMeshletDescriptor ComputeFallbackDescriptor(XRMesh mesh, IReadOnlyList<uint> meshletVertices, MeshOptimizerMeshlet meshlet)
    {
        if (meshlet.VertexCount == 0)
            return new CpuMeshletDescriptor(
                Vector4.Zero,
                meshlet.VertexOffset,
                meshlet.TriangleOffset,
                meshlet.VertexCount,
                meshlet.TriangleCount,
                Vector4.Zero,
                Vector4.Zero,
                0u);

        Vector3 center = Vector3.Zero;
        for (int i = 0; i < meshlet.VertexCount; i++)
            center += mesh.GetPosition(meshletVertices[(int)(meshlet.VertexOffset + i)]);
        center /= meshlet.VertexCount;

        float radius = 0.0f;
        for (int i = 0; i < meshlet.VertexCount; i++)
            radius = Math.Max(radius, Vector3.Distance(center, mesh.GetPosition(meshletVertices[(int)(meshlet.VertexOffset + i)])));

        return new CpuMeshletDescriptor(
            new Vector4(center, radius),
            meshlet.VertexOffset,
            meshlet.TriangleOffset,
            meshlet.VertexCount,
            meshlet.TriangleCount,
            Vector4.Zero,
            Vector4.Zero,
            0u);
    }

    private static CpuMeshletDescriptor CreateDescriptor(MeshOptimizerMeshlet meshlet, MeshOptimizerBounds bounds)
        => new(
            new Vector4(bounds.CenterX, bounds.CenterY, bounds.CenterZ, bounds.Radius),
            meshlet.VertexOffset,
            meshlet.TriangleOffset,
            meshlet.VertexCount,
            meshlet.TriangleCount,
            new Vector4(bounds.ConeAxisX, bounds.ConeAxisY, bounds.ConeAxisZ, bounds.ConeCutoff),
            new Vector4(bounds.ConeApexX, bounds.ConeApexY, bounds.ConeApexZ, 0.0f),
            PackCone(bounds.ConeAxisS8X, bounds.ConeAxisS8Y, bounds.ConeAxisS8Z, bounds.ConeCutoffS8));

    private static uint PackCone(sbyte axisX, sbyte axisY, sbyte axisZ, sbyte cutoff)
        => (uint)(byte)axisX |
           ((uint)(byte)axisY << 8) |
           ((uint)(byte)axisZ << 16) |
           ((uint)(byte)cutoff << 24);

    private static float[] GetPositionArray(XRMesh mesh)
        => GetPositionArray(mesh.Vertices);

    private static float[] GetPositionArray(Vertex[] vertices)
    {
        float[] positions = new float[vertices.Length * 3];
        for (int i = 0; i < vertices.Length; i++)
        {
            positions[i * 3 + 0] = vertices[i].Position.X;
            positions[i * 3 + 1] = vertices[i].Position.Y;
            positions[i * 3 + 2] = vertices[i].Position.Z;
        }

        return positions;
    }

    private static AttributeBuffer BuildAttributeBuffer(XRMesh mesh, Vertex[] vertices, MeshLodGenerationSettings settings)
    {
        bool includeNormals = settings.UseNormals && mesh.HasNormals;
        bool includeTangents = settings.UseTangents && mesh.HasTangents;
        bool includeTexCoords = settings.UseTexCoords && mesh.HasTexCoords;
        bool includeColors = settings.UseColors && mesh.HasColors;

        List<float> weightList = [];
        int stride = 0;
        if (includeNormals)
        {
            stride += 3;
            weightList.AddRange(Enumerable.Repeat(settings.NormalWeight, 3));
        }

        if (includeTangents)
        {
            stride += 4;
            weightList.AddRange(Enumerable.Repeat(settings.TangentWeight, 4));
        }

        if (includeTexCoords)
        {
            int uvFloatCount = (int)mesh.TexCoordCount * 2;
            stride += uvFloatCount;
            weightList.AddRange(Enumerable.Repeat(settings.TexCoordWeight, uvFloatCount));
        }

        if (includeColors)
        {
            int colorFloatCount = (int)mesh.ColorCount * 4;
            stride += colorFloatCount;
            weightList.AddRange(Enumerable.Repeat(settings.ColorWeight, colorFloatCount));
        }

        if (stride == 0)
            return new AttributeBuffer([], [], 0, includeNormals, includeTangents, includeTexCoords, includeColors);

        int maxStride = Math.Min(stride, 32);
        float[] buffer = new float[vertices.Length * maxStride];
        float[] weights = weightList.Take(maxStride).ToArray();
        for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
        {
            int offset = vertexIndex * maxStride;
            int cursor = 0;
            if (includeNormals && cursor + 3 <= maxStride)
            {
                Vector3 normal = vertices[vertexIndex].Normal ?? Vector3.Zero;
                buffer[offset + cursor + 0] = normal.X;
                buffer[offset + cursor + 1] = normal.Y;
                buffer[offset + cursor + 2] = normal.Z;
                cursor += 3;
            }

            if (includeTangents && cursor + 4 <= maxStride)
            {
                Vector3 tangent = vertices[vertexIndex].Tangent ?? Vector3.UnitX;
                buffer[offset + cursor + 0] = tangent.X;
                buffer[offset + cursor + 1] = tangent.Y;
                buffer[offset + cursor + 2] = tangent.Z;
                buffer[offset + cursor + 3] = vertices[vertexIndex].BitangentSign;
                cursor += 4;
            }

            if (includeTexCoords)
            {
                for (int setIndex = 0; setIndex < mesh.TexCoordCount && cursor + 2 <= maxStride; setIndex++)
                {
                    List<Vector2>? texCoords = vertices[vertexIndex].TextureCoordinateSets;
                    Vector2 uv = texCoords is not null && texCoords.Count > setIndex ? texCoords[setIndex] : Vector2.Zero;
                    buffer[offset + cursor + 0] = uv.X;
                    buffer[offset + cursor + 1] = uv.Y;
                    cursor += 2;
                }
            }

            if (includeColors)
            {
                for (int setIndex = 0; setIndex < mesh.ColorCount && cursor + 4 <= maxStride; setIndex++)
                {
                    List<Vector4>? colors = vertices[vertexIndex].ColorSets;
                    Vector4 color = colors is not null && colors.Count > setIndex ? colors[setIndex] : Vector4.One;
                    buffer[offset + cursor + 0] = color.X;
                    buffer[offset + cursor + 1] = color.Y;
                    buffer[offset + cursor + 2] = color.Z;
                    buffer[offset + cursor + 3] = color.W;
                    cursor += 4;
                }
            }
        }

        return new AttributeBuffer(buffer, weights, maxStride, includeNormals, includeTangents, includeTexCoords, includeColors);
    }

    private static byte[]? BuildVertexLockBuffer(XRMesh mesh, Vertex[] vertices, MeshLodGenerationSettings settings)
    {
        bool needsLockBuffer = settings.ProtectAttributeSeams || settings.PrioritizeBorderVertices || (settings.LockWeightedVertices && mesh.HasSkinning);
        if (!needsLockBuffer)
            return null;

        byte[] locks = new byte[vertices.Length];

        if (settings.LockWeightedVertices && mesh.HasSkinning)
        {
            for (int i = 0; i < vertices.Length; i++)
            {
                if (vertices[i].Weights is { Count: > 0 })
                    locks[i] |= (byte)MeshOptimizerVertexLockFlags.Lock;
            }
        }

        HashSet<int> borderVertices = GetBorderVertexIndices(mesh.GetIndices(EPrimitiveType.Triangles) ?? []);
        if (settings.PrioritizeBorderVertices)
        {
            foreach (int vertexIndex in borderVertices)
                locks[vertexIndex] |= (byte)MeshOptimizerVertexLockFlags.Priority;
        }

        if (settings.ProtectAttributeSeams)
        {
            Dictionary<Vector3, List<int>> sharedPositions = [];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 position = vertices[i].Position;
                if (!sharedPositions.TryGetValue(position, out List<int>? group))
                {
                    group = [];
                    sharedPositions.Add(position, group);
                }

                group.Add(i);
            }

            foreach (List<int> group in sharedPositions.Values)
            {
                if (group.Count < 2)
                    continue;

                int referenceIndex = group[0];
                for (int i = 1; i < group.Count; i++)
                {
                    int vertexIndex = group[i];
                    if (!AttributesMatch(vertices[referenceIndex], vertices[vertexIndex]))
                    {
                        locks[referenceIndex] |= (byte)MeshOptimizerVertexLockFlags.Protect;
                        locks[vertexIndex] |= (byte)MeshOptimizerVertexLockFlags.Protect;
                    }
                }
            }
        }

        return locks.Any(static value => value != 0) ? locks : null;
    }

    private static bool AttributesMatch(Vertex left, Vertex right)
    {
        if (left.Normal != right.Normal || left.Tangent != right.Tangent)
            return false;

        if (!SequenceEqual(left.TextureCoordinateSets, right.TextureCoordinateSets))
            return false;

        return SequenceEqual(left.ColorSets, right.ColorSets);
    }

    private static bool SequenceEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null || left.Count != right.Count)
            return false;

        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < left.Count; i++)
        {
            if (!comparer.Equals(left[i], right[i]))
                return false;
        }

        return true;
    }

    private static HashSet<int> GetBorderVertexIndices(int[] indices)
    {
        Dictionary<(int A, int B), int> edgeCounts = [];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            CountEdge(indices[i], indices[i + 1], edgeCounts);
            CountEdge(indices[i + 1], indices[i + 2], edgeCounts);
            CountEdge(indices[i + 2], indices[i], edgeCounts);
        }

        HashSet<int> borderVertices = [];
        foreach ((int A, int B) edge in edgeCounts.Where(static pair => pair.Value == 1).Select(static pair => pair.Key))
        {
            borderVertices.Add(edge.A);
            borderVertices.Add(edge.B);
        }

        return borderVertices;
    }

    private static void CountEdge(int a, int b, Dictionary<(int A, int B), int> edgeCounts)
    {
        (int A, int B) key = a < b ? (a, b) : (b, a);
        edgeCounts.TryGetValue(key, out int count);
        edgeCounts[key] = count + 1;
    }

    private static int AlignIndexCount(int value)
    {
        if (value <= 0)
            return 0;

        return value - (value % 3);
    }

    private readonly record struct AttributeBuffer(
        float[] Buffer,
        float[] Weights,
        int Stride,
        bool IncludeNormals,
        bool IncludeTangents,
        bool IncludeTexCoords,
        bool IncludeColors);
}

