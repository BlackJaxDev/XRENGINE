using NUnit.Framework;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public sealed class RuntimeHierarchyRootRoutesTests
{
    [Test]
    public void MovingARootToAnotherScenePreventsTheOldSceneFromRemovingIt()
    {
        SceneNode root = new("Root");
        XRScene first = new("First", root), second = new("Second");
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", first, second));
        RuntimeHierarchyRootRoutes before = world.CaptureHierarchyRootRoutes(root);
        RuntimeHierarchyRootRoutes after = world.PlanHierarchyRootRoutes(root, [second]);
        first.RootNodes.Remove(root);
        second.RootNodes.Add(root);

        world.RestoreHierarchyRootRoutes(before, after);
        first.IsVisible = false;
        world.UnloadScene(first);

        Assert.That(world.RootNodes, Does.Contain(root));
        Assert.That(world.HasHierarchyRootRoutes(after), Is.True);
        second.IsVisible = false;
        Assert.That(world.RootNodes, Does.Not.Contain(root));
        root.Destroy(true);
    }

    [Test]
    public void RestoringRoutesAfterCompensationRestoresTheOriginalVisibilityOwner()
    {
        SceneNode root = new("Root");
        XRScene first = new("First", root), second = new("Second");
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", first, second));
        RuntimeHierarchyRootRoutes before = world.CaptureHierarchyRootRoutes(root);
        RuntimeHierarchyRootRoutes after = world.PlanHierarchyRootRoutes(root, [second]);
        first.RootNodes.Remove(root);
        second.RootNodes.Add(root);
        world.RestoreHierarchyRootRoutes(before, after);
        second.RootNodes.Remove(root);
        first.RootNodes.Add(root);

        world.RestoreHierarchyRootRoutes(after, before);
        second.IsVisible = false;

        Assert.That(world.RootNodes, Does.Contain(root));
        first.IsVisible = false;
        Assert.That(world.RootNodes, Does.Not.Contain(root));
        root.Destroy(true);
    }

    [Test]
    public void HiddenSceneMembershipDoesNotCreateRuntimeRoutesOrActivateTheRoot()
    {
        SceneNode root = new("Hidden root");
        XRScene first = new("First", root) { IsVisible = false };
        XRScene second = new("Second", root) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", first, second));
        RuntimeHierarchyRootRoutes before = world.CaptureHierarchyRootRoutes(root);
        RuntimeHierarchyRootRoutes unchanged = world.PlanHierarchyRootRoutes(root, []);
        world.RestoreHierarchyRootRoutes(before, unchanged);

        Assert.That(root.World, Is.Null);
        Assert.That(world.RootNodes, Does.Not.Contain(root));
        Assert.That(unchanged.Scenes, Is.Empty);
        Assert.That(world.CaptureHierarchySceneMemberships(root), Is.EquivalentTo(new[] { first, second }));
        Assert.Throws<InvalidOperationException>(() => world.PlanHierarchyRootRoutes(root, [first]));
        first.RootNodes.Remove(root);
        second.IsVisible = true;
        Assert.That(world.CaptureHierarchyRootRoutes(root).Scenes, Is.EqualTo(new[] { second }));
        root.Destroy(true);
    }

    [Test]
    public void AStaleExpectedReceiptCannotReplaceRoutesInstalledByAnotherEdit()
    {
        SceneNode root = new("Root");
        XRScene first = new("First", root), second = new("Second");
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", first, second));
        RuntimeHierarchyRootRoutes before = world.CaptureHierarchyRootRoutes(root);
        RuntimeHierarchyRootRoutes moved = world.PlanHierarchyRootRoutes(root, [second]);
        RuntimeHierarchyRootRoutes detached = world.PlanHierarchyRootRoutes(root, []);
        first.RootNodes.Remove(root);
        second.RootNodes.Add(root);
        world.RestoreHierarchyRootRoutes(before, moved);

        Assert.Throws<InvalidOperationException>(() => world.RestoreHierarchyRootRoutes(before, detached));
        Assert.That(world.HasHierarchyRootRoutes(moved), Is.True);
        root.Destroy(true);
    }

    [Test]
    public void ReceiptRejectsAnotherWorldOrReplacementTransformWithoutChangingRoutes()
    {
        SceneNode root = new("Root");
        XRScene scene = new("Scene", root);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        using RuntimeWorld other = new(new HierarchyTestPhysicsScene());
        RuntimeHierarchyRootRoutes before = world.CaptureHierarchyRootRoutes(root);
        RuntimeHierarchyRootRoutes detached = world.PlanHierarchyRootRoutes(root, []);

        Assert.Throws<InvalidOperationException>(() => other.RestoreHierarchyRootRoutes(before, detached));
        root.SetTransform<Transform>();
        Assert.Throws<InvalidOperationException>(() => world.RestoreHierarchyRootRoutes(before, detached));
        Assert.That(world.CaptureHierarchyRootRoutes(root).Scenes, Is.EqualTo(new[] { scene }));
        root.Destroy(true);
    }

    [Test]
    public void RouteChangesPreserveExternalOwnershipAndDoNotInvokeLifecycleCallbacks()
    {
        SceneNode root = new("External root");
        XRScene scene = new("Scene", root);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene());
        world.RootNodes.Add(root);
        world.LoadScene(scene);
        RuntimeHierarchyRootRoutes before = world.CaptureHierarchyRootRoutes(root);
        Assert.That(before.ExternallyOwned, Is.True);
        RuntimeHierarchyRootRoutes detached = world.PlanHierarchyRootRoutes(root, []);
        int callbacks = 0;
        root.PropertyChanged += (_, _) => callbacks++;
        world.RootNodes.NodeCacheAction = _ => callbacks++;
        world.RootNodes.NodeUncacheAction = _ => callbacks++;

        world.RestoreHierarchyRootRoutes(before, detached);
        world.RestoreHierarchyRootRoutes(detached, before);

        Assert.That(callbacks, Is.Zero);
        scene.IsVisible = false;
        Assert.That(world.RootNodes, Does.Contain(root));
        root.Destroy(true);
    }
}
