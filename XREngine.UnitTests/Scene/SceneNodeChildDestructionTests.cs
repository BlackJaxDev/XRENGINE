using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture, NonParallelizable]
public sealed class SceneNodeChildDestructionTests
{
    [Test]
    public void MovedChildAndDescendantSurviveEarlierChildDestruction()
    {
        SceneNode owner = new("Owner");
        SceneNode outside = new("Outside");
        SceneNode first = owner.NewChild("First");
        SceneNode later = owner.NewChild("Later");
        SceneNode descendant = later.NewChild("Descendant");
        int laterDestroying = 0;
        later.Destroying += _ => { laterDestroying++; return true; };
        first.Destroying += _ =>
        {
            later.Parent = outside;
            return true;
        };
        try
        {
            owner.Destroy(true);
            Assert.Multiple(() =>
            {
                Assert.That(first.IsDestroyed, Is.True);
                Assert.That(owner.IsDestroyed, Is.True);
                Assert.That(later.IsDestroyed, Is.False);
                Assert.That(descendant.IsDestroyed, Is.False);
                Assert.That(later.Parent, Is.SameAs(outside));
                Assert.That(descendant.Parent, Is.SameAs(later));
                Assert.That(laterDestroying, Is.Zero);
            });
        }
        finally
        {
            if (!owner.IsDestroyed) owner.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
            outside.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [TestCase("node")]
    [TestCase("transform")]
    [TestCase("replacement")]
    public void ChangedChildLifetimeSkipsItsOldDestructionSnapshot(string change)
    {
        SceneNode owner = new("Owner");
        SceneNode first = owner.NewChild("First");
        SceneNode later = owner.NewChild("Later");
        TransformBase originalTransform = later.Transform;
        int laterDestroying = 0;
        later.Destroying += _ => { laterDestroying++; return true; };
        first.Destroying += _ =>
        {
            switch (change)
            {
                case "node": later.Generate(); break;
                case "transform": later.Transform.Generate(); break;
                case "replacement": later.SetTransform<Transform>(); break;
            }
            return true;
        };
        try
        {
            owner.Destroy(true);
            Assert.Multiple(() =>
            {
                Assert.That(owner.IsDestroyed, Is.True);
                Assert.That(later.IsDestroyed, Is.False);
                Assert.That(laterDestroying, Is.Zero);
                if (change == "replacement")
                    Assert.That(later.Transform, Is.Not.SameAs(originalTransform));
            });
        }
        finally
        {
            if (!owner.IsDestroyed) owner.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
            if (!later.IsDestroyed) later.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [TestCase("transform")]
    [TestCase("list-lifetime")]
    [TestCase("list-replacement")]
    public void ChangedSourceStorageStopsLaterChildDispatch(string change)
    {
        SceneNode owner = new("Owner");
        SceneNode first = owner.NewChild("First");
        SceneNode later = owner.NewChild("Later");
        int laterDestroying = 0;
        later.Destroying += _ => { laterDestroying++; return true; };
        first.Destroying += _ =>
        {
            switch (change)
            {
                case "transform": owner.Transform.Generate(); break;
                case "list-lifetime": owner.Transform.Children.Generate(); break;
                case "list-replacement":
                    owner.Transform.Children = new([first.Transform, later.Transform]);
                    break;
            }
            return true;
        };
        try
        {
            owner.Destroy(true);
            Assert.Multiple(() =>
            {
                Assert.That(laterDestroying, Is.Zero);
                Assert.That(later.IsDestroyed, Is.False);
            });
        }
        finally
        {
            if (!owner.IsDestroyed) owner.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
            if (!later.IsDestroyed) later.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void UnchangedChildrenCompleteCanonicalDestructionOnce()
    {
        SceneNode owner = new("Owner");
        SceneNode first = owner.NewChild("First");
        SceneNode later = owner.NewChild("Later");
        int firstDestroying = 0;
        int laterDestroying = 0;
        first.Destroying += _ => { firstDestroying++; return true; };
        later.Destroying += _ => { laterDestroying++; return true; };

        owner.Destroy(true);
        owner.Destroy(true);
        XRObjectBase.ProcessPendingDestructions();
        Assert.Multiple(() =>
        {
            Assert.That(first.IsDestroyed, Is.True);
            Assert.That(later.IsDestroyed, Is.True);
            Assert.That(firstDestroying, Is.EqualTo(1));
            Assert.That(laterDestroying, Is.EqualTo(1));
        });
    }

    [Test]
    public void ChildDestructionFailureStopsLaterCallbacks()
    {
        SceneNode owner = new("Owner");
        SceneNode first = owner.NewChild("First");
        SceneNode later = owner.NewChild("Later");
        InvalidOperationException failure = new("First child failed.");
        int laterDestroying = 0;
        Func<XREngine.Data.Core.XRObjectBase, bool> fail = _ => throw failure;
        first.Destroying += fail;
        later.Destroying += _ => { laterDestroying++; return true; };
        try
        {
            Assert.That(Assert.Throws<InvalidOperationException>(() => owner.Destroy(true)), Is.SameAs(failure));
            Assert.Multiple(() =>
            {
                Assert.That(laterDestroying, Is.Zero);
                Assert.That(later.IsDestroyed, Is.False);
            });
        }
        finally
        {
            first.Destroying -= fail;
            owner.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void VetoedChildDoesNotStopLaterChildDestruction()
    {
        SceneNode owner = new("Owner");
        SceneNode first = owner.NewChild("First");
        SceneNode later = owner.NewChild("Later");
        int laterDestroying = 0;
        Func<XREngine.Data.Core.XRObjectBase, bool> veto = _ => false;
        first.Destroying += veto;
        later.Destroying += _ => { laterDestroying++; return true; };
        try
        {
            owner.Destroy(true);
            Assert.Multiple(() =>
            {
                Assert.That(first.IsDestroyed, Is.False);
                Assert.That(later.IsDestroyed, Is.True);
                Assert.That(laterDestroying, Is.EqualTo(1));
            });
        }
        finally
        {
            first.Destroying -= veto;
            XRObjectBase.ProcessPendingDestructions();
            if (!first.IsDestroyed) first.Destroy(true);
            if (!owner.IsDestroyed) owner.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void QueuedChildStillReceivesImmediateCanonicalDestructionOnce()
    {
        SceneNode owner = new("Owner");
        SceneNode child = owner.NewChild("Child");
        int callbacks = 0;
        child.Destroying += _ => { callbacks++; return true; };
        child.Destroy();

        owner.Destroy(true);
        XRObjectBase.ProcessPendingDestructions();

        Assert.Multiple(() =>
        {
            Assert.That(child.IsDestroyed, Is.True);
            Assert.That(callbacks, Is.EqualTo(1));
        });
    }

    [Test]
    public void DuplicateCurrentChildOccurrenceDispatchesDestructionOnce()
    {
        SceneNode owner = new("Owner");
        SceneNode child = owner.NewChild("Child");
        int callbacks = 0;
        child.Destroying += _ => { callbacks++; return true; };
        owner.Transform.Children.Add(child.Transform, false, false);

        owner.Destroy(true);
        XRObjectBase.ProcessPendingDestructions();

        Assert.Multiple(() =>
        {
            Assert.That(owner.IsDestroyed, Is.True);
            Assert.That(child.IsDestroyed, Is.True);
            Assert.That(callbacks, Is.EqualTo(1));
        });
    }
}
