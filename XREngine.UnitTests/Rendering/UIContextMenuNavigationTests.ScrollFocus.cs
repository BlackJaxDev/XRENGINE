using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Input.Devices;
using XREngine.Data.Core;

namespace XREngine.UnitTests.Rendering;

public sealed partial class UIContextMenuNavigationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void KeyboardEndScrollsToItsTargetAndWaitsForPresentedFocus(bool extraRows)
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateLongMenu(root, out UIScrollableTransform scroll, extraRows);
            List<UIContextMenuItemComponent> rows = Rows(menu);
            FocusSource(input, rows[0]);

            UITestTreeKeyRegistration keys = new();
            rows[0].RegisterInput(keys);
            keys.Press(EKey.End).ShouldBeTrue();

            scroll.ScrollOffset.Y.ShouldBe(scroll.MaxScrollOffset.Y);
            input.PresentedFocusAttempts.ShouldBe(1);
            input.FocusedComponent.ShouldBeSameAs(rows[0]);
            UICanvasTransform canvas = (UICanvasTransform)root.Transform;
            ulong generation = canvas.CurrentLayoutGeneration;
            canvas.UpdateLayoutAsync();
            SpinWait.SpinUntil(() =>
            {
                canvas.PumpLayoutResults();
                return canvas.CurrentLayoutGeneration > generation;
            }, TimeSpan.FromSeconds(5)).ShouldBeTrue();
            input.PresentedFocusAccepted = true;
            NavigationTick(menu)();
            input.FocusedComponent.ShouldBeSameAs(rows[^1]);

            input.PresentedFocusAccepted = false;
            Move(menu, rows[^1], -1, toEdge: true).ShouldBeTrue();
            scroll.ScrollOffset.Y.ShouldBe(0);
            input.FocusedComponent.ShouldBeSameAs(rows[^1]);
            input.PresentedFocusAccepted = true;
            NavigationTick(menu)();
            input.FocusedComponent.ShouldBeSameAs(rows[0]);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void RepeatedArrowRequestsAdvanceThePendingTarget()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateLongMenu(root, out _);
            List<UIContextMenuItemComponent> rows = Rows(menu);
            FocusSource(input, rows[0]);

            Move(menu, rows[0], 1).ShouldBeTrue();
            Move(menu, rows[0], 1).ShouldBeTrue();
            input.FocusedComponent.ShouldBeSameAs(rows[0]);
            input.PresentedFocusAccepted = true;
            NavigationTick(menu)();

            input.FocusedComponent.ShouldBeSameAs(rows[2]);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ANewerFocusIntentCancelsPendingMenuNavigation()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateLongMenu(root, out _);
            List<UIContextMenuItemComponent> rows = Rows(menu);
            FocusSource(input, rows[0]);
            Move(menu, rows[0], 1).ShouldBeTrue();
            int attempts = input.PresentedFocusAttempts;
            input.FocusedComponent = rows[2];
            input.FocusedComponent = rows[0];
            input.PresentedFocusAccepted = true;

            NavigationTick(menu)();

            input.PresentedFocusAttempts.ShouldBe(attempts);
            input.FocusedComponent.ShouldBeSameAs(rows[0]);
        }
        finally { root.Destroy(true); }
    }

    [TestCase("virtual-focus")]
    [TestCase("target")]
    [TestCase("menu")]
    [TestCase("canvas")]
    [TestCase("scroll")]
    [TestCase("hide")]
    [TestCase("replace-show")]
    [TestCase("deactivate")]
    [TestCase("destroy-target")]
    [TestCase("destroy-source")]
    [TestCase("input")]
    public void RetiredNavigationOwnershipStopsFocusRetries(string change)
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateLongMenu(root, out UIScrollableTransform scroll);
            List<UIContextMenuItemComponent> rows = Rows(menu);
            FocusSource(input, rows[0]);
            Move(menu, rows[0], 1).ShouldBeTrue();
            int attempts = input.PresentedFocusAttempts;
            switch (change)
            {
                case "virtual-focus": input.HasPendingFocusRequest = true; break;
                case "target": rows[1].Generate(); break;
                case "menu": menu.Generate(); break;
                case "canvas": root.GetComponent<XREngine.Components.UICanvasComponent>()!.Generate(); break;
                case "scroll": scroll.Generate(); break;
                case "hide": menu.Hide(); break;
                case "replace-show": menu.Show(Vector2.Zero, new ContextMenuItem("Replacement", () => { })); break;
                case "deactivate": menu.SceneNode.IsActiveSelf = false; break;
                case "destroy-target": rows[1].Destroy(true); break;
                case "destroy-source": rows[0].Destroy(true); break;
                case "input": input.Generate(); break;
            }
            input.PresentedFocusAccepted = true;

            NavigationTick(menu)();
            NavigationTick(menu)();

            input.PresentedFocusAttempts.ShouldBe(attempts);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void NavigationStopsAfterItsRetryBudget()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateLongMenu(root, out _);
            List<UIContextMenuItemComponent> rows = Rows(menu);
            FocusSource(input, rows[0]);
            Move(menu, rows[0], 1).ShouldBeTrue();
            Action retry = NavigationTick(menu);
            for (int tick = 0; tick < 121; tick++)
                retry();
            int attempts = input.PresentedFocusAttempts;
            attempts.ShouldBe(121);
            input.PresentedFocusAccepted = true;
            retry();
            input.PresentedFocusAttempts.ShouldBe(attempts);
            input.FocusedComponent.ShouldBeSameAs(rows[0]);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ScrollCallbackCanReplaceThePendingTarget()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateLongMenu(root, out UIScrollableTransform scroll);
            List<UIContextMenuItemComponent> rows = Rows(menu);
            FocusSource(input, rows[0]);
            void OnScroll(object? sender, IXRPropertyChangedEventArgs args)
            {
                if (args.PropertyName != nameof(UIScrollableTransform.ScrollOffset))
                    return;
                scroll.PropertyChanged -= OnScroll;
                Move(menu, rows[0], 1).ShouldBeTrue();
            }
            scroll.PropertyChanged += OnScroll;

            Move(menu, rows[0], 1, toEdge: true).ShouldBeFalse();
            input.PresentedFocusAccepted = true;
            NavigationTick(menu)();

            input.FocusedComponent.ShouldBeSameAs(rows[1]);
            scroll.ScrollOffset.Y.ShouldBe(UIContextMenuComponent.ItemHeight);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void WaitingForPresentationDoesNotAllocateOnEachRetry()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateLongMenu(root, out _);
            List<UIContextMenuItemComponent> rows = Rows(menu);
            FocusSource(input, rows[0]);
            Move(menu, rows[0], 1).ShouldBeTrue();
            Action retry = NavigationTick(menu);
            retry();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int tick = 0; tick < 20; tick++)
                retry();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

            bytes.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void StaleKeyboardFocusFlagCannotStartNavigation()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateLongMenu(root, out _);
            List<UIContextMenuItemComponent> rows = Rows(menu);
            FocusSource(input, rows[0]);
            UITestTreeKeyRegistration keys = new();
            rows[0].RegisterInput(keys);
            input.FocusedComponent = rows[2];
            input.PresentedFocusAccepted = true;

            keys.Press(EKey.Down).ShouldBeTrue();
            NavigationTick(menu)();

            input.PresentedFocusAttempts.ShouldBe(0);
            input.FocusedComponent.ShouldBeSameAs(rows[2]);
        }
        finally { root.Destroy(true); }
    }

    private static void FocusSource(UITestCanvasInputProbe input, UIContextMenuItemComponent source)
    {
        input.FocusedComponent = source;
        source.IsFocused = true;
    }

    private static UIContextMenuComponent CreateLongMenu(SceneNode root, out UIScrollableTransform scroll,
        bool extraRows = false)
    {
        UIContextMenuComponent menu = CreateMenu(root);
        List<ContextMenuItem> items = [];
        for (int index = 0; index < 16; index++)
        {
            if (extraRows && index == 8)
            {
                items.Add(ContextMenuItem.Separator());
                items.Add(new ContextMenuItem("Disabled", () => { }, enabled: false));
            }
            items.Add(new ContextMenuItem($"Item {index}", () => { }));
        }
        menu.Show(new Vector2(8, 110), items.ToArray());
        UICanvasTransform canvas = (UICanvasTransform)root.Transform;
        UIScrollableTransform contentScroll = (UIScrollableTransform)menu.SceneNode.FirstChild!.Transform;
        Action place = typeof(UIContextMenuComponent).GetMethod("CompletePlacement",
            BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action>(menu);
        canvas.UpdateLayoutAsync();
        SpinWait.SpinUntil(() =>
        {
            canvas.PumpLayoutResults();
            place();
            return menu.UITransform.Visibility == EVisibility.Visible &&
                contentScroll.ViewportExtent.Y == 120 && contentScroll.MaxScrollOffset.Y > 0;
        }, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        scroll = contentScroll;
        return menu;
    }

    private static Action NavigationTick(UIContextMenuComponent menu)
        => typeof(UIContextMenuComponent).GetMethod("NavigationFocusTick",
            BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action>(menu);
}
