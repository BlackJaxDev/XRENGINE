using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainWorldTests
{
    private static readonly PropertyInfo WorldProperty = typeof(RuntimeWorldObjectBase).GetProperty(
        nameof(RuntimeWorldObjectBase.World),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

    private static readonly FieldInfo CpuBackendField = typeof(PhysicsChainWorld).GetField(
        "_cpuBackend",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ExecutePreparedCpuBatchMethod = typeof(PhysicsChainWorld).GetMethod(
        "ExecutePreparedCpuBatch",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly BindingFlags PrivateInstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public void Registration_UsesOneWorldCallbackSetForMultipleComponents()
    {
        var world = new TestWorldContext();
        var firstNode = new SceneNode("FirstPhysicsChain");
        var secondNode = new SceneNode("SecondPhysicsChain");
        PhysicsChainComponent first = firstNode.AddComponent<PhysicsChainComponent>()!;
        PhysicsChainComponent second = secondNode.AddComponent<PhysicsChainComponent>()!;

        SetWorld(firstNode, world);
        SetWorld(secondNode, world);
        world.Invoke(ETickGroup.Normal);

        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        scheduler.ShouldNotBeNull();
        scheduler!.RegisteredCount.ShouldBe(2);
        scheduler.SlotCapacity.ShouldBe(2);
        scheduler.TryResolveRuntimeHandle(first.RuntimeHandle, out PhysicsChainComponent? resolved).ShouldBeTrue();
        resolved.ShouldBeSameAs(first);
        first.RuntimeHandle.IsValid.ShouldBeTrue();
        second.RuntimeHandle.IsValid.ShouldBeTrue();
        world.RegisteredTickCount.ShouldBe(3);

        SetWorld(firstNode, null);
        SetWorld(secondNode, null);
        world.Invoke(ETickGroup.Normal);
    }

    [Test]
    public void RuntimeHandle_RemovalAndSlotReuseChangeGeneration()
    {
        var world = new TestWorldContext();
        var firstNode = new SceneNode("FirstPhysicsChain");
        PhysicsChainComponent first = firstNode.AddComponent<PhysicsChainComponent>()!;

        SetWorld(firstNode, world);
        world.Invoke(ETickGroup.Normal);

        PhysicsChainRuntimeHandle staleHandle = first.RuntimeHandle;
        staleHandle.IsValid.ShouldBeTrue();

        SetWorld(firstNode, null);
        world.Invoke(ETickGroup.Normal);

        first.RuntimeHandle.ShouldBe(PhysicsChainRuntimeHandle.Invalid);

        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        scheduler.ShouldNotBeNull();
        scheduler!.TryResolveRuntimeHandle(staleHandle, out _).ShouldBeFalse();

        var secondNode = new SceneNode("SecondPhysicsChain");
        PhysicsChainComponent second = secondNode.AddComponent<PhysicsChainComponent>()!;
        SetWorld(secondNode, world);
        world.Invoke(ETickGroup.Normal);

        PhysicsChainRuntimeHandle currentHandle = second.RuntimeHandle;
        currentHandle.Slot.ShouldBe(staleHandle.Slot);
        currentHandle.Generation.ShouldNotBe(staleHandle.Generation);
        scheduler.RegisteredCount.ShouldBe(1);
        scheduler.SlotCapacity.ShouldBe(1);
        scheduler.TryResolveRuntimeHandle(staleHandle, out _).ShouldBeFalse();
        scheduler.TryResolveRuntimeHandle(currentHandle, out PhysicsChainComponent? resolved).ShouldBeTrue();
        resolved.ShouldBeSameAs(second);

        SetWorld(secondNode, null);
        world.Invoke(ETickGroup.Normal);
    }

    [Test]
    public void PreparedCpuBatch_PublishesEveryWorldBackendOutput()
    {
        const int componentCount = 64;
        var world = new TestWorldContext();
        var nodes = new SceneNode[componentCount];
        var components = new List<PhysicsChainComponent>(componentCount);
        for (int componentIndex = 0; componentIndex < componentCount; ++componentIndex)
        {
            SceneNode node = new($"CpuBatch{componentIndex}");
            PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;
            component.EnableAutomaticSleep = false;
            component.CpuTransformMirrorEnabled = false;
            nodes[componentIndex] = node;
            components.Add(component);
            SetWorld(node, world);
        }
        world.Invoke(ETickGroup.Normal);

        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        scheduler.ShouldNotBeNull();
        var backend = (PhysicsChainCpuBackend)CpuBackendField.GetValue(scheduler)!;
        PhysicsChainTemplate template = CreateLinearTemplate();
        for (int componentIndex = 0; componentIndex < componentCount; ++componentIndex)
        {
            PhysicsChainArenaHandle handle = backend.Register(
                template,
                CreateInput(),
                [new PhysicsChainCpuTreeInput(Vector3.Zero)],
                [
                    new PhysicsChainCpuParticleInput(Matrix4x4.Identity),
                    new PhysicsChainCpuParticleInput(Matrix4x4.CreateTranslation(Vector3.UnitX)),
                ],
                consumerFlags: PhysicsChainCpuConsumerFlags.Palette | PhysicsChainCpuConsumerFlags.Bounds);
            SetPrivateField(components[componentIndex], "_cpuBackendHandle", handle);
            SetPrivateField(components[componentIndex], "_cpuStepCount", 1);
            SetPrivateField(components[componentIndex], "_usePreparedCpuBackend", true);
        }

        ExecutePreparedCpuBatchMethod.Invoke(scheduler, [components]);
        for (int componentIndex = 0; componentIndex < componentCount; ++componentIndex)
        {
            PhysicsChainComponent component = components[componentIndex];
            component.SolveWorldLateTick();
            GetPrivateField<bool>(component, "_lastSimulationProducedResults").ShouldBeTrue();
            component.PublishWorldLateTick();

            component.TryGetCpuRenderOutput(out PhysicsChainCpuRenderOutput output).ShouldBeTrue();
            output.SimulationFrame.ShouldBe(1L);
            output.HasPalette.ShouldBeTrue();
            output.HasBounds.ShouldBeTrue();
            output.HasValidHistory.ShouldBeTrue();
        }

        for (int componentIndex = 0; componentIndex < componentCount; ++componentIndex)
            SetWorld(nodes[componentIndex], null);
        world.Invoke(ETickGroup.Normal);
    }

    [Test]
    public void ParallelInputGather_UsesReusableWeightedRangesAndDeterministicCompaction()
    {
        string source = ReadWorkspaceFile("XRENGINE/Scene/Components/Physics/PhysicsChainWorld.cs");
        source.ShouldContain("private PhysicsChainComponent[] _prepareComponents = [];");
        source.ShouldContain("private void EnsurePrepareCapacity(int requiredCount)");
        source.ShouldContain("private void BuildWeightedPrepareRanges(int count, int sliceCount)");
        source.ShouldContain("workItem.ConfigurePrepare(");
        source.ShouldContain("ThreadPool.UnsafeQueueUserWorkItem(static state => state.Run()");
        source.ShouldContain("component.FinalizeWorldLateTickParallelPreparation();");
        source.ShouldContain("for (int prepareIndex = 0; prepareIndex < prepareCount; ++prepareIndex)");
        source.ShouldNotContain("Task.Run(");

        string componentSource = ReadWorkspaceFile("XRENGINE/Scene/Components/Physics/PhysicsChainComponent.cs");
        componentSource.ShouldContain("internal bool CanPrepareWorldLateTickInputsInParallel");
        componentSource.ShouldContain("internal bool BeginWorldLateTickParallelPreparation()");
        componentSource.ShouldContain("internal void FinalizeWorldLateTickParallelPreparation()");
    }

    private static PhysicsChainCpuInput CreateInput()
        => new(1.0f / 60.0f, 1.0f, 1.0f, 1.0f, Vector3.Zero, Vector3.Zero, Vector3.Zero, 0u);

    private static PhysicsChainTemplate CreateLinearTemplate()
        => new(
            [new PhysicsChainTemplateTree(0, 2, 1, 1.0f)],
            [
                new PhysicsChainTemplateParticle(-1, 0, 0, 1, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, Vector3.Zero, Quaternion.Identity),
                new PhysicsChainTemplateParticle(0, 1, 1, 0, 1.0f, 1.0f, 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, Vector3.UnitX, Quaternion.Identity),
            ],
            [0, 1],
            [new PhysicsChainDepthRange(0, 0, 0, 1), new PhysicsChainDepthRange(0, 1, 1, 1)],
            freezeAxis: 0);

    private static void SetPrivateField<T>(object target, string fieldName, T value)
        => target.GetType().GetField(fieldName, PrivateInstanceFlags)!.SetValue(target, value);

    private static T GetPrivateField<T>(object target, string fieldName)
        => (T)target.GetType().GetField(fieldName, PrivateInstanceFlags)!.GetValue(target)!;

    private static string ReadWorkspaceFile(string relativePath)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.WorkDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate workspace file '{relativePath}'.");
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
            for (int i = 0; i < _ticks.Count; ++i)
                if (_ticks[i].Group == group)
                    _ticks[i].Tick();
        }
    }
}
