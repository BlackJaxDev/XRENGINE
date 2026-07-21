using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainGpuResidencyTests
{
    [Test]
    public void UnchangedArenaSlice_HasNoStaticOrStateUpload()
    {
        GPUPhysicsChainUploadPlan plan = GPUPhysicsChainUploadPlan.Create(
            allocationChanged: false,
            uploadedParticleStateVersion: 3,
            particleStateVersion: 3,
            uploadedStaticVersion: 7,
            staticVersion: 7,
            uploadedTransformVersion: 11,
            transformVersion: 11,
            uploadedColliderVersion: 13,
            colliderVersion: 13);

        plan.UploadParticleState.ShouldBeFalse();
        plan.UploadStaticTemplate.ShouldBeFalse();
        plan.UploadTransformInputs.ShouldBeFalse();
        plan.UploadColliderData.ShouldBeFalse();
    }

    [Test]
    public void ChangedRootPose_UploadsOnlyDynamicTransformRange()
    {
        GPUPhysicsChainUploadPlan plan = GPUPhysicsChainUploadPlan.Create(
            allocationChanged: false,
            uploadedParticleStateVersion: 3,
            particleStateVersion: 3,
            uploadedStaticVersion: 7,
            staticVersion: 7,
            uploadedTransformVersion: 10,
            transformVersion: 11,
            uploadedColliderVersion: 13,
            colliderVersion: 13);

        plan.UploadTransformInputs.ShouldBeTrue();
        plan.UploadParticleState.ShouldBeFalse();
        plan.UploadStaticTemplate.ShouldBeFalse();
        plan.UploadColliderData.ShouldBeFalse();
    }

    [Test]
    public void NewArenaSlice_UploadsEveryRequiredResourceOnce()
    {
        GPUPhysicsChainUploadPlan plan = GPUPhysicsChainUploadPlan.Create(
            allocationChanged: true,
            uploadedParticleStateVersion: 3,
            particleStateVersion: 3,
            uploadedStaticVersion: 7,
            staticVersion: 7,
            uploadedTransformVersion: 11,
            transformVersion: 11,
            uploadedColliderVersion: 13,
            colliderVersion: 13);

        plan.UploadParticleState.ShouldBeTrue();
        plan.UploadStaticTemplate.ShouldBeTrue();
        plan.UploadTransformInputs.ShouldBeTrue();
        plan.UploadColliderData.ShouldBeTrue();
    }

    [Test]
    public void DispatcherSource_HasNoLegacySteadyStateRepackOrResidentCopyPath()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs");

        source.ShouldContain("BuildResidentArenaBuffers");
        source.ShouldContain("GPUPhysicsChainUploadPlan.Create");
        source.ShouldContain("IsDynamicHeaderDirty");
        source.ShouldContain("Span<GPUPerTreeParams> dirtyHeaders");
        source.ShouldContain("WriteDataRaw(dirtyHeaders, (uint)dirtyStart)");
        source.ShouldContain("DeferArenaResource");
        source.ShouldContain("RetirementFence");
        source.ShouldNotContain("CopyResidentParticlesIntoCombinedBuffer");
        source.ShouldNotContain("CopyCombinedParticlesBackToResidentBuffers");
        source.ShouldNotContain("ComputeStaticParticleSignature");
        source.ShouldNotContain("_allParticleStaticData");
        source.ShouldNotContain("ResidentParticlesBuffer");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate workspace file '{relativePath}'.");
    }
}
