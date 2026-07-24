using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainGpuKernelFamilyTests
{
    private static readonly string WorkspaceRoot = ResolveWorkspaceRoot();

    [Test]
    public void DepthRangeRecord_MatchesStd430ScalarLayout()
    {
        Marshal.SizeOf<PhysicsChainGpuDepthRange>().ShouldBe(8);
        Marshal.OffsetOf<PhysicsChainGpuDepthRange>(nameof(PhysicsChainGpuDepthRange.ParticleIdOffset)).ToInt32().ShouldBe(0);
        Marshal.OffsetOf<PhysicsChainGpuDepthRange>(nameof(PhysicsChainGpuDepthRange.ParticleCount)).ToInt32().ShouldBe(4);
    }
    [Test]
    public void DepthOrder_PrecomputesParentsBeforeChildrenForBranchedTree()
    {
        var particles = new GPUPhysicsChainDispatcher.GPUParticleStaticData[5];
        particles[0].ParentIndex = -1;
        particles[1].ParentIndex = 0;
        particles[2].ParentIndex = 1;
        particles[3].ParentIndex = 1;
        particles[4].ParentIndex = 3;
        var tree = new GPUPhysicsChainDispatcher.GPUParticleTreeData
        {
            ParticleOffset = 0,
            ParticleCount = particles.Length,
        };
        Span<int> depths = stackalloc int[particles.Length];

        GPUPhysicsChainDispatcher.TryBuildTreeDepthOrder(
            particles,
            tree,
            depths,
            out int depthCount).ShouldBeTrue();

        depthCount.ShouldBe(4);
        depths[0].ShouldBe(0);
        depths[1].ShouldBe(1);
        depths[2].ShouldBe(2);
        depths[3].ShouldBe(2);
        depths[4].ShouldBe(3);
        for (int particleIndex = 1; particleIndex < particles.Length; ++particleIndex)
        {
            int parentIndex = particles[particleIndex].ParentIndex;
            depths[parentIndex].ShouldBeLessThan(depths[particleIndex]);
        }
    }


    [Test]
    public void IndirectArguments_ScaleByKernelExecutionShape()
    {
        string source = ReadShader("PhysicsChainActiveWork.comp");

        source.ShouldContain("IndirectArguments[argumentBase] = bucket == 0u");
        source.ShouldContain("(clampedCount + gl_WorkGroupSize.x - 1u) / gl_WorkGroupSize.x");
        source.ShouldContain(": clampedCount;");
    }

    [Test]
    public void BranchedKernel_UsesPrecomputedDepthRangesAndDependencyBarriers()
    {
        string source = ReadShader("PhysicsChainBranched.comp");

        source.ShouldContain("uint activeTreeIndex = gl_WorkGroupID.x;");
        source.ShouldContain("DepthRanges[tree.DepthRangeOffset + depth]");
        source.ShouldContain("DepthParticleIds[range.ParticleIdOffset + rangeIndex]");
        source.ShouldContain("memoryBarrierBuffer();\n    barrier();");
        source.ShouldContain("rangeIndex += gl_WorkGroupSize.x");
        source.ShouldContain("activeTreeIndex >= activeCount");
        source.ShouldContain("particleId < uint(particleStart) || particleId >= uint(particleEnd)");
    }

    [Test]
    public void Dispatcher_UsesExplicitProgramsLabelsAndZeroReadbackCounters()
    {
        string dispatcher = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Kernels.cs");

        dispatcher.ShouldContain("PhysicsChainBranched.comp");
        dispatcher.ShouldContain("GPUPhysicsChainDispatcher.Solver.ShortLinear");
        dispatcher.ShouldContain("GPUPhysicsChainDispatcher.Solver.BranchedOrLong");
        dispatcher.ShouldContain("if (_kernelCandidateCounts[(int)bucket] == 0)");
        dispatcher.ShouldContain("TryDispatchIndirect(backend, program");
        dispatcher.ShouldContain("DynamicWorkgroupCountsRemainGpuAuthored: true");
        dispatcher.ShouldNotContain("TryReadBuffer(_activeWorkCounterBuffer");
    }

    [Test]
    public void BranchedKernel_DeclaresAllInterPassInputsAsStorageBuffers()
    {
        string source = ReadShader("PhysicsChainBranched.comp");

        source.ShouldContain("layout(std430, binding = 5) readonly buffer PerTreeParamsBuffer");
        source.ShouldContain("layout(std430, binding = 6) readonly buffer ActiveTreeIdBuffer");
        source.ShouldContain("layout(std430, binding = 7) readonly buffer ActiveWorkCounterBuffer");
        source.ShouldContain("layout(std430, binding = 8) readonly buffer DepthRangeBuffer");
        source.ShouldContain("layout(std430, binding = 9) readonly buffer DepthParticleIdBuffer");
    }

    private static string ReadShader(string name)
        => ReadWorkspaceFile($"Build/CommonAssets/Shaders/Compute/PhysicsChain/{name}");

    private static string ReadWorkspaceFile(string relativePath)
        => File.ReadAllText(Path.Combine(WorkspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .Replace("\r\n", "\n");

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate XRENGINE workspace root.");
    }
}
