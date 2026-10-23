using NUnit.Framework;
using System.Numerics;
using Shouldly;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI;
using XREngine.Rendering;
using XREngine.Rendering.UI.State;
using XREngine.Runtime.InputIntegration;
using XREngine.Components;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIRetainedControlsTests
{
    private UITestShaderScope _shaderScope = null!;
    private readonly List<SceneNode> _ownedRoots = [];
    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown]
    public void RestoreShaderAssets()
    {
        List<Exception>? failures = null;
        try
        {
            for (int index = _ownedRoots.Count - 1; index >= 0; index--)
                try { _ownedRoots[index].Destroy(true); }
                catch (Exception error) { (failures ??= []).Add(error); }
            _ownedRoots.Clear();
        }
        finally
        {
            _shaderScope.Dispose();
        }
        if (failures is not null)
            throw new AggregateException("Focused input test roots failed to destroy.", failures);
    }

    [Test]
    public void TypedValueState_ReportsOnlyActualChanges()
    {
        var state = new UIValueState<int>(3);
        var changes = new List<int>();
        state.ValueChanged += changes.Add;

        state.SetValue(3);
        state.SetValue(4);
        state.SetValue(4);

        state.Value.ShouldBe(4);
        changes.ShouldBe(new List<int> { 4 });
    }

    [Test]
    public void TabStrip_ReusesSlotsWithoutTransferringSelectionToAnotherId()
    {
        SceneNode root = new("Tab strip", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode tabsNode = new(root) { Name = "Tabs" };
        UIListTransform list = tabsNode.SetTransform<UIListTransform>();
        UITabStripComponent tabs = tabsNode.AddComponent<UITabStripComponent>()!;
        root.IsActiveSelf = true;
        try
        {
            tabs.SetTabs([new(1, "Assets"), new(2, "Console")], 1);
            UITabButtonComponent first = list.Children[0].SceneNode!.GetComponent<UITabButtonComponent>()!;
            first.IsSelected.ShouldBeTrue();
            long revision = first.InteractionRevision;

            tabs.SetTabs([new(2, "Console"), new(1, "Assets", false)], 2);
            list.Children[0].SceneNode!.GetComponent<UITabButtonComponent>().ShouldBeSameAs(first);
            first.InteractionRevision.ShouldBeGreaterThan(revision);
            first.IsSelected.ShouldBeTrue();
            tabs.SelectedId.ShouldBe(2UL);
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void DirectionalNavigation_EditsFocusedControlsBeforeLeavingThem()
    {
        UITextInputComponent input = CreateFocusedInput(out _);
        input.SingleLineMode = true;
        input.Text = "A\U0001F1FA\U0001F1F8B";
        input.CursorPosition = 1;
        input.TryConsumeFocusDirection(Vector2.UnitX).ShouldBeTrue();
        input.CursorPosition.ShouldBe(5);
        input.TryConsumeFocusDirection(Vector2.UnitY).ShouldBeFalse();

        SceneNode root = new("Navigation controls", new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode sliderNode = new(root) { Name = "Slider" };
        sliderNode.SetTransform<UIBoundableTransform>();
        UIRangeSliderComponent slider = sliderNode.AddComponent<UIRangeSliderComponent>()!;
        SceneNode comboNode = new(root) { Name = "Combo" };
        comboNode.SetTransform<UIBoundableTransform>();
        UIComboBoxComponent combo = comboNode.AddComponent<UIComboBoxComponent>()!;
        combo.SetOptions([new(1, "First"), new(2, "Second")]);
        root.IsActiveSelf = true;

        slider.TryConsumeFocusDirection(Vector2.UnitX).ShouldBeTrue();
        slider.Value.ShouldBeGreaterThan(0);
        slider.TryConsumeFocusDirection(Vector2.UnitY).ShouldBeFalse();
        combo.TryConsumeFocusDirection(Vector2.UnitY).ShouldBeTrue();
        combo.SelectedId.ShouldBe(1UL);
        combo.TryConsumeFocusDirection(Vector2.UnitX).ShouldBeFalse();
    }

    [Test]
    public void MultilineCaret_MovesByPlacedLinesAndRejectsStaleTextGeometry()
    {
        UITextInputComponent input = CreateFocusedInput(out _);
        input.SingleLineMode = false;
        input.Text = "AB\nA";
        UITextLayoutInput captured = input.TextComponent.CaptureLayoutInput().ShouldNotBeNull();
        UITextLayoutResult layout = UITextLayoutKernel.Measure(captured, new Vector2(200, 100));
        UITextLayoutResult changedWidth = UITextLayoutKernel.Measure(captured, new Vector2(20, 100));
        input.CursorPosition = 2;

        int[] clusters = System.Globalization.StringInfo.ParseCombiningCharacters(input.Text);
        UITextInputComponent.FindNearestCaretOffset(layout.Carets.Span, 1, 200, clusters, input.Text.Length)
            .ShouldBe(4);
        UITextInputComponent.FindNearestCaretOffset(layout.Carets.Span, 0, 200, clusters, input.Text.Length)
            .ShouldBe(2);
        changedWidth.TextRevision.ShouldBe(layout.TextRevision);
        input.TryMoveCaretVertically(changedWidth, 1).ShouldBeFalse();
        input.CursorPosition.ShouldBe(2);

        input.Text = "Changed";
        input.TryMoveCaretVertically(layout, 1).ShouldBeFalse();
    }

    [Test]
    public void RadioSelection_PreservesLogicalIdUntilOptionRemoval()
    {
        var state = new UIValueState<ulong?>(42);
        using var group = new UIRadioGroupState { Binding = state };

        group.SelectedId.ShouldBe(42UL);
        group.RemoveOption(42);

        group.SelectedId.ShouldBeNull();
        state.Value.ShouldBeNull();
    }

    [Test]
    public void RadioBinding_ReentrantReplacementKeepsReplacementSelection()
    {
        UIValueState<ulong?> original = new(7);
        UIValueState<ulong?> replacement = new(9);
        using UIRadioGroupState group = new() { Binding = original };
        original.ValueChanged += _ => group.Binding = replacement;

        group.RemoveOption(7);

        group.Binding.ShouldBeSameAs(replacement);
        group.SelectedId.ShouldBe(9UL);
        original.Value.ShouldBeNull();
    }

    [Test]
    public void RadioBinding_ReentrantDisposalDoesNotResumeCommit()
    {
        UIValueState<ulong?> state = new(7);
        using UIRadioGroupState group = new() { Binding = state };
        state.ValueChanged += _ => group.Dispose();

        group.RemoveOption(7);

        group.Binding.ShouldBeNull();
        group.SelectedId.ShouldBe(7UL);
    }

    [Test]
    public void RadioGroup_SceneTeardownReleasesBindingAndCallbacks()
    {
        SceneNode root = new("Radio host");
        UIValueState<ulong?> binding = new(1);
        UIRadioGroupState group = new() { Binding = binding };
        int callbacks = 0;
        group.SelectionChanged += _ => callbacks++;
        root.Destroyed += _ => group.Dispose();

        root.Destroy(true);
        binding.SetValue(2);

        group.Binding.ShouldBeNull();
        callbacks.ShouldBe(0);
        Should.Throw<ObjectDisposedException>(() => group.Binding = binding);
    }

    [Test]
    public void TextCaret_NeverStopsInsideCombiningOrEmojiCluster()
    {
        var input = new UITextInputComponent { Text = "A\U0001F1FA\U0001F1F8e\u0301\U0001F469\u200d\U0001F680B" };

        input.CursorPosition = 2;
        input.CursorPosition.ShouldBe(1);
        input.MoveCaretByGrapheme(1);
        input.CursorPosition.ShouldBe(5);
        input.MoveCaretByGrapheme(1);
        input.CursorPosition.ShouldBe(7);
        input.MoveCaretByGrapheme(1);
        input.CursorPosition.ShouldBe(12);
        input.MoveCaretByGrapheme(-1, extendSelection: true);
        input.SelectionStart.ShouldBe(7);
        input.SelectionEnd.ShouldBe(12);
    }

    [Test]
    public void ShapedVisualCaretNavigation_FollowsDisplayedOrderAndYieldsAtEdges()
    {
        UITextCaretStop[] visualStops =
        [
            new(2, 0, new(0, 0), 10, 0, UITextCaretAffinity.Upstream),
            new(1, 0, new(9, 0), 10, 1, UITextCaretAffinity.Downstream),
            new(1, 0, new(9, 0), 10, 2, UITextCaretAffinity.Upstream),
            new(0, 0, new(18, 0), 10, 3, UITextCaretAffinity.Downstream)
        ];
        int[] boundaries = [0, 1];

        UITextInputComponent.FindNextVisualCaretIndex(visualStops, 0, 1, 2, boundaries, 2)
            .ShouldBe(1);
        UITextInputComponent.FindNextVisualCaretIndex(visualStops, 1, 1, 1, boundaries, 2)
            .ShouldBe(3);
        UITextInputComponent.FindNextVisualCaretIndex(visualStops, 3, 1, 0, boundaries, 2)
            .ShouldBe(-1);
        UITextInputComponent.FindNextVisualCaretIndex(visualStops, 3, -1, 0, boundaries, 2)
            .ShouldBe(2);
    }

    [Test]
    public void NumericInput_HoldsInvalidIntermediateTextForCommit()
    {
        var input = new UIIntInputComponent();

        input.SingleLineMode.ShouldBeTrue();
        input.CommitOnSubmit.ShouldBeTrue();
        input.AllowInvalidDraft.ShouldBeTrue();
        input.PostValidateInput("-").ShouldBeFalse();
        input.PostValidateInput("-12").ShouldBeTrue();
    }

    [Test]
    public void NumericInput_CommitsValidDraftAndCancelsInvalidDraftWithoutBindingWrite()
    {
        SceneNode root = new("Numeric test canvas", new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode field = new(root) { Name = "Number" };
        field.SetTransform<UIBoundableTransform>();
        field.AddComponent<UITextComponent>();
        UIIntInputComponent input = field.AddComponent<UIIntInputComponent>()!;
        UIValueState<string> binding = new("12");
        int writes = 0;
        binding.ValueChanged += _ => writes++;
        input.Binding = binding;
        root.IsActiveSelf = true;
        input.IsFocused = true;

        input.UserReplaceText("-", 1, 1).ShouldBeTrue();
        input.IsInvalid.ShouldBeTrue();
        input.UserSubmit();
        binding.Value.ShouldBe("12");
        writes.ShouldBe(0);
        input.UserCancel();
        input.Text.ShouldBe("12");

        input.UserReplaceText("-34", 3, 3).ShouldBeTrue();
        input.UserSubmit();
        binding.Value.ShouldBe("-34");
        writes.ShouldBe(1);
    }

    [Test]
    public void RangeDrag_CancelsPreviewAndCommitsOnceAtRelease()
    {
        SceneNode root = new("Range test canvas", new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode node = new(root) { Name = "Range" };
        node.SetTransform<UIBoundableTransform>();
        UIRangeSliderComponent slider = node.AddComponent<UIRangeSliderComponent>()!;
        UIValueState<double> binding = new(0.2);
        int writes = 0;
        int commits = 0;
        binding.ValueChanged += _ => writes++;
        slider.ValueCommitted += _ => commits++;
        slider.Binding = binding;
        root.IsActiveSelf = true;

        slider.BeginDrag(0.8f).ShouldBeTrue();
        slider.IsDragging.ShouldBeTrue();
        binding.Value.ShouldBe(0.2);
        slider.EndDrag(false);
        slider.Value.ShouldBe(0.2);
        writes.ShouldBe(0);
        commits.ShouldBe(0);

        slider.BeginDrag(0.7f).ShouldBeTrue();
        slider.EndDrag(true);
        binding.Value.ShouldBe(0.7, 0.00001);
        writes.ShouldBe(1);
        commits.ShouldBe(1);
        slider.SetValue(1);
        slider.TryConsumeFocusDirection(Vector2.UnitX).ShouldBeFalse();
    }

    [Test]
    public void RangeDrag_BindingReplacementDuringCommitDoesNotUseOldSource()
    {
        SceneNode root = new("Range replacement", new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode node = new(root) { Name = "Range" };
        node.SetTransform<UIBoundableTransform>();
        UIRangeSliderComponent slider = node.AddComponent<UIRangeSliderComponent>()!;
        UIValueState<double> oldBinding = new(0.2);
        UIValueState<double> replacement = new(0.4);
        slider.Binding = oldBinding;
        int commits = 0;
        slider.ValueCommitted += _ => commits++;
        root.IsActiveSelf = true;
        oldBinding.ValueChanged += _ => slider.Binding = replacement;

        slider.BeginDrag(0.8f).ShouldBeTrue();
        slider.EndDrag(true);

        slider.Binding.ShouldBeSameAs(replacement);
        slider.Value.ShouldBe(0.4);
        commits.ShouldBe(0);
    }

    [Test]
    public void ColorSelector_ConvertsSrgbInputToLinearSwatch()
    {
        var selector = new UIColorSelectorComponent();

        selector.SetColor(new ColorF4(0.5f, 0.5f, 0.5f, 1f), EUIColorSpace.SRgb);

        selector.LinearColor.R.ShouldBeInRange(0.213f, 0.215f);
        selector.AccessibilityValue.ShouldBe("#808080FF");
    }

    [Test]
    public void Tooltip_ProvidesAndReleasesAccessibleHelpWithComponentLifetime()
    {
        SceneNode root = new("Tooltip canvas", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        try
        {
            root.AddComponent<UICanvasComponent>();
            SceneNode targetNode = new(root) { Name = "Save" };
            targetNode.SetTransform<UIBoundableTransform>();
            UIButtonComponent button = targetNode.AddComponent<UIButtonComponent>()!;
            UITooltipComponent tooltip = targetNode.AddComponent<UITooltipComponent>()!;
            tooltip.Text = "Save the document";

            button.AccessibilityDescription.ShouldBe("Save the document");
            tooltip.IsActive = false;
            button.AccessibilityDescription.ShouldBeNull();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void VirtualTable_RebindsStableRowsAndClearsRecycledCellState()
    {
        TableSource source = new();
        UIVirtualTableProvider<string> table = new(source,
            UIVirtualTableColumn<string>.Text("name", "Name", 120, value => value));
        table.AccessibleRowName = value => value;
        table.TryGetAccessibilityItem(1, out UIVirtualAccessibilityItem offscreen).ShouldBeTrue();
        offscreen.ItemId.ShouldBe(73UL);
        offscreen.Name.ShouldBe("Second");
        UIBoundableTransform row = table.CreateRow();
        UITextComponent label = row.SceneNode!.FirstChild!.GetComponent<UITextComponent>()!;
        UIVirtualTableRowComponent interaction = row.SceneNode.GetComponent<UIVirtualTableRowComponent>()!;

        table.BindRow(row, 0, 42, 1);
        label.Text.ShouldBe("First");
        table.SelectedId = 42;
        interaction.IsSelected.ShouldBeTrue();

        table.UnbindRow(row, 42, 1);
        label.Text.ShouldBe(string.Empty);
        interaction.IsSelected.ShouldBeFalse();
        source.Swap();
        table.BindRow(row, 0, 73, 2);
        label.Text.ShouldBe("Second");
        table.SelectedId.ShouldBe(42UL);
        interaction.IsSelected.ShouldBeFalse();
        row.SceneNode.Destroy(true);
    }

    [Test]
    public void VirtualTreeAccessibility_DescribesLogicalRowsWithoutRealization()
    {
        int created = 0;
        UIVirtualTreeProvider tree = new(
            () => { created++; return new UIBoundableTransform(); },
            (_, _, _, _) => { }, (_, _, _) => { });
        tree.Insert(1, 0, 0, "Section", isSection: true);
        tree.Insert(2, 1, 0, "Child");
        tree.Insert(3, 2, 0, "Grandchild");
        tree.AccessibilityItemCount.ShouldBe(1);
        tree.TryGetAccessibilityItem(0, out UIVirtualAccessibilityItem section).ShouldBeTrue();
        section.ItemId.ShouldBe(1UL);
        section.Role.ShouldBe(EUIAccessibilityRole.TreeItem);
        section.IsSection.ShouldBeTrue();
        section.HasChildren.ShouldBeTrue();
        section.IsExpanded.ShouldBeFalse();

        tree.SetExpanded(1, true);
        tree.Rename(2, "Renamed child");
        tree.SetSelected(2, true);
        tree.Activate(2);
        tree.AccessibilityItemCount.ShouldBe(2);
        tree.TryGetAccessibilityItem(1, out UIVirtualAccessibilityItem child).ShouldBeTrue();
        child.ItemId.ShouldBe(2UL);
        child.Name.ShouldBe("Renamed child");
        child.Depth.ShouldBe(1);
        child.IsSelected.ShouldBeTrue();
        child.IsActive.ShouldBeTrue();
        child.HasChildren.ShouldBeTrue();
        tree.TryGetAccessibilityItem(2, out _).ShouldBeFalse();
        tree.SetExpanded(2, true);
        tree.TryGetAccessibilityItem(2, out UIVirtualAccessibilityItem grandchild).ShouldBeTrue();
        grandchild.ItemId.ShouldBe(3UL);
        grandchild.Depth.ShouldBe(2);
        created.ShouldBe(0);
    }

    private sealed class TableSource : IUIVirtualTableSource<string>
    {
        private (ulong Id, string Text)[] _items = [(42, "First"), (73, "Second")];
        public int Count => _items.Length;
        public ulong Revision { get; private set; } = 1;
        public ulong GetItemId(int index) => _items[index].Id;
        public string GetItem(int index) => _items[index].Text;
        public void Swap()
        {
            _items = [_items[1], _items[0]];
            Revision++;
        }
    }

    [Test]
    public void ScrollRegion_ClampsOffsetAfterAcceptedExtent()
    {
        var transform = new UIScrollableTransform { ScrollOffset = new Vector2(500, 500) };

        transform.SynchronizeScroll(new Vector2(100, 80));

        transform.ScrollOffset.ShouldBe(Vector2.Zero);
        transform.ContentExtent.ShouldBe(new Vector2(100, 80));
    }

    [Test]
    public void ScrollRegion_DirectionalInputYieldsAtAcceptedBoundary()
    {
        SceneNode root = new("Scroll navigation", new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode scrollNode = new(root) { Name = "Scroll" };
        UIScrollableTransform scroll = scrollNode.SetTransform<UIScrollableTransform>();
        UIScrollRegionComponent region = scrollNode.AddComponent<UIScrollRegionComponent>()!;
        UIListTransform list = new() { ItemSize = 20, VirtualOverscan = 0 };
        new SceneNode(scrollNode, "Rows", list);
        UIVirtualTreeProvider provider = new(() =>
            new SceneNode("Row", new UIBoundableTransform()).GetTransformAs<UIBoundableTransform>(true)!,
            (_, _, _, _) => { }, (_, _, _) => { });
        for (ulong id = 1; id <= 10; id++)
            provider.Insert(id, 0, (int)id - 1, $"Row {id}");
        list.VirtualProvider = provider;
        root.IsActiveSelf = true;
        list.SynchronizeVirtualRows(60);
        scroll.SynchronizeScroll(new Vector2(100, 60));

        scroll.MaxScrollOffset.Y.ShouldBe(140);
        region.TryConsumeFocusDirection(-Vector2.UnitY).ShouldBeTrue();
        scroll.ScrollOffset.Y.ShouldBe(48);
        region.AccessibilityAdjust(1).ShouldBeTrue();
        scroll.ScrollOffset.Y.ShouldBe(96);
        region.AccessibilityAdjust(1).ShouldBeTrue();
        scroll.ScrollOffset.Y.ShouldBe(140);
        region.TryConsumeFocusDirection(-Vector2.UnitY).ShouldBeFalse();
        region.HandleMouseScroll(-1).ShouldBeFalse();
        region.TryConsumeFocusDirection(Vector2.UnitY).ShouldBeTrue();
        scroll.ScrollOffset.Y.ShouldBe(92);
    }

    [Test]
    public void OrdinaryContextMenu_MountsItemsInScrollableViewport()
    {
        SceneNode root = new("Popup canvas", new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode popupNode = new(root) { Name = "Popup" };
        popupNode.SetTransform<UIBoundableTransform>();
        UIContextMenuComponent popup = popupNode.AddComponent<UIContextMenuComponent>()!;
        root.IsActiveSelf = true;

        ContextMenuItem[] items = Enumerable.Range(0, 12)
            .Select(index => new ContextMenuItem($"Item {index}", () => { })).ToArray();
        popup.Show(Vector2.Zero, items);

        SceneNode scrollNode = popupNode.FirstChild.ShouldNotBeNull();
        UIScrollableTransform scroll = scrollNode.GetTransformAs<UIScrollableTransform>(true)!;
        UIScrollRegionComponent region = scrollNode.GetComponent<UIScrollRegionComponent>()!;
        UIListTransform list = scrollNode.FirstChild!.GetTransformAs<UIListTransform>(true)!;
        scroll.ScrollableY.ShouldBeTrue();
        scroll.ScrollableX.ShouldBeFalse();
        region.IsTabStop.ShouldBeFalse();
        list.Children.Count.ShouldBe(12);
        list.Height.ShouldBe(UIContextMenuComponent.ItemHeight * 12);
        popup.Hide();
    }

    [Test]
    public void OrdinaryContextMenu_WaitsForBoundedAcceptedHeightThenScrolls()
    {
        UICanvasTransform canvas = new();
        canvas.SetSize(new Vector2(200, 80));
        SceneNode root = new("Short popup canvas", canvas) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode popupNode = new(root) { Name = "Popup" };
        UIBoundableTransform popupTransform = popupNode.SetTransform<UIBoundableTransform>();
        UIContextMenuComponent popup = popupNode.AddComponent<UIContextMenuComponent>()!;
        root.IsActiveSelf = true;
        canvas.UpdateLayoutAsync();
        SpinWait.SpinUntil(() =>
        {
            canvas.PumpLayoutResults();
            return canvas.ActualHeight == 80;
        }, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        ContextMenuItem[] items = Enumerable.Range(0, 12)
            .Select(index => new ContextMenuItem($"Item {index}", () => { })).ToArray();

        popup.Show(Vector2.Zero, items);
        SpinWait.SpinUntil(() => canvas.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
        var completePlacement = typeof(UIContextMenuComponent).GetMethod("CompletePlacement",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        completePlacement.Invoke(popup, null);
        popupTransform.Height.ShouldBe(80);
        popupTransform.Visibility.ShouldBe(EVisibility.Hidden);

        SpinWait.SpinUntil(() => canvas.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
        completePlacement.Invoke(popup, null);
        popupTransform.Visibility.ShouldBe(EVisibility.Visible);
        ulong visibleGeneration = canvas.CurrentLayoutGeneration;
        SpinWait.SpinUntil(() =>
        {
            canvas.PumpLayoutResults();
            return canvas.CurrentLayoutGeneration > visibleGeneration;
        }, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        SceneNode scrollNode = popupNode.FirstChild.ShouldNotBeNull();
        UIScrollableTransform scroll = scrollNode.GetTransformAs<UIScrollableTransform>(true)!;
        UIScrollRegionComponent region = scrollNode.GetComponent<UIScrollRegionComponent>()!;
        UIListTransform list = scrollNode.FirstChild.ShouldNotBeNull()
            .GetTransformAs<UIListTransform>(true)!;
        scroll.ViewportExtent.Y.ShouldBe(80);
        float contentHeight = UIContextMenuComponent.ItemHeight * items.Length;
        scroll.ContentExtent.Y.ShouldBe(contentHeight);
        float previousY = list.ActualLocalBottomLeftTranslation.Y;
        ulong previousGeneration = canvas.CurrentLayoutGeneration;
        region.HandleMouseScroll(-1).ShouldBeTrue();
        scroll.ScrollOffset.Y.ShouldBeGreaterThan(0);
        canvas.UpdateLayoutAsync();
        SpinWait.SpinUntil(() =>
        {
            canvas.PumpLayoutResults();
            return canvas.CurrentLayoutGeneration > previousGeneration;
        }, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        list.ActualLocalBottomLeftTranslation.Y.ShouldBeGreaterThan(previousY);
        scroll.ContentExtent.Y.ShouldBe(contentHeight);
    }

    [Test]
    public void ScrollRegion_PositiveHorizontalOffsetMovesContentLeftWithinExtent()
    {
        UICanvasTransform canvas = new();
        canvas.SetSize(new Vector2(100, 80));
        SceneNode root = new("Horizontal scroll canvas", canvas) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode scrollNode = new(root) { Name = "Scroll region" };
        UIScrollableTransform scroll = scrollNode.SetTransform<UIScrollableTransform>();
        scrollNode.AddComponent<UIScrollRegionComponent>();
        SceneNode contentNode = new(scrollNode) { Name = "Wide content" };
        UIBoundableTransform content = contentNode.SetTransform<UIBoundableTransform>();
        content.MinAnchor = Vector2.Zero;
        content.MaxAnchor = Vector2.Zero;
        content.NormalizedPivot = Vector2.Zero;
        content.Width = 240;
        content.Height = 80;
        root.IsActiveSelf = true;
        canvas.UpdateLayoutAsync();
        SpinWait.SpinUntil(() => canvas.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
        scroll.ViewportExtent.X.ShouldBe(100);
        scroll.ContentExtent.X.ShouldBe(240);
        float previousX = content.ActualLocalBottomLeftTranslation.X;
        ulong previousGeneration = canvas.CurrentLayoutGeneration;

        scroll.ScrollOffset = new Vector2(20, 0);
        canvas.UpdateLayoutAsync();
        SpinWait.SpinUntil(() =>
        {
            canvas.PumpLayoutResults();
            return canvas.CurrentLayoutGeneration > previousGeneration;
        }, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        content.ActualLocalBottomLeftTranslation.X.ShouldBe(previousX - 20);
        scroll.ContentExtent.X.ShouldBe(240);
        scroll.ScrollOffset = new Vector2(500, 0);
        scroll.ScrollOffset.X.ShouldBe(140);
    }

    [Test]
    public void ImeSnapshot_CoalescesPreeditButKeepsCommitUntilConsumed()
    {
        WindowInputSnapshotAccumulator input = new();
        UIElementIdentity focus = new(17, 3);
        input.RecordImeEvent(WindowImeEventKind.Start, focusIdentity: focus, focusRevision: 9);
        input.RecordImeEvent(WindowImeEventKind.Update, "a", 1, focus, 9);
        input.RecordImeEvent(WindowImeEventKind.Update, "あ", 1, focus, 9);
        input.RecordImeEvent(WindowImeEventKind.Commit, "亜", 0, focus, 9);

        WindowInputSnapshot first = input.Publish(1, 0, 0, true, false);
        first.ImeEventSpan.ToArray().ShouldBe(new[]
        {
            new WindowImeEvent(WindowImeEventKind.Start, "", 0, focus, 9),
            new WindowImeEvent(WindowImeEventKind.Update, "あ", 1, focus, 9),
            new WindowImeEvent(WindowImeEventKind.Commit, "亜", 0, focus, 9)
        });
        input.ConsumeLatest();
        input.Publish(1, 0, 0, true, false).ImeEventSpan.IsEmpty.ShouldBeTrue();
    }

    [Test]
    public void ComboSelection_UsesStableIdAcrossOptionReorder()
    {
        UIComboBoxComponent combo = new();
        combo.SetOptions([new(3, "Third"), new(8, "Eighth")]);
        combo.Select(8).ShouldBeTrue();
        combo.SetOptions([new(8, "Renamed"), new(3, "Third")]);

        combo.SelectedId.ShouldBe(8UL);
        combo.AccessibilityValue.ShouldBe("Renamed");
        combo.Select(99).ShouldBeFalse();
        combo.SelectedId.ShouldBe(8UL);
    }

    [Test]
    public void ComboBinding_WritesOnceAndDetachesWhenRecycled()
    {
        SceneNode root = new("Combo binding", new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode node = new(root) { Name = "Combo" };
        node.SetTransform<UIBoundableTransform>();
        UIComboBoxComponent combo = node.AddComponent<UIComboBoxComponent>()!;
        combo.SetOptions([new(3, "Third"), new(8, "Eighth")]);
        UIValueState<ulong?> binding = new(3);
        int writes = 0;
        binding.ValueChanged += _ => writes++;
        combo.Binding = binding;
        root.IsActiveSelf = true;

        combo.Select(8, EUIValueChangeOrigin.User).ShouldBeTrue();
        binding.Value.ShouldBe(8UL);
        writes.ShouldBe(1);
        combo.Select(8, EUIValueChangeOrigin.User).ShouldBeTrue();
        writes.ShouldBe(1);

        combo.CancelForRecycle();
        combo.Binding.ShouldBeNull();
        combo.SelectedId.ShouldBe(8UL);
        binding.SetValue(3);
        combo.SelectedId.ShouldBe(8UL);
    }

    [Test]
    public void ComboBinding_ReplacementDuringWriteKeepsNewSourceSelection()
    {
        UIComboBoxComponent combo = new();
        combo.SetOptions([new(3, "Third"), new(8, "Eighth")]);
        UIValueState<ulong?> oldBinding = new(3);
        UIValueState<ulong?> replacement = new(3);
        combo.Binding = oldBinding;
        oldBinding.ValueChanged += _ => combo.Binding = replacement;

        combo.Select(8, EUIValueChangeOrigin.User).ShouldBeFalse();

        combo.Binding.ShouldBeSameAs(replacement);
        combo.SelectedId.ShouldBe(3UL);
    }

    [Test]
    public void ColorSelector_PreviewCancelCommitAndRecycleKeepBindingConsistent()
    {
        SceneNode root = new("Color binding", new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode node = new(root) { Name = "Color" };
        node.SetTransform<UIBoundableTransform>();
        UIColorSelectorComponent selector = node.AddComponent<UIColorSelectorComponent>()!;
        ColorF4 original = new(1, 0, 0, 1);
        UIValueState<ColorF4> binding = new(original);
        int writes = 0;
        int commits = 0;
        binding.ValueChanged += _ => writes++;
        selector.ColorCommitted += _ => commits++;
        selector.Binding = binding;
        root.IsActiveSelf = true;

        selector.BeginEdit().ShouldBeTrue();
        selector.PreviewHexSrgb("#00FF00FF").ShouldBeTrue();
        binding.Value.ShouldBe(original);
        writes.ShouldBe(0);
        selector.CancelEdit();
        selector.LinearColor.ShouldBe(original);
        commits.ShouldBe(0);

        selector.BeginEdit().ShouldBeTrue();
        selector.PreviewHexSrgb("#0000FFFF").ShouldBeTrue();
        selector.CommitEdit();
        writes.ShouldBe(1);
        commits.ShouldBe(1);
        selector.LinearColor.ShouldBe(binding.Value);
        selector.CancelForRecycle();
        selector.Binding.ShouldBeNull();
        ColorF4 retained = selector.LinearColor;
        binding.SetValue(original);
        selector.LinearColor.ShouldBe(retained);
    }

    [Test]
    public void ColorSelector_BindingReplacementDuringCommitDoesNotEmitOldCommit()
    {
        SceneNode root = new("Color replacement", new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode node = new(root) { Name = "Color" };
        node.SetTransform<UIBoundableTransform>();
        UIColorSelectorComponent selector = node.AddComponent<UIColorSelectorComponent>()!;
        UIValueState<ColorF4> oldBinding = new(new ColorF4(1, 0, 0, 1));
        UIValueState<ColorF4> replacement = new(new ColorF4(0, 0, 1, 1));
        selector.Binding = oldBinding;
        int commits = 0;
        selector.ColorCommitted += _ => commits++;
        root.IsActiveSelf = true;
        oldBinding.ValueChanged += _ => selector.Binding = replacement;

        selector.BeginEdit().ShouldBeTrue();
        selector.PreviewHexSrgb("#00FF00FF").ShouldBeTrue();
        selector.CommitEdit();

        selector.Binding.ShouldBeSameAs(replacement);
        selector.LinearColor.ShouldBe(replacement.Value);
        commits.ShouldBe(0);
    }

    [Test]
    public void ImeComposition_PreviewsWithoutBindingWriteAndCommitsAsOneUndoStep()
    {
        UITextInputComponent input = CreateFocusedInput(out _);
        UIValueState<string> binding = new("ab");
        input.Binding = binding;
        input.SelectionAnchor = 0;
        input.CursorPosition = 2;

        input.BeginComposition().ShouldBeTrue();
        input.UpdateComposition("あ", 1).ShouldBeTrue();
        input.Text.ShouldBe("あ");
        binding.Value.ShouldBe("ab");
        input.CommitComposition("亜").ShouldBeTrue();
        binding.Value.ShouldBe("亜");
        input.Undo().ShouldBeTrue();
        input.Text.ShouldBe("ab");
    }

    [Test]
    public void ImeComposition_SnapsNativeCaretInsideGraphemeWithoutLosingPreedit()
    {
        UITextInputComponent input = CreateFocusedInput(out _);
        input.BeginComposition().ShouldBeTrue();

        input.UpdateComposition("e\u0301", 1).ShouldBeTrue();

        input.Text.ShouldBe("e\u0301");
        input.CursorPosition.ShouldBe(0);
        input.CancelComposition();
    }

    [Test]
    public void ImeComposition_CancelsOnDeactivationWithoutWritingDraft()
    {
        UITextInputComponent input = CreateFocusedInput(out SceneNode field);
        UIValueState<string> binding = new("before");
        input.Binding = binding;
        input.CursorPosition = input.Text.Length;
        input.BeginComposition().ShouldBeTrue();
        input.UpdateComposition("仮", 1).ShouldBeTrue();

        field.IsActiveSelf = false;

        input.IsComposing.ShouldBeFalse();
        binding.Value.ShouldBe("before");
        input.Text.ShouldBe("before");
    }

    [Test]
    public void ImeFocusEpoch_InvalidatesEarlierCompositionOnRefocusAndReadOnly()
    {
        UITextInputComponent input = CreateFocusedInput(out _);
        long firstFocus = input.FocusEpoch;

        input.IsFocused = false;
        input.IsFocused = true;
        input.FocusEpoch.ShouldBeGreaterThan(firstFocus);

        input.BeginComposition().ShouldBeTrue();
        input.UpdateComposition("仮", 1).ShouldBeTrue();
        long composingEpoch = input.FocusEpoch;
        input.IsReadOnly = true;

        input.FocusEpoch.ShouldBeGreaterThan(composingEpoch);
        input.IsComposing.ShouldBeFalse();
        input.Text.ShouldBe(string.Empty);
    }

    [Test]
    public void ImeDispatch_UsesFocusEpochAndPreservedPointerRevisionIndependently()
    {
        SceneNode root = new("IME dispatch canvas", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        XRViewport viewport = new(null)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            SetRenderPipelineFromCamera = false
        };
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UICanvasInputComponent dispatcher = root.AddComponent<UICanvasInputComponent>()!;
            PawnComponent pawn = root.NewChild("Pawn").AddComponent<PawnComponent>()!;
            pawn.Controller = new LocalPlayerController { Viewport = viewport };
            dispatcher.OwningPawn = pawn;
            SceneNode field = new(root) { Name = "IME field" };
            field.SetTransform<UIBoundableTransform>();
            UITextComponent text = field.AddComponent<UITextComponent>()!;
            UITextInputComponent control = field.AddComponent<UITextInputComponent>()!;
            dispatcher.FocusedComponent = control;
            dispatcher.FocusedComponent = null;
            dispatcher.FocusedComponent = control;
            control.FocusEpoch.ShouldNotBe(control.InteractionRevision);

            BoundingRectangleF bounds = new(Vector2.Zero, new Vector2(200));
            UIHitRecord hit = new(control.RenderInfo2D, control.RenderInfo2D.MembershipId,
                control.AccessibilityIdentity, control.InteractionRevision,
                Matrix4x4.Identity, new Vector2(100), bounds, null,
                1, 0, 2, 1, 0, 0, true, false);
            UIPublication packet = new(1, 1, 1, 1, 1, default, default, [hit], [],
                bounds, Matrix4x4.Identity, Matrix4x4.Identity);
            canvas.MarkPresented(viewport, packet);

            WindowImeEvent[] events =
            [
                new(WindowImeEventKind.Start, "", 0, control.AccessibilityIdentity, control.FocusEpoch),
                new(WindowImeEventKind.Update, "あ", 1, control.AccessibilityIdentity, control.FocusEpoch),
                new(WindowImeEventKind.Commit, "亜", 0, control.AccessibilityIdentity, control.FocusEpoch)
            ];
            dispatcher.DispatchImeEvents(events);
            control.IsComposing.ShouldBeFalse();
            control.Text.ShouldBe("亜");

            control.MaxInputLength = 2;
            dispatcher.DispatchImeEvents(
            [
                new(WindowImeEventKind.Start, "", 0, control.AccessibilityIdentity, control.FocusEpoch),
                new(WindowImeEventKind.Update, "x", 1, control.AccessibilityIdentity, control.FocusEpoch),
                new(WindowImeEventKind.Commit, "long", 0, control.AccessibilityIdentity, control.FocusEpoch)
            ]);
            control.IsComposing.ShouldBeFalse();
            control.Text.ShouldBe("亜");

            UIRenderRecord frozenText = new()
            {
                RenderInfo = text.RenderInfo2D,
                MembershipId = text.RenderInfo2D.MembershipId,
                Identity = text.UITransform.ElementIdentity,
                WorldMatrix = Matrix4x4.CreateTranslation(20, 30, 0),
                Bounds = new Vector4(0, 0, 100, 30),
                RenderPass = 0,
                ZIndex = 0,
                Material = null
            };
            UIPublication scaled = new(1, 1, 1, 1, 1, default, default, [], [],
                bounds, Matrix4x4.CreateScale(2), Matrix4x4.CreateScale(0.5f));
            UICanvasInputComponent.TryMapImeCaretClientPoint(scaled, frozenText,
                new Vector2(5, 7), out Vector2 client).ShouldBeTrue();
            client.ShouldBe(new Vector2(50, 74));
        }
        finally
        {
            root.Destroy(true);
            viewport.Destroy();
        }
    }

    private UITextInputComponent CreateFocusedInput(out SceneNode field)
    {
        SceneNode root = new("IME test canvas", new UICanvasTransform())
        {
            IsActiveSelf = false
        };
        try
        {
            root.SetWorldContext(new TestWorldContext());
            root.AddComponent<UICanvasComponent>();
            field = new SceneNode(root) { Name = "IME field" };
            field.SetTransform<UIBoundableTransform>();
            field.AddComponent<UITextComponent>();
            UITextInputComponent input = field.AddComponent<UITextInputComponent>()!;
            input.IsFocused = true;
            root.IsActiveSelf = true;
            input.CanInteract.ShouldBeTrue();
            _ownedRoots.Add(root);
            return input;
        }
        catch
        {
            root.Destroy(true);
            throw;
        }
    }
}
