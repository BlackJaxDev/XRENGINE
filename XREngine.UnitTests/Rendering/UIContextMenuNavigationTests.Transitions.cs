using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

public sealed partial class UIContextMenuNavigationTests
{
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
}
