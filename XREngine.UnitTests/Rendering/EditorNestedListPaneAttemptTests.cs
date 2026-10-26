using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class EditorNestedListPaneAttemptTests
{
    [Test]
    public void ContainerTransformChangeDuringInitialLayoutRetiresExactEmptyRoot()
    {
        SceneNode container = new("Nested detail container");
        UIListTransform containerTransform = container.SetTransform<UIListTransform>();
        SceneNode root = new("NestedListDetail");
        EditorNestedListPaneAttempt attempt = new(root, container,
            ObjectConstructionIdentity.Capture(container), containerTransform,
            ObjectConstructionIdentity.Capture(containerTransform));
        bool changed = false;
        void ChangeContainer(object? _, IXRPropertyChangedEventArgs change)
        {
            if (changed || change.PropertyName != nameof(SceneNode.Transform) ||
                root.Transform is not UIListTransform)
                return;
            changed = true;
            container.SetTransform<UIListTransform>();
        }
        root.PropertyChanged += ChangeContainer;
        UIListTransform layout;
        try { layout = root.SetTransform<UIListTransform>(); }
        finally { root.PropertyChanged -= ChangeContainer; }

        bool captured = attempt.SetUnattachedLayout(layout);
        attempt.DestroyRootIfOwned();

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(captured, Is.True,
                "The final detached root proof is independent of the container proof.");
            Assert.That(attempt.IsUnattachedRootOwned, Is.False,
                "A changed container still rejects attachment.");
            Assert.That(root.IsDestroyed || root.IsDestroyQueued, Is.True,
                "The exact never-adopted empty leaf is retired despite the container change.");
        });
        container.Destroy(true);
    }

    [Test]
    public void InitialLayoutCallbackTransferPreservesForeignRoot()
    {
        SceneNode container = new("Nested detail container");
        UIListTransform containerTransform = container.SetTransform<UIListTransform>();
        SceneNode foreign = new("Foreign host");
        foreign.SetTransform<UIListTransform>();
        SceneNode root = new("NestedListDetail");
        EditorNestedListPaneAttempt attempt = new(root, container,
            ObjectConstructionIdentity.Capture(container), containerTransform,
            ObjectConstructionIdentity.Capture(containerTransform));
        bool transferred = false;
        void TransferRoot(object? _, IXRPropertyChangedEventArgs change)
        {
            if (transferred || change.PropertyName != nameof(SceneNode.Transform) ||
                root.Transform is not UIListTransform)
                return;
            transferred = true;
            root.Transform.SetParent(foreign.Transform, false, EParentAssignmentMode.Immediate);
        }
        root.PropertyChanged += TransferRoot;
        UIListTransform layout;
        try { layout = root.SetTransform<UIListTransform>(); }
        finally { root.PropertyChanged -= TransferRoot; }

        bool captured = attempt.SetUnattachedLayout(layout);
        attempt.DestroyRootIfOwned();

        Assert.Multiple(() =>
        {
            Assert.That(transferred, Is.True);
            Assert.That(captured, Is.False);
            Assert.That(root.Transform.Parent, Is.SameAs(foreign.Transform));
            Assert.That(root.IsDestroyed || root.IsDestroyQueued, Is.False);
        });
        root.Destroy(true);
        foreign.Destroy(true);
        container.Destroy(true);
    }

    [Test]
    public void ReplacedInitialLayoutIsNotRetiredAsTheCapturedRoot()
    {
        SceneNode container = new("Nested detail container");
        UIListTransform containerTransform = container.SetTransform<UIListTransform>();
        SceneNode root = new("NestedListDetail");
        EditorNestedListPaneAttempt attempt = new(root, container,
            ObjectConstructionIdentity.Capture(container), containerTransform,
            ObjectConstructionIdentity.Capture(containerTransform));
        UIBoundableTransform? replacement = null;
        void ReplaceRootLayout(object? _, IXRPropertyChangedEventArgs change)
        {
            if (replacement is not null || change.PropertyName != nameof(SceneNode.Transform) ||
                root.Transform is not UIListTransform)
                return;
            replacement = root.SetTransform<UIBoundableTransform>();
        }
        root.PropertyChanged += ReplaceRootLayout;
        UIListTransform returnedLayout;
        try { returnedLayout = root.SetTransform<UIListTransform>(); }
        finally { root.PropertyChanged -= ReplaceRootLayout; }

        bool captured = attempt.SetUnattachedLayout(returnedLayout);
        attempt.DestroyRootIfOwned();

        Assert.Multiple(() =>
        {
            Assert.That(replacement, Is.Not.Null);
            Assert.That(captured, Is.False);
            Assert.That(root.Transform, Is.SameAs(replacement));
            Assert.That(root.IsDestroyed || root.IsDestroyQueued, Is.False);
        });
        root.Destroy(true);
        container.Destroy(true);
    }

    [Test]
    public void FailedLeafRetirementRetainsTheExactReceipt()
    {
        SceneNode container = new("Nested detail container");
        UIListTransform containerTransform = container.SetTransform<UIListTransform>();
        SceneNode root = new("NestedListDetail");
        object? retained = null;
        EditorNestedListPaneAttempt attempt = new(root, container,
            ObjectConstructionIdentity.Capture(container), containerTransform,
            ObjectConstructionIdentity.Capture(containerTransform), receipt => retained = receipt);
        UIListTransform layout = root.SetTransform<UIListTransform>();
        Assert.That(attempt.SetUnattachedLayout(layout), Is.True);
        root.AddComponent<InspectorListDetailRefreshComponent>();

        attempt.DestroyRootIfOwned();

        Assert.Multiple(() =>
        {
            Assert.That(retained, Is.Not.Null);
            Assert.That(retained!.GetType().GetProperty("Node",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(retained),
                Is.SameAs(root));
            Assert.That(root.IsDestroyed || root.IsDestroyQueued, Is.False);
        });
        root.Destroy(true);
        container.Destroy(true);
    }
}
