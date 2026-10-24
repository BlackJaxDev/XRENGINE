using XREngine.Components;
using NUnit.Framework;
using Shouldly;
using XREngine.Input.Devices;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UITabKeyboardTests
{
    private UITestShaderScope _shaderScope = null!;
    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown] public void RestoreShaderAssets() => _shaderScope.Dispose();

    [Test]
    public void FocusedTabShortcutsUseTheCurrentSlotIdAndValidMoveBounds()
    {
        SceneNode root = new("Tab shortcuts", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        try
        {
            _ = root.AddComponent<UICanvasComponent>()!;
            UITestCanvasInputProbe input = root.AddComponent<UITestCanvasInputProbe>()!;
            SceneNode tabNode = new(root);
            UIListTransform list = tabNode.SetTransform<UIListTransform>();
            UITabStripComponent strip = tabNode.AddComponent<UITabStripComponent>()!;
            root.IsActiveSelf = true;
            strip.SetTabs([new(1, "Assets"), new(2, "Console")], 1);
            UITabButtonComponent first = list.Children[0].SceneNode!.GetComponent<UITabButtonComponent>()!;
            UITestTreeKeyRegistration keys = new();
            first.RegisterInput(keys);
            ulong closed = 0;
            ulong moved = 0;
            int direction = 0;
            strip.CloseRequested += id => closed = id;
            strip.MoveRequested += (id, step) => { moved = id; direction = step; };

            first.IsFocused = true;
            keys.Press(EKey.W).ShouldBeTrue();
            closed.ShouldBe(0UL);
            input.IsCtrlHeld = true;
            keys.Press(EKey.W).ShouldBeTrue();
            closed.ShouldBe(1UL);
            input.IsShiftHeld = true;
            keys.Press(EKey.PageUp).ShouldBeTrue();
            moved.ShouldBe(0UL);
            keys.Press(EKey.PageDown).ShouldBeTrue();
            moved.ShouldBe(1UL);
            direction.ShouldBe(1);

            strip.SetTabs([new(2, "Console"), new(1, "Assets")], 2);
            first.InteractionRevision.ShouldBeGreaterThan(0);
            first.IsFocused = true;
            keys.Press(EKey.W).ShouldBeTrue();
            closed.ShouldBe(2UL);
            keys.Press(EKey.PageDown).ShouldBeTrue();
            moved.ShouldBe(2UL);
            direction.ShouldBe(1);

            first.IsFocused = false;
            keys.Press(EKey.W).ShouldBeTrue();
            closed.ShouldBe(2UL);
        }
        finally { root.Destroy(true); }
    }
}
