using System;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class BlendshapeGpuEfficiencyTests
{
    [Test]
    public void NewBlendshapeRenderer_StartsWithNoActiveBlendshapes()
    {
        XRMeshRenderer renderer = CreateBlendshapeRenderer();

        renderer.ActiveBlendshapeCount.ShouldBe(0);
        renderer.HasActiveBlendshapes.ShouldBeFalse();
    }

    [Test]
    public void PushBlendshapeWeightsToGpu_UploadsSingleDirtyWeightAndActivePair()
    {
        XRMeshRenderer renderer = CreateBlendshapeRenderer();
        renderer.BlendshapeWeights.ShouldNotBeNull();
        renderer.BlendshapeActiveWeights.ShouldNotBeNull();

        (int offset, uint length)? weightUpload = null;
        (int offset, uint length)? activeUpload = null;
        renderer.BlendshapeWeights!.PushSubDataRequested += (offset, length) => weightUpload = (offset, length);
        renderer.BlendshapeActiveWeights!.PushSubDataRequested += (offset, length) => activeUpload = (offset, length);

        renderer.SetBlendshapeWeightNormalized(1u, 0.5f);
        renderer.PushBlendshapeWeightsToGPU();

        renderer.ActiveBlendshapeCount.ShouldBe(1);
        renderer.HasActiveBlendshapes.ShouldBeTrue();
        weightUpload.ShouldBe((sizeof(float), (uint)sizeof(float)));
        activeUpload.ShouldBe((0, renderer.BlendshapeActiveWeights.ElementSize));
        renderer.BlendshapeActiveWeights.GetVector2(0u).ShouldBe(new Vector2(1.0f, 0.5f));
    }

    [Test]
    public void PushBlendshapeWeightsToGpu_UploadsContiguousDirtyRange()
    {
        XRMeshRenderer renderer = CreateBlendshapeRenderer();
        renderer.BlendshapeWeights.ShouldNotBeNull();

        (int offset, uint length)? weightUpload = null;
        renderer.BlendshapeWeights!.PushSubDataRequested += (offset, length) => weightUpload = (offset, length);

        renderer.SetBlendshapeWeightNormalized(0u, 0.25f);
        renderer.SetBlendshapeWeightNormalized(2u, -0.75f);
        renderer.PushBlendshapeWeightsToGPU();

        renderer.ActiveBlendshapeCount.ShouldBe(2);
        weightUpload.ShouldBe((0, (uint)(sizeof(float) * 3)));
    }

    [Test]
    public void SettingWeightBackToZeroClearsActiveList()
    {
        XRMeshRenderer renderer = CreateBlendshapeRenderer();

        renderer.SetBlendshapeWeightNormalized(0u, 1.0f);
        renderer.ActiveBlendshapeCount.ShouldBe(1);

        renderer.SetBlendshapeWeightNormalized(0u, 0.0f);

        renderer.ActiveBlendshapeCount.ShouldBe(0);
        renderer.HasActiveBlendshapes.ShouldBeFalse();
    }

    [Test]
    public void BlendshapeLodDisabledTierSuppressesActiveShapes()
    {
        XRMeshRenderer renderer = CreateBlendshapeRenderer();
        renderer.BlendshapeLodProfile = new BlendshapeLodProfile(new BlendshapeLodTier(BlendshapeLodEvaluation.Disabled));
        renderer.ActiveBlendshapeLodTier = 0;

        renderer.SetBlendshapeWeightNormalized(0u, 1.0f);

        renderer.ActiveBlendshapeCount.ShouldBe(0);
        renderer.HasActiveBlendshapes.ShouldBeFalse();
    }

    [Test]
    public void RebuildBlendshapeBuffers_CreatesSparseAndQuantizedPayloads()
    {
        XRMesh mesh = CreateBlendshapeMesh();

        AssertSparseQuantizedBlendshapePayload(mesh);
    }

    [Test]
    public void RuntimeCookedBinarySerializer_RoundTripsSparseQuantizedBlendshapePayload()
    {
        XRMesh mesh = CreateBlendshapeMesh();

        byte[] bytes = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
            () => RuntimeCookedBinarySerializer.Serialize(mesh));
        XRMesh? clone = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
            () => RuntimeCookedBinarySerializer.Deserialize(typeof(XRMesh), bytes) as XRMesh);

        clone.ShouldNotBeNull();
        AssertSparseQuantizedBlendshapePayload(clone!);
        clone!.BlendshapeNames.ShouldBe(mesh.BlendshapeNames);
        clone.BlendshapeSparseRecordCount.ShouldBe(mesh.BlendshapeSparseRecordCount);
        clone.BlendshapeAffectedVertexCount.ShouldBe(mesh.BlendshapeAffectedVertexCount);
    }

    private static void AssertSparseQuantizedBlendshapePayload(XRMesh mesh)
    {
        mesh.BlendshapeCounts.ShouldNotBeNull();
        mesh.BlendshapeIndices.ShouldNotBeNull();
        mesh.BlendshapeDeltas.ShouldNotBeNull();
        mesh.BlendshapeSparseShapeRanges.ShouldNotBeNull();
        mesh.BlendshapeSparseRecords.ShouldNotBeNull();
        mesh.BlendshapeQuantizedDeltas.ShouldNotBeNull();
        mesh.BlendshapeQuantizationMetadata.ShouldNotBeNull();
        mesh.BlendshapeDeltaStorageMode.ShouldBe(BlendshapeDeltaStorageMode.SparseAugmentsDenseFallback);
        mesh.BlendshapeDeltaEncoding.ShouldBe(BlendshapeDeltaEncoding.Snorm16Vector3);
        mesh.BlendshapeShaderVariant.HasFlag(BlendshapeShaderVariant.ActiveList).ShouldBeTrue();
        mesh.BlendshapeShaderVariant.HasFlag(BlendshapeShaderVariant.SparseDeltas).ShouldBeTrue();
        mesh.BlendshapeShaderVariant.HasFlag(BlendshapeShaderVariant.QuantizedDeltas).ShouldBeTrue();
        mesh.BlendshapeAffectedVertexCount.ShouldBe(3);
        mesh.BlendshapeSparseRecordCount.ShouldBe(3);
    }

    [Test]
    public void BlendshapeDeltaQuantizer_RoundTripsMixedMagnitudeDeltas()
    {
        Vector3[] deltas =
        [
            new(-100.0f, 0.0f, 0.001f),
            new(0.0f, -10.0f, 5.0f),
            new(100.0f, 10.0f, -5.0f),
        ];

        BlendshapeDeltaQuantizer.ComputeBounds(deltas, out _, out _, out Vector3 scale, out Vector3 bias);

        foreach (Vector3 delta in deltas)
        {
            (short x, short y, short z) encoded = BlendshapeDeltaQuantizer.EncodeSnorm16(delta, scale, bias);
            Vector3 decoded = BlendshapeDeltaQuantizer.DecodeSnorm16(encoded, scale, bias);

            decoded.X.ShouldBe(delta.X, 0.01f);
            decoded.Y.ShouldBe(delta.Y, 0.01f);
            decoded.Z.ShouldBe(delta.Z, 0.01f);
        }
    }

    [Test]
    public void BlendshapeDeltaQuantizer_PacksSnorm16PairsForShaderUnpack()
    {
        const short x = -12345;
        const short y = 23456;

        uint packed = BlendshapeDeltaQuantizer.PackSnorm16Pair(x, y);
        BlendshapeDeltaQuantizer.UnpackSnorm16Pair(packed).ShouldBe((x, y));
    }

    [Test]
    public unsafe void SparseQuantizedRecords_DecodeToDenseLogicalBlendshapeResult()
    {
        XRMesh mesh = CreateBlendshapeMesh();

        float[] weights =
        [
            1.0f,
            0.5f,
            -0.25f,
        ];

        for (uint vertexIndex = 0; vertexIndex < mesh.VertexCount; vertexIndex++)
        {
            Vector3 denseDelta = EvaluateDensePositionDelta(mesh, vertexIndex, weights);
            Vector3 sparseDelta = EvaluateSparseQuantizedPositionDelta(mesh, vertexIndex, weights);

            sparseDelta.X.ShouldBe(denseDelta.X, 0.0001f);
            sparseDelta.Y.ShouldBe(denseDelta.Y, 0.0001f);
            sparseDelta.Z.ShouldBe(denseDelta.Z, 0.0001f);
        }
    }

    [Test]
    public unsafe void SparseQuantizedRecords_DecodeToDenseLogicalBlendshapeResult_WhenDeltasAreRemapped()
    {
        IRuntimeRenderingHostServices previousServices = RuntimeRenderingHostServices.Current;
        RuntimeRenderingHostServices.Current = RuntimeRenderingHostServicesRemapProxy.Create(previousServices, true);

        try
        {
            XRMesh mesh = CreateBlendshapeMeshWithDuplicateRemappedDeltas();
            mesh.BlendshapeDeltas!.ElementCount.ShouldBe(3u);

            float[] weights =
            [
                1.0f,
                0.5f,
                -0.25f,
            ];

            for (uint vertexIndex = 0; vertexIndex < mesh.VertexCount; vertexIndex++)
            {
                Vector3 denseDelta = EvaluateDensePositionDelta(mesh, vertexIndex, weights);
                Vector3 sparseDelta = EvaluateSparseQuantizedPositionDelta(mesh, vertexIndex, weights);

                sparseDelta.X.ShouldBe(denseDelta.X, 0.0001f);
                sparseDelta.Y.ShouldBe(denseDelta.Y, 0.0001f);
                sparseDelta.Z.ShouldBe(denseDelta.Z, 0.0001f);
            }
        }
        finally
        {
            RuntimeRenderingHostServices.Current = previousServices;
        }
    }

    [Test]
    public void BlendshapeLodProfile_SelectsTierByDistanceScreenCoverageAndRole()
    {
        BlendshapeLodProfile profile = new(
            new BlendshapeLodTier(BlendshapeLodEvaluation.Full, MaxDistance: 2.0f, MinScreenCoverage: 0.15f),
            new BlendshapeLodTier(BlendshapeLodEvaluation.ProtectedAndHighImpact, MaxDistance: 8.0f, MinScreenCoverage: 0.03f),
            new BlendshapeLodTier(BlendshapeLodEvaluation.VisemeOrSilhouette, MaxDistance: 20.0f),
            new BlendshapeLodTier(BlendshapeLodEvaluation.Disabled));

        profile.SelectTier(1.0f, 0.25f).ShouldBe(0);
        profile.SelectTier(6.0f, 0.10f).ShouldBe(1);
        profile.SelectTier(12.0f, 0.01f).ShouldBe(2);
        profile.SelectTier(1.0f, 0.25f, BlendshapeLodAvatarRole.Crowd).ShouldBe(3);

        XRMeshRenderer renderer = CreateBlendshapeRenderer();
        renderer.BlendshapeLodProfile = profile;

        renderer.UpdateBlendshapeLodSelection(12.0f, 0.01f).ShouldBeTrue();
        renderer.ActiveBlendshapeLodTier.ShouldBe(2);
        renderer.LastBlendshapeLodDistance.ShouldBe(12.0f);
        renderer.LastBlendshapeLodScreenCoverage.ShouldBe(0.01f);
        renderer.BlendshapeLodDiagnosticSummary.ShouldContain("tier=2");
    }

    [Test]
    public void BlendshapeBoundsValidation_DetectsMissingAndExpandedExtremes()
    {
        XRMesh mesh = CreateBlendshapeMesh();
        BlendshapeLodTier fullTier = new(BlendshapeLodEvaluation.Full);

        mesh.BoundsContainBlendshapeExtremes(fullTier).ShouldBeFalse();
        mesh.TryCalculateBlendshapeBounds(fullTier, out AABB blendshapeBounds).ShouldBeTrue();
        blendshapeBounds.Max.Z.ShouldBe(0.2f, 0.0001f);
    }

    [Test]
    public void GlobalPackedBlendshapeWeights_ReusesUnchangedRendererSlice()
    {
        XRMeshRenderer renderer = CreateBlendshapeRenderer();
        renderer.SetBlendshapeWeightNormalized(0u, 0.25f);
        renderer.PushBlendshapeWeightsToGPU();

        using GlobalSkinPaletteBuffers globalBuffers = new();
        globalBuffers.BeginFrame(1);
        globalBuffers.EnsurePackedForRenderer(renderer, includeSkinPalette: false, includeBlendshapeWeights: true).ShouldBeTrue();
        globalBuffers.TryGetBlendshapeWeightsSlice(renderer, out uint baseElement, out uint elementCount).ShouldBeTrue();

        globalBuffers.EnsurePackedForRenderer(renderer, includeSkinPalette: false, includeBlendshapeWeights: true).ShouldBeFalse();
        globalBuffers.TryGetBlendshapeWeightsSlice(renderer, out uint reusedBaseElement, out uint reusedElementCount).ShouldBeTrue();
        reusedBaseElement.ShouldBe(baseElement);
        reusedElementCount.ShouldBe(elementCount);
    }

    [Test]
    public void PrecombinedBlendshapeBuffers_AreAllocatedOnceAndInvalidatedByWeightVersion()
    {
        XRMeshRenderer renderer = CreateBlendshapeRenderer();
        XRMesh mesh = renderer.Mesh!;

        renderer.EnsurePrecombinedBlendshapeBuffers(mesh).ShouldBeTrue();
        XRDataBuffer firstPositions = renderer.PrecombinedBlendshapePositionsBuffer!;
        renderer.EnsurePrecombinedBlendshapeBuffers(mesh).ShouldBeTrue();
        renderer.PrecombinedBlendshapePositionsBuffer.ShouldBeSameAs(firstPositions);

        renderer.MarkPrecombinedBlendshapeDeltasValid(mesh);
        renderer.HasValidPrecombinedBlendshapeDeltas.ShouldBeTrue();

        renderer.MarkSkinnedOutputDirty();
        renderer.HasValidPrecombinedBlendshapeDeltas.ShouldBeTrue();

        renderer.SetBlendshapeWeightNormalized(0u, 1.0f);
        renderer.HasValidPrecombinedBlendshapeDeltas.ShouldBeFalse();
    }

    [Test]
    public void BlendshapePrecombineHeuristic_UsesPathSpecificActiveAndAffectedThresholds()
    {
        bool previousEnabled = RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass;
        bool previousDirectEnabled = RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombineForDirectVertexPath;
        int previousComputeMin = RuntimeEngine.Rendering.Settings.BlendshapePrecombineComputeMinActiveShapes;
        int previousDirectMin = RuntimeEngine.Rendering.Settings.BlendshapePrecombineDirectMinActiveShapes;
        int previousAffectedMin = RuntimeEngine.Rendering.Settings.BlendshapePrecombineMinAffectedVertices;

        try
        {
            RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass = true;
            RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombineForDirectVertexPath = true;
            RuntimeEngine.Rendering.Settings.BlendshapePrecombineComputeMinActiveShapes = 3;
            RuntimeEngine.Rendering.Settings.BlendshapePrecombineDirectMinActiveShapes = 4;
            RuntimeEngine.Rendering.Settings.BlendshapePrecombineMinAffectedVertices = 3;

            XRMeshRenderer renderer = CreateBlendshapeRenderer();
            renderer.SetBlendshapeWeightNormalized(0u, 1.0f);
            renderer.SetBlendshapeWeightNormalized(1u, 1.0f);
            renderer.SetBlendshapeWeightNormalized(2u, 1.0f);

            SkinningPrepassDispatcher.ShouldUseBlendshapePrecombine(
                renderer,
                renderer.Mesh!,
                SkinningPrepassDispatcher.BlendshapePrecombineRendererPath.ComputePrepass).ShouldBeTrue();
            SkinningPrepassDispatcher.ShouldUseBlendshapePrecombine(
                renderer,
                renderer.Mesh!,
                SkinningPrepassDispatcher.BlendshapePrecombineRendererPath.DirectVertex).ShouldBeFalse();

            RuntimeEngine.Rendering.Settings.BlendshapePrecombineDirectMinActiveShapes = 3;
            SkinningPrepassDispatcher.ShouldUseBlendshapePrecombine(
                renderer,
                renderer.Mesh!,
                SkinningPrepassDispatcher.BlendshapePrecombineRendererPath.DirectVertex).ShouldBeTrue();

            RuntimeEngine.Rendering.Settings.BlendshapePrecombineMinAffectedVertices = 4;
            SkinningPrepassDispatcher.ShouldUseBlendshapePrecombine(
                renderer,
                renderer.Mesh!,
                SkinningPrepassDispatcher.BlendshapePrecombineRendererPath.ComputePrepass).ShouldBeFalse();
        }
        finally
        {
            RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass = previousEnabled;
            RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombineForDirectVertexPath = previousDirectEnabled;
            RuntimeEngine.Rendering.Settings.BlendshapePrecombineComputeMinActiveShapes = previousComputeMin;
            RuntimeEngine.Rendering.Settings.BlendshapePrecombineDirectMinActiveShapes = previousDirectMin;
            RuntimeEngine.Rendering.Settings.BlendshapePrecombineMinAffectedVertices = previousAffectedMin;
        }
    }

    [Test]
    public void BlendshapeBasisCompressionToggle_RequiresCookedBasisPayload()
    {
        IRuntimeRenderingHostServices previousServices = RuntimeRenderingHostServices.Current;
        RuntimeRenderingHostServices.Current = null!;
        bool previousEnabled = RuntimeEngine.Rendering.Settings.EnableBlendshapePcaBasisCompression;

        try
        {
            RuntimeEngine.Rendering.Settings.EnableBlendshapePcaBasisCompression = true;

            XRMeshRenderer renderer = CreateBlendshapeRenderer();
            renderer.SetBlendshapeWeightNormalized(0u, 1.0f);
            XRMesh mesh = renderer.Mesh!;

            mesh.HasBlendshapeBasisCompressionPayload.ShouldBeFalse();
            SkinningPrepassDispatcher.ShouldUseBlendshapeBasisCompression(renderer, mesh).ShouldBeFalse();
        }
        finally
        {
            RuntimeEngine.Rendering.Settings.EnableBlendshapePcaBasisCompression = previousEnabled;
            RuntimeRenderingHostServices.Current = previousServices;
        }
    }

    [Test]
    public void SkinningPrepassOutputCache_DoesNotReuseBeforeSkinInputsSettle()
    {
        XRMeshRenderer renderer = new();
        renderer.MarkSkinnedOutputClean();

        Type resourcesType = typeof(SkinningPrepassDispatcher).GetNestedType("RendererResources", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RendererResources type was not found.");
        object resources = Activator.CreateInstance(
            resourcesType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [renderer],
            culture: null)!;

        SetPrivateField(resourcesType, resources, "_hasValidOutput", true);
        SetPrivateField(resourcesType, resources, "_lastDidSkinning", true);
        SetPrivateField(resourcesType, resources, "_lastDidBlendshapes", false);
        SetPrivateField(resourcesType, resources, "_lastUsedPrecombinedBlendshapes", false);
        SetPrivateField(resourcesType, resources, "_lastOutputVersion", renderer.SkinnedOutputVersion);

        var canReuse = resourcesType.GetMethod("CanReuseOutput", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("CanReuseOutput method was not found.");

        ((bool)canReuse.Invoke(resources, [true, false, false])!).ShouldBeFalse();

        SetPrivateField(resourcesType, resources, "_seedInputsSettled", true);

        ((bool)canReuse.Invoke(resources, [true, false, false])!).ShouldBeTrue();
    }

    private static XRMeshRenderer CreateBlendshapeRenderer()
    {
        XRMeshRenderer renderer = new()
        {
            Mesh = CreateBlendshapeMesh(),
        };

        renderer.EnsureBlendshapeBuffers().ShouldBeTrue();
        return renderer;
    }

    private static XRMesh CreateBlendshapeMesh()
    {
        Vector3 normal = Vector3.UnitZ;
        Vector3 tangent = Vector3.UnitX;

        Vertex v0 = CreateVertex(new Vector3(0.0f, 0.0f, 0.0f), normal, tangent);
        Vertex v1 = CreateVertex(new Vector3(1.0f, 0.0f, 0.0f), normal, tangent);
        Vertex v2 = CreateVertex(new Vector3(0.0f, 1.0f, 0.0f), normal, tangent);

        v0.Blendshapes =
        [
            ("Smile", CreateTarget(v0, new Vector3(0.0f, 0.1f, 0.0f))),
        ];
        v1.Blendshapes =
        [
            ("Blink", CreateTarget(v1, new Vector3(0.0f, 0.0f, 0.2f))),
        ];
        v2.Blendshapes =
        [
            ("Jaw", CreateTarget(v2, new Vector3(-0.15f, 0.0f, 0.0f))),
        ];

        XRMesh mesh = XRMesh.Create(new VertexTriangle(v0, v1, v2));
        mesh.BlendshapeNames = ["Smile", "Blink", "Jaw"];
        mesh.RebuildBlendshapeBuffersFromVertices();
        return mesh;
    }

    private static XRMesh CreateBlendshapeMeshWithDuplicateRemappedDeltas()
    {
        Vector3 normal = Vector3.UnitZ;
        Vector3 tangent = Vector3.UnitX;

        Vertex v0 = CreateVertex(new Vector3(0.0f, 0.0f, 0.0f), normal, tangent);
        Vertex v1 = CreateVertex(new Vector3(1.0f, 0.0f, 0.0f), normal, tangent);
        Vertex v2 = CreateVertex(new Vector3(0.0f, 1.0f, 0.0f), normal, tangent);

        Vector3 duplicateDelta = new(0.0f, 0.1f, 0.0f);
        Vector3 laterUniqueDelta = new(-0.15f, 0.0f, 0.25f);

        v0.Blendshapes =
        [
            ("Smile", CreateTarget(v0, duplicateDelta)),
        ];
        v1.Blendshapes =
        [
            ("Blink", CreateTarget(v1, duplicateDelta)),
        ];
        v2.Blendshapes =
        [
            ("Jaw", CreateTarget(v2, laterUniqueDelta)),
        ];

        XRMesh mesh = XRMesh.Create(new VertexTriangle(v0, v1, v2));
        mesh.BlendshapeNames = ["Smile", "Blink", "Jaw"];
        mesh.RebuildBlendshapeBuffersFromVertices();
        return mesh;
    }

    private static Vertex CreateVertex(Vector3 position, Vector3 normal, Vector3 tangent)
        => new(position, normal, tangent, Vector2.Zero, Vector4.One);

    private static VertexData CreateTarget(Vertex source, Vector3 delta)
        => new()
        {
            Position = source.Position + delta,
            Normal = source.Normal,
            Tangent = source.Tangent,
        };

    private static unsafe Vector3 EvaluateDensePositionDelta(XRMesh mesh, uint vertexIndex, float[] weights)
    {
        XRDataBuffer counts = mesh.BlendshapeCounts!;
        XRDataBuffer indices = mesh.BlendshapeIndices!;
        XRDataBuffer deltas = mesh.BlendshapeDeltas!;

        int start = ReadBufferIntComponent(counts, vertexIndex, 0);
        int count = ReadBufferIntComponent(counts, vertexIndex, 1);
        Vector3 result = Vector3.Zero;
        for (int i = 0; i < count; i++)
        {
            uint recordIndex = (uint)(start + i);
            int shapeIndex = ReadBufferIntComponent(indices, recordIndex, 0);
            int deltaIndex = ReadBufferIntComponent(indices, recordIndex, 1);
            Vector4 delta = deltas.GetVector4((uint)deltaIndex);
            result += new Vector3(delta.X, delta.Y, delta.Z) * weights[shapeIndex];
        }

        return result;
    }

    private static unsafe Vector3 EvaluateSparseQuantizedPositionDelta(XRMesh mesh, uint vertexIndex, float[] weights)
    {
        XRDataBuffer ranges = mesh.BlendshapeSparseShapeRanges!;
        XRDataBuffer records = mesh.BlendshapeSparseRecords!;
        Vector3 result = Vector3.Zero;

        for (int shapeIndex = 0; shapeIndex < weights.Length; shapeIndex++)
        {
            float weight = weights[shapeIndex];
            if (weight == 0.0f)
                continue;

            int start = ReadBufferIntComponent(ranges, (uint)shapeIndex, 0);
            int count = ReadBufferIntComponent(ranges, (uint)shapeIndex, 1);
            for (int recordOffset = 0; recordOffset < count; recordOffset++)
            {
                uint recordIndex = (uint)(start + recordOffset);
                if (ReadBufferIntComponent(records, recordIndex, 0) != vertexIndex)
                    continue;

                int deltaIndex = ReadBufferIntComponent(records, recordIndex, 1);
                result += DecodeQuantizedDelta(mesh, shapeIndex, deltaIndex) * weight;
                break;
            }
        }

        return result;
    }

    private static unsafe int ReadBufferIntComponent(XRDataBuffer buffer, uint elementIndex, int componentIndex)
    {
        int offset = checked((int)(elementIndex * buffer.ComponentCount + (uint)componentIndex));
        return buffer.ComponentType switch
        {
            EComponentType.Int => ((int*)buffer.Address)[offset],
            EComponentType.UInt => checked((int)((uint*)buffer.Address)[offset]),
            EComponentType.Float => (int)MathF.Round(((float*)buffer.Address)[offset]),
            _ => throw new NotSupportedException(buffer.ComponentType.ToString()),
        };
    }

    private static unsafe Vector3 DecodeQuantizedDelta(XRMesh mesh, int shapeIndex, int deltaIndex)
    {
        if (deltaIndex == 0)
            return Vector3.Zero;

        XRDataBuffer quantized = mesh.BlendshapeQuantizedDeltas!;
        uint* packed = (uint*)quantized.Address + deltaIndex * 2;
        (short x, short y) xy = BlendshapeDeltaQuantizer.UnpackSnorm16Pair(packed[0]);
        (short z, _) = BlendshapeDeltaQuantizer.UnpackSnorm16Pair(packed[1]);

        XRDataBuffer metadata = mesh.BlendshapeQuantizationMetadata!;
        Vector4 scale4 = metadata.GetVector4((uint)(shapeIndex * 4 + 2));
        Vector4 bias4 = metadata.GetVector4((uint)(shapeIndex * 4 + 3));
        return BlendshapeDeltaQuantizer.DecodeSnorm16(
            (xy.x, xy.y, z),
            new Vector3(scale4.X, scale4.Y, scale4.Z),
            new Vector3(bias4.X, bias4.Y, bias4.Z));
    }

    private static void SetPrivateField(Type type, object instance, string fieldName, object value)
    {
        FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");
        field.SetValue(instance, value);
    }

    private class RuntimeRenderingHostServicesRemapProxy : DispatchProxy
    {
        public IRuntimeRenderingHostServices Inner { get; set; } = null!;
        public bool RemapBlendshapeDeltas { get; set; }

        public static IRuntimeRenderingHostServices Create(IRuntimeRenderingHostServices inner, bool remapBlendshapeDeltas)
        {
            IRuntimeRenderingHostServices proxy = Create<IRuntimeRenderingHostServices, RuntimeRenderingHostServicesRemapProxy>();
            RuntimeRenderingHostServicesRemapProxy state = (RuntimeRenderingHostServicesRemapProxy)(object)proxy;
            state.Inner = inner;
            state.RemapBlendshapeDeltas = remapBlendshapeDeltas;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                return null;

            if (targetMethod.Name == $"get_{nameof(IRuntimeRenderingHostServices.RemapBlendshapeDeltas)}")
                return RemapBlendshapeDeltas;

            return targetMethod.Invoke(Inner, args);
        }
    }
}
