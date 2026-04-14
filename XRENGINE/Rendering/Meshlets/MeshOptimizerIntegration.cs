using System.Numerics;
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
}

public static class MeshOptimizerIntegration
{
    public static MeshletBuildResult BuildMeshlets(XRMesh mesh, MeshletGenerationSettings settings)
    {
        if (mesh is null)
            throw new ArgumentNullException(nameof(mesh));

        int[] indices = mesh.GetIndices(EPrimitiveType.Triangles) ?? [];
        if (indices.Length == 0 || mesh.VertexCount == 0)
        {
            return new MeshletBuildResult
            {
                Meshlets = [],
                VertexIndices = [],
                TriangleIndices = [],
                Vertices = [],
                Stats = new MeshOptimizerMeshletStats(0, 0, 0, 0),
            };
        }

        uint[] sourceIndices = new uint[indices.Length];
        for (int i = 0; i < indices.Length; i++)
            sourceIndices[i] = (uint)indices[i];

        MeshletVertex[] vertices = BuildMeshletVertices(mesh);

        uint minTriangles = settings.BuildMode is MeshletBuildMode.Flex or MeshletBuildMode.Spatial
            ? Math.Clamp(settings.MinTriangles, 1u, settings.MaxTriangles)
            : settings.MaxTriangles;

        nuint maxMeshlets = MeshOptimizerNative.BuildMeshletsBound((nuint)sourceIndices.Length, settings.MaxVertices, minTriangles);
        if (maxMeshlets == 0)
        {
            return new MeshletBuildResult
            {
                Meshlets = [],
                VertexIndices = [],
                TriangleIndices = [],
                Vertices = vertices,
                Stats = new MeshOptimizerMeshletStats(0, 0, 0, 0),
            };
        }

        MeshOptimizerNative.MeshoptMeshlet[] meshoptMeshlets = new MeshOptimizerNative.MeshoptMeshlet[(int)maxMeshlets];
        uint[] meshletVertices = new uint[sourceIndices.Length];
        byte[] meshletTriangles = new byte[sourceIndices.Length];

        nuint meshletCount = MeshOptimizerNative.BuildMeshlets(
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
            return new MeshletBuildResult
            {
                Meshlets = [],
                VertexIndices = [],
                TriangleIndices = [],
                Vertices = vertices,
                Stats = new MeshOptimizerMeshletStats(0, 0, 0, 0),
            };
        }

        int finalMeshletCount = (int)meshletCount;
        if (settings.OptimizeMeshlets)
        {
            for (int i = 0; i < finalMeshletCount; i++)
            {
                MeshOptimizerNative.MeshoptMeshlet meshlet = meshoptMeshlets[i];
                MeshOptimizerNative.OptimizeMeshletLevel(
                    meshletVertices.AsSpan((int)meshlet.VertexOffset, (int)meshlet.VertexCount),
                    meshletTriangles.AsSpan((int)meshlet.TriangleOffset, (int)meshlet.TriangleCount * 3),
                    settings.OptimizeLevel);
            }
        }

        MeshOptimizerNative.MeshoptMeshlet last = meshoptMeshlets[finalMeshletCount - 1];
        int vertexReferenceCount = (int)(last.VertexOffset + last.VertexCount);
        int triangleByteCount = (int)(last.TriangleOffset + last.TriangleCount * 3);

        Array.Resize(ref meshletVertices, vertexReferenceCount);
        Array.Resize(ref meshletTriangles, triangleByteCount);
        Array.Resize(ref meshoptMeshlets, finalMeshletCount);

        int encodedByteCount = 0;
        Meshlet[] results = new Meshlet[finalMeshletCount];
        float[] positionArray = GetPositionArray(mesh);
        for (int i = 0; i < finalMeshletCount; i++)
        {
            MeshOptimizerNative.MeshoptMeshlet meshlet = meshoptMeshlets[i];
            Vector4 sphere = settings.ComputeBounds
                ? ComputeMeshletSphere(meshletVertices, meshletTriangles, meshlet, positionArray, mesh.VertexCount)
                : ComputeFallbackBounds(mesh, meshletVertices, meshlet);

            if (settings.EncodeMeshlets)
            {
                uint[]? encodedVertices = settings.EncodeVertexReferences
                    ? meshletVertices.AsSpan((int)meshlet.VertexOffset, (int)meshlet.VertexCount).ToArray()
                    : null;
                byte[] encodedTriangles = meshletTriangles.AsSpan((int)meshlet.TriangleOffset, (int)meshlet.TriangleCount * 3).ToArray();
                encodedByteCount += MeshOptimizerNative.EncodeMeshlet(encodedVertices, encodedTriangles, (int)settings.MaxVertices, (int)settings.MaxTriangles);
            }

            results[i] = new Meshlet
            {
                BoundingSphere = sphere,
                VertexOffset = meshlet.VertexOffset,
                TriangleOffset = meshlet.TriangleOffset / 3u,
                VertexCount = meshlet.VertexCount,
                TriangleCount = meshlet.TriangleCount,
                MeshID = 0,
                MaterialID = 0,
                RenderPass = 0,
            };
        }

        return new MeshletBuildResult
        {
            Meshlets = results,
            VertexIndices = meshletVertices,
            TriangleIndices = meshletTriangles,
            Vertices = vertices,
            Stats = new MeshOptimizerMeshletStats(finalMeshletCount, vertexReferenceCount, triangleByteCount, encodedByteCount),
        };
    }

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

    private static Vector4 ComputeMeshletSphere(uint[] meshletVertices, byte[] meshletTriangles, MeshOptimizerNative.MeshoptMeshlet meshlet, float[] positions, int vertexCount)
    {
        MeshOptimizerNative.MeshoptBounds bounds = MeshOptimizerNative.ComputeMeshletBounds(
            meshletVertices.AsSpan((int)meshlet.VertexOffset, (int)meshlet.VertexCount),
            meshletTriangles.AsSpan((int)meshlet.TriangleOffset, (int)meshlet.TriangleCount * 3),
            positions,
            vertexCount);
        return new Vector4(bounds.CenterX, bounds.CenterY, bounds.CenterZ, bounds.Radius);
    }

    private static Vector4 ComputeFallbackBounds(XRMesh mesh, IReadOnlyList<uint> meshletVertices, MeshOptimizerNative.MeshoptMeshlet meshlet)
    {
        if (meshlet.VertexCount == 0)
            return Vector4.Zero;

        Vector3 center = Vector3.Zero;
        for (int i = 0; i < meshlet.VertexCount; i++)
            center += mesh.GetPosition(meshletVertices[(int)(meshlet.VertexOffset + i)]);
        center /= meshlet.VertexCount;

        float radius = 0.0f;
        for (int i = 0; i < meshlet.VertexCount; i++)
            radius = Math.Max(radius, Vector3.Distance(center, mesh.GetPosition(meshletVertices[(int)(meshlet.VertexOffset + i)])));
        return new Vector4(center, radius);
    }

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

internal static class MeshOptimizerNative
{
    private const string NativeLibraryName = "meshoptimizer";
    private const uint SimplifyPermissiveWithSeamsMask = (uint)MeshOptimizerSimplifyOptions.Permissive;
    private static readonly Lazy<nint> s_nativeLibraryHandle = new(LoadNativeLibraryHandle, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<MeshoptOptimizeMeshletLevelDelegate?> s_optimizeMeshletLevel = new(() => TryLoadExport<MeshoptOptimizeMeshletLevelDelegate>("meshopt_optimizeMeshletLevel"), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<MeshoptOptimizeMeshletDelegate?> s_optimizeMeshlet = new(() => TryLoadExport<MeshoptOptimizeMeshletDelegate>("meshopt_optimizeMeshlet"), LazyThreadSafetyMode.ExecutionAndPublication);
    private static int s_loggedLegacyOptimizeLevelFallback;
    private static int s_loggedMissingOptimizeMeshletExport;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void MeshoptOptimizeMeshletLevelDelegate(uint* meshletVertices, nuint vertexCount, byte* meshletTriangles, nuint triangleCount, int level);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void MeshoptOptimizeMeshletDelegate(uint* meshletVertices, byte* meshletTriangles, nuint triangleCount, nuint vertexCount);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MeshoptMeshlet
    {
        public uint VertexOffset;
        public uint TriangleOffset;
        public uint VertexCount;
        public uint TriangleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MeshoptBounds
    {
        public float CenterX;
        public float CenterY;
        public float CenterZ;
        public float Radius;
        public float ConeApexX;
        public float ConeApexY;
        public float ConeApexZ;
        public float ConeAxisX;
        public float ConeAxisY;
        public float ConeAxisZ;
        public float ConeCutoff;
        public sbyte ConeAxisS8X;
        public sbyte ConeAxisS8Y;
        public sbyte ConeAxisS8Z;
        public sbyte ConeCutoffS8;
    }

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_buildMeshletsBound")]
    private static extern nuint MeshoptBuildMeshletsBound(nuint indexCount, uint maxVertices, uint maxTriangles);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_buildMeshlets")]
    private static extern unsafe nuint MeshoptBuildMeshlets(
        MeshoptMeshlet* meshlets,
        uint* meshletVertices,
        byte* meshletTriangles,
        uint* indices,
        nuint indexCount,
        float* vertexPositions,
        nuint vertexCount,
        nuint vertexPositionsStride,
        uint maxVertices,
        uint maxTriangles,
        float coneWeight);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_buildMeshletsScan")]
    private static extern unsafe nuint MeshoptBuildMeshletsScan(
        MeshoptMeshlet* meshlets,
        uint* meshletVertices,
        byte* meshletTriangles,
        uint* indices,
        nuint indexCount,
        nuint vertexCount,
        uint maxVertices,
        uint maxTriangles);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_buildMeshletsFlex")]
    private static extern unsafe nuint MeshoptBuildMeshletsFlex(
        MeshoptMeshlet* meshlets,
        uint* meshletVertices,
        byte* meshletTriangles,
        uint* indices,
        nuint indexCount,
        float* vertexPositions,
        nuint vertexCount,
        nuint vertexPositionsStride,
        uint maxVertices,
        uint minTriangles,
        uint maxTriangles,
        float coneWeight,
        float splitFactor);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_buildMeshletsSpatial")]
    private static extern unsafe nuint MeshoptBuildMeshletsSpatial(
        MeshoptMeshlet* meshlets,
        uint* meshletVertices,
        byte* meshletTriangles,
        uint* indices,
        nuint indexCount,
        float* vertexPositions,
        nuint vertexCount,
        nuint vertexPositionsStride,
        uint maxVertices,
        uint minTriangles,
        uint maxTriangles,
        float fillWeight);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_computeMeshletBounds")]
    private static extern unsafe MeshoptBounds MeshoptComputeMeshletBounds(uint* meshletVertices, byte* meshletTriangles, nuint triangleCount, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_encodeMeshletBound")]
    private static extern nuint MeshoptEncodeMeshletBound(nuint maxVertices, nuint maxTriangles);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_encodeMeshlet")]
    private static extern unsafe nuint MeshoptEncodeMeshlet(byte* buffer, nuint bufferSize, uint* vertices, nuint vertexCount, byte* triangles, nuint triangleCount);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_simplify")]
    private static extern unsafe nuint MeshoptSimplify(uint* destination, uint* indices, nuint indexCount, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride, nuint targetIndexCount, float targetError, uint options, float* resultError);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_simplifyWithAttributes")]
    private static extern unsafe nuint MeshoptSimplifyWithAttributes(uint* destination, uint* indices, nuint indexCount, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride, float* vertexAttributes, nuint vertexAttributesStride, float* attributeWeights, nuint attributeCount, byte* vertexLock, nuint targetIndexCount, float targetError, uint options, float* resultError);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_simplifyWithUpdate")]
    private static extern unsafe nuint MeshoptSimplifyWithUpdate(uint* indices, nuint indexCount, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride, float* vertexAttributes, nuint vertexAttributesStride, float* attributeWeights, nuint attributeCount, byte* vertexLock, nuint targetIndexCount, float targetError, uint options, float* resultError);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_simplifySloppy")]
    private static extern unsafe nuint MeshoptSimplifySloppy(uint* destination, uint* indices, nuint indexCount, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride, byte* vertexLock, nuint targetIndexCount, float targetError, float* resultError);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_simplifyScale")]
    private static extern unsafe float MeshoptSimplifyScale(float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride);

    public static nuint BuildMeshletsBound(nuint indexCount, uint maxVertices, uint maxTriangles)
        => MeshoptBuildMeshletsBound(indexCount, maxVertices, maxTriangles);

    public static unsafe nuint BuildMeshlets(MeshletBuildMode buildMode, MeshoptMeshlet[] meshlets, uint[] meshletVertices, byte[] meshletTriangles, uint[] indices, float[] vertexPositions, nuint vertexCount, uint maxVertices, uint minTriangles, uint maxTriangles, float coneWeight, float splitFactor, float fillWeight)
    {
        fixed (MeshoptMeshlet* meshletPtr = meshlets)
        fixed (uint* meshletVerticesPtr = meshletVertices)
        fixed (byte* meshletTrianglesPtr = meshletTriangles)
        fixed (uint* indicesPtr = indices)
        fixed (float* positionsPtr = vertexPositions)
        {
            return buildMode switch
            {
                MeshletBuildMode.Scan => MeshoptBuildMeshletsScan(meshletPtr, meshletVerticesPtr, meshletTrianglesPtr, indicesPtr, (nuint)indices.Length, vertexCount, maxVertices, maxTriangles),
                MeshletBuildMode.Flex => MeshoptBuildMeshletsFlex(meshletPtr, meshletVerticesPtr, meshletTrianglesPtr, indicesPtr, (nuint)indices.Length, positionsPtr, vertexCount, sizeof(float) * 3u, maxVertices, minTriangles, maxTriangles, coneWeight, splitFactor),
                MeshletBuildMode.Spatial => MeshoptBuildMeshletsSpatial(meshletPtr, meshletVerticesPtr, meshletTrianglesPtr, indicesPtr, (nuint)indices.Length, positionsPtr, vertexCount, sizeof(float) * 3u, maxVertices, minTriangles, maxTriangles, fillWeight),
                _ => MeshoptBuildMeshlets(meshletPtr, meshletVerticesPtr, meshletTrianglesPtr, indicesPtr, (nuint)indices.Length, positionsPtr, vertexCount, sizeof(float) * 3u, maxVertices, maxTriangles, coneWeight),
            };
        }
    }

    public static unsafe void OptimizeMeshletLevel(Span<uint> meshletVertices, Span<byte> meshletTriangles, int level)
    {
        if (meshletVertices.IsEmpty || meshletTriangles.IsEmpty)
            return;

        fixed (uint* verticesPtr = meshletVertices)
        fixed (byte* trianglesPtr = meshletTriangles)
        {
            nuint triangleCount = (nuint)(meshletTriangles.Length / 3);
            if (triangleCount == 0)
                return;

            if (s_optimizeMeshletLevel.Value is { } optimizeMeshletLevel)
            {
                optimizeMeshletLevel(verticesPtr, (nuint)meshletVertices.Length, trianglesPtr, triangleCount, level);
                return;
            }

            if (s_optimizeMeshlet.Value is { } optimizeMeshlet)
            {
                optimizeMeshlet(verticesPtr, trianglesPtr, triangleCount, (nuint)meshletVertices.Length);

                if (level != 0 && Interlocked.Exchange(ref s_loggedLegacyOptimizeLevelFallback, 1) == 0)
                {
                    Debug.LogWarning($"meshoptimizer native DLL does not expose 'meshopt_optimizeMeshletLevel'; falling back to legacy 'meshopt_optimizeMeshlet'. OptimizeLevel={level} will be ignored.");
                }

                return;
            }

            if (Interlocked.Exchange(ref s_loggedMissingOptimizeMeshletExport, 1) == 0)
            {
                Debug.LogWarning("meshoptimizer native DLL does not expose meshlet optimization exports. Meshlets will be built without the optional post-build optimization pass.");
            }
        }
    }

    private static nint LoadNativeLibraryHandle()
    {
        try
        {
            if (NativeLibrary.TryLoad(NativeLibraryName, typeof(MeshOptimizerNative).Assembly, null, out IntPtr assemblyHandle)
                && assemblyHandle != IntPtr.Zero)
            {
                return assemblyHandle;
            }
        }
        catch
        {
        }

        try
        {
            if (NativeLibrary.TryLoad(NativeLibraryName, out IntPtr defaultHandle) && defaultHandle != IntPtr.Zero)
                return defaultHandle;
        }
        catch
        {
        }

        return IntPtr.Zero;
    }

    private static T? TryLoadExport<T>(string exportName) where T : Delegate
    {
        IntPtr libraryHandle = s_nativeLibraryHandle.Value;
        if (libraryHandle == IntPtr.Zero
            || !NativeLibrary.TryGetExport(libraryHandle, exportName, out IntPtr exportPtr)
            || exportPtr == IntPtr.Zero)
        {
            return null;
        }

        return Marshal.GetDelegateForFunctionPointer<T>(exportPtr);
    }

    public static unsafe MeshoptBounds ComputeMeshletBounds(ReadOnlySpan<uint> meshletVertices, ReadOnlySpan<byte> meshletTriangles, float[] vertexPositions, int vertexCount)
    {
        fixed (uint* verticesPtr = meshletVertices)
        fixed (byte* trianglesPtr = meshletTriangles)
        fixed (float* positionsPtr = vertexPositions)
            return MeshoptComputeMeshletBounds(verticesPtr, trianglesPtr, (nuint)(meshletTriangles.Length / 3), positionsPtr, (nuint)vertexCount, sizeof(float) * 3u);
    }

    public static unsafe int EncodeMeshlet(uint[]? vertices, byte[] triangles, int maxVertices, int maxTriangles)
    {
        int bufferSize = (int)MeshoptEncodeMeshletBound((nuint)maxVertices, (nuint)maxTriangles);
        if (bufferSize == 0)
            return 0;

        byte[] buffer = new byte[bufferSize];
        fixed (byte* bufferPtr = buffer)
        fixed (byte* trianglesPtr = triangles)
        {
            if (vertices is null || vertices.Length == 0)
                return (int)MeshoptEncodeMeshlet(bufferPtr, (nuint)buffer.Length, null, 0, trianglesPtr, (nuint)(triangles.Length / 3));

            fixed (uint* verticesPtr = vertices)
                return (int)MeshoptEncodeMeshlet(bufferPtr, (nuint)buffer.Length, verticesPtr, (nuint)vertices.Length, trianglesPtr, (nuint)(triangles.Length / 3));
        }
    }

    public static unsafe int Simplify(uint[] indices, float[] positions, int vertexCount, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, out float resultError)
    {
        fixed (uint* destinationPtr = indices)
        fixed (uint* indicesPtr = indices)
        fixed (float* positionsPtr = positions)
        fixed (float* errorPtr = &resultError)
            return (int)MeshoptSimplify(destinationPtr, indicesPtr, (nuint)indices.Length, positionsPtr, (nuint)vertexCount, sizeof(float) * 3u, (nuint)targetIndexCount, targetError, (uint)options, errorPtr);
    }

    public static unsafe int SimplifyWithAttributes(uint[] indices, float[] positions, int vertexCount, float[] attributeBuffer, int attributeStride, float[] attributeWeights, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, byte[]? vertexLock, out float resultError)
    {
        fixed (uint* destinationPtr = indices)
        fixed (uint* indicesPtr = indices)
        fixed (float* positionsPtr = positions)
        fixed (float* attributesPtr = attributeBuffer)
        fixed (float* weightsPtr = attributeWeights)
        fixed (float* errorPtr = &resultError)
        {
            if (vertexLock is null)
                return (int)MeshoptSimplifyWithAttributes(destinationPtr, indicesPtr, (nuint)indices.Length, positionsPtr, (nuint)vertexCount, sizeof(float) * 3u, attributesPtr, (nuint)(attributeStride * sizeof(float)), weightsPtr, (nuint)attributeWeights.Length, null, (nuint)targetIndexCount, targetError, (uint)options, errorPtr);

            fixed (byte* lockPtr = vertexLock)
                return (int)MeshoptSimplifyWithAttributes(destinationPtr, indicesPtr, (nuint)indices.Length, positionsPtr, (nuint)vertexCount, sizeof(float) * 3u, attributesPtr, (nuint)(attributeStride * sizeof(float)), weightsPtr, (nuint)attributeWeights.Length, lockPtr, (nuint)targetIndexCount, targetError, (uint)options, errorPtr);
        }
    }

    public static unsafe int SimplifyWithUpdate(uint[] indices, float[] positions, int vertexCount, float[] attributeBuffer, int attributeStride, float[] attributeWeights, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, byte[]? vertexLock, out float resultError)
    {
        fixed (uint* indicesPtr = indices)
        fixed (float* positionsPtr = positions)
        fixed (float* attributesPtr = attributeBuffer)
        fixed (float* weightsPtr = attributeWeights)
        fixed (float* errorPtr = &resultError)
        {
            if (vertexLock is null)
                return (int)MeshoptSimplifyWithUpdate(indicesPtr, (nuint)indices.Length, positionsPtr, (nuint)vertexCount, sizeof(float) * 3u, attributesPtr, (nuint)(attributeStride * sizeof(float)), weightsPtr, (nuint)attributeWeights.Length, null, (nuint)targetIndexCount, targetError, (uint)options, errorPtr);

            fixed (byte* lockPtr = vertexLock)
                return (int)MeshoptSimplifyWithUpdate(indicesPtr, (nuint)indices.Length, positionsPtr, (nuint)vertexCount, sizeof(float) * 3u, attributesPtr, (nuint)(attributeStride * sizeof(float)), weightsPtr, (nuint)attributeWeights.Length, lockPtr, (nuint)targetIndexCount, targetError, (uint)options, errorPtr);
        }
    }

    public static unsafe int SimplifySloppy(uint[] indices, float[] positions, int vertexCount, int targetIndexCount, float targetError, byte[]? vertexLock, out float resultError)
    {
        fixed (uint* destinationPtr = indices)
        fixed (uint* indicesPtr = indices)
        fixed (float* positionsPtr = positions)
        fixed (float* errorPtr = &resultError)
        {
            if (vertexLock is null)
                return (int)MeshoptSimplifySloppy(destinationPtr, indicesPtr, (nuint)indices.Length, positionsPtr, (nuint)vertexCount, sizeof(float) * 3u, null, (nuint)targetIndexCount, targetError, errorPtr);

            fixed (byte* lockPtr = vertexLock)
                return (int)MeshoptSimplifySloppy(destinationPtr, indicesPtr, (nuint)indices.Length, positionsPtr, (nuint)vertexCount, sizeof(float) * 3u, lockPtr, (nuint)targetIndexCount, targetError, errorPtr);
        }
    }

    public static unsafe float SimplifyScale(float[] positions, int vertexCount)
    {
        fixed (float* positionsPtr = positions)
            return MeshoptSimplifyScale(positionsPtr, (nuint)vertexCount, sizeof(float) * 3u);
    }
}