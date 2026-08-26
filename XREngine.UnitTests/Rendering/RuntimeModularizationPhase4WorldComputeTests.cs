using NUnit.Framework;
using Shouldly;
using System.Reflection;
using XREngine.Components.Lights;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Scene.Physics.Jitter2;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RuntimeModularizationPhase4WorldComputeTests
{
    private IRuntimeShaderServices? _previousShaderServices;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
        => RuntimeShaderServices.Current = _previousShaderServices;

    [Test]
    public void PhysicsCompute_IsOwnedByRuntimeRendering_ThroughNeutralSources()
    {
        string root = ResolveWorkspaceRoot();
        string legacyCompute = Path.Combine(root, "XRENGINE", "Rendering", "Compute");
        Directory.Exists(legacyCompute).ShouldBeFalse();

        string dispatcher = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs");
        string contracts = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/PhysicsCompute/IPhysicsChainComputeSource.cs");
        string readbackContract = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/PhysicsCompute/IPhysicsChainReadbackCoordinator.cs");
        string softbodyContract = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/PhysicsCompute/IGpuSoftbodyComputeSource.cs");

        dispatcher.ShouldContain("ConcurrentDictionary<IPhysicsChainComputeSource, GPUPhysicsChainRequest>");
        dispatcher.ShouldNotContain("ConcurrentDictionary<PhysicsChainComponent, GPUPhysicsChainRequest>");
        dispatcher.ShouldNotContain("PhysicsChainWorld.TryGet");
        contracts.ShouldContain("public interface IPhysicsChainComputeSource");
        readbackContract.ShouldContain("public interface IPhysicsChainReadbackCoordinator");
        softbodyContract.ShouldContain("public interface IGpuSoftbodyComputeSource");
    }

    [Test]
    public void ProductionWorldIdentities_AreOwnedByCore_WithoutParallelWorldTypes()
    {
        typeof(XRWorld).Assembly.ShouldBe(typeof(RuntimeWorldLifecycle).Assembly);
        typeof(XRScene).Assembly.ShouldBe(typeof(RuntimeWorldLifecycle).Assembly);
        typeof(WorldSettings).Assembly.ShouldBe(typeof(RuntimeWorldLifecycle).Assembly);
        typeof(RootNodeCollection).Assembly.ShouldBe(typeof(RuntimeWorldLifecycle).Assembly);
        typeof(XRWorldObjectBase).Assembly.ShouldBe(typeof(RuntimeWorldLifecycle).Assembly);

        typeof(RuntimeWorldLifecycle).Assembly.GetType("XREngine.XRWorldInstance").ShouldBeNull();
        typeof(RuntimeWorldLifecycle).Assembly.GetType("XREngine.RuntimeWorldInstance").ShouldBeNull();
        typeof(RuntimeWorldLifecycle).Assembly.GetType("XREngine.RuntimeWorldObjectBase").ShouldBeNull();
        typeof(RuntimeWorldRenderState).Assembly
            .GetType("XREngine.Rendering.RuntimeRenderWorldInstance")
            .ShouldBeNull();
        typeof(RuntimeWorldRenderState).Assembly
            .GetType("XREngine.XRWorldInstance")
            .ShouldBeNull();

        const BindingFlags instanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(RuntimeWorld).GetField("_lifecycle", instanceFields)!.FieldType
            .ShouldBe(typeof(RuntimeWorldLifecycle));
        typeof(RuntimeWorldRenderer).GetField("_state", instanceFields)!.FieldType
            .ShouldBe(typeof(RuntimeWorldRenderState));

        File.Exists(Path.Combine(
            ResolveWorkspaceRoot(),
            "XREngine.Runtime.Core",
            "Scene",
            "Components",
            "Physics",
            "Readback",
            "PhysicsChainReadbackGatherPlan.cs")).ShouldBeTrue();
    }

    [Test]
    public void ProductionRuntimeWorld_LoadsAndDestroysRealSceneRoots()
    {
        SceneNode root = new("LifecycleRoot");
        XRWorld worldAsset = new("LifecycleWorld", new XRScene("Scene", root));
        using RuntimeWorld instance = new(new JitterScene(), worldAsset);

        instance.RootNodes.Count.ShouldBe(1);
        instance.RootNodes[0].ShouldBeSameAs(root);
        root.World.ShouldBeSameAs(instance);

        root.Destroy(now: true);

        root.IsDestroyed.ShouldBeTrue();
        instance.RootNodes.Count.ShouldBe(0);
    }

    [Test]
    public void ProductionRuntimeWorld_RegistersAndRemovesRenderableThroughRenderingOwner()
    {
        using RuntimeWorld instance = new(new JitterScene());
        using RuntimeWorldRenderer renderer = new(instance, new VisualScene3D());
        TestRenderable owner = new();
        RenderInfo3D renderInfo = RenderInfo3D.New(
            owner,
            new RenderCommandMethod3D(0, static () => { }));
        renderInfo.LocalCullingVolume = AABB.FromCenterSize(default, System.Numerics.Vector3.One);
        owner.RenderedObjects = [renderInfo];
        IRuntimeRenderInfo3DRegistrationTarget registration = renderer;

        registration.AddRenderable3D(renderInfo);
        renderer.VisualScene.GlobalCollectVisible();
        renderer.VisualScene.ShouldContain(renderInfo);

        registration.RemoveRenderable3D(renderInfo);
        renderer.VisualScene.GlobalCollectVisible();
        renderer.VisualScene.ShouldNotContain(renderInfo);
    }

    [Test]
    public void BootstrapHost_AttachesRenderingBeforeInitialSceneActivation_AndDetachesItOnDispose()
    {
        SceneNode root = new("ComposedRoot");
        DirectionalLightComponent light = root.AddComponent<DirectionalLightComponent>()!;
        XRWorld worldAsset = new("ComposedWorld", new XRScene("Scene", root));
        RuntimeWorldHost host = new(new JitterScene(), new VisualScene3D());

        host.Initialize(worldAsset);

        RuntimeWorld coreWorld = host.CoreWorld;
        root.World.ShouldBeSameAs(coreWorld);
        host.RenderWorld.Lights.DynamicDirectionalLights.ShouldContain(light);
        coreWorld.TryGetCapability<IRuntimeRenderWorld>(out IRuntimeRenderWorld? capability).ShouldBeTrue();
        capability.ShouldBeSameAs(host.RenderWorld);
        RuntimeRenderWorldRegistry.TryGet(coreWorld, out IRuntimeRenderWorld? registered).ShouldBeTrue();
        registered.ShouldBeSameAs(host.RenderWorld);

        host.Dispose();

        root.World.ShouldBeNull();
        coreWorld.TryGetCapability<IRuntimeRenderWorld>(out _).ShouldBeFalse();
        RuntimeRenderWorldRegistry.TryGet(coreWorld, out _).ShouldBeFalse();
    }

    [Test]
    public void RuntimeWorldRegistry_RetargetsOneIdentityAmongMultipleWorlds_AndResetsDeterministically()
    {
        SceneNode firstRoot = new("FirstRoot");
        SceneNode secondRoot = new("SecondRoot");
        XRWorld firstAsset = new("First", new XRScene("FirstScene", firstRoot));
        XRWorld secondAsset = new("Second", new XRScene("SecondScene", secondRoot));
        XRWorld otherAsset = new("Other", new XRScene("OtherScene", new SceneNode("OtherRoot")));
        RuntimeWorldRegistry registry = new();
        RuntimeWorld retargeted = new(new JitterScene(), firstAsset);
        RuntimeWorld other = new(new JitterScene(), otherAsset);
        int disposalCount = 0;
        retargeted.Disposing += _ => disposalCount++;
        other.Disposing += _ => disposalCount++;
        registry.Register(firstAsset, retargeted);
        registry.Register(otherAsset, other);

        retargeted.RetargetWorld(
            secondAsset,
            afterTargetAssigned: () => registry.Retarget(firstAsset, secondAsset, retargeted));

        registry.TryGet(firstAsset, out _).ShouldBeFalse();
        registry.TryGet(secondAsset, out RuntimeWorld? resolved).ShouldBeTrue();
        resolved.ShouldBeSameAs(retargeted);
        registry.Snapshot().Count.ShouldBe(2);
        firstRoot.World.ShouldBeNull();
        secondRoot.World.ShouldBeSameAs(retargeted);

        registry.ResetForTests();

        registry.Snapshot().ShouldBeEmpty();
        disposalCount.ShouldBe(2);
        secondRoot.World.ShouldBeNull();
    }

    private sealed class TestRenderable : IRenderable
    {
        public RenderInfo[] RenderedObjects { get; set; } = [];
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string path = Path.Combine(
            ResolveWorkspaceRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file to exist: {relativePath}");
        return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find workspace root from '{AppContext.BaseDirectory}'.");
    }
}
