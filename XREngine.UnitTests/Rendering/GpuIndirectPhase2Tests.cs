using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using System.Reflection;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class GpuIndirectPhase2Tests
{
    [SetUp]
    public void SetUp()
    {
        // Ensure consistent defaults per test.
        GPURenderPassCollection.ConfigureIndirectDebug(d =>
        {
            d.DisableCpuReadbackCount = true;
            d.ForceCpuFallbackCount = false;
            d.EnableCpuBatching = false;
        });

        // Reset per-frame stats accumulation.
        XREngine.Engine.Rendering.Stats.BeginFrame();
    }

    [Test]
    public void IndirectDebug_EnableCpuBatching_DefaultsOff()
    {
        GPURenderPassCollection.IndirectDebug.EnableCpuBatching.ShouldBeFalse();
    }

    [Test]
    public void GetVisibleCounts_DoesNotReadback_WhenCpuReadbackDisabled()
    {
        var pass = new GPURenderPassCollection(renderPass: 0);

        // Inject a non-null culled-count buffer so the method would previously try to map/read it.
        var bufferType = typeof(GPURenderPassCollection).GetField("_culledCountBuffer", BindingFlags.Instance | BindingFlags.NonPublic);
        bufferType.ShouldNotBeNull();

        // XRDataBuffer constructor is in the engine; creating it without Generate is enough
        // for this test because the Phase 2 guard should return before mapping.
        var fakeCountBuffer = new XREngine.Rendering.XRDataBuffer(
            "TestCulledCount",
            EBufferTarget.ShaderStorageBuffer,
            1,
            EComponentType.UInt,
            1,
            false,
            true);

        bufferType!.SetValue(pass, fakeCountBuffer);

        pass.GetVisibleCounts(out uint draws, out uint instances, out uint overflow);

        draws.ShouldBe(0u);
        instances.ShouldBe(0u);
        overflow.ShouldBe(0u);

        XREngine.Engine.Rendering.Stats.GpuMappedBuffers.ShouldBe(0);
        XREngine.Engine.Rendering.Stats.GpuReadbackBytes.ShouldBe(0);
    }

    [Test]
    public void UpdateVisibleCountersFromBuffer_IsNoOp_WhenCpuReadbackDisabled()
    {
        var pass = new GPURenderPassCollection(renderPass: 0);

        // Set VisibleCommandCount backing field directly to a sentinel.
        var visibleField = typeof(GPURenderPassCollection).GetField("_visibleCommandCount", BindingFlags.Instance | BindingFlags.NonPublic);
        visibleField.ShouldNotBeNull();
        visibleField!.SetValue(pass, 123u);

        // Call the private method via reflection.
        var method = typeof(GPURenderPassCollection).GetMethod(
            "UpdateVisibleCountersFromBuffer",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        method.ShouldNotBeNull();
        method!.Invoke(pass, null);

        // Should remain unchanged since DisableCpuReadbackCount=true.
        pass.VisibleCommandCount.ShouldBe(123u);

        XREngine.Engine.Rendering.Stats.GpuMappedBuffers.ShouldBe(0);
        XREngine.Engine.Rendering.Stats.GpuReadbackBytes.ShouldBe(0);
    }

    [Test]
    public void RenderingStats_ReadbackCounters_ResetEachFrame()
    {
        XREngine.Engine.Rendering.Stats.RecordGpuBufferMapped(2);
        XREngine.Engine.Rendering.Stats.RecordGpuReadbackBytes(64);

        // BeginFrame swaps last-frame stats and resets current.
        XREngine.Engine.Rendering.Stats.BeginFrame();

        XREngine.Engine.Rendering.Stats.GpuMappedBuffers.ShouldBe(2);
        XREngine.Engine.Rendering.Stats.GpuReadbackBytes.ShouldBe(64);

        // Next frame with no new records should be zero.
        XREngine.Engine.Rendering.Stats.BeginFrame();
        XREngine.Engine.Rendering.Stats.GpuMappedBuffers.ShouldBe(0);
        XREngine.Engine.Rendering.Stats.GpuReadbackBytes.ShouldBe(0);
    }
}
