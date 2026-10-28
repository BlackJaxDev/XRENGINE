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
        string fields = ReadWorkspaceFile("XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainComponent Fields.cs");
        string component = ReadWorkspaceFile("XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainComponent.Diagnostics.cs");
        string gpu = ReadWorkspaceFile("XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainComponent.GPU.cs");
        string dispatcherDebug = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Debug.cs");

        fields.ShouldContain("private bool _debugDrawChains;");
        fields.ShouldNotContain("private bool _debugDrawChains = true;");
        component.ShouldContain("if (DebugDrawChains)");
        gpu.ShouldContain("RuntimePhysicsChainRendering.Current.RenderDebug(this)");
        string renderingBridge = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/RuntimePhysicsChainRenderingBridge.cs");
        renderingBridge.ShouldContain("GPUPhysicsChainDispatcher.Instance.RenderSelectedGpuDebug(chain.World)");
        renderingBridge.ShouldContain("private volatile bool _debugDrawChains = chain.DebugDrawChains;");
        renderingBridge.ShouldContain("GPUPhysicsChainDispatcher.Instance.SetDebugDrawChainsSelection(this, selected);");
        dispatcherDebug.ShouldContain("if (!request.DebugSelected");
        dispatcherDebug.IndexOf("if (!request.DebugSelected", StringComparison.Ordinal)
            .ShouldBeLessThan(dispatcherDebug.IndexOf("items.Add(new PhysicsChainGpuDebugItem(", StringComparison.Ordinal));
    }

    [Test]
    public void ActiveRequests_PreserveStableDispatchBucketOrderWithoutSorting()
    {
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs");

        // TryAcceptInput rejects a request that does not need an update.
        dispatcher.ShouldContain("if (!NeedsUpdate)");
        int acceptIndex = dispatcher.IndexOf("if (!request.TryAcceptInput(", StringComparison.Ordinal);
        acceptIndex.ShouldBeGreaterThanOrEqualTo(0);
        int batchedIndex = dispatcher.IndexOf("if (request.DispatchIsolationKey == 0)", acceptIndex, StringComparison.Ordinal);
        batchedIndex.ShouldBeGreaterThan(acceptIndex);
        int isolatedIndex = dispatcher.IndexOf("_isolatedRequests.Add(request);", batchedIndex, StringComparison.Ordinal);
        isolatedIndex.ShouldBeGreaterThan(batchedIndex);
        int appendIndex = dispatcher.IndexOf("_activeRequests.Add(_isolatedRequests[i]);", isolatedIndex, StringComparison.Ordinal);
        appendIndex.ShouldBeGreaterThan(isolatedIndex);
        dispatcher.ShouldContain("Registration order is the stable order within both dispatch buckets.");
        dispatcher.ShouldNotContain("_activeRequests.Sort(");
        dispatcher.ShouldNotContain("RequiresDispatchSort(");
    }

    [Test]
    public void GpuDebugBufferGrowth_RebindsReplacementAllocations()
    {
        string dispatcherDebug = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Debug.cs");

        dispatcherDebug.ShouldContain("ReplaceDebugRendererBuffer(batch.PointsRenderer, batch.PointsBuffer)");
        dispatcherDebug.ShouldContain("ReplaceDebugRendererBuffer(batch.LinesRenderer, batch.LinesBuffer)");
        dispatcherDebug.ShouldContain("renderer.Buffers[name] = buffer;");
        dispatcherDebug.ShouldNotContain("!batch.PointsRenderer.Buffers.ContainsKey");
        dispatcherDebug.ShouldNotContain("!batch.LinesRenderer.Buffers.ContainsKey");
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
