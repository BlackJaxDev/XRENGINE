using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainQualityFeedbackTests
{
    private static readonly PropertyInfo WorldProperty = typeof(RuntimeWorldObjectBase).GetProperty(
        nameof(RuntimeWorldObjectBase.World),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

    [TestCase(false)]
    [TestCase(true)]
    public void DelayedTimingCalibratesOnlyAutomaticPoolAndNeverDemotesStrict(bool useGpu)
    {
        var world = new TestWorldContext();
        (SceneNode fixedNode, PhysicsChainComponent fixedChain) = CreateChain("Fixed", PhysicsChainQualityTier.Strict, useGpu);
        (SceneNode automaticNode, PhysicsChainComponent automaticChain) = CreateChain("Automatic", PhysicsChainQualityTier.Automatic, useGpu);

        try
        {
            PhysicsChainWorld scheduler = Register(world, fixedNode, automaticNode);
            scheduler.MaximumAutomaticQualityTransitionsPerFrame = 1;
            scheduler.MinimumAutomaticTierResidenceFrames = 0;
            if (useGpu)
                scheduler.AutomaticGpuTimeBudgetMilliseconds = 4.0;
            else
                scheduler.AutomaticCpuTimeBudgetMilliseconds = 4.0;

            world.Invoke(ETickGroup.Late);
            var sample = new PhysicsChainQualityFeedbackSample(
                0L,
                useGpu ? PhysicsChainQualityFeedbackBackend.Gpu : PhysicsChainQualityFeedbackBackend.Cpu,
                8L,
                8.0,
                0.0f);

            scheduler.TrySubmitDelayedQualityFeedback(
                automaticChain.RuntimeHandle,
                sample,
                out PhysicsChainQualityFeedbackRejectionReason rejection).ShouldBeTrue();
            rejection.ShouldBe(PhysicsChainQualityFeedbackRejectionReason.None);
            world.Invoke(ETickGroup.Late);

            fixedChain.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Strict);
            automaticChain.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Hz30);
            PhysicsChainQualityBudgetDiagnostics diagnostics = scheduler.QualityBudgetDiagnostics;
            (useGpu ? diagnostics.GpuEffectiveBudgetWorkUnits : diagnostics.CpuEffectiveBudgetWorkUnits)
                .ShouldBe(4L);
            (useGpu ? diagnostics.GpuEffectiveWorkUnits : diagnostics.CpuEffectiveWorkUnits)
                .ShouldBe(4L);
            diagnostics.AcceptedFeedbackSamples.ShouldBe(1L);
        }
        finally
        {
            Unregister(world, fixedNode, automaticNode);
        }
    }

    [Test]
    public void InvalidCurrentOutOfOrderExpiredAndStaleGenerationSamplesAreRejected()
    {
        var world = new TestWorldContext();
        (SceneNode node, PhysicsChainComponent chain) = CreateChain("Automatic", PhysicsChainQualityTier.Automatic);

        try
        {
            PhysicsChainWorld scheduler = Register(world, node);
            world.Invoke(ETickGroup.Late);
            PhysicsChainRuntimeHandle handle = chain.RuntimeHandle;

            Submit(scheduler, handle, new(0L, PhysicsChainQualityFeedbackBackend.Cpu, 8L, double.NaN, 0.0f))
                .ShouldBe(PhysicsChainQualityFeedbackRejectionReason.InvalidSample);
            Submit(scheduler, handle, new(1L, PhysicsChainQualityFeedbackBackend.Cpu, 8L, 1.0, 0.0f))
                .ShouldBe(PhysicsChainQualityFeedbackRejectionReason.CurrentOrFutureFrame);
            Submit(scheduler, handle, new(0L, PhysicsChainQualityFeedbackBackend.Gpu, 8L, 1.0, 0.0f))
                .ShouldBe(PhysicsChainQualityFeedbackRejectionReason.BackendMismatch);
            Submit(scheduler, handle, new(0L, PhysicsChainQualityFeedbackBackend.Cpu, 8L, 1.0, 0.0f))
                .ShouldBe(PhysicsChainQualityFeedbackRejectionReason.None);
            Submit(scheduler, handle, new(0L, PhysicsChainQualityFeedbackBackend.Cpu, 8L, 1.0, 0.0f))
                .ShouldBe(PhysicsChainQualityFeedbackRejectionReason.OutOfOrder);

            for (int i = 0; i < 9; ++i)
                world.Invoke(ETickGroup.Late);
            Submit(scheduler, handle, new(1L, PhysicsChainQualityFeedbackBackend.Cpu, 8L, 1.0, 0.0f))
                .ShouldBe(PhysicsChainQualityFeedbackRejectionReason.Expired);

            Unregister(world, node);
            Submit(scheduler, handle, new(9L, PhysicsChainQualityFeedbackBackend.Cpu, 8L, 1.0, 0.0f))
                .ShouldBe(PhysicsChainQualityFeedbackRejectionReason.StaleGeneration);
        }
        finally
        {
            SetWorld(node, null);
            world.Invoke(ETickGroup.Normal);
        }
    }

    [Test]
    public void FixedTierRejectsFeedbackAndRemainsStrictUnderZeroAutomaticBudget()
    {
        var world = new TestWorldContext();
        (SceneNode node, PhysicsChainComponent chain) = CreateChain("Strict", PhysicsChainQualityTier.Strict);

        try
        {
            PhysicsChainWorld scheduler = Register(world, node);
            scheduler.AutomaticCpuWorkUnitBudget = 0L;
            world.Invoke(ETickGroup.Late);

            Submit(scheduler, chain.RuntimeHandle, new(0L, PhysicsChainQualityFeedbackBackend.Cpu, 8L, 10.0, 10.0f))
                .ShouldBe(PhysicsChainQualityFeedbackRejectionReason.NotAutomatic);
            world.Invoke(ETickGroup.Late);

            chain.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Strict);
            scheduler.QualityBudgetDiagnostics.AutomaticChainCount.ShouldBe(0);
            scheduler.QualityBudgetDiagnostics.CpuEffectiveWorkUnits.ShouldBe(0L);
        }
        finally
        {
            Unregister(world, node);
        }
    }

    [Test]
    public void ErrorSmoothingUsesDeterministicPromotionRecoveryHysteresis()
    {
        var world = new TestWorldContext();
        (SceneNode node, PhysicsChainComponent chain) = CreateChain("Distant", PhysicsChainQualityTier.Automatic);
        chain.AutomaticQualityRelevance = PhysicsChainAutomaticRelevance.Distant;

        try
        {
            PhysicsChainWorld scheduler = Register(world, node);
            scheduler.MaximumAutomaticQualityTransitionsPerFrame = 1;
            scheduler.MinimumAutomaticTierResidenceFrames = 0;
            scheduler.QualityFeedbackSmoothingPermille = 500;
            world.Invoke(ETickGroup.Late);
            chain.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Hz30);

            Submit(scheduler, chain.RuntimeHandle, new(0L, PhysicsChainQualityFeedbackBackend.Cpu, 4L, 1.0, 1.2f))
                .ShouldBe(PhysicsChainQualityFeedbackRejectionReason.None);
            world.Invoke(ETickGroup.Late);
            chain.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Strict);

            Submit(scheduler, chain.RuntimeHandle, new(1L, PhysicsChainQualityFeedbackBackend.Cpu, 8L, 1.0, 0.6f))
                .ShouldBe(PhysicsChainQualityFeedbackRejectionReason.None);
            world.Invoke(ETickGroup.Late);
            chain.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Strict);

            Submit(scheduler, chain.RuntimeHandle, new(2L, PhysicsChainQualityFeedbackBackend.Cpu, 8L, 1.0, 0.6f))
                .ShouldBe(PhysicsChainQualityFeedbackRejectionReason.None);
            world.Invoke(ETickGroup.Late);
            chain.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Hz30);

            scheduler.TryGetQualityDiagnostics(chain.RuntimeHandle, out PhysicsChainQualityDiagnostics diagnostics)
                .ShouldBeTrue();
            diagnostics.HasDelayedFeedback.ShouldBeTrue();
            diagnostics.LastFeedbackSourceFrame.ShouldBe(2L);
            diagnostics.SmoothedNormalizedError.ShouldBe(0.75f, 0.0001f);
        }
        finally
        {
            Unregister(world, node);
        }
    }

    [Test]
    public void AcceptedSamplePathAllocatesNoManagedMemory()
    {
        var world = new TestWorldContext();
        (SceneNode node, PhysicsChainComponent chain) = CreateChain("Automatic", PhysicsChainQualityTier.Automatic);

        try
        {
            PhysicsChainWorld scheduler = Register(world, node);
            world.Invoke(ETickGroup.Late);
            var sample = new PhysicsChainQualityFeedbackSample(
                0L,
                PhysicsChainQualityFeedbackBackend.Cpu,
                8L,
                1.0,
                0.25f);

            long before = GC.GetAllocatedBytesForCurrentThread();
            bool accepted = scheduler.TrySubmitDelayedQualityFeedback(chain.RuntimeHandle, sample, out _);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            accepted.ShouldBeTrue();
            allocated.ShouldBe(0L);
        }
        finally
        {
            Unregister(world, node);
        }
    }

    private static PhysicsChainQualityFeedbackRejectionReason Submit(
        PhysicsChainWorld scheduler,
        PhysicsChainRuntimeHandle handle,
        in PhysicsChainQualityFeedbackSample sample)
    {
        scheduler.TrySubmitDelayedQualityFeedback(handle, sample, out PhysicsChainQualityFeedbackRejectionReason rejection);
        return rejection;
    }

    private static (SceneNode Node, PhysicsChainComponent Chain) CreateChain(
        string name,
        PhysicsChainQualityTier qualityTier,
        bool useGpu = false)
    {
        var node = new SceneNode(name);
        PhysicsChainComponent chain = node.AddComponent<PhysicsChainComponent>()!;
        chain.QualityTier = qualityTier;
        chain.UseGPU = useGpu;
        return (node, chain);
    }

    private static PhysicsChainWorld Register(TestWorldContext world, params SceneNode[] nodes)
    {
        for (int i = 0; i < nodes.Length; ++i)
            SetWorld(nodes[i], world);
        world.Invoke(ETickGroup.Normal);

        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        return scheduler.ShouldNotBeNull();
    }

    private static void Unregister(TestWorldContext world, params SceneNode[] nodes)
    {
        for (int i = 0; i < nodes.Length; ++i)
            SetWorld(nodes[i], null);
        world.Invoke(ETickGroup.Normal);
    }

    private static void SetWorld(SceneNode node, IRuntimeWorldContext? world)
        => WorldProperty.SetValue(node, world);

    private sealed class TestWorldContext : IRuntimeWorldContext
    {
        private readonly List<(ETickGroup Group, WorldTick Tick)> _ticks = [];

        public bool IsPlaySessionActive => false;

        public void RegisterTick(ETickGroup group, int order, WorldTick tick)
            => _ticks.Add((group, tick));

        public void UnregisterTick(ETickGroup group, int order, WorldTick tick)
            => _ticks.Remove((group, tick));

        public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject)
        {
        }

        public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix)
        {
        }

        public void Invoke(ETickGroup group)
        {
            for (int i = 0; i < _ticks.Count; ++i)
                if (_ticks[i].Group == group)
                    _ticks[i].Tick();
        }
    }
}
