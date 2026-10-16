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
        string renderingBridge = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/RuntimePhysicsChainRenderingBridge.cs");
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs");
        string dispatcherDebug = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Debug.cs");
        string debugPhysicsCommand = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderDebugPhysics.cs");

        fields.ShouldContain("private bool _debugDrawChains;");
        fields.ShouldNotContain("private bool _debugDrawChains = true;");
        fields.ShouldContain("RuntimePhysicsChainRendering.Current.SetDebugDrawChains(this, value);");
        component.ShouldContain("if (DebugDrawChains)");
        gpu.ShouldContain("RuntimePhysicsChainRendering.Current.RenderDebug(this)");

        // The chain choice becomes the dispatcher selection at registration and on each change.
        renderingBridge.ShouldContain("GPUPhysicsChainDispatcher.Instance.SetDebugDrawChainsSelection(this, selected);");
        dispatcher.ShouldContain("request.DebugSelected = component.DebugDrawChains;");

        // All draw entries go through the selected-chain path for one world.
        renderingBridge.ShouldContain("GPUPhysicsChainDispatcher.Instance.RenderSelectedGpuDebug(chain.World)");
        debugPhysicsCommand.ShouldContain("GPUPhysicsChainDispatcher.Instance.RenderSelectedGpuDebug(world.WorldContext)");
        dispatcherDebug.ShouldContain("Volatile.Read(ref _selectedGpuDebugChainCount) == 0");

        // Only selected chains add debug items.
        dispatcherDebug.ShouldContain("if (!request.DebugSelected");
        dispatcherDebug.ShouldContain("items.Add(new PhysicsChainGpuDebugItem(");
        dispatcherDebug.IndexOf("if (!request.DebugSelected", StringComparison.Ordinal)
            .ShouldBeLessThan(dispatcherDebug.IndexOf("items.Add(new PhysicsChainGpuDebugItem(", StringComparison.Ordinal));
    }

    [Test]
    public void ActiveRequests_PreserveStableDispatchBucketOrderWithoutSorting()
    {
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs");

        // A request enters a dispatch bucket only after it accepts new input.
        ShouldContainInOrder(dispatcher,
            "internal bool TryAcceptInput(",
            "if (!NeedsUpdate)",
            "return false;");

        // One pass in registration order fills the batched bucket and the isolated bucket.
        // The isolated bucket then follows the batched bucket.
        ShouldContainInOrder(dispatcher,
            "if (!request.TryAcceptInput(out ulong rejectedSequence,",
            "if (request.DispatchIsolationKey == 0)",
            "_activeRequests.Add(request);",
            "_isolatedRequests.Add(request);",
            "Registration order is the stable order within both dispatch buckets.",
            "_activeRequests.Add(_isolatedRequests[i]);");
        dispatcher.ShouldNotContain("_activeRequests.Sort(");
        dispatcher.ShouldNotContain("_isolatedRequests.Sort(");
        dispatcher.ShouldNotContain("RequiresDispatchSort(");
    }

    [Test]
    public void GpuDebugBufferGrowth_RebindsReplacementAllocations()
    {
        string dispatcherDebug = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Debug.cs");

        // The renderers take the buffers again after each capacity check.
        ShouldContainInOrder(dispatcherDebug,
            "EnsureRawDebugBuffer(ref batch.PointsBuffer,",
            "EnsureRawDebugBuffer(ref batch.LinesBuffer,",
            "ReplaceDebugRendererBuffer(batch.PointsRenderer, batch.PointsBuffer)",
            "ReplaceDebugRendererBuffer(batch.LinesRenderer, batch.LinesBuffer)");
        dispatcherDebug.ShouldContain("renderer.Buffers[name] = buffer;");
        dispatcherDebug.ShouldNotContain("Buffers.ContainsKey");
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
