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
    private static readonly string PhysicsComputeDirectory = Path.Combine(
        WorkspaceRoot, "XREngine.Runtime.Rendering", "Rendering", "PhysicsCompute");

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
            WorkspaceRoot, "XREngine.Runtime.Rendering", "Rendering", "PhysicsCompute", "GPUPhysicsChainDispatcher.cs"));

        scheduler.ShouldContain("CapacityPerBucket");
        solver.ShouldContain("ActiveTreeIds[ActiveTreeIdBase + dispatchIndex]");
        dispatcher.ShouldContain("EBufferTarget.DispatchIndirectBuffer");
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
    public void ActiveWorkAndTreeHeaders_WriteFullRangesIntoEachInputPage()
    {
        string dispatcher = File.ReadAllText(Path.Combine(PhysicsComputeDirectory, "GPUPhysicsChainDispatcher.cs"))
            .Replace("\r\n", "\n");
        string inputPages = File.ReadAllText(Path.Combine(PhysicsComputeDirectory, "GPUPhysicsChainDispatcher.InputPages.cs"))
            .Replace("\r\n", "\n");
        string physicsCompute = ReadPhysicsComputeSources();

        // Each dispatch group binds a fresh input page. That page holds data of an older frame.
        string acquire = ExtractMember(inputPages, "private bool TryAcquireInputPage(IPhysicsChainComputeBackend backend)");
        acquire.ShouldContain("_perTreeParamsBuffer = page.Headers;");
        acquire.ShouldContain("_instanceMetadataBuffer = page.Instances;");
        acquire.ShouldContain("_treeWorkItemBuffer = page.TreeWork;");
        ShouldContainInOrder(
            ExtractMember(dispatcher, "private bool ProcessDispatchGroups(IPhysicsChainComputeBackend backend)"),
            "if (!TryAcquireInputPage(backend))",
            "if (!ProcessDispatchGroup(backend, out string failureStage))");
        ExtractMember(dispatcher, "private bool ProcessDispatchGroup(")
            .ShouldContain("if (!BuildResidentArenaBuffers(backend, _dispatchGroup))");

        // The group rebuilds its records, then writes them into the page it holds.
        ShouldContainInOrder(
            ExtractMember(dispatcher, "private bool BuildResidentArenaBuffers("),
            "_allPerTreeParams.Clear();",
            "BuildActiveWorkRecords(requests);",
            "|| !UploadDynamicTreeHeaders()",
            "|| !UploadActiveWorkInputs()");

        // The full ranges go to element zero. No dirty slice writes only part of a page.
        string activeWork = ExtractMember(dispatcher, "private bool UploadActiveWorkInputs()");
        activeWork.ShouldContain("TryWriteInputPage(_instanceMetadataBuffer,");
        activeWork.ShouldContain("(ReadOnlySpan<PhysicsChainGpuInstanceMetadata>)CollectionsMarshal.AsSpan(_instanceMetadata), 0)");
        activeWork.ShouldContain("TryWriteInputPage(_treeWorkItemBuffer,");
        activeWork.ShouldContain("(ReadOnlySpan<PhysicsChainGpuTreeWorkItem>)CollectionsMarshal.AsSpan(_treeWorkItems), 0)");
        activeWork.ShouldNotContain(".Slice(");
        string treeHeaders = ExtractMember(dispatcher, "private bool UploadDynamicTreeHeaders()");
        treeHeaders.ShouldContain("TryWriteInputPage(_perTreeParamsBuffer,");
        treeHeaders.ShouldContain("(ReadOnlySpan<GPUPerTreeParams>)CollectionsMarshal.AsSpan(_allPerTreeParams), 0)");
        treeHeaders.ShouldNotContain(".Slice(");

        // Kernel buckets come from the request cache, not from a classification of each tree each frame.
        string records = ExtractMember(dispatcher, "private void BuildActiveWorkRecords(IReadOnlyList<GPUPhysicsChainRequest> requests)");
        records.ShouldContain("ReadOnlySpan<PhysicsChainKernelBucket> buckets = request.GetKernelBuckets();");
        records.ShouldNotContain("ClassifyKernelBucket(");

        // The removed layout checks and per-request upload stamps must not come back.
        physicsCompute.ShouldNotContain("IsActiveWorkLayoutCurrent");
        physicsCompute.ShouldNotContain("IsDynamicHeaderLayoutCurrent");
        physicsCompute.ShouldNotContain("IsDynamicHeaderDirty");
        physicsCompute.ShouldNotContain("MarkDynamicHeaderUploaded");
        physicsCompute.ShouldNotContain("PhysicsChainDynamicHeaderLayoutEntry");
        physicsCompute.ShouldNotContain("SchedulingMetadataVersion");
        physicsCompute.ShouldNotContain("DynamicHeaderVersion");
        physicsCompute.ShouldNotContain("UploadedTreeWork");
        physicsCompute.ShouldNotContain("UploadedHeader");
        physicsCompute.ShouldNotContain("CollectionsMarshal.AsSpan(_instanceMetadata).Slice(");
        physicsCompute.ShouldNotContain("CollectionsMarshal.AsSpan(_treeWorkItems).Slice(");
        physicsCompute.ShouldNotContain("CollectionsMarshal.AsSpan(_allPerTreeParams).Slice(");

        dispatcher.ShouldContain("CalculateActiveListCapacityPerBucket(candidateCount)");
        physicsCompute.ShouldNotContain("TryReadBuffer(_activeWorkCounterBuffer");
    }

    [Test]
    public void KernelBuckets_ReuseCacheUntilStaticOrTreeVersionChanges()
    {
        var particles = new GPUPhysicsChainDispatcher.GPUParticleStaticData[8];
        particles[0].ParentIndex = -1;
        for (int index = 1; index < particles.Length; ++index)
            particles[index].ParentIndex = index - 1;
        GPUPhysicsChainDispatcher.GPUParticleTreeData[] trees =
        [
            new GPUPhysicsChainDispatcher.GPUParticleTreeData
            {
                RestGravity = Vector3.UnitY,
                ParticleOffset = 0,
                ParticleCount = particles.Length,
            },
        ];
        // The bucket cache reads no member of the source, so the test gives no source.
        var request = new GPUPhysicsChainRequest(null!)
        {
            ParticleStaticData = particles,
            Trees = trees,
            StaticDataVersion = 1,
            TreeDataVersion = 1,
        };

        ReadOnlySpan<PhysicsChainKernelBucket> first = request.GetKernelBuckets();
        first.Length.ShouldBe(1);
        first[0].ShouldBe(PhysicsChainKernelBucket.ShortLinear);

        // Change the data in place but keep the versions. The cached bucket must stay.
        particles[5].ParentIndex = 2;
        ReadOnlySpan<PhysicsChainKernelBucket> reused = request.GetKernelBuckets();
        (reused == first).ShouldBeTrue();
        reused[0].ShouldBe(PhysicsChainKernelBucket.ShortLinear);

        // A new static data version classifies the branched data again.
        request.StaticDataVersion = 2;
        request.GetKernelBuckets()[0].ShouldBe(PhysicsChainKernelBucket.BranchedOrLong);

        // Shorten the tree in place. Only a new tree version classifies it again.
        trees[0].ParticleCount = 4;
        request.GetKernelBuckets()[0].ShouldBe(PhysicsChainKernelBucket.BranchedOrLong);
        request.TreeDataVersion = 2;
        request.GetKernelBuckets()[0].ShouldBe(PhysicsChainKernelBucket.ShortLinear);
    }

    /// <summary>Reads all runtime sources of the physics compute folder in a stable order.</summary>
    private static string ReadPhysicsComputeSources()
    {
        string[] files = Directory.GetFiles(PhysicsComputeDirectory, "*.cs", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        return string.Join("\n", files.Select(file => File.ReadAllText(file).Replace("\r\n", "\n")));
    }

    /// <summary>Returns one member from its signature to its matching closing brace.</summary>
    private static string ExtractMember(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Missing member '{signature}'.");
        int open = source.IndexOf('{', start + signature.Length);
        open.ShouldBeGreaterThan(start, $"Member '{signature}' has no block body.");

        int depth = 0;
        for (int index = open; index < source.Length; ++index)
        {
            if (source[index] == '{')
                ++depth;
            else if (source[index] == '}' && --depth == 0)
                return source.Substring(start, index - start + 1);
        }

        throw new InvalidOperationException($"Member '{signature}' has no closing brace.");
    }

    /// <summary>Checks that each marker occurs after the marker before it.</summary>
    private static void ShouldContainInOrder(string source, params string[] markers)
    {
        int previous = -1;
        foreach (string marker in markers)
        {
            int position = source.IndexOf(marker, previous + 1, StringComparison.Ordinal);
            position.ShouldBeGreaterThan(previous, $"'{marker}' is missing or out of order.");
            previous = position;
        }
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
