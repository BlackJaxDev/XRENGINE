using System;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuBvhAsyncOverflowReadbackTests
{
    [Test]
    public void GpuBvhBuild_EnqueuesFenceInsteadOfConsumingOverflowSynchronously()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs");
        string overflowSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Overflow.cs");
        string build = Slice(source, "public void Build(XRDataBuffer aabbBuffer", "public void Refit()", StringComparison.Ordinal);

        build.ShouldContain("PollPendingOverflowCore()");
        build.ShouldContain("EnqueueOverflowFlagReadback(primitiveCount, _lastNodeCount)");
        build.ShouldNotContain("ConsumeOverflowFlag(primitiveCount, _lastNodeCount)");

        overflowSource.ShouldContain("XRGpuFence? fence = AbstractRenderer.Current?.InsertGpuFence();");
        overflowSource.ShouldContain("EGpuFenceStatus status = _pendingOverflowFence.Poll();");
        overflowSource.ShouldContain("RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy().IsGpuZeroReadbackStrategy()");
    }

    [Test]
    public void GpuScene_PollsPendingBvhOverflowEvenWhenBvhIsNotDirty()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.Bvh.cs");
        string prepare = Slice(source, "public void PrepareBvhForCulling(uint commandCount)", "private void EnsureGpuBvhResources", StringComparison.Ordinal);

        prepare.ShouldContain("_gpuBvhTree?.PollPendingOverflow() == true");
        prepare.ShouldContain("_bvhBuildSuppressed = true;");
        prepare.ShouldContain("_bvhSuppressedCommandCount = commandCount;");
    }

    [Test]
    public void CleanBuildIdentity_SkipsOnlyExactStableInputsBeforeOverflowReset()
    {
        using var aabbBuffer = new XRDataBuffer(
            "StableAabbs",
            EBufferTarget.ShaderStorageBuffer,
            8u,
            EComponentType.Float,
            8u,
            false,
            true);
        using var differentBuffer = new XRDataBuffer(
            "DifferentAabbs",
            EBufferTarget.ShaderStorageBuffer,
            8u,
            EComponentType.Float,
            8u,
            false,
            true);
        Vector3 sceneMin = new(-10.0f);
        Vector3 sceneMax = new(10.0f);
        var identity = new GpuBvhBuildIdentity(aabbBuffer, 8u, sceneMin, sceneMax);

        GpuBvhTree.CanReuseCompletedBuild(false, identity, aabbBuffer, 8u, sceneMin, sceneMax).ShouldBeTrue();
        GpuBvhTree.CanReuseCompletedBuild(true, identity, aabbBuffer, 8u, sceneMin, sceneMax).ShouldBeFalse();
        GpuBvhTree.CanReuseCompletedBuild(false, identity, differentBuffer, 8u, sceneMin, sceneMax).ShouldBeFalse();
        GpuBvhTree.CanReuseCompletedBuild(false, identity, aabbBuffer, 7u, sceneMin, sceneMax).ShouldBeFalse();

        Vector3 changedMax = new(11.0f);
        GpuBvhTree.CanReuseCompletedBuild(false, identity, aabbBuffer, 8u, sceneMin, changedMax).ShouldBeFalse();

        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs");
        string build = Slice(source, "public void Build(XRDataBuffer aabbBuffer", "public void Refit()", StringComparison.Ordinal);
        int stableGate = build.IndexOf("CanReuseCompletedBuild(", StringComparison.Ordinal);
        int buildTiming = build.IndexOf("SubmissionScope(BvhGpuProfiler.Stage.Build)", StringComparison.Ordinal);
        int overflowReset = build.IndexOf("IsOverflowFlagBufferReady()", StringComparison.Ordinal);
        stableGate.ShouldBeGreaterThanOrEqualTo(0);
        buildTiming.ShouldBeGreaterThan(stableGate, "clean duplicates must not allocate timestamp queries or count as build submissions");
        overflowReset.ShouldBeGreaterThan(stableGate, "the stabilized-frame return must happen before overflow-buffer readiness work");
    }

    [Test]
    public void GpuBvhBuild_DefersDispatchUntilOverflowResetIsDescriptorReady()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs");
        string overflowSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Overflow.cs");
        string build = Slice(source, "public void Build(XRDataBuffer aabbBuffer", "public void Refit()", StringComparison.Ordinal);
        string reset = Slice(overflowSource, "private bool IsOverflowFlagBufferReady()", "private bool EnqueueOverflowFlagReadback", StringComparison.Ordinal);

        int readyGate = build.IndexOf("if (!IsOverflowFlagBufferReady())", StringComparison.Ordinal);
        int firstDispatch = build.IndexOf("DispatchMortonCodes(", StringComparison.Ordinal);
        readyGate.ShouldBeGreaterThanOrEqualTo(0);
        firstDispatch.ShouldBeGreaterThan(readyGate, "Vulkan must not capture compute descriptors while the reset upload is pending");

        reset.ShouldContain("_overflowFlagBuffer?.IsReadyForGpuUse == true");
        reset.ShouldNotContain("PushSubData");
        reset.ShouldNotContain("program.BindBuffer");

        string dispatch = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Dispatch.cs");
        dispatch.ShouldContain("program.Uniform(\"resetOverflow\", 1u);");
        dispatch.ShouldContain("program.Uniform(\"resetOverflow\", 0u);");
        string shader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/OctreeGeneration/morton_codes.comp");
        shader.ShouldContain("uniform uint resetOverflow;");
        shader.ShouldContain("overflowFlags = 0u;");

        string scene = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.Bvh.cs");
        scene.ShouldContain("if (_gpuBvhTree.IsBuildPendingResources)");
        scene.ShouldContain("return false;");
    }

    [Test]
    public void GpuBvhBuffers_IncludeStableOwnerAndInstanceDiagnostics()
    {
        string tree = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs");
        string scene = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.Bvh.cs");
        string mesh = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuMeshBvh.cs");

        tree.ShouldContain("GpuBvhTree[{ownerName}:{id}]");
        scene.ShouldContain("new GpuBvhTree(\"GPUScene.CommandBvh\")");
        mesh.ShouldContain("new(\"GpuMeshBvh\")");
    }

    [Test]
    public void GpuScene_ReenableRetainsReadyTreeWhileMutationsDirtyRetainedTopology()
    {
        string bvh = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.Bvh.cs");
        string buffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.CommandBuffers.cs");
        string addRemove = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.AddRemove.cs");
        string setter = Slice(bvh, "public bool UseInternalBvh", "// -------------------------------------------------------------------------", StringComparison.Ordinal);

        setter.ShouldContain("if (value && (_gpuBvhTree is null || !_bvhReady))");
        buffers.ShouldContain("if (_gpuBvhTree is not null)\n                    MarkBvhDirty();");
        CountOccurrences(addRemove, "if (_gpuBvhTree is not null)\n                        MarkBvhDirty();").ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public void RendererFenceAbstraction_HasOpenGLNonBlockingImplementation()
    {
        string abstractRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/AbstractRenderer.cs");
        string fence = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/XRGpuFence.cs");
        string openGlFence = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Frame/OpenGLRenderer.GpuFence.cs");

        abstractRenderer.ShouldContain("public virtual XRGpuFence? InsertGpuFence()");
        fence.ShouldContain("public enum EGpuFenceStatus");
        openGlFence.ShouldContain("Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u)");
        openGlFence.ShouldContain("ClientWaitSync(_sync, 0u, 0u)");
        openGlFence.ShouldContain("DeleteSync(_sync)");
    }

    [Test]
    public void BufferMapFlags_UseDistinctOpenGlBitValues()
    {
        ((int)EBufferMapRangeFlags.Persistent).ShouldBe(0x0040);
        ((int)EBufferMapRangeFlags.Coherent).ShouldBe(0x0080);
        ((int)EBufferMapStorageFlags.Persistent).ShouldBe(0x0040);
        ((int)EBufferMapStorageFlags.Coherent).ShouldBe(0x0080);
        ((int)EBufferMapStorageFlags.DynamicStorage).ShouldBe(0x0100);
        ((int)EBufferMapStorageFlags.ClientStorage).ShouldBe(0x0200);
    }

    private static string Slice(string source, string startToken, string endToken, StringComparison comparison)
    {
        string normalized = source.Replace("\r\n", "\n");
        int start = normalized.IndexOf(startToken, comparison);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Could not find start token '{startToken}'.");

        int end = normalized.IndexOf(endToken, start + startToken.Length, comparison);
        end.ShouldBeGreaterThan(start, $"Could not find end token '{endToken}' after '{startToken}'.");

        return normalized[start..end];
    }

    private static int CountOccurrences(string source, string token)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }
        return count;
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string root = ResolveWorkspaceRoot();
        string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(fullPath).ShouldBeTrue($"Expected workspace file to exist: {relativePath}");
        return File.ReadAllText(fullPath).Replace("\r\n", "\n");
    }

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XRENGINE.sln")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find workspace root from base directory '{AppContext.BaseDirectory}'.");
    }
}
