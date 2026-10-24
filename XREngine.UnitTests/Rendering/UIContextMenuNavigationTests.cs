using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIContextMenuNavigationTests
{
    private UITestShaderScope _shaderScope = null!;

    [SetUp]
    public void SetUp() => _shaderScope = new UITestShaderScope();

    [TearDown]
    public void TearDown() => _shaderScope.Dispose();

    [Test]
    public void ArrowKeysSkipSeparatorsAndDisabledRowsAndWrap()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            menu.Show(new Vector2(8, 80),
                new ContextMenuItem("First", () => { }),
                ContextMenuItem.Separator(),
                new ContextMenuItem("Disabled", () => { }, false),
                new ContextMenuItem("Second", () => { }),
                new ContextMenuItem("Third", () => { }));
            menu.UITransform.Visibility = EVisibility.Visible;
            input.PresentedFocusAccepted = true;
            List<UIContextMenuItemComponent> rows = Rows(menu);
            rows.Count.ShouldBe(3);
            UIContextMenuItemComponent first = rows[0];
            UIContextMenuItemComponent second = rows[1];
            UIContextMenuItemComponent third = rows[2];

            Move(menu, first, 1).ShouldBeTrue();
            input.FocusedComponent.ShouldBeSameAs(second);
            Move(menu, second, 1).ShouldBeTrue();
            input.FocusedComponent.ShouldBeSameAs(third);
            Move(menu, third, 1).ShouldBeTrue();
            input.FocusedComponent.ShouldBeSameAs(first);
            Move(menu, first, -1).ShouldBeTrue();
            input.FocusedComponent.ShouldBeSameAs(third);
            Move(menu, third, -1, toEdge: true).ShouldBeTrue();
            input.FocusedComponent.ShouldBeSameAs(first);
            Move(menu, first, 1, toEdge: true).ShouldBeTrue();
            input.FocusedComponent.ShouldBeSameAs(third);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void FocusRejectedByPresentationKeepsTheCurrentRow()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            menu.Show(new Vector2(8, 80),
                new ContextMenuItem("First", () => { }),
                new ContextMenuItem("Second", () => { }),
                new ContextMenuItem("Third", () => { }));
            menu.UITransform.Visibility = EVisibility.Visible;
            List<UIContextMenuItemComponent> rows = Rows(menu);
            input.FocusedComponent = rows[0];

            Move(menu, rows[0], 1).ShouldBeFalse();

            input.PresentedFocusAttempts.ShouldBe(2);
            input.FocusedComponent.ShouldBeSameAs(rows[0]);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void RowFromEarlierShowCannotMoveFocusInReplacementMenu()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            menu.Show(new Vector2(8, 80),
                new ContextMenuItem("Old first", () => { }),
                new ContextMenuItem("Old second", () => { }));
            UIContextMenuItemComponent stale = Rows(menu)[0];
            ulong staleRevision = stale.ShowRevision;

            menu.Show(new Vector2(8, 80),
                new ContextMenuItem("New first", () => { }),
                new ContextMenuItem("New second", () => { }));
            menu.UITransform.Visibility = EVisibility.Visible;
            input.PresentedFocusAccepted = true;

            stale.Menu.ShouldBeNull();
            menu.MoveItemFocus(stale, staleRevision, 1, toEdge: false).ShouldBeFalse();
            Move(menu, Rows(menu)[0], 1).ShouldBeTrue();
            input.PresentedFocusAttempts.ShouldBe(1);
            input.FocusedComponent.ShouldBeSameAs(Rows(menu)[1]);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void RowsUseTheItemsAndHeightsCapturedAtShow()
    {
        SceneNode root = CreateCanvas(out _);
        float itemHeight = UIContextMenuComponent.ItemHeight;
        float separatorHeight = UIContextMenuComponent.SeparatorHeight;
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            ContextMenuItem[] items =
            [
                new ContextMenuItem("First", () => { }),
                ContextMenuItem.Separator(),
                new ContextMenuItem("Second", () => { }),
            ];
            void OnChanged(object? sender, IXRPropertyChangedEventArgs args)
            {
                if (args.PropertyName != nameof(UIContextMenuComponent.IsOpen))
                    return;
                menu.PropertyChanged -= OnChanged;
                // Callbacks during show must not change the rows the height describes.
                items[1] = new ContextMenuItem("Replaced", () => { });
                UIContextMenuComponent.ItemHeight = itemHeight * 2;
                UIContextMenuComponent.SeparatorHeight = separatorHeight * 3;
            }
            menu.PropertyChanged += OnChanged;

            menu.Show(new Vector2(8, 80), items);

            UIListTransform list = menu.SceneNode.FirstChild!.FirstChild!.GetTransformAs<UIListTransform>(true)!;
            float total = 0;
            foreach (UITransform child in list.Children)
                total += ((UIBoundableTransform)child).Height ?? 0;
            Rows(menu).Count.ShouldBe(2);
            total.ShouldBe(2 * itemHeight + separatorHeight);
            ((UIBoundableTransform)menu.UITransform).Height.ShouldBe(total);
        }
        finally
        {
            UIContextMenuComponent.ItemHeight = itemHeight;
            UIContextMenuComponent.SeparatorHeight = separatorHeight;
            root.Destroy(true);
        }
    }

    [Test]
    public void MenuMovedOffItsCanvasDuringShowCloses()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        UIContextMenuComponent menu = CreateMenu(root);
        SceneNode menuNode = menu.SceneNode;
        try
        {
            int dismissed = 0;
            menu.Dismissed += () => dismissed++;
            void OnChanged(object? sender, IXRPropertyChangedEventArgs args)
            {
                if (args.PropertyName != nameof(UIContextMenuComponent.IsOpen) || !menu.IsOpen)
                    return;
                menu.PropertyChanged -= OnChanged;
                menuNode.Transform.Parent = null;
            }
            menu.PropertyChanged += OnChanged;

            Should.NotThrow(() => menu.Show(new Vector2(8, 80), new ContextMenuItem("Action", () => { })));

            menu.IsOpen.ShouldBeFalse();
            dismissed.ShouldBe(1);
            input.SubscriberCount.ShouldBe(0);
            Rows(menu).ShouldBeEmpty();
        }
        finally
        {
            menuNode.Destroy(true);
            root.Destroy(true);
        }
    }

    [Test]
    public void SubmenuOpensAsSiblingFocusesFirstRowAndLeafClosesTheChain()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            int ran = 0;
            int dismissed = 0;
            menu.Dismissed += () => dismissed++;
            menu.Show(new Vector2(8, 80),
                new ContextMenuItem("Action", () => { }),
                ContextMenuItem.Submenu("More",
                [
                    new ContextMenuItem("Disabled", () => { }, false),
                    new ContextMenuItem("Leaf", () => ran++),
                ]));
            menu.UITransform.Visibility = EVisibility.Visible;
            input.PresentedFocusAccepted = true;
            UIContextMenuItemComponent more = Rows(menu)[1];

            menu.OpenItemSubmenu(more, more.ShowRevision, focusFirstItem: true).ShouldBeTrue();

            UIContextMenuComponent submenu = menu.OpenSubmenu.ShouldNotBeNull();
            SceneNode submenuNode = submenu.SceneNode;
            submenu.OwnerMenu.ShouldBeSameAs(menu);
            submenu.SceneNode.Parent.ShouldBeSameAs(menu.SceneNode.Parent);
            List<UIContextMenuItemComponent> subRows = Rows(submenu);
            subRows.Count.ShouldBe(1);
            // Rows of an unplaced submenu cannot take focus; placement retries the request.
            input.FocusedComponent.ShouldBeNull();
            submenu.UITransform.Visibility = EVisibility.Visible;
            submenu.FocusFirstItem().ShouldBeTrue();
            input.FocusedComponent.ShouldBeSameAs(subRows[0]);
            // The root handles dismissal for the whole chain.
            input.SubscriberCount.ShouldBe(4);

            subRows[0].OnInteract();

            ran.ShouldBe(1);
            menu.IsOpen.ShouldBeFalse();
            submenuNode.IsDestroyed.ShouldBeTrue();
            dismissed.ShouldBe(1);
            input.SubscriberCount.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void EscapeAndLeftCloseOnlyTheInnermostSubmenu()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            menu.Show(new Vector2(8, 80),
                ContextMenuItem.Submenu("More",
                [
                    ContextMenuItem.Submenu("Deeper", [new ContextMenuItem("Leaf", () => { })]),
                ]));
            menu.UITransform.Visibility = EVisibility.Visible;
            input.PresentedFocusAccepted = true;
            UIContextMenuItemComponent more = Rows(menu)[0];
            menu.OpenItemSubmenu(more, more.ShowRevision, focusFirstItem: true).ShouldBeTrue();
            UIContextMenuComponent first = menu.OpenSubmenu.ShouldNotBeNull();
            UIContextMenuItemComponent deeper = Rows(first)[0];
            first.OpenItemSubmenu(deeper, deeper.ShowRevision, focusFirstItem: true).ShouldBeTrue();
            UIContextMenuComponent second = first.OpenSubmenu.ShouldNotBeNull();
            SceneNode firstNode = first.SceneNode;
            SceneNode secondNode = second.SceneNode;

            input.RaiseEscape();

            secondNode.IsDestroyed.ShouldBeTrue();
            first.IsOpen.ShouldBeTrue();
            first.OpenSubmenu.ShouldBeNull();
            input.FocusedComponent.ShouldBeSameAs(deeper);

            first.CloseFromItem(deeper, deeper.ShowRevision).ShouldBeTrue();
            firstNode.IsDestroyed.ShouldBeTrue();
            menu.IsOpen.ShouldBeTrue();
            input.FocusedComponent.ShouldBeSameAs(more);

            input.RaiseEscape();
            menu.IsOpen.ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ReplacedShowClosesItsSubmenuAndRejectsStaleRows()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            ContextMenuItem[] children = [new ContextMenuItem("Leaf", () => { })];
            ContextMenuItem more = ContextMenuItem.Submenu("More", children);
            // The item keeps its own copy of the submenu rows.
            children[0] = new ContextMenuItem("Replaced", () => { });
            more.SubmenuItems[0].Label.ShouldBe("Leaf");
            menu.Show(new Vector2(8, 80), more);
            menu.UITransform.Visibility = EVisibility.Visible;
            UIContextMenuItemComponent stale = Rows(menu)[0];
            ulong staleRevision = stale.ShowRevision;
            menu.OpenItemSubmenu(stale, staleRevision, focusFirstItem: false).ShouldBeTrue();
            SceneNode oldSubmenuNode = menu.OpenSubmenu.ShouldNotBeNull().SceneNode;

            menu.Show(new Vector2(8, 80), more);

            oldSubmenuNode.IsDestroyed.ShouldBeTrue();
            menu.OpenSubmenu.ShouldBeNull();
            menu.OpenItemSubmenu(stale, staleRevision, focusFirstItem: false).ShouldBeFalse();
            menu.OpenSubmenu.ShouldBeNull();
            UIContextMenuItemComponent current = Rows(menu)[0];
            menu.OpenItemSubmenu(current, current.ShowRevision, focusFirstItem: false).ShouldBeTrue();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DestroyingTheCanvasWithAnOpenSubmenuDoesNotChangeTheHierarchyInTeardown()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        UIContextMenuComponent menu = CreateMenu(root);
        menu.Show(new Vector2(8, 80),
            ContextMenuItem.Submenu("More", [new ContextMenuItem("Leaf", () => { })]));
        menu.UITransform.Visibility = EVisibility.Visible;
        UIContextMenuItemComponent more = Rows(menu)[0];
        menu.OpenItemSubmenu(more, more.ShowRevision, focusFirstItem: false).ShouldBeTrue();

        Should.NotThrow(() => root.Destroy(true));

        root.IsDestroyed.ShouldBeTrue();
        input.SubscriberCount.ShouldBe(0);
    }

    private static bool Move(UIContextMenuComponent menu, UIContextMenuItemComponent from,
        int direction, bool toEdge = false)
        => menu.MoveItemFocus(from, from.ShowRevision, direction, toEdge);

    private static List<UIContextMenuItemComponent> Rows(UIContextMenuComponent menu)
    {
        List<UIContextMenuItemComponent> rows = [];
        Collect(menu.SceneNode, rows);
        return rows;
    }

    private static void Collect(SceneNode node, List<UIContextMenuItemComponent> rows)
    {
        if (node.GetComponent<UIContextMenuItemComponent>() is { IsEnabled: true } row)
            rows.Add(row);
        foreach (TransformBase child in node.Transform.Children)
            if (child.SceneNode is { } childNode)
                Collect(childNode, rows);
    }

    private static SceneNode CreateCanvas(out UITestCanvasInputProbe input)
    {
        UICanvasTransform canvas = new();
        canvas.SetSize(new Vector2(240, 120));
        SceneNode root = new("Menu canvas", canvas) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        input = root.AddComponent<UITestCanvasInputProbe>()!;
        root.IsActiveSelf = true;
        return root;
    }

    private static UIContextMenuComponent CreateMenu(SceneNode root)
    {
        SceneNode node = new(root) { Name = "Popup" };
        node.SetTransform<UIBoundableTransform>();
        return node.AddComponent<UIContextMenuComponent>()!;
    }
}
