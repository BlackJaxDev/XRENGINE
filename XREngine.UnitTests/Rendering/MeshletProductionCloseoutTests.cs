using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Meshlets;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class MeshletProductionCloseoutTests
{
    [TestCase(1.0f)]
    [TestCase(0.01f)]
    [TestCase(0.00001f)]
    public void UniformPositiveTransform_RemainsEligibleAtProductionImportScales(float scale)
    {
        Matrix4x4 matrix = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(0.73f);

        MeshletTransformEligibility.HasUniformPositiveScale(matrix).ShouldBeTrue();
    }

    [Test]
    public void MirroredNonUniformShearedDegenerateAndNonFiniteTransforms_AreIneligible()
    {
        Matrix4x4 mirrored = Matrix4x4.CreateScale(-1.0f, 1.0f, 1.0f);
        Matrix4x4 nonUniform = Matrix4x4.CreateScale(1.0f, 1.1f, 1.0f);
        Matrix4x4 sheared = Matrix4x4.Identity;
        sheared.M12 = 0.25f;
        Matrix4x4 degenerate = Matrix4x4.CreateScale(1.0f, 0.0f, 1.0f);
        Matrix4x4 nonFinite = Matrix4x4.Identity;
        nonFinite.M22 = float.NaN;

        MeshletTransformEligibility.HasUniformPositiveScale(mirrored).ShouldBeFalse();
        MeshletTransformEligibility.HasUniformPositiveScale(nonUniform).ShouldBeFalse();
        MeshletTransformEligibility.HasUniformPositiveScale(sheared).ShouldBeFalse();
        MeshletTransformEligibility.HasUniformPositiveScale(degenerate).ShouldBeFalse();
        MeshletTransformEligibility.HasUniformPositiveScale(nonFinite).ShouldBeFalse();
    }

    [Test]
    public void DisabledAndEmptyPayloadStates_ArePortableAndContainNoMeshletStreams()
    {
        XRMesh triangleMesh = XRMesh.CreateTriangles(
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitY);
        MeshletGenerationSettings disabledSettings = new() { Enabled = false };
        MeshletPayload disabled = MeshletPayload.CreateDisabled(
            triangleMesh,
            disabledSettings,
            lodSettings: null,
            sourceMeshIdentity: "gate7-disabled");

        disabled.State.ShouldBe(MeshletPayloadState.Disabled);
        disabled.GenerationEnabled.ShouldBeFalse();
        disabled.Meshlets.ShouldBeEmpty();
        disabled.VertexIndices.ShouldBeEmpty();
        disabled.TriangleIndices.ShouldBeEmpty();
        Should.NotThrow(disabled.ValidatePortablePayload);

        XRMesh emptyMesh = new() { Name = "Gate7EmptyPayload" };
        MeshletPayload empty = MeshOptimizerIntegration.BuildMeshletPayloadForMesh(
            emptyMesh,
            CreateEnabledSettings(),
            lodSettings: null,
            sourceMeshIdentity: "gate7-empty").Payload;

        empty.State.ShouldBe(MeshletPayloadState.Empty);
        empty.GenerationEnabled.ShouldBeTrue();
        empty.Meshlets.ShouldBeEmpty();
        empty.VertexIndices.ShouldBeEmpty();
        empty.TriangleIndices.ShouldBeEmpty();
        Should.NotThrow(empty.ValidatePortablePayload);
    }

    [Test]
    public void PortablePayloadValidation_RejectsOutOfRangeLocalTriangleIndex()
    {
        XRMesh mesh = CreateTriangleGrid("Gate7PayloadValidation", triangleCount: 48);
        MeshletPayload valid = MeshOptimizerIntegration.BuildMeshletPayloadForMesh(
            mesh,
            CreateEnabledSettings(),
            lodSettings: null,
            sourceMeshIdentity: "gate7-validation").Payload;
        valid.Meshlets.ShouldNotBeEmpty();

        byte[] invalidTriangles = valid.TriangleIndices.ToArray();
        CpuMeshletDescriptor first = valid.Meshlets[0];
        invalidTriangles[first.TriangleOffset] = checked((byte)first.VertexCount);
        MeshletPayload invalid = CopyPayload(
            valid,
            triangleIndices: invalidTriangles.ToImmutableArray());

        InvalidDataException exception = Should.Throw<InvalidDataException>(invalid.ValidatePortablePayload);
        exception.Message.ShouldContain("invalid local triangle index");
    }

    [Test]
    public void ResidentPayloadReplacement_IsCoalescedAndPublishedAtTheFrameBoundary()
    {
        XRMesh mesh = CreateTriangleGrid("Gate7ResidentReplacement", triangleCount: 240);
        MeshletPayload initial = mesh.GetOrCreateMeshletPayload(CreateEnabledSettings(maxTriangles: 124u));
        GPUScene scene = new();
        try
        {
            scene.RegisterLogicalMeshLODs([(mesh, 0.0f)], out uint logicalMeshId, out string? failureReason)
                .ShouldBeTrue(failureReason);
            typeof(GPUScene)
                .GetMethod("AcquireLogicalMeshResidency", BindingFlags.Instance | BindingFlags.NonPublic)
                .ShouldNotBeNull()!
                .Invoke(scene, [logicalMeshId]);
            scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry lodEntry).ShouldBeTrue();
            scene.TryGetMeshletRange(lodEntry.LOD0_MeshDataID, out GPUScene.GpuMeshletRange initialRange).ShouldBeTrue();
            initialRange.MeshletCount.ShouldBe((uint)initial.Meshlets.Length);
            scene.SwapCommandBuffers();
            ulong initialGeneration = scene.MeshletBufferGeneration;

            MeshletPayload replacement = MeshOptimizerIntegration.BuildMeshletPayloadForMesh(
                mesh,
                CreateEnabledSettings(maxVertices: 16u, maxTriangles: 16u),
                lodSettings: null,
                sourceMeshIdentity: null).Payload;
            replacement.ValidateForMesh(mesh);
            replacement.FreshnessHash.ShouldNotBe(initial.FreshnessHash);
            replacement.ValidationRevision.ShouldNotBe(initial.ValidationRevision);
            replacement.Meshlets.Length.ShouldNotBe(initial.Meshlets.Length);

            // Both notifications arrive before publication. GPUScene must expose
            // the old coherent range until the frame boundary, then publish only
            // the final replacement rather than a transient empty range.
            mesh.MeshletPayload = null;
            mesh.MeshletPayload = replacement;
            scene.TryGetMeshletRange(lodEntry.LOD0_MeshDataID, out GPUScene.GpuMeshletRange beforeSwap).ShouldBeTrue();
            beforeSwap.ShouldBe(initialRange);

            scene.SwapCommandBuffers();

            scene.TryGetMeshletRange(lodEntry.LOD0_MeshDataID, out GPUScene.GpuMeshletRange afterSwap).ShouldBeTrue();
            afterSwap.MeshletCount.ShouldBe((uint)replacement.Meshlets.Length);
            scene.MeshletDescriptorCount.ShouldBe(replacement.Meshlets.Length);
            scene.MeshletBufferGeneration.ShouldBe(initialGeneration + 1UL);
            scene.HasRenderableMeshlets(lodEntry.LOD0_MeshDataID).ShouldBeTrue();
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Test]
    public void ConservativeHiZContract_IsIdenticalAcrossTaskShaderDialects()
    {
        string nv = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Meshlets/MeshletCulling.task");
        string ext = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Meshlets/MeshletCullingExt.task");
        string nvContract = Slice(nv, "bool TryProjectConservativeSphereBounds(", "void main()");
        string extContract = Slice(ext, "bool TryProjectConservativeSphereBounds(", "void main()");

        extContract.ShouldBe(nvContract);
        nvContract.ShouldContain("vec3 corners[8]");
        nvContract.ShouldContain("clip.w <= 0.000001");
        nvContract.ShouldContain("A clipped footprint cannot be proven occluded");
        nvContract.ShouldContain("TrySelectHiZFootprint");
        nvContract.ShouldContain("TrySampleHiZFootprint");
        nvContract.ShouldContain("? min(hiZDepth, sampleDepth)");
        nvContract.ShouldContain(": max(hiZDepth, sampleDepth)");
        nvContract.ShouldContain("if (stereoActive && EnableStereoHiZ == 0u)");
        nvContract.ShouldContain("? nearestDepth + HiZDepthBias < hiZDepth");
        nvContract.ShouldContain(": nearestDepth - HiZDepthBias > hiZDepth");
    }

    [Test]
    public void DebugColorContract_IsStableAcrossStaticSkinnedAndShaderDialects()
    {
        string[] shaderPaths =
        [
            "Build/CommonAssets/Shaders/Meshlets/MeshletRender.mesh",
            "Build/CommonAssets/Shaders/Meshlets/MeshletRenderExt.mesh",
            "Build/CommonAssets/Shaders/Meshlets/MeshletRenderSkinned.mesh",
            "Build/CommonAssets/Shaders/Meshlets/MeshletRenderSkinnedExt.mesh",
        ];

        string expectedFunction = string.Empty;
        foreach (string shaderPath in shaderPaths)
        {
            string shader = SourceContractWorkspace.ReadFile(shaderPath);
            string function = Slice(shader, "vec4 XRE_MeshletDebugColor(uint meshletIndex)", "vec3 SafeNormalize(");
            if (expectedFunction.Length == 0)
                expectedFunction = function;
            else
                function.ShouldBe(expectedFunction);

            shader.ShouldContain("EnableMeshletDebugDisplay != 0u");
            shader.ShouldContain("? XRE_MeshletDebugColor(meshletIndex)");
        }

        expectedFunction.ShouldContain("XRE_HashUint(meshletIndex + 1u)");
        expectedFunction.ShouldContain("XRE_HsvToRgb(vec3(hue, 0.82, 1.0))");

        string materialTable = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        materialTable.ShouldContain(
            "AlbedoOpacity = vec4({FragMeshletDebugColorName}.rgb, 1.0);");
    }

    private static MeshletGenerationSettings CreateEnabledSettings(
        uint maxVertices = 64u,
        uint maxTriangles = 124u)
        => new()
        {
            Enabled = true,
            BuildMode = MeshletBuildMode.Dense,
            MaxVertices = maxVertices,
            MaxTriangles = maxTriangles,
            OptimizeMeshlets = true,
            ComputeBounds = true,
        };

    private static XRMesh CreateTriangleGrid(string name, int triangleCount)
    {
        List<Vector3> positions = new(triangleCount * 3);
        for (int index = 0; index < triangleCount; index++)
        {
            float x = index % 24;
            float y = index / 24;
            positions.Add(new Vector3(x, y, 0.0f));
            positions.Add(new Vector3(x + 0.4f, y, 0.0f));
            positions.Add(new Vector3(x, y + 0.4f, 0.0f));
        }

        XRMesh mesh = XRMesh.CreateTriangles(positions);
        mesh.Name = name;
        return mesh;
    }

    private static MeshletPayload CopyPayload(
        MeshletPayload source,
        ImmutableArray<byte> triangleIndices)
        => new()
        {
            PayloadVersion = source.PayloadVersion,
            GenerationEnabled = source.GenerationEnabled,
            State = source.State,
            MeshOptimizerVersionKey = source.MeshOptimizerVersionKey,
            CookProvenanceKey = source.CookProvenanceKey,
            RuntimeCompatibilityToken = source.RuntimeCompatibilityToken,
            SourceMeshIdentity = source.SourceMeshIdentity,
            SourceVertexCount = source.SourceVertexCount,
            SourceTriangleCount = source.SourceTriangleCount,
            SourceMeshHash = source.SourceMeshHash,
            MeshletSettingsHash = source.MeshletSettingsHash,
            LodSettingsHash = source.LodSettingsHash,
            FreshnessHash = source.FreshnessHash,
            MeshletSettings = source.MeshletSettings,
            LodSettings = source.LodSettings,
            Meshlets = source.Meshlets,
            VertexIndices = source.VertexIndices,
            TriangleIndices = triangleIndices,
            Vertices = source.Vertices,
            Stats = source.Stats,
        };

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);
        return source[start..end];
    }
}
