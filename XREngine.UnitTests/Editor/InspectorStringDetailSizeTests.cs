using System.Numerics;
using NUnit.Framework;
using XREngine.Data.Rendering;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Rendering;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class InspectorStringDetailSizeTests
{
    [Test]
    public void AcceptedLayoutSizesExactDetailAndRetiredDetailIgnoresLaterResults()
    {
        using UITestShaderScope scope = new();
        FontGlyphSet font = new()
        {
            Glyphs = new Dictionary<string, FontGlyphSet.Glyph>
            {
                ["A"] = new(new Vector2(8), Vector2.Zero)
            },
            Atlas = new XRTexture2D(8, 8, EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba, EPixelType.UnsignedByte, true),
            AtlasType = EFontAtlasType.Mtsdf,
            LayoutEmSize = 8
        };
        SceneNode root = new("String detail canvas", new UICanvasTransform()) { IsActiveSelf = false };
        UICanvasTransform canvas = (UICanvasTransform)root.Transform;
        canvas.Width = 100;
        canvas.Height = 100;
        SceneNode host = new(root, "String detail");
        UIBoundableTransform bounds = host.SetTransform<UIBoundableTransform>();
        bounds.Width = 80;
        bounds.Height = 180;
        bounds.MaxAnchor = Vector2.Zero;
        try
        {
            SceneNode textNode = host.NewChild<UITextComponent>(out UITextComponent text);
            text.BoundableTransform.Width = 80;
            text.BoundableTransform.Height = 180;
            text.BoundableTransform.MaxAnchor = Vector2.Zero;
            text.Font = font;
            text.FontSize = 8;
            text.WrapMode = FontGlyphSet.EWrapMode.Word;
            text.Text = string.Join("\n", Enumerable.Repeat("A", 80));
            Pump();
            Assert.That(text.TryGetAcceptedLayoutResult(out UITextLayoutResult first, out _), Is.True);

            InspectorStringDetailSizeComponent owner = host.AddComponent<InspectorStringDetailSizeComponent>()!;
            owner.Bind(text, bounds);
            Assert.That(bounds.Height,
                Is.EqualTo(Math.Clamp(first.IntrinsicSize.Y + 12, 180, 20000)));
            Assert.That(bounds.Height, Is.GreaterThan(180));

            root.SetWorldContext(new TestWorldContext());
            root.IsActiveSelf = true;
            text.Text = "A";
            Pump();
            Assert.That(bounds.Height, Is.EqualTo(180));

            root.IsActiveSelf = false;
            text.Text = string.Join("\n", Enumerable.Repeat("A", 80));
            Pump();
            Assert.That(bounds.Height, Is.EqualTo(180));
            root.IsActiveSelf = true;
            Pump();
            Assert.That(bounds.Height, Is.GreaterThan(180));

            owner.Retire();
            float retiredHeight = bounds.Height ?? 0;
            text.Text = "A";
            Pump();
            Assert.That(bounds.Height, Is.EqualTo(retiredHeight));
            Assert.That(textNode.Parent, Is.SameAs(host));

            void Pump()
            {
                canvas.UpdateLayoutAsync();
                Assert.That(SpinWait.SpinUntil(() => canvas.PumpLayoutResults(),
                    TimeSpan.FromSeconds(5)), Is.True);
            }
        }
        finally
        {
            root.Destroy(true);
            font.Atlas?.Destroy(true);
            font.Destroy(true);
        }
    }
}
