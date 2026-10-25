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
    [Test]
    public void HideKeepsLaterRowAndLabelMovedByEarlierRowDestruction()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            menu.Show(Vector2.Zero, new ContextMenuItem("First", null),
                new ContextMenuItem("Second", null));
            List<UIContextMenuItemComponent> rows = Rows(menu);
            SceneNode first = rows[0].SceneNode;
            SceneNode later = rows[1].SceneNode;
            ulong laterRevision = rows[1].ShowRevision;
            SceneNode label = later.Transform.Children.Single().SceneNode!;
            int laterDestroying = 0;
            later.Destroying += _ => { laterDestroying++; return true; };
            first.Destroying += _ =>
            {
                later.Parent = root;
                return true;
            };

            menu.Hide();

            first.IsDestroyed.ShouldBeTrue();
            later.IsDestroyed.ShouldBeFalse();
            label.IsDestroyed.ShouldBeFalse();
            later.Parent.ShouldBeSameAs(root);
            label.Parent.ShouldBeSameAs(later);
            laterDestroying.ShouldBe(0);
            rows[1].HasCurrentBinding(menu, laterRevision).ShouldBeFalse();
            rows[1].Menu.ShouldBeNull();
            Rows(menu).ShouldBeEmpty();
            input.SubscriberCount.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [TestCase("row", 0, 0)]
    [TestCase("label", 1, 0)]
    [TestCase("separator", 0, 2)]
    public void QueuedHideStopsLaterOrdinaryMenuFactories(string boundary, int expectedText,
        int expectedMaterials)
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            int textCount = 0;
            int materialCount = 0;
            bool queued = false;
            void OnCreated(XRComponent component)
            {
                if (component is UITextComponent) textCount++;
                if (component is UIMaterialComponent) materialCount++;
                if (queued) return;
                if (boundary == "row" && component is UIContextMenuItemComponent ||
                    boundary == "label" && component is UITextComponent ||
                    boundary == "separator" && component is UIMaterialComponent && materialCount == 2)
                {
                    queued = true;
                    menu.Hide();
                }
            }
            XRComponent.ComponentCreated += OnCreated;
            try
            {
                if (boundary == "separator")
                    menu.Show(Vector2.Zero, ContextMenuItem.Separator());
                else
                    menu.Show(Vector2.Zero, ContextMenuItem.Submenu("Old", [new ContextMenuItem("Child", null)]));
            }
            finally { XRComponent.ComponentCreated -= OnCreated; }

            queued.ShouldBeTrue();
            textCount.ShouldBe(expectedText);
            if (boundary == "separator") materialCount.ShouldBe(expectedMaterials);
            menu.IsOpen.ShouldBeFalse();
            Rows(menu).ShouldBeEmpty();
            input.SubscriberCount.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void QueuedHideDoesNotReplaceLabelFactoryFailure()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            var failure = new InvalidOperationException("Label factory callback failed.");
            bool queued = false;
            void OnCreated(XRComponent component)
            {
                if (queued || component is not UITextComponent) return;
                queued = true;
                menu.Hide();
                throw failure;
            }
            XRComponent.ComponentCreated += OnCreated;
            try
            {
                Should.Throw<InvalidOperationException>(() => menu.Show(Vector2.Zero,
                    ContextMenuItem.Submenu("Old", [new ContextMenuItem("Child", null)])))
                    .ShouldBeSameAs(failure);
            }
            finally { XRComponent.ComponentCreated -= OnCreated; }

            queued.ShouldBeTrue();
            menu.IsOpen.ShouldBeFalse();
            Rows(menu).ShouldBeEmpty();
            input.SubscriberCount.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void QueuedHideStopsVirtualScrollConfiguration()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            UIVirtualPopupScrollComponent? scroll = null;
            void OnCreated(XRComponent component)
            {
                if (component is not UIVirtualPopupScrollComponent created) return;
                scroll = created;
                menu.Hide();
            }
            XRComponent.ComponentCreated += OnCreated;
            try { menu.ShowVirtual(Vector2.Zero, new CountingVirtualListProvider(10)); }
            finally { XRComponent.ComponentCreated -= OnCreated; }

            scroll.ShouldNotBeNull().List.ShouldBeNull();
            menu.IsOpen.ShouldBeFalse();
            Rows(menu).ShouldBeEmpty();
            input.SubscriberCount.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void QueuedRequestDoesNotHideAContentBuilderFailure(bool hide)
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            var failure = new InvalidOperationException("Builder failed after requesting a transition.");

            Should.Throw<InvalidOperationException>(() => menu.ShowContent(Vector2.Zero, 40, _ =>
            {
                if (hide) menu.Hide();
                else menu.Show(Vector2.Zero, new ContextMenuItem("Must not open", null));
                throw failure;
            })).ShouldBeSameAs(failure);

            menu.IsOpen.ShouldBeFalse();
            Rows(menu).ShouldBeEmpty();
            input.SubscriberCount.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [TestCase("open", false)]
    [TestCase("open", true)]
    [TestCase("width", false)]
    [TestCase("width", true)]
    [TestCase("attachment", false)]
    [TestCase("attachment", true)]
    public void NewerRequestStopsTheOldContentBuilder(string boundary, bool hide)
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            int oldBuilderCalls = 0;
            bool requested = false;
            void Replace()
            {
                if (requested) return;
                requested = true;
                if (hide) menu.Hide();
                else menu.Show(Vector2.Zero, new ContextMenuItem("Replacement", null));
            }
            void OnMenuChanged(object? sender, IXRPropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(UIContextMenuComponent.IsOpen) && menu.IsOpen)
                    Replace();
            }
            void OnTransformChanged(object? sender, IXRPropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(UIBoundableTransform.Width)) Replace();
            }
            void OnChildAdded(TransformBase child) => Replace();
            if (boundary == "open") menu.PropertyChanged += OnMenuChanged;
            else if (boundary == "width") menu.Transform.PropertyChanged += OnTransformChanged;
            else menu.Transform.Children.PostAnythingAdded += OnChildAdded;

            menu.ShowContent(Vector2.Zero, 40, _ => oldBuilderCalls++);

            requested.ShouldBeTrue();
            oldBuilderCalls.ShouldBe(0);
            menu.IsOpen.ShouldBe(!hide);
            input.SubscriberCount.ShouldBe(hide ? 0 : 4);
            if (hide) menu.Transform.Children.ShouldBeEmpty();
            else Rows(menu).Single().Item!.Label.ShouldBe("Replacement");
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ShowFromAPlacementPropertyCallbackKeepsTheReplacementSizeAndRows()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            void Replace(object? sender, IXRPropertyChangedEventArgs args)
            {
                if (args.PropertyName != nameof(UIBoundableTransform.Width)) return;
                menu.UITransform.PropertyChanged -= Replace;
                menu.Show(Vector2.Zero, new ContextMenuItem("Replacement one", null),
                    new ContextMenuItem("Replacement two", null));
            }
            menu.UITransform.PropertyChanged += Replace;

            menu.Show(Vector2.Zero, new ContextMenuItem("Old", null));

            menu.IsOpen.ShouldBeTrue();
            Rows(menu).Select(row => row.Item!.Label).ShouldBe(["Replacement one", "Replacement two"]);
            ((UIBoundableTransform)menu.UITransform).Height.ShouldBe(UIContextMenuComponent.ItemHeight * 2);
            menu.SceneNode.Transform.Children.Count.ShouldBe(1);
        }
        finally { root.Destroy(true); }
    }

    [TestCase("closed")]
    [TestCase("destroyed-content")]
    public void ShowRequestedDuringHideRunsAfterTheOldContentIsRetired(string callback)
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            menu.Show(Vector2.Zero, new ContextMenuItem("Old", null));
            if (callback == "closed")
            {
                void Replace(object? sender, IXRPropertyChangedEventArgs args)
                {
                    if (args.PropertyName != nameof(UIContextMenuComponent.IsOpen) || menu.IsOpen) return;
                    menu.PropertyChanged -= Replace;
                    menu.Show(Vector2.Zero, new ContextMenuItem("Replacement", null));
                }
                menu.PropertyChanged += Replace;
            }
            else
                Rows(menu)[0].SceneNode.Destroyed += _ =>
                    menu.Show(Vector2.Zero, new ContextMenuItem("Replacement", null));

            menu.Hide();

            menu.IsOpen.ShouldBeTrue();
            Rows(menu).Single().Item!.Label.ShouldBe("Replacement");
            input.SubscriberCount.ShouldBe(4);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void FailedContentBuilderRetiresPartialContentAndCanBeOpenedAgain()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode? partial = null;
            var error = new InvalidOperationException("Content builder failed.");
            Should.Throw<InvalidOperationException>(() => menu.ShowContent(Vector2.Zero, 40, parent =>
            {
                partial = parent.NewChild();
                throw error;
            })).ShouldBeSameAs(error);

            menu.IsOpen.ShouldBeFalse();
            partial.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            menu.SceneNode.Transform.Children.ShouldBeEmpty();
            input.SubscriberCount.ShouldBe(0);
            menu.Show(Vector2.Zero, new ContextMenuItem("Recovered", null));
            Rows(menu).Single().Item!.Label.ShouldBe("Recovered");
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void HideRetiresOnlyMenuContentAndKeepsOtherOwnedChildren()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode decoration = menu.SceneNode.NewChild();
            decoration.Name = "Owner decoration";
            menu.Show(Vector2.Zero, new ContextMenuItem("Action", null));
            decoration.IsDestroyed.ShouldBeFalse();

            menu.Hide();

            decoration.IsDestroyed.ShouldBeFalse();
            decoration.Parent.ShouldBeSameAs(menu.SceneNode);
            menu.SceneNode.Transform.Children.Count.ShouldBe(1);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LatestRequestDuringContentConstructionWins(bool hideLast)
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            ContextMenuItem[] replacement = [new("Captured", null)];
            menu.ShowContent(Vector2.Zero, 40, _ =>
            {
                menu.Show(Vector2.Zero, new ContextMenuItem("Discarded", null));
                menu.Show(Vector2.Zero, replacement);
                replacement[0] = new("Changed after request", null);
                if (hideLast) menu.Hide();
            });

            menu.IsOpen.ShouldBe(!hideLast);
            if (hideLast) Rows(menu).ShouldBeEmpty();
            else Rows(menu).Single().Item!.Label.ShouldBe("Captured");
        }
        finally { root.Destroy(true); }
    }

    [TestCase("menu")]
    [TestCase("node")]
    [TestCase("transform")]
    [TestCase("parent")]
    [TestCase("moved")]
    public void QueuedShowDoesNotCrossAnOwnerLifetimeChange(string change)
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            menu.Show(Vector2.Zero, new ContextMenuItem("Old", null));
            void Reopen()
            {
                menu.Dismissed -= Reopen;
                menu.Show(Vector2.Zero, new ContextMenuItem("Stale", null));
                switch (change)
                {
                    case "menu": menu.Generate(); break;
                    case "node": menu.SceneNode.Generate(); break;
                    case "transform": menu.Transform.Generate(); break;
                    case "parent": root.Transform.Generate(); break;
                    case "moved": menu.Transform.Parent = root.NewChild().Transform; break;
                }
            }
            menu.Dismissed += Reopen;

            menu.Hide();

            menu.IsOpen.ShouldBeFalse();
            Rows(menu).ShouldBeEmpty();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ContentTransferredToAnotherOwnerIsNotDestroyedByHide()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            menu.Show(Vector2.Zero, new ContextMenuItem("Old", null));
            SceneNode content = menu.SceneNode.Transform.Children.Single().SceneNode!;
            content.Transform.Parent = root.Transform;

            menu.Hide();

            content.IsDestroyed.ShouldBeFalse();
            content.Parent.ShouldBeSameAs(root);
            Rows(menu).ShouldBeEmpty();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void UnboundedReopenCallbacksStopAndLeaveTheMenuClosed()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            int calls = 0;
            void Reopen(object? sender, IXRPropertyChangedEventArgs args)
            {
                if (args.PropertyName != nameof(UIContextMenuComponent.IsOpen) || !menu.IsOpen) return;
                calls++;
                menu.Show(Vector2.Zero, new ContextMenuItem("Again", null));
            }
            menu.PropertyChanged += Reopen;

            Should.Throw<InvalidOperationException>(() => menu.Show(Vector2.Zero, new ContextMenuItem("First", null)));

            calls.ShouldBeLessThan(100);
            menu.IsOpen.ShouldBeFalse();
            Rows(menu).ShouldBeEmpty();
            input.SubscriberCount.ShouldBe(0);
            menu.PropertyChanged -= Reopen;
            menu.Show(Vector2.Zero, new ContextMenuItem("Recovered", null));
            Rows(menu).Single().Item!.Label.ShouldBe("Recovered");
        }
        finally { root.Destroy(true); }
    }

    [TestCase("moved", false)]
    [TestCase("moved", true)]
    [TestCase("node", false)]
    [TestCase("node", true)]
    [TestCase("transform", false)]
    [TestCase("transform", true)]
    public void ContentAttachmentOwnershipChangeStopsTheBuilder(string change, bool custom)
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode other = root.NewChild();
            SceneNode? transferred = null;
            var children = menu.Transform.Children;
            void ChangeOwner(TransformBase transform)
            {
                children.PostAnythingAdded -= ChangeOwner;
                transferred = transform.SceneNode!;
                switch (change)
                {
                    case "moved": transform.SetParent(other.Transform, false, EParentAssignmentMode.Immediate); break;
                    case "node": transferred.Generate(); break;
                    case "transform": transform.Generate(); break;
                }
            }
            children.PostAnythingAdded += ChangeOwner;
            bool builderCalled = false;
            Action show = custom
                ? () => menu.ShowContent(Vector2.Zero, 40, _ => builderCalled = true)
                : () => menu.Show(Vector2.Zero, new ContextMenuItem("Action", null));

            Should.Throw<InvalidOperationException>(show);

            transferred.ShouldNotBeNull().IsDestroyed.ShouldBeFalse();
            transferred!.Name.ShouldBe("ContextMenuContent");
            transferred.Transform.Children.ShouldBeEmpty();
            if (change == "moved") transferred.Parent.ShouldBeSameAs(other);
            builderCalled.ShouldBeFalse();
            menu.IsOpen.ShouldBeFalse();
            input.SubscriberCount.ShouldBe(0);
            menu.Show(Vector2.Zero, new ContextMenuItem("Recovered", null));
            Rows(menu).Single().Item!.Label.ShouldBe("Recovered");
        }
        finally { root.Destroy(true); }
    }

}
