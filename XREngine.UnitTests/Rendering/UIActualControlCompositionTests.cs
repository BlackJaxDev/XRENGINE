using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIActualControlCompositionTests
{
    [Test]
    public void TextCaretSelectionAndButtonHoverPreserveChromeSegments()
    {
        using UITestShaderScope shaders = new();
        UICanvasTransform transform = new() { Width = 400, Height = 240 };
        SceneNode root = new("Control composition", transform);
        root.SetWorldContext(new TestWorldContext());
        List<UIPublication> held = [];
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            SceneNode firstChrome = AddSolid(root, "First chrome", 0, 0, 300, 20,
                EUICompositionLayerKind.StaticChrome);
            SceneNode overlay = new(root) { Name = "Controls" };
            UIBoundableTransform overlayBounds = overlay.SetTransform<UIBoundableTransform>();
            overlayBounds.Width = 300;
            overlayBounds.Height = 120;
            overlay.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.Overlay;

            SceneNode field = new(overlay) { Name = "Text input" };
            UIBoundableTransform fieldBounds = field.SetTransform<UIBoundableTransform>();
            fieldBounds.Width = 180;
            fieldBounds.Height = 24;
            fieldBounds.Translation = new Vector2(0, 25);
            UITextComponent text = field.AddComponent<UITextComponent>()!;
            text.FontSize = 12;
            UITextInputComponent input = field.AddComponent<UITextInputComponent>()!;
            input.Text = "abcd";
            input.IsFocused = true;

            SceneNode buttonNode = new(overlay) { Name = "Hover button" };
            UIBoundableTransform buttonBounds = buttonNode.SetTransform<UIBoundableTransform>();
            buttonBounds.Width = 80;
            buttonBounds.Height = 24;
            buttonBounds.Translation = new Vector2(0, 55);
            UIButtonComponent button = buttonNode.AddComponent<UIButtonComponent>()!;
            UIMaterialComponent buttonPaint = buttonNode.AddComponent<UIMaterialComponent>()!;
            buttonPaint.SetBatchSolidColor(ColorF4.Gray);
            button.DefaultBackgroundColor = ColorF4.Gray;
            button.HighlightBackgroundColor = ColorF4.White;

            SceneNode lastChrome = AddSolid(root, "Last chrome", 0, 140, 300, 20,
                EUICompositionLayerKind.StaticChrome);
            UIPublication baseline = Capture(canvas, transform, held, () =>
                text.TryGetCurrentLayoutResult(out _));
            BaselineChrome(baseline, firstChrome, lastChrome);
            UICompositionOwner overlayOwner = new(overlayBounds.ElementIdentity,
                EUICompositionLayerKind.Overlay);

            input.Text = "abcde";
            UIPublication typed = Capture(canvas, transform, held, () =>
                text.TryGetCurrentLayoutResult(out var layout) && layout.Glyphs.Length == 5);
            AssertChromeUnchanged(baseline, typed);
            AssertOwnerChanged(baseline, typed, overlayOwner);

            input.CursorPosition = 2;
            input.SelectionAnchor = 2;
            UpdateEditOverlay(input);
            UIPublication caret = Capture(canvas, transform, held);
            AssertChromeUnchanged(baseline, caret);
            AssertOwnerChanged(typed, caret, overlayOwner);
            caret.Renders.ToArray().Any(record => record.Visible &&
                record.Identity == CaretIdentity(input)).ShouldBeTrue();

            input.SelectionAnchor = 0;
            UpdateEditOverlay(input);
            UIPublication selected = Capture(canvas, transform, held);
            AssertChromeUnchanged(baseline, selected);
            AssertOwnerChanged(caret, selected, overlayOwner);
            UIBoundableTransform selection = ((List<UIBoundableTransform>)typeof(UITextInputComponent)
                .GetField("_selectionOverlays", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(input)!).Single();
            FindRecord(selected, selection.ElementIdentity).Visible.ShouldBeTrue();

            button.IsMouseDirectlyOver = true;
            UIPublication hovered = Capture(canvas, transform, held);
            AssertChromeUnchanged(baseline, hovered);
            AssertOwnerChanged(selected, hovered, overlayOwner);
            FindRecord(hovered, buttonBounds.ElementIdentity).QuadColor.ShouldNotBe(
                FindRecord(selected, buttonBounds.ElementIdentity).QuadColor);
        }
        finally
        {
            foreach (UIPublication publication in held)
                publication.Release();
            root.Destroy(true);
        }
    }

    [Test]
    public void VirtualListScrollPreservesChromeSegments()
    {
        using UITestShaderScope shaders = new();
        UICanvasTransform transform = new() { Width = 400, Height = 240 };
        SceneNode root = new("Virtual composition", transform);
        root.SetWorldContext(new TestWorldContext());
        List<UIPublication> held = [];
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            SceneNode firstChrome = AddSolid(root, "First chrome", 0, 0, 300, 20,
                EUICompositionLayerKind.StaticChrome);
            SceneNode listNode = new(root) { Name = "Virtual rows" };
            UIListTransform list = listNode.SetTransform<UIListTransform>();
            list.Width = 300;
            list.Height = 60;
            list.ItemSize = 20;
            list.VirtualOverscan = 0;
            listNode.AddComponent<UICompositionLayerComponent>()!.Kind =
                EUICompositionLayerKind.ScrollingContent;
            PaintedRows provider = new(listNode, 100);
            list.VirtualProvider = provider;
            list.SynchronizeVirtualRows(60);
            SceneNode lastChrome = AddSolid(root, "Last chrome", 0, 140, 300, 20,
                EUICompositionLayerKind.StaticChrome);
            UIPublication baseline = Capture(canvas, transform, held);
            BaselineChrome(baseline, firstChrome, lastChrome);
            list.VirtualFirstIndex.ShouldBe(0);
            list.TryGetRealizedVirtualRow(1, out _, out _).ShouldBeTrue();

            list.ContentScrollOffset = 80;
            list.SynchronizeVirtualRows(60);
            UIPublication scrolled = Capture(canvas, transform, held);
            AssertChromeUnchanged(baseline, scrolled);
            list.VirtualFirstIndex.ShouldBeGreaterThan(0);
            list.VirtualScrollOffset.ShouldBeGreaterThan(0);
            list.TryGetRealizedVirtualRow(1, out _, out _).ShouldBeFalse();
            UICompositionOwner rowsOwner = new(list.ElementIdentity,
                EUICompositionLayerKind.ScrollingContent);
            AssertOwnerChanged(baseline, scrolled, rowsOwner);

            provider.AdvanceRevision();
            list.SynchronizeVirtualRows(60);
            UIPublication rebound = Capture(canvas, transform, held);
            AssertChromeUnchanged(baseline, rebound);
            AssertOwnerChanged(scrolled, rebound, rowsOwner);
        }
        finally
        {
            foreach (UIPublication publication in held)
                publication.Release();
            root.Destroy(true);
        }
    }

    private static SceneNode AddSolid(SceneNode parent, string name, float x, float y,
        float width, float height, EUICompositionLayerKind kind)
    {
        SceneNode node = new(parent) { Name = name };
        UIBoundableTransform bounds = node.SetTransform<UIBoundableTransform>();
        bounds.Translation = new Vector2(x, y);
        bounds.Width = width;
        bounds.Height = height;
        node.AddComponent<UICompositionLayerComponent>()!.Kind = kind;
        node.AddComponent<UIMaterialComponent>()!.SetBatchSolidColor(ColorF4.Gray);
        return node;
    }

    private static UIPublication Capture(UICanvasComponent canvas, UICanvasTransform transform,
        List<UIPublication> held, Func<bool>? ready = null)
    {
        transform.UpdateLayoutAsync();
        MethodInfo capture = typeof(UICanvasComponent).GetMethod("CapturePublicationCandidate",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo candidateField = typeof(UICanvasComponent).GetField("_candidatePublication",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        UIPublication? publication = null;
        bool captured = SpinWait.SpinUntil(() =>
        {
            transform.PumpLayoutResults();
            if (transform.IsLayoutInvalidated && !transform.IsUpdatingLayout)
                transform.UpdateLayoutAsync();
            if (transform.CurrentLayoutGeneration == 0 || transform.IsLayoutInvalidated ||
                transform.IsUpdatingLayout || !(ready?.Invoke() ?? true))
                return false;
            capture.Invoke(canvas, null);
            publication = (UIPublication?)candidateField.GetValue(canvas);
            return publication is not null &&
                publication.LayoutGeneration == transform.CurrentLayoutGeneration &&
                publication.ChangeRevision == transform.ChangeJournal.Revision;
        }, TimeSpan.FromSeconds(5));
        if (!captured || publication is null)
            throw new AssertionException("No current publication was captured: " +
                (typeof(UICanvasComponent).GetField("_lastPublicationCaptureFailure",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas) ??
                 "layout or painter-order admission did not settle") +
                $"; layout={transform.CurrentLayoutGeneration} invalid={transform.IsLayoutInvalidated}" +
                $" updating={transform.IsUpdatingLayout} journal={transform.ChangeJournal.Revision}" +
                $" candidate={publication?.LayoutGeneration}/{publication?.ChangeRevision}");
        publication.Retain();
        held.Add(publication);
        return publication;
    }

    private static void BaselineChrome(UIPublication publication, params SceneNode[] nodes)
    {
        UICompositionSegment[] chrome = Chrome(publication);
        chrome.Length.ShouldBe(nodes.Length);
        for (int index = 0; index < nodes.Length; index++)
            chrome[index].Owner.Identity.ShouldBe(((UITransform)nodes[index].Transform).ElementIdentity);
    }

    private static void AssertChromeUnchanged(UIPublication before, UIPublication after)
    {
        UICompositionSegment[] prior = Chrome(before);
        UICompositionSegment[] current = Chrome(after);
        current.Length.ShouldBe(prior.Length);
        for (int index = 0; index < prior.Length; index++)
        {
            current[index].Owner.ShouldBe(prior[index].Owner);
            current[index].Identity.ShouldBe(prior[index].Identity);
            current[index].Revision.ShouldBe(prior[index].Revision);
            current[index].Records.SequenceEqual(prior[index].Records).ShouldBeTrue();
            foreach (ref readonly UIRenderRecord record in current[index].Records)
            {
                UIRenderRecord previous = FindRecord(before, record.Identity);
                record.RenderPass.ShouldBe(previous.RenderPass);
                record.ZIndex.ShouldBe(previous.ZIndex);
            }
        }
    }

    private static UICompositionSegment[] Chrome(UIPublication publication)
        => publication.CompositionSegments.ToArray().Where(segment =>
            segment.Owner.Kind == EUICompositionLayerKind.StaticChrome).ToArray();

    private static void AssertOwnerChanged(UIPublication before, UIPublication after,
        UICompositionOwner owner)
    {
        UICompositionSegment[] prior = before.CompositionSegments.ToArray()
            .Where(segment => segment.Owner == owner).ToArray();
        UICompositionSegment[] current = after.CompositionSegments.ToArray()
            .Where(segment => segment.Owner == owner).ToArray();
        prior.Length.ShouldBeGreaterThan(0);
        current.Length.ShouldBeGreaterThan(0);
        current.SelectMany(segment => segment.Records.ToArray()).SequenceEqual(
            prior.SelectMany(segment => segment.Records.ToArray())).ShouldBeFalse();
    }

    private static UIRenderRecord FindRecord(UIPublication publication, UIElementIdentity identity)
    {
        foreach (ref readonly UIRenderRecord record in publication.Renders)
            if (record.Identity == identity)
                return record;
        throw new AssertionException("The expected control record was not captured.");
    }

    private static UIElementIdentity CaretIdentity(UITextInputComponent input)
    {
        input.TryGetCaretOverlayPublicationKey(out _, out UIElementIdentity identity)
            .ShouldBeTrue();
        return identity;
    }

    private static void UpdateEditOverlay(UITextInputComponent input)
        => typeof(UITextInputComponent).GetMethod("UpdateEditOverlay",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(input, null);

    private sealed class PaintedRows(SceneNode parent, int count) : IVirtualListProvider
    {
        public int Count => count;
        public ulong Revision { get; private set; } = 1;
        public ulong GetItemId(int index) => (ulong)index + 1;
        public void AdvanceRevision() => Revision++;

        public UIBoundableTransform CreateRow()
        {
            UIBoundableTransform bounds = new();
            SceneNode node = new(parent, "Painted row", bounds);
            node.AddComponent<UIMaterialComponent>()!.SetBatchSolidColor(ColorF4.Gray);
            return bounds;
        }

        public void BindRow(UIBoundableTransform row, int index, ulong itemId, ulong incarnation)
        {
            row.Width = 300;
            row.Height = 20;
            row.SceneNode!.GetComponent<UIMaterialComponent>()!.SetBatchSolidColor(
                new ColorF4(itemId / (float)count, Revision == 1 ? 0.2f : 0.8f, 0.5f, 1));
        }

        public void UnbindRow(UIBoundableTransform row, ulong itemId, ulong incarnation) { }
    }
}
