using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainRuntimeDiagnosticsTests
{
    [Test]
    public void SnapshotReportsBackendKernelQualityAndExplicitCompatibilityWorkWithoutAllocating()
    {
        var node = new SceneNode("DiagnosticsChain");
        PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;
        component.EndLength = 0.25f;
        component.SetupParticles();

        PhysicsChainRuntimeDiagnostics cpu = component.GetRuntimeDiagnostics();
        cpu.Backend.ShouldBe(PhysicsChainRuntimeBackend.CpuDataOriented);
        cpu.GpuKernelFamilies.ShouldBe(PhysicsChainGpuKernelMask.ShortLinear);
        cpu.RequestedQualityTier.ShouldBe(PhysicsChainQualityTier.Strict);
        cpu.CompatibilityFeatures.ShouldBe(PhysicsChainCompatibilityFeatures.CpuTransformMirror);

        component.UseGPU = true;
        component.UseBatchedDispatcher = false;
        component.GpuSyncToBones = true;
        component.DebugDrawChains = true;
        component.QualityTier = PhysicsChainQualityTier.Hz15;
        PhysicsChainRuntimeDiagnostics gpu = component.GetRuntimeDiagnostics();
        gpu.Backend.ShouldBe(PhysicsChainRuntimeBackend.GpuStandalone);
        gpu.RequestedQualityTier.ShouldBe(PhysicsChainQualityTier.Hz15);
        gpu.CompatibilityFeatures.ShouldBe(
            PhysicsChainCompatibilityFeatures.CpuTransformMirror
            | PhysicsChainCompatibilityFeatures.GpuBoneReadback
            | PhysicsChainCompatibilityFeatures.PerChainDebugRendering);

        _ = component.GetRuntimeDiagnostics();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; ++iteration)
            _ = component.GetRuntimeDiagnostics();
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0L);
    }
}
