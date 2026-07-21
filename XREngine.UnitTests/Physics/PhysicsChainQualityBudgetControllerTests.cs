using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainQualityBudgetControllerTests
{
    private static readonly PropertyInfo WorldProperty = typeof(RuntimeWorldObjectBase).GetProperty(
        nameof(RuntimeWorldObjectBase.World),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

    [Test]
    public void CpuPressure_DemotesOnlyAutomaticChains()
    {
        var world = new TestWorldContext();
        (SceneNode fixedNode, PhysicsChainComponent fixedChain) = CreateChain("Fixed", PhysicsChainQualityTier.Strict);
        (SceneNode automaticNode, PhysicsChainComponent automaticChain) = CreateChain("Automatic", PhysicsChainQualityTier.Automatic);

        try
        {
            PhysicsChainWorld scheduler = Register(world, fixedNode, automaticNode);
            scheduler.AutomaticCpuWorkUnitBudget = 4L;
            scheduler.MaximumAutomaticQualityTransitionsPerFrame = 1;
            scheduler.MinimumAutomaticTierResidenceFrames = 0;

            world.Invoke(ETickGroup.Late);

            fixedChain.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Strict);
            automaticChain.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Hz30);
            scheduler.TryGetQualityDiagnostics(automaticChain.RuntimeHandle, out PhysicsChainQualityDiagnostics diagnostics).ShouldBeTrue();
            diagnostics.RequestedTier.ShouldBe(PhysicsChainQualityTier.Strict);
            diagnostics.EffectiveTier.ShouldBe(PhysicsChainQualityTier.Hz30);
            diagnostics.Reason.ShouldBe(PhysicsChainQualityDecisionReason.CpuBudgetPressure);
            scheduler.QualityBudgetDiagnostics.Transitions.ShouldBe(1);
            scheduler.QualityBudgetDiagnostics.CpuEffectiveWorkUnits.ShouldBe(4L);
        }
        finally
        {
            Unregister(world, fixedNode, automaticNode);
        }
    }

    [Test]
    public void CpuAndGpuBudgets_AreIndependent()
    {
        var world = new TestWorldContext();
        (SceneNode cpuNode, PhysicsChainComponent cpuChain) = CreateChain("CPU", PhysicsChainQualityTier.Automatic);
        (SceneNode gpuNode, PhysicsChainComponent gpuChain) = CreateChain("GPU", PhysicsChainQualityTier.Automatic);
        gpuChain.UseGPU = true;

        try
        {
            PhysicsChainWorld scheduler = Register(world, cpuNode, gpuNode);
            scheduler.AutomaticCpuWorkUnitBudget = 8L;
            scheduler.AutomaticGpuWorkUnitBudget = 4L;
            scheduler.MaximumAutomaticQualityTransitionsPerFrame = 2;
            scheduler.MinimumAutomaticTierResidenceFrames = 0;

            world.Invoke(ETickGroup.Late);

            cpuChain.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Strict);
            gpuChain.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Hz30);
            scheduler.TryGetQualityDiagnostics(gpuChain.RuntimeHandle, out PhysicsChainQualityDiagnostics diagnostics).ShouldBeTrue();
            diagnostics.Reason.ShouldBe(PhysicsChainQualityDecisionReason.GpuBudgetPressure);
            scheduler.QualityBudgetDiagnostics.CpuEffectiveWorkUnits.ShouldBe(8L);
            scheduler.QualityBudgetDiagnostics.GpuEffectiveWorkUnits.ShouldBe(4L);
        }
        finally
        {
            Unregister(world, cpuNode, gpuNode);
        }
    }

    [Test]
    public void TransitionCap_SleepsIrrelevantBeforeReducingDistantCadence()
    {
        var world = new TestWorldContext();
        (SceneNode distantNode, PhysicsChainComponent distant) = CreateChain("Distant", PhysicsChainQualityTier.Automatic);
        (SceneNode irrelevantNode, PhysicsChainComponent irrelevant) = CreateChain("Irrelevant", PhysicsChainQualityTier.Automatic);
        distant.AutomaticQualityRelevance = PhysicsChainAutomaticRelevance.Distant;
        irrelevant.AutomaticQualityRelevance = PhysicsChainAutomaticRelevance.Irrelevant;

        try
        {
            PhysicsChainWorld scheduler = Register(world, distantNode, irrelevantNode);
            scheduler.AutomaticCpuWorkUnitBudget = 100L;
            scheduler.MaximumAutomaticQualityTransitionsPerFrame = 1;
            scheduler.MinimumAutomaticTierResidenceFrames = 0;

            world.Invoke(ETickGroup.Late);

            irrelevant.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Sleep);
            distant.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Strict);
            scheduler.TryGetQualityDiagnostics(distant.RuntimeHandle, out PhysicsChainQualityDiagnostics diagnostics).ShouldBeTrue();
            diagnostics.RequestedTier.ShouldBe(PhysicsChainQualityTier.Hz30);
            diagnostics.Reason.ShouldBe(PhysicsChainQualityDecisionReason.TransitionLimit);
            scheduler.QualityBudgetDiagnostics.DeferredByTransitionLimit.ShouldBe(1);
        }
        finally
        {
            Unregister(world, distantNode, irrelevantNode);
        }
    }

    [Test]
    public void DemotionPriority_IsDeterministicByImportance()
    {
        var world = new TestWorldContext();
        (SceneNode importantNode, PhysicsChainComponent important) = CreateChain("Important", PhysicsChainQualityTier.Automatic);
        (SceneNode expendableNode, PhysicsChainComponent expendable) = CreateChain("Expendable", PhysicsChainQualityTier.Automatic);
        important.AutomaticQualityImportance = 90;
        expendable.AutomaticQualityImportance = 10;

        try
        {
            PhysicsChainWorld scheduler = Register(world, importantNode, expendableNode);
            scheduler.AutomaticCpuWorkUnitBudget = 12L;
            scheduler.MaximumAutomaticQualityTransitionsPerFrame = 1;
            scheduler.MinimumAutomaticTierResidenceFrames = 0;

            world.Invoke(ETickGroup.Late);

            expendable.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Hz30);
            important.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Strict);
            scheduler.QualityBudgetDiagnostics.CpuEffectiveWorkUnits.ShouldBe(12L);
        }
        finally
        {
            Unregister(world, importantNode, expendableNode);
        }
    }

    [Test]
    public void Promotion_RequiresMinimumResidenceAndHysteresisHeadroom()
    {
        var world = new TestWorldContext();
        (SceneNode node, PhysicsChainComponent chain) = CreateChain("Automatic", PhysicsChainQualityTier.Automatic);

        try
        {
            PhysicsChainWorld scheduler = Register(world, node);
            scheduler.AutomaticCpuWorkUnitBudget = 4L;
            scheduler.MaximumAutomaticQualityTransitionsPerFrame = 1;
            scheduler.MinimumAutomaticTierResidenceFrames = 2;
            scheduler.AutomaticQualityPromotionHysteresisPercent = 25;

            world.Invoke(ETickGroup.Late);
            chain.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Hz30);

            scheduler.AutomaticCpuWorkUnitBudget = 16L;
            world.Invoke(ETickGroup.Late);
            chain.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Hz30);
            scheduler.TryGetQualityDiagnostics(chain.RuntimeHandle, out PhysicsChainQualityDiagnostics resident).ShouldBeTrue();
            resident.Reason.ShouldBe(PhysicsChainQualityDecisionReason.MinimumResidency);
            resident.ResidenceFrames.ShouldBe(1L);

            world.Invoke(ETickGroup.Late);
            chain.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Strict);
            scheduler.TryGetQualityDiagnostics(chain.RuntimeHandle, out PhysicsChainQualityDiagnostics promoted).ShouldBeTrue();
            promoted.Reason.ShouldBe(PhysicsChainQualityDecisionReason.PromotionHeadroom);
            promoted.ResidenceFrames.ShouldBe(0L);
        }
        finally
        {
            Unregister(world, node);
        }
    }

    [Test]
    public void Promotion_RemainsDemotedWithoutConfiguredHeadroom()
    {
        var world = new TestWorldContext();
        (SceneNode node, PhysicsChainComponent chain) = CreateChain("Automatic", PhysicsChainQualityTier.Automatic);

        try
        {
            PhysicsChainWorld scheduler = Register(world, node);
            scheduler.AutomaticCpuWorkUnitBudget = 4L;
            scheduler.MaximumAutomaticQualityTransitionsPerFrame = 1;
            scheduler.MinimumAutomaticTierResidenceFrames = 0;
            scheduler.AutomaticQualityPromotionHysteresisPercent = 25;
            world.Invoke(ETickGroup.Late);

            scheduler.AutomaticCpuWorkUnitBudget = 9L;
            world.Invoke(ETickGroup.Late);

            chain.EffectiveQualityTier.ShouldBe(PhysicsChainQualityTier.Hz30);
            scheduler.TryGetQualityDiagnostics(chain.RuntimeHandle, out PhysicsChainQualityDiagnostics diagnostics).ShouldBeTrue();
            diagnostics.Reason.ShouldBe(PhysicsChainQualityDecisionReason.CpuBudgetPressure);
        }
        finally
        {
            Unregister(world, node);
        }
    }

    private static (SceneNode Node, PhysicsChainComponent Chain) CreateChain(
        string name,
        PhysicsChainQualityTier qualityTier)
    {
        var node = new SceneNode(name);
        PhysicsChainComponent chain = node.AddComponent<PhysicsChainComponent>()!;
        chain.QualityTier = qualityTier;
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
