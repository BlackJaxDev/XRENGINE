using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

/// <summary>Checks pointer caret placement from the text layout frozen in the hit packet.</summary>
[TestFixture, NonParallelizable]
public sealed class UITextPointerCaretTests
{
    private UITestShaderScope _shaderScope = null!;
    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown] public void RestoreShaderAssets() => _shaderScope.Dispose();

    [Test]
    public void PointHitSelectsNearestLineThenNearestGraphemeBoundary()
    {
        // "e" plus a combining acute accent is one grapheme, so offset 1 is not a boundary.
        UITextCaretStop[] stops =
        [
            new(0, 0, new Vector2(0, 20), 20), new(1, 0, new Vector2(5, 20), 20),
            new(2, 0, new Vector2(10, 20), 20), new(3, 0, new Vector2(15, 20), 20),
            new(3, 1, new Vector2(0, 40), 20), new(4, 1, new Vector2(10, 40), 20),
        ];
        int[] boundaries = [0, 2, 3];

        UITextInputComponent.FindCaretOffsetAtPoint(stops, new Vector2(6, 10), boundaries, 4)
            .ShouldBe(2);
        UITextInputComponent.FindCaretOffsetAtPoint(stops, new Vector2(9, 30), boundaries, 4)
            .ShouldBe(4);
        // A point outside every line uses the nearest line.
        UITextInputComponent.FindCaretOffsetAtPoint(stops, new Vector2(-50, -100), boundaries, 4)
            .ShouldBe(0);
        UITextInputComponent.FindCaretOffsetAtPoint([], Vector2.Zero, boundaries, 4).ShouldBe(-1);
    }

    [Test]
    public void PressAndDragSelectFromPressPacketLayout()
    {
        SceneNode root = new("Pointer caret", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        try
        {
            _ = root.AddComponent<UICanvasComponent>()!;
            SceneNode field = new(root);
            field.SetTransform<UIBoundableTransform>();
            field.AddComponent<UITextComponent>();
            UITextInputComponent input = field.AddComponent<UITextInputComponent>()!;
            input.Binding = new UIValueState<string>("hello world");
            root.IsActiveSelf = true;
            input.IsFocused = true;
            input.CursorPosition = 0;
            input.SelectionAnchor = 0;
            input.NeedsMouseMove.ShouldBeTrue();

            UITextCaretStop[] stops = new UITextCaretStop[input.Text.Length + 1];
            for (int offset = 0; offset < stops.Length; offset++)
                stops[offset] = new(offset, 0, new Vector2(offset * 10, 20), 20);
            UITextLayoutResult layout = new(new Vector2(110, 20), [], stops, 1, 1);
            UIPointerGeometry pressed = new(new Vector2(200, 20),
                Metadata: new UITextCaretHitMetadata(input, layout, input.Text));

            input.PointerPressed(new Vector2(42, 10), in pressed);
            input.IsPressed.ShouldBeTrue();
            input.CursorPosition.ShouldBe(4);
            input.SelectionAnchor.ShouldBe(4);

            // Later hits carry no layout; the drag keeps the press packet's layout.
            input.MouseMoved(new Vector2(42, 10), new Vector2(71, 10), default);
            input.CursorPosition.ShouldBe(7);
            input.SelectionAnchor.ShouldBe(4);
            input.PointerReleased(new Vector2(89, 10), true, default);
            input.SelectionStart.ShouldBe(4);
            input.SelectionEnd.ShouldBe(9);
            input.IsPressed.ShouldBeFalse();

            // A move after release does not change the selection.
            input.MouseMoved(new Vector2(89, 10), new Vector2(10, 10), default);
            input.SelectionEnd.ShouldBe(9);

            // An edit changes the text, so the old layout cannot place the caret.
            input.CursorPosition = input.Text.Length;
            input.SelectionAnchor = input.CursorPosition;
            input.UserAddText("!");
            int caret = input.CursorPosition;
            input.PointerPressed(new Vector2(12, 10), in pressed);
            input.CursorPosition.ShouldBe(caret);
            input.SelectionAnchor.ShouldBe(caret);
            input.PointerCancelled();

            // Metadata of another field is rejected.
            UITextInputComponent other = new SceneNode(root).AddComponent<UITextInputComponent>()!;
            UIPointerGeometry foreign = new(new Vector2(200, 20),
                Metadata: new UITextCaretHitMetadata(other, layout, input.Text));
            input.PointerPressed(new Vector2(12, 10), in foreign);
            input.CursorPosition.ShouldBe(caret);
            input.PointerCancelled();
        }
        finally { root.Destroy(true); }
    }
}
