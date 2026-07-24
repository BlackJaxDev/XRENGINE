using NUnit.Framework;
using Shouldly;
using System.Reflection;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
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

        typeof(RuntimeWorldLifecycle).Assembly.GetType("XREngine.RuntimeWorld").ShouldBeNull();
        typeof(RuntimeWorldLifecycle).Assembly.GetType("XREngine.RuntimeWorldInstance").ShouldBeNull();
        typeof(RuntimeWorldLifecycle).Assembly.GetType("XREngine.RuntimeWorldObjectBase").ShouldBeNull();
        typeof(RuntimeWorldRenderState).Assembly
            .GetType("XREngine.Rendering.RuntimeRenderWorldInstance")
            .ShouldBeNull();

        const BindingFlags instanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(XRWorldInstance).GetField("_lifecycle", instanceFields)!.FieldType
            .ShouldBe(typeof(RuntimeWorldLifecycle));
        typeof(XRWorldInstance).GetField("_renderState", instanceFields)!.FieldType
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
    public void ProductionWorldInstance_LoadsAndDestroysRealSceneRoots()
    {
        SceneNode root = new("LifecycleRoot");
        XRWorld worldAsset = new("LifecycleWorld", new XRScene("Scene", root));
        XRWorldInstance instance = CreateWorldInstance();

        instance.TargetWorld = worldAsset;

        instance.RootNodes.Count.ShouldBe(1);
        instance.RootNodes[0].ShouldBeSameAs(root);
        root.World.ShouldBeSameAs(instance);

        root.Destroy(now: true);

        root.IsDestroyed.ShouldBeTrue();
        instance.RootNodes.Count.ShouldBe(0);
    }

    [Test]
    public void ProductionWorldInstance_RegistersAndRemovesRenderableThroughRenderingOwner()
    {
        XRWorldInstance instance = CreateWorldInstance();
        TestRenderable owner = new();
        RenderInfo3D renderInfo = RenderInfo3D.New(
            owner,
            new RenderCommandMethod3D(0, static () => { }));
        renderInfo.LocalCullingVolume = AABB.FromCenterSize(default, System.Numerics.Vector3.One);
        owner.RenderedObjects = [renderInfo];
        IRuntimeRenderInfo3DRegistrationTarget registration = instance;

        registration.AddRenderable3D(renderInfo);
        instance.VisualScene.GlobalCollectVisible();
        instance.VisualScene.ShouldContain(renderInfo);

        registration.RemoveRenderable3D(renderInfo);
        instance.VisualScene.GlobalCollectVisible();
        instance.VisualScene.ShouldNotContain(renderInfo);
    }

    private static XRWorldInstance CreateWorldInstance()
        => new(new VisualScene3D(), new JitterScene());

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
        return File.ReadAllText(path);
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
