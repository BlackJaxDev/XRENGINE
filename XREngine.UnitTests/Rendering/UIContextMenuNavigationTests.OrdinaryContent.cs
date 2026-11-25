using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

public sealed partial class UIContextMenuNavigationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void ScrollFactoryFailureRetiresItsLeafAndKeepsTheOriginalError(bool queueHide)
    {
        SceneNode root = CreateCanvas(out var input);
        UIScrollRegionComponent? failed = null;
        SceneNode? failedNode = null;
        var failure = new InvalidOperationException("Scroll factory observer failed.");
        try
        {
            var menu = CreateMenu(root);
            void OnCreated(XRComponent component)
            {
                if (component is not UIScrollRegionComponent scroll) return;
                failed = scroll;
                failedNode = scroll.SceneNode;
                failedNode.Parent.ShouldBeNull();
                if (queueHide) menu.Hide();
                throw failure;
            }
            XRComponent.ComponentCreated += OnCreated;
            try { Should.Throw<InvalidOperationException>(() => menu.Show(Vector2.Zero, new ContextMenuItem("Old", null))).ShouldBeSameAs(failure); }
            finally { XRComponent.ComponentCreated -= OnCreated; }
            failed.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            failedNode.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            menu.FailedConstructionLeaves.ShouldBeEmpty();
            menu.Transform.Children.ShouldBeEmpty();
            menu.IsOpen.ShouldBeFalse();
            input.SubscriberCount.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void SupersededScrollConstructionStopsBeforeLaterConfiguration(bool duringConfiguration, bool replace)
    {
        SceneNode root = CreateCanvas(out _);
        UIScrollRegionComponent? old = null;
        SceneNode? oldNode = null;
        bool requested = false;
        try
        {
            var menu = CreateMenu(root);
            void Request()
            {
                if (requested) return;
                requested = true;
                if (replace) menu.Show(Vector2.Zero, new ContextMenuItem("Replacement", null));
                else menu.Hide();
            }
            void OnCreated(XRComponent component)
            {
                if (old is not null || component is not UIScrollRegionComponent scroll) return;
                old = scroll;
                oldNode = scroll.SceneNode;
                if (!duringConfiguration) Request();
                else scroll.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(UIScrollRegionComponent.IsTabStop)) Request();
                };
            }
            XRComponent.ComponentCreated += OnCreated;
            try { menu.Show(Vector2.Zero, new ContextMenuItem("Old", null)); }
            finally { XRComponent.ComponentCreated -= OnCreated; }
            requested.ShouldBeTrue();
            old.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            old.ConsumeWheelAtBoundary.ShouldBeFalse();
            oldNode.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            menu.FailedConstructionLeaves.ShouldBeEmpty();
            menu.IsOpen.ShouldBe(replace);
            if (replace) Rows(menu).Single().Item!.Label.ShouldBe("Replacement");
            else menu.Transform.Children.ShouldBeEmpty();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ScrollFactoryAdoptionKeepsTheForeignNodeAndUninsertedComponent()
    {
        SceneNode root = CreateCanvas(out _);
        UIScrollRegionComponent? transferred = null;
        SceneNode? transferredNode = null;
        try
        {
            var menu = CreateMenu(root);
            void OnCreated(XRComponent component)
            {
                if (component is not UIScrollRegionComponent scroll) return;
                transferred = scroll;
                transferredNode = scroll.SceneNode;
                transferredNode.Parent = root;
            }
            XRComponent.ComponentCreated += OnCreated;
            try { Should.Throw<InvalidOperationException>(() => menu.Show(Vector2.Zero, new ContextMenuItem("Old", null))); }
            finally { XRComponent.ComponentCreated -= OnCreated; }
            transferred.ShouldNotBeNull().IsDestroyed.ShouldBeFalse();
            transferred.ConsumeWheelAtBoundary.ShouldBeFalse();
            transferredNode.ShouldNotBeNull().Parent.ShouldBeSameAs(root);
            transferredNode.GetComponent<UIScrollRegionComponent>().ShouldBeNull();
            transferredNode.Transform.Children.ShouldBeEmpty();
            menu.FailedConstructionLeaves.Count.ShouldBe(1);
            menu.Transform.Children.ShouldBeEmpty();
            menu.IsOpen.ShouldBeFalse();
        }
        finally
        {
            if (transferred is { IsDestroyed: false }) transferred.Destroy(true);
            root.Destroy(true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TransferredScrollOrListAttachmentStopsBeforeRows(bool transferList)
    {
        SceneNode root = CreateCanvas(out _);
        SceneNode? transferred = null;
        try
        {
            var menu = CreateMenu(root);
            void Transfer(TransformBase child)
            {
                if (transferred is not null) return;
                transferred = child.SceneNode!;
                transferred.Parent = root;
            }
            void OnCreated(XRComponent component)
            {
                if (component is UIScrollRegionComponent scroll)
                    scroll.Transform.Children.PostAnythingAdded += Transfer;
            }
            if (transferList) XRComponent.ComponentCreated += OnCreated;
            else menu.Transform.Children.PostAnythingAdded += Transfer;
            try { Should.Throw<InvalidOperationException>(() => menu.Show(Vector2.Zero, new ContextMenuItem("Old", null))); }
            finally
            {
                XRComponent.ComponentCreated -= OnCreated;
                menu.Transform.Children.PostAnythingAdded -= Transfer;
            }
            transferred.ShouldNotBeNull().IsDestroyed.ShouldBeFalse();
            transferred.Parent.ShouldBeSameAs(root);
            transferred.Transform.Children.ShouldBeEmpty();
            menu.FailedConstructionLeaves.Count.ShouldBe(1);
            menu.Transform.Children.ShouldBeEmpty();
            menu.IsOpen.ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void SupersededScrollOrListAttachmentRetiresOnlyTheOldContent(bool atList, bool replace)
    {
        SceneNode root = CreateCanvas(out _);
        SceneNode? old = null;
        try
        {
            var menu = CreateMenu(root);
            void Replace(TransformBase child)
            {
                if (old is not null) return;
                old = child.SceneNode!;
                if (replace) menu.Show(Vector2.Zero, new ContextMenuItem("Replacement", null));
                else menu.Hide();
            }
            void OnCreated(XRComponent component)
            {
                if (component is UIScrollRegionComponent scroll)
                    scroll.Transform.Children.PostAnythingAdded += Replace;
            }
            if (atList) XRComponent.ComponentCreated += OnCreated;
            else menu.Transform.Children.PostAnythingAdded += Replace;
            try { menu.Show(Vector2.Zero, new ContextMenuItem("Old", null)); }
            finally
            {
                XRComponent.ComponentCreated -= OnCreated;
                menu.Transform.Children.PostAnythingAdded -= Replace;
            }
            old.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            menu.FailedConstructionLeaves.ShouldBeEmpty();
            menu.IsOpen.ShouldBe(replace);
            if (replace)
            {
                menu.Transform.Children.Count.ShouldBe(1);
                Rows(menu).Single().Item!.Label.ShouldBe("Replacement");
            }
            else menu.Transform.Children.ShouldBeEmpty();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ScrollAndListPublishTheirFinalConfigurationBeforeRows()
    {
        SceneNode root = CreateCanvas(out _);
        bool sawScroll = false, sawList = false;
        try
        {
            var menu = CreateMenu(root);
            void OnRoot(TransformBase child)
            {
                UIScrollableTransform scroll = child.ShouldBeOfType<UIScrollableTransform>();
                sawScroll = true;
                scroll.ScrollableX.ShouldBeFalse();
                UIScrollRegionComponent input = child.SceneNode!.GetComponent<UIScrollRegionComponent>()!;
                input.IsTabStop.ShouldBeFalse();
                input.ConsumeWheelAtBoundary.ShouldBeTrue();
                child.Children.PostAnythingAdded += listChild =>
                {
                    UIListTransform list = listChild.ShouldBeOfType<UIListTransform>();
                    sawList = true;
                    list.Height.ShouldBe(UIContextMenuComponent.ItemHeight * 2);
                    list.MinAnchor.ShouldBe(new Vector2(0, 1));
                    list.NormalizedPivot.ShouldBe(new Vector2(0, 1));
                    list.Children.ShouldBeEmpty();
                };
            }
            menu.Transform.Children.PostAnythingAdded += OnRoot;
            try { menu.Show(Vector2.Zero, new ContextMenuItem("First", null), new ContextMenuItem("Second", null)); }
            finally { menu.Transform.Children.PostAnythingAdded -= OnRoot; }
            sawScroll.ShouldBeTrue();
            sawList.ShouldBeTrue();
            Rows(menu).Count.ShouldBe(2);
            menu.FailedConstructionLeaves.ShouldBeEmpty();
        }
        finally { root.Destroy(true); }
    }
}
