using XREngine.Components;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Input.Devices;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UITabFocusRestoreTests
{
    private UITestShaderScope _shaderScope = null!;
    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown] public void RestoreShaderAssets() => _shaderScope.Dispose();

    [Test]
    public void ReorderedTabWaitsForAcceptedFocusBeforeRestoringItsStableId()
    {
        SceneNode root = CreateStrip(out UITabStripComponent strip, out UIListTransform list,
            out UITestCanvasInputProbe input);
        try
        {
            UITabButtonComponent original = list.Children[0].SceneNode!.GetComponent<UITabButtonComponent>()!;
            input.FocusedComponent = original;
            original.IsFocused = true;
            strip.SetTabs([new(2, "Console"), new(1, "Assets")], 2);
            UITabButtonComponent restored = list.Children[1].SceneNode!.GetComponent<UITabButtonComponent>()!;
            restored.IsFocused.ShouldBeFalse();
            Action retry = GetRestoreTick(strip);
            retry();
            input.PresentedFocusAttempts.ShouldBe(1);
            restored.IsFocused.ShouldBeFalse();

            input.PresentedFocusAccepted = true;
            retry();
            input.PresentedFocusAttempts.ShouldBe(2);
            input.FocusedComponent.ShouldBeSameAs(restored);
            restored.IsFocused.ShouldBeTrue();

            UITestTreeKeyRegistration keys = new();
            restored.RegisterInput(keys);
            input.IsCtrlHeld = true;
            input.IsShiftHeld = true;
            strip.MoveRequested += (id, direction) =>
            {
                id.ShouldBe(1UL);
                direction.ShouldBe(-1);
                strip.SetTabs([new(1, "Assets"), new(2, "Console")], 1);
            };
            keys.Press(EKey.PageUp).ShouldBeTrue();
            retry();
            UITabButtonComponent movedAgain = list.Children[0].SceneNode!.GetComponent<UITabButtonComponent>()!;
            input.FocusedComponent.ShouldBeSameAs(movedAgain);
            movedAgain.IsFocused.ShouldBeTrue();
            strip.SetTabs([new(1, "Assets", false), new(2, "Console")], 1);
            strip.SetTabs([new(1, "Assets"), new(2, "Console")], 1);
            input.FocusedComponent.ShouldBeSameAs(movedAgain);
            movedAgain.IsFocused.ShouldBeTrue();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void InterveningFocusIntentOrVirtualRequestCancelsTheRestore()
    {
        SceneNode root = CreateStrip(out UITabStripComponent strip, out UIListTransform list,
            out UITestCanvasInputProbe input);
        try
        {
            UITabButtonComponent original = list.Children[0].SceneNode!.GetComponent<UITabButtonComponent>()!;
            input.FocusedComponent = original;
            original.IsFocused = true;
            strip.SetTabs([new(2, "Console"), new(1, "Assets")], 2);
            UITabButtonComponent replacement = list.Children[1].SceneNode!.GetComponent<UITabButtonComponent>()!;
            input.FocusedComponent = replacement;
            input.FocusedComponent = original;
            input.PresentedFocusAccepted = true;
            Action retry = GetRestoreTick(strip);
            retry();
            input.PresentedFocusAttempts.ShouldBe(0);
            replacement.IsFocused.ShouldBeFalse();

            input.FocusedComponent = original;
            original.IsFocused = true;
            strip.SetTabs([new(1, "Assets"), new(2, "Console")], 1);
            input.HasPendingFocusRequest = true;
            retry();
            input.PresentedFocusAttempts.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void LostFocusReentryAppliesOnlyTheLatestTabRequest()
    {
        SceneNode root = CreateStrip(out UITabStripComponent strip, out UIListTransform list,
            out UITestCanvasInputProbe input);
        try
        {
            UITabButtonComponent original = list.Children[0].SceneNode!.GetComponent<UITabButtonComponent>()!;
            input.FocusedComponent = original;
            original.IsFocused = true;
            int reentries = 0;
            original.LostFocus += _ =>
            {
                reentries++;
                strip.SetTabs([new(3, "Three"), new(2, "Two")], 3);
            };

            strip.SetTabs([new(2, "Outer"), new(1, "Stale")], 2);

            reentries.ShouldBe(1);
            strip.Count.ShouldBe(2);
            strip.SelectedId.ShouldBe(3UL);
            UITabButtonComponent first = list.Children[0].SceneNode!.GetComponent<UITabButtonComponent>()!;
            UITabButtonComponent second = list.Children[1].SceneNode!.GetComponent<UITabButtonComponent>()!;
            first.AccessibilityLabel.ShouldBe("Three");
            second.AccessibilityLabel.ShouldBe("Two");
            input.PresentedFocusAccepted = true;
            GetRestoreTick(strip)();
            input.PresentedFocusAttempts.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    private static SceneNode CreateStrip(out UITabStripComponent strip, out UIListTransform list,
        out UITestCanvasInputProbe input)
    {
        SceneNode root = new("Tab focus", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        _ = root.AddComponent<UICanvasComponent>()!;
        input = root.AddComponent<UITestCanvasInputProbe>()!;
        SceneNode tabs = new(root);
        list = tabs.SetTransform<UIListTransform>();
        strip = tabs.AddComponent<UITabStripComponent>()!;
        root.IsActiveSelf = true;
        strip.SetTabs([new(1, "Assets"), new(2, "Console")], 1);
        return root;
    }

    private static Action GetRestoreTick(UITabStripComponent strip)
        => typeof(UITabStripComponent).GetMethod("RestoreTabFocusTick",
            BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action>(strip);
}
