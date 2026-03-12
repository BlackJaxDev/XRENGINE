using NUnit.Framework;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public class SceneNodeLifecycleTests
{
    [Test]
    public void AddComponent_AfterBeginPlay_InvokesComponentBeginPlay()
    {
        SceneNode node = new("LifecycleRoot");

        node.OnBeginPlay();

        LifecycleTrackingComponent component = node.AddComponent<LifecycleTrackingComponent>()!;

        Assert.Multiple(() =>
        {
            Assert.That(component, Is.Not.Null);
            Assert.That(component.BeginPlayCount, Is.EqualTo(1));
            Assert.That(component.EndPlayCount, Is.Zero);
        });
    }

    [Test]
    public void DetachAndReattachComponent_AfterBeginPlay_ReplaysPlayLifecycle()
    {
        SceneNode node = new("LifecycleRoot");
        LifecycleTrackingComponent component = node.AddComponent<LifecycleTrackingComponent>()!;

        node.OnBeginPlay();

        bool detached = node.DetachComponent(component);
        node.ReattachComponent(component);

        Assert.Multiple(() =>
        {
            Assert.That(detached, Is.True);
            Assert.That(component.BeginPlayCount, Is.EqualTo(2));
            Assert.That(component.EndPlayCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void ParentingIntoBegunPlayNode_BeginsPlayForChildComponents()
    {
        SceneNode parent = new("Parent");
        SceneNode child = new("Child");
        LifecycleTrackingComponent component = child.AddComponent<LifecycleTrackingComponent>()!;

        parent.OnBeginPlay();
        child.Parent = parent;

        Assert.Multiple(() =>
        {
            Assert.That(child.HasBegunPlay, Is.True);
            Assert.That(component.BeginPlayCount, Is.EqualTo(1));
            Assert.That(component.EndPlayCount, Is.Zero);
        });
    }

    [Test]
    public void UnparentingFromBegunPlayNode_EndsPlayForChildComponents()
    {
        SceneNode parent = new("Parent");
        SceneNode child = new(parent, "Child");
        LifecycleTrackingComponent component = child.AddComponent<LifecycleTrackingComponent>()!;

        parent.OnBeginPlay();
        child.Parent = null;

        Assert.Multiple(() =>
        {
            Assert.That(child.HasBegunPlay, Is.False);
            Assert.That(component.BeginPlayCount, Is.EqualTo(1));
            Assert.That(component.EndPlayCount, Is.EqualTo(1));
        });
    }

    private sealed class LifecycleTrackingComponent : XRComponent
    {
        public int BeginPlayCount { get; private set; }

        public int EndPlayCount { get; private set; }

        protected internal override void OnBeginPlay()
        {
            base.OnBeginPlay();
            BeginPlayCount++;
        }

        protected internal override void OnEndPlay()
        {
            base.OnEndPlay();
            EndPlayCount++;
        }
    }
}
