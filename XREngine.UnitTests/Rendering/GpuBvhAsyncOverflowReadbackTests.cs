using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuBvhAsyncOverflowReadbackTests
{
    [Test]
    public void GpuBvhBuild_EnqueuesFenceInsteadOfConsumingOverflowSynchronously()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs");
        string build = Slice(source, "public void Build(XRDataBuffer aabbBuffer", "public void Refit()", StringComparison.Ordinal);

        build.ShouldContain("PollPendingOverflowCore()");
        build.ShouldContain("EnqueueOverflowFlagReadback(primitiveCount, _lastNodeCount)");
        build.ShouldNotContain("ConsumeOverflowFlag(primitiveCount, _lastNodeCount)");

        source.ShouldContain("XRGpuFence? fence = AbstractRenderer.Current?.InsertGpuFence();");
        source.ShouldContain("EGpuFenceStatus status = _pendingOverflowFence.Poll();");
        source.ShouldContain("RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy() == EMeshSubmissionStrategy.GpuIndirectZeroReadback");
    }

    [Test]
    public void GpuScene_PollsPendingBvhOverflowEvenWhenBvhIsNotDirty()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs");
        string prepare = Slice(source, "public void PrepareBvhForCulling(uint commandCount)", "private void EnsureGpuBvhResources", StringComparison.Ordinal);

        prepare.ShouldContain("_gpuBvhTree?.PollPendingOverflow() == true");
        prepare.ShouldContain("_bvhBuildSuppressed = true;");
        prepare.ShouldContain("_bvhSuppressedCommandCount = commandCount;");
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
