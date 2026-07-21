using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainWorldLifecycleTests
{
    private static readonly PropertyInfo WorldProperty = typeof(RuntimeWorldObjectBase).GetProperty(
        nameof(RuntimeWorldObjectBase.World),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
    private static readonly MethodInfo DrainDynamicCommandsMethod = typeof(PhysicsChainWorld).GetMethod(
        "DrainDynamicCommands",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Test]
    public void StructuralCommands_ApplyLatestIntentAtBoundaryAndPreserveState()
    {
        var world = new TestWorldContext();
        PhysicsChainComponent component = CreateComponent(world);
        world.Invoke(ETickGroup.Normal);
        PhysicsChainWorld scheduler = GetScheduler(world);
        PhysicsChainRuntimeHandle handle = component.RuntimeHandle;
        scheduler.TryGetRegistration(handle, out PhysicsChainInstance original, out PhysicsChainOutput originalOutput).ShouldBeTrue();
        PhysicsChainWorldLifecycleSnapshot before = scheduler.GetLifecycleSnapshot();

        PhysicsChainWorld.RequestStructural(component, PhysicsChainWorldCommandKind.Retemplate);
        PhysicsChainWorld.RequestStructural(component, PhysicsChainWorldCommandKind.Resize);
        PhysicsChainWorld.RequestStructural(component, PhysicsChainWorldCommandKind.Rebind);
        PhysicsChainWorld.RequestStructural(component, PhysicsChainWorldCommandKind.BackendSwitch);

        scheduler.GetLifecycleSnapshot().AppliedStructuralCommands.ShouldBe(before.AppliedStructuralCommands);
        scheduler.TryGetRegistration(handle, out _, out PhysicsChainOutput beforeBoundaryOutput).ShouldBeTrue();
        beforeBoundaryOutput.OutputGeneration.ShouldBe(originalOutput.OutputGeneration);

        world.Invoke(ETickGroup.Normal);

        PhysicsChainWorldLifecycleSnapshot after = scheduler.GetLifecycleSnapshot();
        after.AppliedStructuralCommands.ShouldBe(before.AppliedStructuralCommands + 1L);
        component.RuntimeHandle.ShouldBe(handle);
        scheduler.TryGetRegistration(handle, out PhysicsChainInstance current, out PhysicsChainOutput currentOutput).ShouldBeTrue();
        current.StateSlice.ShouldBe(original.StateSlice);
        currentOutput.OutputGeneration.ShouldBe(originalOutput.OutputGeneration + 1u);
        currentOutput.PreviousPalette.ShouldBe(currentOutput.CurrentPalette);

        RemoveComponent(component, world);
    }

    [Test]
    public void DynamicCommands_ApplyLatestIntentPerKindAtBoundary()
    {
        var world = new TestWorldContext();
        PhysicsChainComponent component = CreateComponent(world);
        world.Invoke(ETickGroup.Normal);
        PhysicsChainWorld scheduler = GetScheduler(world);
        PhysicsChainWorldLifecycleSnapshot before = scheduler.GetLifecycleSnapshot();

        foreach (PhysicsChainWorldDynamicCommandKind kind in Enum.GetValues<PhysicsChainWorldDynamicCommandKind>())
        {
            PhysicsChainWorld.NotifyDynamic(component, kind);
            PhysicsChainWorld.NotifyDynamic(component, kind);
        }

        scheduler.GetLifecycleSnapshot().AppliedDynamicCommands.ShouldBe(before.AppliedDynamicCommands);
        world.Invoke(ETickGroup.Normal);

        PhysicsChainWorldLifecycleSnapshot after = scheduler.GetLifecycleSnapshot();
        after.AppliedDynamicCommands.ShouldBe(
            before.AppliedDynamicCommands + Enum.GetValues<PhysicsChainWorldDynamicCommandKind>().Length);

        RemoveComponent(component, world);
    }

    [Test]
    public void Removal_InvalidatesHandleAndDefersArenaReclamationPastActiveFrame()
    {
        var world = new TestWorldContext();
        PhysicsChainComponent component = CreateComponent(world);
        world.Invoke(ETickGroup.Normal);
        PhysicsChainWorld scheduler = GetScheduler(world);
        PhysicsChainRuntimeHandle staleHandle = component.RuntimeHandle;

        SetWorld(component.SceneNode!, null);
        world.Invoke(ETickGroup.Normal);

        scheduler.TryGetRegistration(staleHandle, out _, out _).ShouldBeFalse();
        PhysicsChainWorldLifecycleSnapshot retired = scheduler.GetLifecycleSnapshot();
        retired.LiveInstances.ShouldBe(1);
        retired.LiveStates.ShouldBe(1);
        retired.LiveOutputs.ShouldBe(1);
        retired.DeferredRetirements.ShouldBe(1);

        world.Invoke(ETickGroup.Late);
        PhysicsChainWorldLifecycleSnapshot frameAdvanced = scheduler.GetLifecycleSnapshot();
        frameAdvanced.ActiveFrame.ShouldBe(retired.ActiveFrame + 1L);
        frameAdvanced.DeferredRetirements.ShouldBe(1);

        world.Invoke(ETickGroup.Normal);
        PhysicsChainWorldLifecycleSnapshot reclaimed = scheduler.GetLifecycleSnapshot();
        reclaimed.LiveInstances.ShouldBe(0);
        reclaimed.LiveStates.ShouldBe(0);
        reclaimed.LiveOutputs.ShouldBe(0);
        reclaimed.DeferredRetirements.ShouldBe(0);
    }

    [Test]
    public void WarmDynamicCommandDrain_DoesNotAllocate()
    {
        var world = new TestWorldContext();
        PhysicsChainComponent component = CreateComponent(world);
        world.Invoke(ETickGroup.Normal);
        PhysicsChainWorld scheduler = GetScheduler(world);
        var drain = (Action)DrainDynamicCommandsMethod.CreateDelegate(typeof(Action), scheduler);

        PhysicsChainWorld.NotifyDynamic(component, PhysicsChainWorldDynamicCommandKind.Root);
        drain();
        PhysicsChainWorld.NotifyDynamic(component, PhysicsChainWorldDynamicCommandKind.Force);
        PhysicsChainWorld.NotifyDynamic(component, PhysicsChainWorldDynamicCommandKind.Parameters);
        PhysicsChainWorld.NotifyDynamic(component, PhysicsChainWorldDynamicCommandKind.Relevance);
        PhysicsChainWorld.NotifyDynamic(component, PhysicsChainWorldDynamicCommandKind.Quality);

        long before = GC.GetAllocatedBytesForCurrentThread();
        drain();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0L);
        RemoveComponent(component, world);
    }

    [Test]
    public void TenThousandSleepingRegistrationsUseOneWorldTickTripletAndReclaimCleanly()
    {
        const int chainCount = 10_000;
        var world = new TestWorldContext();
        var nodes = new SceneNode[chainCount];
        for (int index = 0; index < chainCount; ++index)
        {
            var node = new SceneNode($"SleepingPhysicsChain{index}");
            PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;
            component.QualityTier = PhysicsChainQualityTier.Sleep;
            SetWorld(node, world);
            nodes[index] = node;
        }

        world.RegisteredTickCount.ShouldBe(3);
        world.Invoke(ETickGroup.Normal);
        PhysicsChainWorld scheduler = GetScheduler(world);
        scheduler.RegisteredCount.ShouldBe(chainCount);
        scheduler.SlotCapacity.ShouldBe(chainCount);

        for (int index = 0; index < nodes.Length; ++index)
            SetWorld(nodes[index], null);
        world.Invoke(ETickGroup.Normal);
        scheduler.RegisteredCount.ShouldBe(0);
        scheduler.SlotCapacity.ShouldBe(chainCount);

        world.Invoke(ETickGroup.Late);
        world.Invoke(ETickGroup.Normal);
        PhysicsChainWorldLifecycleSnapshot reclaimed = scheduler.GetLifecycleSnapshot();
        reclaimed.LiveInstances.ShouldBe(0);
        reclaimed.LiveStates.ShouldBe(0);
        reclaimed.LiveOutputs.ShouldBe(0);
        reclaimed.DeferredRetirements.ShouldBe(0);
        world.RegisteredTickCount.ShouldBe(3);
    }

    private static PhysicsChainComponent CreateComponent(IRuntimeWorldContext world)
    {
        var node = new SceneNode("PhysicsChainLifecycle");
        PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;
        component.EndLength = 0.25f;
        SetWorld(node, world);
        return component;
    }

    private static PhysicsChainWorld GetScheduler(IRuntimeWorldContext world)
    {
        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        scheduler.ShouldNotBeNull();
        return scheduler!;
    }

    private static void RemoveComponent(PhysicsChainComponent component, TestWorldContext world)
    {
        SetWorld(component.SceneNode!, null);
        world.Invoke(ETickGroup.Normal);
    }

    private static void SetWorld(SceneNode node, IRuntimeWorldContext? world)
        => WorldProperty.SetValue(node, world);

    private sealed class TestWorldContext : IRuntimeWorldContext
    {
        private readonly List<(ETickGroup Group, WorldTick Tick)> _ticks = [];

        public bool IsPlaySessionActive => false;
        public int RegisteredTickCount => _ticks.Count;

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
            for (int index = 0; index < _ticks.Count; ++index)
                if (_ticks[index].Group == group)
                    _ticks[index].Tick();
        }
    }
}
