using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture, NonParallelizable]
public sealed class QueuedTransformDestructionTests
{
    [Test]
    public void QueuedEmptyTransformClearsItsExactChildList()
    {
        Transform transform = new();
        transform.Destroy();

        XRObjectBase.ProcessPendingDestructions();

        Assert.That(transform.IsDestroyed, Is.True);
        Assert.That(transform.Children, Is.Empty);
    }

    [Test]
    public void QueuedTransformDetachesAChildWithoutDestroyingIt()
    {
        Transform parent = new();
        Transform child = new(parent);
        try
        {
            Assert.That(parent.Children, Does.Contain(child));
            parent.Destroy();

            XRObjectBase.ProcessPendingDestructions();

            Assert.Multiple(() =>
            {
                Assert.That(parent.IsDestroyed, Is.True);
                Assert.That(parent.Children, Is.Empty);
                Assert.That(child.IsDestroyed, Is.False);
                Assert.That(child.Parent, Is.Null);
            });
        }
        finally
        {
            if (!child.IsDestroyed) child.Destroy(true);
            if (!parent.IsDestroyed) parent.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void QueuedTransformAllowsRemovalButRejectsNewChild()
    {
        Transform parent = new();
        Transform child = new(parent);
        Transform foreign = new();
        try
        {
            parent.Destroy();

            Assert.That(parent.Children.Remove(child), Is.True);
            child.Parent = null;
            Assert.Throws<InvalidOperationException>(() => parent.Children.Add(foreign));
            Assert.That(parent.Children, Is.Empty);

            XRObjectBase.ProcessPendingDestructions();
            Assert.That(parent.IsDestroyed, Is.True);
            Assert.That(foreign.Parent, Is.Null);
        }
        finally
        {
            if (!child.IsDestroyed) child.Destroy(true);
            if (!foreign.IsDestroyed) foreign.Destroy(true);
            if (!parent.IsDestroyed) parent.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }
}
