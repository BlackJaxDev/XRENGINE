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
