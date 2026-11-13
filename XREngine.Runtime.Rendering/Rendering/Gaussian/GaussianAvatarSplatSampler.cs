using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;

namespace XREngine.Rendering.Gaussian;

/// <summary>
/// Stratifies dense surface-aligned 3D Gaussian splat samples across all active triangle submeshes of an avatar
/// hierarchy, tagging each sample with its source submesh material, UVs, UV edge derivatives, and rest-pose area.
/// </summary>
internal static class GaussianAvatarSplatSampler
{
    private const float BaseSplatOpacity = 0.995f;
    private const float SigmaOverlapFactor = 1.22f;
    private const float MinRestSigma = 0.00008f;
    private const float MaxRestSigma = 0.015f;
    private const double R2Alpha1 = 0.7548776662466927;
    private const double R2Alpha2 = 0.5698402909980532;

    /// <summary>
    /// Samples <paramref name="targetSplatCount"/> surface splats across active <see cref="RenderableComponent"/>
    /// submeshes under <paramref name="avatarRoot"/>, uploads one <see cref="GaussianAvatarSubmeshSlice"/> sample
    /// buffer per submesh, and returns the slice list.
    /// </summary>
    public static List<GaussianAvatarSubmeshSlice> SampleAvatar(
        SceneNode avatarRoot,
        int targetSplatCount,
        GaussianAvatarSplatMaterialTable materialTable,
        out int totalSplats)
    {
        ArgumentNullException.ThrowIfNull(avatarRoot);
        ArgumentNullException.ThrowIfNull(materialTable);

        int clampedBudget = Math.Clamp(targetSplatCount, 1_000, GaussianSplatRenderer.MaxCapacity);
        avatarRoot.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);

        List<PreparedSubmesh> preparedSubmeshes = [];
        double grandTotalArea = 0.0;

        avatarRoot.IterateComponents<RenderableComponent>(component =>
        {
            if (!IsComponentRendered(component, avatarRoot))
                return;

            if (component is ModelComponent modelComponent)
            {
                if (modelComponent.Meshes.Count == 0)
                    modelComponent.RebuildRuntimeMeshes();
                modelComponent.EnsureDefaultBlendShapeWeightsApplied();
            }

            Vector3 worldScale = Vector3.Abs(component.Transform.LossyWorldScale);
            float unskinnedAreaScale = MathF.Max(worldScale.X * worldScale.Y, MathF.Max(worldScale.Y * worldScale.Z, worldScale.X * worldScale.Z));
            if (!float.IsFinite(unskinnedAreaScale) || unskinnedAreaScale <= 1.0e-8f)
                unskinnedAreaScale = 1.0f;

            foreach (RenderableMesh renderableMesh in component.Meshes)
            {
                XRMeshRenderer? sourceRenderer = renderableMesh.LODs.First?.Value.Renderer;
                XRMesh? mesh = sourceRenderer?.Mesh;
                if (sourceRenderer is null ||
                    mesh is null ||
                    mesh.VertexCount <= 0 ||
                    mesh.Type != EPrimitiveType.Triangles ||
                    mesh.Triangles is not { Count: > 0 } triangles)
                {
                    continue;
                }

                XRMaterial? sourceMaterial = renderableMesh.MaterialOverride ?? sourceRenderer.Material;
                uint materialDenseIndex = materialTable.GetOrAddMaterial(
                    sourceMaterial,
                    out uint cullMode,
                    out ushort udimDiscardMask,
                    out int udimDiscardMode,
                    out int udimDiscardUv);
                uint packedMaterialAndFlags = (materialDenseIndex & 0xFFFFu) | ((cullMode & 0x3u) << 16);

                float areaMultiplier = mesh.HasSkinning ? 1.0f : unskinnedAreaScale;
                Vector3[]? deformedRestPositions = TryBuildBlendshapeRestPositions(sourceRenderer, mesh);
                List<PreparedTriangle> validTriangles = new(triangles.Count);
                List<double> cumulativeAreas = new(triangles.Count);
                double submeshArea = 0.0;
                int vertexCount = mesh.VertexCount;

                for (int triIdx = 0; triIdx < triangles.Count; triIdx++)
                {
                    IndexTriangle tri = triangles[triIdx];
                    int a = tri.Point0;
                    int b = tri.Point1;
                    int c = tri.Point2;
                    if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
                        continue;

                    uint i0 = (uint)a;
                    uint i1 = (uint)b;
                    uint i2 = (uint)c;

                    if (udimDiscardMask != 0 && udimDiscardMode == 0 && IsTriangleUdimDiscarded(mesh, i0, i1, i2, udimDiscardMask, udimDiscardUv))
                        continue;

                    Vector3 p0 = GetRestPosition(mesh, deformedRestPositions, i0);
                    Vector3 p1 = GetRestPosition(mesh, deformedRestPositions, i1);
                    Vector3 p2 = GetRestPosition(mesh, deformedRestPositions, i2);
                    Vector3 e1 = p1 - p0;
                    Vector3 e2 = p2 - p0;
                    Vector3 e3 = p2 - p1;
                    float maxEdgeSq = MathF.Max(e1.LengthSquared(), MathF.Max(e2.LengthSquared(), e3.LengthSquared()));
                    float rawDoubleArea = Vector3.Cross(e1, e2).Length();
                    if (!float.IsFinite(rawDoubleArea) || rawDoubleArea <= 1.0e-11f || rawDoubleArea < 0.004f * maxEdgeSq)
                        continue;

                    float restDoubleArea = rawDoubleArea * areaMultiplier;

                    double triArea = 0.5 * restDoubleArea;
                    submeshArea += triArea;
                    validTriangles.Add(new PreparedTriangle(i0, i1, i2, restDoubleArea));
                    cumulativeAreas.Add(submeshArea);
                }

                if (validTriangles.Count == 0 || submeshArea <= 1.0e-11)
                    continue;

                grandTotalArea += submeshArea;
                preparedSubmeshes.Add(new PreparedSubmesh(
                    component,
                    renderableMesh,
                    sourceRenderer,
                    mesh,
                    deformedRestPositions,
                    packedMaterialAndFlags,
                    udimDiscardMask,
                    udimDiscardMode,
                    udimDiscardUv,
                    validTriangles,
                    cumulativeAreas,
                    submeshArea));
            }
        }, iterateChildHierarchy: true);

        if (preparedSubmeshes.Count == 0 || grandTotalArea <= 1.0e-11)
        {
            totalSplats = 0;
            return [];
        }

        int[] counts = AllocateSubmeshCounts(preparedSubmeshes, clampedBudget, grandTotalArea);
        List<GaussianAvatarSubmeshSlice> slices = new(preparedSubmeshes.Count);
        uint nextSplatSlot = 0u;

        for (int sliceIdx = 0; sliceIdx < preparedSubmeshes.Count; sliceIdx++)
        {
            int targetSliceSamples = counts[sliceIdx];
            if (targetSliceSamples <= 0)
                continue;

            PreparedSubmesh submesh = preparedSubmeshes[sliceIdx];
            List<GaussianLiveSurfaceGpuSample> samples = BuildSubmeshSamples(submesh, targetSliceSamples, nextSplatSlot);
            if (samples.Count == 0)
                continue;

            XRDataBuffer samplesBuffer = new($"GaussianAvatarSamples_{sliceIdx}", EBufferTarget.ShaderStorageBuffer, false)
            {
                Usage = EBufferUsage.StaticDraw,
                DisposeOnPush = false,
                PadEndingToVec4 = true,
            };
            samplesBuffer.EnsureRawCapacity<GaussianLiveSurfaceGpuSample>((uint)samples.Count, growCapacityGeometrically: false);
            uint byteLength = samplesBuffer.WriteDataRaw<GaussianLiveSurfaceGpuSample>(CollectionsMarshal.AsSpan(samples), 0u);

            slices.Add(new GaussianAvatarSubmeshSlice(
                submesh.Component,
                submesh.RenderableMesh,
                submesh.SourceRenderer,
                submesh.SourceMesh,
                samplesBuffer,
                (uint)samples.Count,
                nextSplatSlot,
                byteLength));
            nextSplatSlot += (uint)samples.Count;
        }

        totalSplats = checked((int)nextSplatSlot);
        return slices;
    }

    private static List<GaussianLiveSurfaceGpuSample> BuildSubmeshSamples(
        PreparedSubmesh submesh,
        int sampleCount,
        uint baseSplatSlot)
    {
        XRMesh mesh = submesh.SourceMesh;
        List<PreparedTriangle> triangles = submesh.Triangles;
        List<double> cumulativeAreas = submesh.CumulativeAreas;
        double totalArea = submesh.TotalArea;

        int[] perTriangleCounts = new int[triangles.Count];
        int triCursor = 0;
        for (int s = 0; s < sampleCount; s++)
        {
            double target = ((s + 0.5) / sampleCount) * totalArea;
            while (triCursor + 1 < cumulativeAreas.Count && cumulativeAreas[triCursor] < target)
                triCursor++;
            perTriangleCounts[triCursor]++;
        }

        bool hasTexCoord0 = mesh.TexCoordCount > 0;
        bool hasTexCoord1 = mesh.TexCoordCount > 1;
        bool hasTexCoord2 = mesh.TexCoordCount > 2;
        bool hasTexCoord3 = mesh.TexCoordCount > 3;
        bool hasColors = mesh.ColorCount > 0;
        bool hasTangents = mesh.HasTangents;

        List<GaussianLiveSurfaceGpuSample> samples = new(sampleCount);
        uint currentSlot = baseSplatSlot;

        for (int triIdx = 0; triIdx < triangles.Count; triIdx++)
        {
            int countInTri = perTriangleCounts[triIdx];
            if (countInTri <= 0)
                continue;

            PreparedTriangle tri = triangles[triIdx];
            Vector3 p0 = GetRestPosition(mesh, submesh.DeformedRestPositions, tri.Index0);
            Vector3 p1 = GetRestPosition(mesh, submesh.DeformedRestPositions, tri.Index1);
            Vector3 p2 = GetRestPosition(mesh, submesh.DeformedRestPositions, tri.Index2);

            Vector2 uv0A = hasTexCoord0 ? mesh.GetTexCoord(tri.Index0, 0u) : Vector2.Zero;
            Vector2 uv0B = hasTexCoord0 ? mesh.GetTexCoord(tri.Index1, 0u) : Vector2.Zero;
            Vector2 uv0C = hasTexCoord0 ? mesh.GetTexCoord(tri.Index2, 0u) : Vector2.Zero;

            Vector2 uv1A = hasTexCoord1 ? mesh.GetTexCoord(tri.Index0, 1u) : Vector2.Zero;
            Vector2 uv1B = hasTexCoord1 ? mesh.GetTexCoord(tri.Index1, 1u) : Vector2.Zero;
            Vector2 uv1C = hasTexCoord1 ? mesh.GetTexCoord(tri.Index2, 1u) : Vector2.Zero;

            Vector2 uv2A = hasTexCoord2 ? mesh.GetTexCoord(tri.Index0, 2u) : Vector2.Zero;
            Vector2 uv2B = hasTexCoord2 ? mesh.GetTexCoord(tri.Index1, 2u) : Vector2.Zero;
            Vector2 uv2C = hasTexCoord2 ? mesh.GetTexCoord(tri.Index2, 2u) : Vector2.Zero;

            Vector2 uv3A = hasTexCoord3 ? mesh.GetTexCoord(tri.Index0, 3u) : Vector2.Zero;
            Vector2 uv3B = hasTexCoord3 ? mesh.GetTexCoord(tri.Index1, 3u) : Vector2.Zero;
            Vector2 uv3C = hasTexCoord3 ? mesh.GetTexCoord(tri.Index2, 3u) : Vector2.Zero;

            Vector4 c0 = hasColors ? mesh.GetColor(tri.Index0, 0u) : Vector4.One;
            Vector4 c1 = hasColors ? mesh.GetColor(tri.Index1, 0u) : Vector4.One;
            Vector4 c2 = hasColors ? mesh.GetColor(tri.Index2, 0u) : Vector4.One;

            float tangentSign = 1.0f;
            if (hasTangents)
            {
                float wSum = mesh.GetTangentWithSign(tri.Index0).W +
                             mesh.GetTangentWithSign(tri.Index1).W +
                             mesh.GetTangentWithSign(tri.Index2).W;
                tangentSign = wSum >= 0.0f ? 1.0f : -1.0f;
            }

            Vector4 uv0Edges = new(
                uv0B.X - uv0A.X,
                uv0B.Y - uv0A.Y,
                uv0C.X - uv0A.X,
                uv0C.Y - uv0A.Y);

            float restArea = 0.5f * tri.RestDoubleArea;
            float invSqrtRestDoubleArea = 1.0f / MathF.Sqrt(MathF.Max(tri.RestDoubleArea, 1.0e-12f));
            float spacing = MathF.Sqrt(restArea / Math.Max(countInTri, 1));
            float restSigma = Math.Clamp(spacing * SigmaOverlapFactor, MinRestSigma, MaxRestSigma);

            double seedPhase = HashToUnitDouble((uint)triIdx * 747796405u + 2891336453u);
            for (int localSample = 0; localSample < countInTri; localSample++)
            {
                float b1;
                float b2;
                if (countInTri == 1)
                {
                    b1 = 1.0f / 3.0f;
                    b2 = 1.0f / 3.0f;
                }
                else
                {
                    double u = Fract(seedPhase + ((localSample + 0.5) * R2Alpha1));
                    double v = Fract((seedPhase * 0.6180339887498949) + ((localSample + 0.5) * R2Alpha2));
                    if (u + v > 1.0)
                    {
                        u = 1.0 - u;
                        v = 1.0 - v;
                    }

                    b1 = (float)u;
                    b2 = (float)v;
                }

                float b0 = MathF.Max(0.0f, 1.0f - b1 - b2);
                Vector2 uv0 = (b0 * uv0A) + (b1 * uv0B) + (b2 * uv0C);
                Vector2 uv1 = (b0 * uv1A) + (b1 * uv1B) + (b2 * uv1C);
                Vector2 uv2 = (b0 * uv2A) + (b1 * uv2B) + (b2 * uv2C);
                Vector2 uv3 = (b0 * uv3A) + (b1 * uv3B) + (b2 * uv3C);

                if (submesh.UdimDiscardMask != 0 && submesh.UdimDiscardMode == 1)
                {
                    uint discardChannel = (uint)Math.Max(submesh.UdimDiscardUv, 0);
                    Vector2 discardUv = discardChannel switch
                    {
                        0u => uv0,
                        1u => uv1,
                        2u => uv2,
                        3u => uv3,
                        _ when discardChannel < mesh.TexCoordCount =>
                            (b0 * mesh.GetTexCoord(tri.Index0, discardChannel)) +
                            (b1 * mesh.GetTexCoord(tri.Index1, discardChannel)) +
                            (b2 * mesh.GetTexCoord(tri.Index2, discardChannel)),
                        _ => Vector2.Zero,
                    };
                    if (IsUvInDiscardedTile(discardUv, submesh.UdimDiscardMask))
                        continue;
                }

                Vector3 localPos = (b0 * p0) + (b1 * p1) + (b2 * p2);
                Vector4 color = hasColors ? ((b0 * c0) + (b1 * c1) + (b2 * c2)) : Vector4.One;
                uint packedColor = PackUnorm4x8(color);

                samples.Add(new GaussianLiveSurfaceGpuSample(
                    tri.Index0,
                    tri.Index1,
                    tri.Index2,
                    currentSlot++,
                    b1,
                    b2,
                    invSqrtRestDoubleArea,
                    BaseSplatOpacity,
                    submesh.PackedMaterialAndFlags,
                    packedColor,
                    restSigma,
                    restSigma,
                    new Vector4(uv0.X, uv0.Y, uv1.X, uv1.Y),
                    new Vector4(uv2.X, uv2.Y, uv3.X, uv3.Y),
                    localPos,
                    tangentSign,
                    uv0Edges));
            }
        }

        return samples;
    }

    private static int[] AllocateSubmeshCounts(
        List<PreparedSubmesh> submeshes,
        int totalBudget,
        double grandTotalArea)
    {
        int n = submeshes.Count;
        int[] counts = new int[n];
        double[] remainders = new double[n];
        int assigned = 0;

        for (int i = 0; i < n; i++)
        {
            double exact = (submeshes[i].TotalArea / grandTotalArea) * totalBudget;
            int floor = Math.Max(1, (int)Math.Floor(exact));
            counts[i] = floor;
            remainders[i] = exact - floor;
            assigned += floor;
        }

        while (assigned < totalBudget)
        {
            int bestIdx = 0;
            double bestRem = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                if (remainders[i] > bestRem)
                {
                    bestRem = remainders[i];
                    bestIdx = i;
                }
            }

            counts[bestIdx]++;
            remainders[bestIdx] -= 1.0;
            assigned++;
        }

        while (assigned > totalBudget)
        {
            int worstIdx = -1;
            int highestCount = 1;
            for (int i = 0; i < n; i++)
            {
                if (counts[i] > highestCount)
                {
                    highestCount = counts[i];
                    worstIdx = i;
                }
            }

            if (worstIdx < 0)
                break;

            counts[worstIdx]--;
            assigned--;
        }

        return counts;
    }

    private static bool IsTriangleUdimDiscarded(
        XRMesh mesh,
        uint i0,
        uint i1,
        uint i2,
        ushort mask,
        int uvChannel)
    {
        uint channel = (uint)Math.Max(uvChannel, 0);
        if (mesh.TexCoordCount <= channel)
            return IsUvInDiscardedTile(Vector2.Zero, mask);

        return IsUvInDiscardedTile(mesh.GetTexCoord(i0, channel), mask) &&
               IsUvInDiscardedTile(mesh.GetTexCoord(i1, channel), mask) &&
               IsUvInDiscardedTile(mesh.GetTexCoord(i2, channel), mask);
    }

    private static bool IsUvInDiscardedTile(Vector2 uv, ushort mask)
    {
        int col = (int)MathF.Floor(uv.X);
        int row = (int)MathF.Floor(uv.Y);
        if ((uint)col >= 4u || (uint)row >= 4u)
            return false;
        return (mask & (1 << ((row * 4) + col))) != 0;
    }

    private static bool IsComponentRendered(XRComponent component, SceneNode root)
    {
        if (!component.IsActive)
            return false;

        for (SceneNode? node = component.SceneNode; node is not null; node = node.Parent)
        {
            if (!node.IsActiveSelf)
                return false;
            if (ReferenceEquals(node, root))
                break;
        }

        return true;
    }

    private static uint PackUnorm4x8(Vector4 color)
    {
        uint r = (uint)Math.Clamp((int)MathF.Round(color.X * 255.0f), 0, 255);
        uint g = (uint)Math.Clamp((int)MathF.Round(color.Y * 255.0f), 0, 255);
        uint b = (uint)Math.Clamp((int)MathF.Round(color.Z * 255.0f), 0, 255);
        uint a = (uint)Math.Clamp((int)MathF.Round(color.W * 255.0f), 0, 255);
        return r | (g << 8) | (b << 16) | (a << 24);
    }

    private static double Fract(double x)
        => x - Math.Floor(x);

    private static double HashToUnitDouble(uint state)
    {
        state ^= state >> 16;
        state *= 0x7feb352du;
        state ^= state >> 15;
        state *= 0x846ca68bu;
        state ^= state >> 16;
        return state / 4294967296.0;
    }

    private static Vector3 GetRestPosition(XRMesh mesh, Vector3[]? deformedPositions, uint index)
        => deformedPositions is not null && index < (uint)deformedPositions.Length
            ? deformedPositions[index]
            : mesh.GetPosition(index);

    /// <summary>
    /// Evaluates the renderer's initial authored blendshape weights on the CPU so rest-area triangle
    /// sampling and tangent-frame scales match clothing-shrink and body-proportion morphs.
    /// </summary>
    private static Vector3[]? TryBuildBlendshapeRestPositions(XRMeshRenderer sourceRenderer, XRMesh mesh)
    {
        if (!mesh.HasBlendshapeDeformation || !XRMeshBlendshapeDeltaTable.TryCreate(mesh, out XRMeshBlendshapeDeltaTable table))
            return null;

        int shapeCount = table.ShapeCount;
        if (shapeCount <= 0)
            return null;

        bool anyActiveShape = false;
        for (int shape = 0; shape < shapeCount; shape++)
        {
            if (MathF.Abs(sourceRenderer.GetBlendshapeWeightNormalized(mesh, (uint)shape)) > 1.0e-5f)
            {
                anyActiveShape = true;
                break;
            }
        }

        if (!anyActiveShape)
            return null;

        int vertexCount = mesh.VertexCount;
        Vector3[] positions = new Vector3[vertexCount];
        for (int v = 0; v < vertexCount; v++)
            positions[v] = mesh.GetPosition((uint)v);

        ReadOnlySpan<int> recordVertices = table.RecordVertices;
        ReadOnlySpan<Vector3> positionDeltas = table.PositionDeltas;
        ReadOnlySpan<byte> flags = table.Flags;

        for (int shape = 0; shape < shapeCount; shape++)
        {
            float weight = sourceRenderer.GetBlendshapeWeightNormalized(mesh, (uint)shape);
            if (MathF.Abs(weight) <= 1.0e-5f)
                continue;

            int first = table.GetShapeRecordOffset(shape);
            int end = first + table.GetShapeRecordCount(shape);
            for (int record = first; record < end; record++)
            {
                if ((flags[record] & XRMeshBlendshapeDeltaTable.PositionFlag) == 0)
                    continue;

                int vertex = recordVertices[record];
                if ((uint)vertex < (uint)vertexCount)
                    positions[vertex] += positionDeltas[record] * weight;
            }
        }

        return positions;
    }

    private readonly record struct PreparedTriangle(
        uint Index0,
        uint Index1,
        uint Index2,
        float RestDoubleArea);

    private sealed record PreparedSubmesh(
        RenderableComponent Component,
        RenderableMesh RenderableMesh,
        XRMeshRenderer SourceRenderer,
        XRMesh SourceMesh,
        Vector3[]? DeformedRestPositions,
        uint PackedMaterialAndFlags,
        ushort UdimDiscardMask,
        int UdimDiscardMode,
        int UdimDiscardUv,
        List<PreparedTriangle> Triangles,
        List<double> CumulativeAreas,
        double TotalArea);
}
