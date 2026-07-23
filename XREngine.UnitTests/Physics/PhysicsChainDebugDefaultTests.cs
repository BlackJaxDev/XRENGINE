using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainDebugDefaultTests
{
    [Test]
    public void PerChainDebugRendering_IsExplicitOptIn()
    {
        string fields = ReadWorkspaceFile("XRENGINE/Scene/Components/Physics/PhysicsChainComponent Fields.cs");
        string component = ReadWorkspaceFile("XRENGINE/Scene/Components/Physics/PhysicsChainComponent.cs");
        string gpu = ReadWorkspaceFile("XRENGINE/Scene/Components/Physics/PhysicsChainComponent.GPU.cs");
        string dispatcherDebug = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.Debug.cs");

        fields.ShouldContain("private bool _debugDrawChains;");
        fields.ShouldNotContain("private bool _debugDrawChains = true;");
        component.ShouldContain("if (!IsActiveInHierarchy || Engine.Rendering.State.IsShadowPass || !DebugDrawChains)");
        gpu.ShouldContain("GPUPhysicsChainDispatcher.Instance.RenderSelectedGpuDebug()");
        dispatcherDebug.ShouldContain("if (!request.Component.DebugDrawChains");
        dispatcherDebug.IndexOf("if (!request.Component.DebugDrawChains", StringComparison.Ordinal)
            .ShouldBeLessThan(dispatcherDebug.IndexOf("_gpuDebugItems.Add(", StringComparison.Ordinal));
    }

    [Test]
    public void ActiveRequests_PreserveStableDispatchBucketOrderWithoutSorting()
    {
        string dispatcher = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs");

        dispatcher.ShouldContain("request.NeedsUpdate && request.DispatchIsolationKey == 0");
        dispatcher.ShouldContain("request.NeedsUpdate && request.DispatchIsolationKey != 0");
        dispatcher.ShouldContain("Registration order is the stable order within both dispatch buckets.");
        dispatcher.ShouldNotContain("_activeRequests.Sort(");
        dispatcher.ShouldNotContain("RequiresDispatchSort(");
    }

    [Test]
    public void GpuDebugBufferGrowth_RebindsReplacementAllocations()
    {
        string dispatcherDebug = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.Debug.cs");

        dispatcherDebug.ShouldContain("ReplaceDebugRendererBuffer(_gpuDebugPointsRenderer, _gpuDebugPointsBuffer)");
        dispatcherDebug.ShouldContain("ReplaceDebugRendererBuffer(_gpuDebugLinesRenderer, _gpuDebugLinesBuffer)");
        dispatcherDebug.ShouldContain("renderer.Buffers[name] = buffer;");
        dispatcherDebug.ShouldNotContain("!_gpuDebugPointsRenderer.Buffers.ContainsKey");
        dispatcherDebug.ShouldNotContain("!_gpuDebugLinesRenderer.Buffers.ContainsKey");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return File.ReadAllText(Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
