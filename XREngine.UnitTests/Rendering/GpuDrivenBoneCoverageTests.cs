using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class GpuDrivenBoneCoverageTests
{
    private bool _previousSkinning;
    private bool _previousComputeSkinning;

    [SetUp]
    public void EnableGpuSkinning()
    {
        _previousSkinning = RuntimeEngine.Rendering.Settings.AllowSkinning;
        _previousComputeSkinning = RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader;
        RuntimeEngine.Rendering.Settings.AllowSkinning = true;
        RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader = true;
    }

    [TearDown]
    public void RestoreSettings()
    {
        RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader = _previousComputeSkinning;
        RuntimeEngine.Rendering.Settings.AllowSkinning = _previousSkinning;
    }

    [Test]
    public void Coverage_RequiresEveryUtilizedBoneAndCompleteExternalPalette()
    {
        XRMeshRenderer renderer = CreateRenderer();
        using XRDataBuffer<SkinPaletteMatrix> palette = CreatePalette();
        object owner = new();
        long generation = Register(renderer, [1u]);
        renderer.SetGpuDrivenSkinPaletteSource(owner, palette, null, 0u, 3u, generation, true).ShouldBeTrue();
        renderer.CaptureGpuDrivenBoneCoverage().IsFullyCovered.ShouldBeFalse();

        Register(renderer, [2u]).ShouldBe(generation);
        renderer.CaptureGpuDrivenBoneCoverage().IsFullyCovered.ShouldBeTrue();
        renderer.HasCompleteGpuDrivenBoneCoverage.ShouldBeTrue();

        renderer.UnregisterGpuDrivenBoneIndices([2u], generation);
        renderer.CaptureGpuDrivenBoneCoverage().IsFullyCovered.ShouldBeFalse();
        renderer.ClearGpuDrivenSkinPaletteSource(owner);
        renderer.CaptureGpuDrivenBoneCoverage().HasExternalPaletteSource.ShouldBeFalse();
        renderer.UnregisterGpuDrivenBoneIndices([1u], generation);
    }

    [Test]
    public void PartialPaletteUnion_DoesNotCertifyCompleteCoverage()
    {
        XRMeshRenderer renderer = CreateRenderer();
        using XRDataBuffer<SkinPaletteMatrix> palette = CreatePalette();
        long generation = Register(renderer, [1u, 2u]);
        object owner = new();
        renderer.SetGpuDrivenSkinPaletteSource(owner, palette, null, 0u, 3u, generation, false).ShouldBeTrue();
        renderer.CaptureGpuDrivenBoneCoverage().DrivenBoneCount.ShouldBe(2);
        renderer.HasCompleteGpuDrivenBoneCoverage.ShouldBeFalse();
        renderer.ClearGpuDrivenSkinPaletteSource(owner);
        renderer.UnregisterGpuDrivenBoneIndices([1u, 2u], generation);
    }

    [Test]
    public void IdentityAndOutOfRangeIndices_DoNotCountAsUtilizedBones()
    {
        XRMeshRenderer renderer = CreateRenderer();
        long generation = Register(renderer, [0u, 3u, uint.MaxValue]);
        generation.ShouldBeGreaterThan(0);
        renderer.CaptureGpuDrivenBoneCoverage().DrivenBoneCount.ShouldBe(0);
        renderer.UnregisterGpuDrivenBoneIndices([0u, 3u, uint.MaxValue], generation);
        renderer.StaleGpuDrivenBoneReleaseCount.ShouldBe(0);
    }

    [Test]
    public void OldRelease_CannotChangeReplacementOwnershipOrListeners()
    {
        XRMeshRenderer renderer = CreateRenderer();
        long oldGeneration = Register(renderer, [1u, 2u]);
        XRMesh replacement = CreateMesh(out Transform firstBone);
        renderer.Mesh = replacement;
        renderer.EnsureSkinningBuffers().ShouldBeTrue();
        long newGeneration = Register(renderer, [1u, 2u]);
        newGeneration.ShouldBeGreaterThan(oldGeneration);

        renderer.UnregisterGpuDrivenBoneIndices([1u, 2u], oldGeneration);
        renderer.StaleGpuDrivenBoneReleaseCount.ShouldBe(1);
        renderer.CaptureGpuDrivenBoneCoverage().DrivenBoneCount.ShouldBe(2);

        Matrix4x4 releasedMatrix = Matrix4x4.CreateTranslation(7.0f, 8.0f, 9.0f);
        firstBone.SetRenderMatrix(releasedMatrix, recalcAllChildRenderMatrices: false).Wait();
        renderer.UnregisterGpuDrivenBoneIndices([1u, 2u], newGeneration);
        renderer.PushBoneMatricesToGPU();
        renderer.BoneMatricesBuffer!.GetDataRawAtIndex<Matrix4x4>(1u).ShouldBe(releasedMatrix);

        Matrix4x4 nextMatrix = Matrix4x4.CreateTranslation(10.0f, 11.0f, 12.0f);
        firstBone.SetRenderMatrix(nextMatrix, recalcAllChildRenderMatrices: false).Wait();
        renderer.PushBoneMatricesToGPU();
        renderer.BoneMatricesBuffer.GetDataRawAtIndex<Matrix4x4>(1u).ShouldBe(nextMatrix);
    }

    [Test]
    public void ReplacementBeforeRegistration_RejectsCapturedOldMapping()
    {
        XRMeshRenderer renderer = CreateRenderer();
        (XRMesh? oldMesh, long oldGeneration) = renderer.CaptureGpuDrivenBoneMappingSource();
        renderer.Mesh = CreateMesh(out _);
        renderer.EnsureSkinningBuffers().ShouldBeTrue();

        renderer.RegisterGpuDrivenBoneIndices([1u, 2u], oldGeneration, oldMesh!).ShouldBe(0);
        renderer.CaptureGpuDrivenBoneCoverage().DrivenBoneCount.ShouldBe(0);
    }

    [Test]
    public void CoverageNotification_ReportsGenerationAndSettingsTransitions()
    {
        XRMeshRenderer renderer = CreateRenderer();
        using XRDataBuffer<SkinPaletteMatrix> palette = CreatePalette();
        List<GpuDrivenBoneCoverageSnapshot> changes = [];
        void OnCoverageChanged(XRMeshRenderer _, GpuDrivenBoneCoverageSnapshot snapshot) => changes.Add(snapshot);
        renderer.GpuDrivenBoneCoverageChanged += OnCoverageChanged;
        object owner = new();
        long generation = Register(renderer, [1u, 2u]);
        try
        {
            renderer.SetGpuDrivenSkinPaletteSource(owner, palette, null, 0u, 3u, generation, true).ShouldBeTrue();
            changes[^1].Generation.ShouldBe(generation);
            changes[^1].IsFullyCovered.ShouldBeTrue();
            RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader = false;
            changes[^1].IsFullyCovered.ShouldBeFalse();
            RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader = true;
            changes[^1].IsFullyCovered.ShouldBeTrue();

            renderer.Mesh = CreateMesh(out _);
            renderer.EnsureSkinningBuffers().ShouldBeTrue();
            changes[^1].Generation.ShouldBeGreaterThan(generation);
            changes[^1].IsFullyCovered.ShouldBeFalse();
            renderer.CaptureGpuDrivenBoneCoverage().DrivenBoneCount.ShouldBe(0);
        }
        finally
        {
            renderer.GpuDrivenBoneCoverageChanged -= OnCoverageChanged;
            renderer.ClearGpuDrivenSkinPaletteSource(owner);
        }
    }

    private static long Register(XRMeshRenderer renderer, uint[] indices)
    {
        (XRMesh? mesh, long generation) = renderer.CaptureGpuDrivenBoneMappingSource();
        return renderer.RegisterGpuDrivenBoneIndices(indices, generation, mesh!);
    }

    private static XRMeshRenderer CreateRenderer()
    {
        XRMeshRenderer renderer = new() { GenerateAsync = false, Mesh = CreateMesh(out _) };
        renderer.EnsureSkinningBuffers().ShouldBeTrue();
        return renderer;
    }

    private static XRMesh CreateMesh(out Transform firstBone)
    {
        firstBone = new SceneNode("FirstBone").SetTransform<Transform>();
        Transform secondBone = new SceneNode("SecondBone").SetTransform<Transform>();
        firstBone.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
        secondBone.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
        XRMesh mesh = XRMesh.CreateTriangles([Vector3.Zero, Vector3.UnitX, Vector3.UnitY]);
        mesh.UtilizedBones = [(firstBone, Matrix4x4.Identity), (secondBone, Matrix4x4.Identity)];
        return mesh;
    }

    private static XRDataBuffer<SkinPaletteMatrix> CreatePalette()
        => new("CoverageTestPalette", EBufferTarget.ShaderStorageBuffer, 3u);
}
