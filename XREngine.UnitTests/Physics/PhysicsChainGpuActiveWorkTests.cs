using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainGpuActiveWorkTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();
    private static readonly string ShaderPath = Path.Combine(
        WorkspaceRoot, "Build", "CommonAssets", "Shaders", "Compute", "PhysicsChain", "PhysicsChainActiveWork.comp");

    [Test]
    public void GpuSchedulingLayouts_MatchStd430ScalarRecords()
    {
        Marshal.SizeOf<PhysicsChainGpuInstanceMetadata>().ShouldBe(32);
        Marshal.SizeOf<PhysicsChainGpuTreeWorkItem>().ShouldBe(16);
        Marshal.SizeOf<PhysicsChainIndirectDispatchArguments>().ShouldBe(12);
    }

    [Test]
    public void PortableFallback_RemainsGpuOwnedAndExplicit()
    {
        var capabilities = new PhysicsChainComputeCapabilities(true, true, true, true, true, false);

        GPUPhysicsChainDispatcher.SelectActiveWorkScanMode(capabilities)
            .ShouldBe(PhysicsChainActiveWorkScanMode.PortableWorkgroup);

        string source = File.ReadAllText(ShaderPath).Replace("\r\n", "\n");
        source.ShouldContain("shared uint Prefix[128]");
        source.ShouldContain("atomicAdd(WorkCounters[bucket], groupCount)");
        source.ShouldContain("min(WorkCounters[bucket], CapacityPerBucket)");
        source.ShouldContain("atomicAdd(WorkCounters[KernelBucketCount + bucket], 1u)");
        source.ShouldContain("IndirectArguments[argumentBase]");
        source.ShouldNotContain("subgroupExclusiveAdd");
    }

    [Test]
    public void TopologyBucketing_IsStableAcrossRepeatedOrderingChecks()
    {
        var linear = new GPUPhysicsChainDispatcher.GPUParticleStaticData[8];
        linear[0].ParentIndex = -1;
        for (int index = 1; index < linear.Length; ++index)
            linear[index].ParentIndex = index - 1;

        var branched = (GPUPhysicsChainDispatcher.GPUParticleStaticData[])linear.Clone();
        branched[5].ParentIndex = 2;
        var tree = new GPUPhysicsChainDispatcher.GPUParticleTreeData
        {
            RestGravity = Vector3.UnitY,
            ParticleOffset = 0,
            ParticleCount = linear.Length,
        };

        for (int iteration = 0; iteration < 256; ++iteration)
        {
            GPUPhysicsChainDispatcher.ClassifyKernelBucket(linear, tree)
                .ShouldBe(PhysicsChainKernelBucket.ShortLinear);
            GPUPhysicsChainDispatcher.ClassifyKernelBucket(branched, tree)
                .ShouldBe(PhysicsChainKernelBucket.BranchedOrLong);
        }
    }

    [Test]
    public void MainKernel_ConsumesGpuCompactedIdsWithoutCpuCountReadback()
    {
        string scheduler = File.ReadAllText(ShaderPath).Replace("\r\n", "\n");
        string solver = File.ReadAllText(Path.Combine(Path.GetDirectoryName(ShaderPath)!, "PhysicsChain.comp"))
            .Replace("\r\n", "\n");
        string dispatcher = File.ReadAllText(Path.Combine(
            WorkspaceRoot, "XRENGINE", "Rendering", "Compute", "GPUPhysicsChainDispatcher.cs"));

        scheduler.ShouldContain("CapacityPerBucket");
        solver.ShouldContain("ActiveTreeIds[ActiveTreeIdBase + dispatchIndex]");
        dispatcher.ShouldContain("TryDispatchIndirect(_mainPhysicsProgram");
        dispatcher.ShouldContain("EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command");
        dispatcher.ShouldNotContain("TryReadBuffer(_activeWorkCounterBuffer");
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(127)]
    [TestCase(10_000)]
    public void CapacityPerBucket_CoversEveryCandidateWithoutOverflowReadback(int candidateCount)
    {
        int capacity = GPUPhysicsChainDispatcher.CalculateActiveListCapacityPerBucket(candidateCount);

        capacity.ShouldBeGreaterThanOrEqualTo(Math.Max(candidateCount, 1));
        (capacity & (capacity - 1)).ShouldBe(0);
    }

    [Test]
    public void StableActiveInputs_UploadOnlyVersionedDirtyRanges()
    {
        string dispatcher = File.ReadAllText(Path.Combine(
            WorkspaceRoot, "XRENGINE", "Rendering", "Compute", "GPUPhysicsChainDispatcher.cs"))
            .Replace("\r\n", "\n");

        dispatcher.ShouldContain("bool fullUpload = !IsActiveWorkLayoutCurrent(requests)");
        dispatcher.ShouldContain("request.UploadedSchedulingMetadataVersion != request.SchedulingMetadataVersion");
        dispatcher.ShouldContain("request.UploadedTreeWorkStaticVersion != request.StaticDataVersion");
        dispatcher.ShouldContain("CollectionsMarshal.AsSpan(_instanceMetadata).Slice(metadataDirtyStart, dirtyCount)");
        dispatcher.ShouldContain("CollectionsMarshal.AsSpan(_treeWorkItems).Slice(treeDirtyStart, dirtyCount)");
        dispatcher.ShouldContain("CalculateActiveListCapacityPerBucket(candidateCount)");
        dispatcher.ShouldNotContain("TryReadBuffer(_activeWorkCounterBuffer");
    }

    private static string FindWorkspaceRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Build", "CommonAssets", "Shaders")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XRENGINE workspace root.");
    }

}
