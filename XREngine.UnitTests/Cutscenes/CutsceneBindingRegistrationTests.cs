using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Cutscenes;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Physics.Jitter2;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
[NonParallelizable]
public sealed class CutsceneBindingRegistrationTests
{
    private Func<CutsceneDirectorComponent, ICutsceneBindingResolver?>? _previous;

    [SetUp]
    public void SetUp()
    {
        _previous = CutsceneDirectorComponent.DefaultBindingResolverFactory;
        CutsceneDirectorComponent.DefaultBindingResolverFactory = null;
    }

    [TearDown]
    public void TearDown()
        => CutsceneDirectorComponent.DefaultBindingResolverFactory = _previous;

    [Test]
    public void Install_WithNoFactory_SetsTheFactory_AndDisposeRemovesIt()
    {
        static ICutsceneBindingResolver? Factory(CutsceneDirectorComponent _) => null;
        Func<CutsceneDirectorComponent, ICutsceneBindingResolver?> factory = Factory;

        IDisposable lease = CutsceneBindingRegistration.InstallDefaultResolverFactory(factory);
        CutsceneDirectorComponent.DefaultBindingResolverFactory.ShouldBeSameAs(factory);

        lease.Dispose();
        CutsceneDirectorComponent.DefaultBindingResolverFactory.ShouldBeNull();
    }

    [Test]
    public void Install_KeepsAFactoryThatIsAlreadySet()
    {
        static ICutsceneBindingResolver? Existing(CutsceneDirectorComponent _) => null;
        Func<CutsceneDirectorComponent, ICutsceneBindingResolver?> existing = Existing;
        CutsceneDirectorComponent.DefaultBindingResolverFactory = existing;

        IDisposable lease = CutsceneBindingRegistration.InstallSceneResolverFactory(Engine.Assets);
        CutsceneDirectorComponent.DefaultBindingResolverFactory.ShouldBeSameAs(existing);

        lease.Dispose();
        CutsceneDirectorComponent.DefaultBindingResolverFactory.ShouldBeSameAs(existing);
    }

    [Test]
    public void Dispose_KeepsAFactoryThatWasSetLater()
    {
        IDisposable lease = CutsceneBindingRegistration.InstallSceneResolverFactory(Engine.Assets);
        static ICutsceneBindingResolver? Later(CutsceneDirectorComponent _) => null;
        Func<CutsceneDirectorComponent, ICutsceneBindingResolver?> later = Later;
        CutsceneDirectorComponent.DefaultBindingResolverFactory = later;

        lease.Dispose();
        CutsceneDirectorComponent.DefaultBindingResolverFactory.ShouldBeSameAs(later);
    }

    [Test]
    public void CreateSceneResolver_ForADirectorWithNoWorld_ReturnsNull()
    {
        SceneNode owner = new("Director", new Transform());
        CutsceneDirectorComponent director = owner.AddComponent<CutsceneDirectorComponent>()!;

        CutsceneBindingRegistration.CreateSceneResolver(Engine.Assets, director).ShouldBeNull();
    }

    [Test]
    public void DirectorOpen_InAWorld_UsesTheSceneResolver_WithNoGameCode()
    {
        SceneNode hero = new("Hero", new Transform());
        SceneNode owner = new("Director", new Transform());
        XRWorld world = new("Cutscene Binding World", new XRScene("Cutscene Binding Scene"));
        using RuntimeWorld runtimeWorld = new(new JitterScene(), world);
        runtimeWorld.RootNodes.Add(hero);
        runtimeWorld.RootNodes.Add(owner);

        CutsceneAsset asset = new() { Duration = CutsceneTime.FromSeconds(1) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = hero.ID });
        CutsceneDirectorComponent director = owner.AddComponent<CutsceneDirectorComponent>()!;
        director.Asset = asset;
        director.World.ShouldBeSameAs(runtimeWorld);

        using IDisposable lease = CutsceneBindingRegistration.InstallSceneResolverFactory(Engine.Assets);
        ICutsceneBindingResolver? resolver = CutsceneBindingRegistration.CreateSceneResolver(Engine.Assets, director);
        resolver.ShouldNotBeNull();
        resolver.FindNodeById(hero.ID).ShouldBeSameAs(hero);

        director.OpenAsync().IsCompletedSuccessfully.ShouldBeTrue();
        director.IsOpen.ShouldBeTrue();
        director.Close();
    }
}
