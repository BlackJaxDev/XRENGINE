using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Prefabs;

[TestFixture, NonParallelizable]
public sealed class SceneVisibilityGuardedWriteTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void EqualValueReentryRejectsTheOlderVisibilityWrite(bool guarded)
    {
        XRScene scene = new();
        ulong before = scene.VisibilityWriteIntentRevision;
        XRPropertyChangingEventHandler callback = (_, args) =>
        {
            if (args.PropertyName == nameof(XRScene.IsVisible))
                scene.IsVisible = true;
        };
        scene.PropertyChanging += callback;
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                if (guarded) scene.SetVisibilityGuarded(false, 0, static _ => { });
                else scene.IsVisible = false;
            });
            Assert.That(scene.IsVisible, Is.True);
            Assert.That(scene.VisibilityWriteIntentRevision, Is.EqualTo(before + 2));
        }
        finally { scene.PropertyChanging -= callback; scene.Destroy(true); }
    }

    [Test]
    public void RegeneratedSceneCannotReceiveTheOldWrite()
    {
        XRScene scene = new();
        ObjectConstructionIdentity before = ObjectConstructionIdentity.Capture(scene);
        XRPropertyChangingEventHandler callback = (_, args) =>
        {
            if (args.PropertyName == nameof(XRScene.IsVisible)) scene.Generate();
        };
        scene.PropertyChanging += callback;
        try
        {
            Assert.Throws<InvalidOperationException>(() => scene.SetVisibilityGuarded(false, 0, static _ => { }));
            Assert.That(before.IsCurrent, Is.False);
            Assert.That(scene.IsVisible, Is.True);
        }
        finally { scene.PropertyChanging -= callback; scene.Destroy(true); }
    }

    [Test]
    public void CallerInvalidationBeforeCommitPreservesVisibility()
    {
        XRScene scene = new();
        bool current = true;
        XRPropertyChangingEventHandler callback = (_, args) =>
        {
            if (args.PropertyName == nameof(XRScene.IsVisible)) current = false;
        };
        scene.PropertyChanging += callback;
        try
        {
            Assert.Throws<InvalidOperationException>(() => scene.SetVisibilityGuarded(false, 0, _ =>
            {
                if (!current) throw new InvalidOperationException("Owner changed.");
            }));
            Assert.That(scene.IsVisible, Is.True);
        }
        finally { scene.PropertyChanging -= callback; scene.Destroy(true); }
    }

    [Test]
    public void PropertyChangedReplacementRemainsTheNewerValue()
    {
        XRScene scene = new();
        bool replace = true;
        XRPropertyChangedEventHandler callback = (_, args) =>
        {
            if (args.PropertyName != nameof(XRScene.IsVisible) || !replace) return;
            replace = false;
            scene.IsVisible = true;
        };
        scene.PropertyChanged += callback;
        try
        {
            Assert.Throws<InvalidOperationException>(() => scene.SetVisibilityGuarded(false, 0, static _ => { }));
            Assert.That(scene.IsVisible, Is.True);
        }
        finally { scene.PropertyChanged -= callback; scene.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void VisibilityVetoKeepsItsNormalAndGuardedSemantics(bool guarded)
    {
        XRScene scene = new();
        XRPropertyChangingEventHandler callback = (_, args) =>
        {
            if (args.PropertyName == nameof(XRScene.IsVisible))
                ((XRPropertyChangingEventArgs<bool>)args).AllowChange = false;
        };
        scene.PropertyChanging += callback;
        try
        {
            if (guarded)
                Assert.Throws<InvalidOperationException>(() => scene.SetVisibilityGuarded(false, 0, static _ => { }));
            else
                Assert.DoesNotThrow(() => scene.IsVisible = false);
            Assert.That(scene.IsVisible, Is.True);
        }
        finally { scene.PropertyChanging -= callback; scene.Destroy(true); }
    }

    [Test]
    public void AcceptedEqualRequestsStillAdvanceIntentWithoutPublishingAChange()
    {
        XRScene scene = new();
        int changes = 0;
        XRPropertyChangedEventHandler callback = (_, args) =>
        {
            if (args.PropertyName == nameof(XRScene.IsVisible)) changes++;
        };
        scene.PropertyChanged += callback;
        ulong before = scene.VisibilityWriteIntentRevision;
        try
        {
            scene.SetVisibilityGuarded(true, 0, static _ => { });
            scene.IsVisible = true;
            Assert.That(scene.VisibilityWriteIntentRevision, Is.EqualTo(before + 2));
            Assert.That(changes, Is.Zero);
            scene.SetVisibilityGuarded(false, 0, static _ => { });
            Assert.That(scene.IsVisible, Is.False);
            Assert.That(changes, Is.EqualTo(1));
        }
        finally { scene.PropertyChanged -= callback; scene.Destroy(true); }
    }
}
