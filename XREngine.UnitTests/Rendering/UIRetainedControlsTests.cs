using NUnit.Framework;
using System.Globalization;
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
    public void ToggleBinding_ReentrantReplacementDoesNotCommitOldSource()
    {
        SceneNode root = new("Toggle replacement", new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        UIToggleComponent toggle = root.NewChild("Toggle").AddComponent<UIToggleComponent>()!;
        UIValueState<UIToggleComponent.ECurrentState> original = new(UIToggleComponent.ECurrentState.False);
        UIValueState<UIToggleComponent.ECurrentState> replacement = new(UIToggleComponent.ECurrentState.Intermediate);
        toggle.Binding = original;
        int commits = 0;
        toggle.ValueCommitted += _ => commits++;
        root.IsActiveSelf = true;
        original.ValueChanged += _ => toggle.Binding = replacement;

        toggle.Toggle().ShouldBeTrue();

        original.Value.ShouldBe(UIToggleComponent.ECurrentState.True);
        toggle.Binding.ShouldBeSameAs(replacement);
        toggle.CurrentState.ShouldBe(UIToggleComponent.ECurrentState.Intermediate);
        commits.ShouldBe(0);

        toggle.Toggle().ShouldBeTrue();
        replacement.Value.ShouldBe(UIToggleComponent.ECurrentState.True);
        commits.ShouldBe(1);
    }

    [Test]
    public void ToggleBinding_ReentrantDestructionDoesNotCommit()
    {
        SceneNode root = new("Toggle destroy", new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        UIToggleComponent toggle = root.NewChild("Toggle").AddComponent<UIToggleComponent>()!;
        UIValueState<UIToggleComponent.ECurrentState> binding = new(UIToggleComponent.ECurrentState.Intermediate);
        toggle.Binding = binding;
        int commits = 0;
        toggle.ValueCommitted += _ => commits++;
        root.IsActiveSelf = true;
        binding.ValueChanged += _ => root.Destroy(true);

        toggle.Toggle().ShouldBeTrue();

        binding.Value.ShouldBe(UIToggleComponent.ECurrentState.True);
        commits.ShouldBe(0);
    }

    [Test]
    public void ButtonFocusRing_UsesBorderPrimitiveAndClearsOnDeactivation()
    {
        SceneNode root = new("Button focus ring", new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode buttonNode = new(root) { Name = "Button" };
        buttonNode.SetTransform<UIBoundableTransform>();
        UIButtonComponent button = buttonNode.AddComponent<UIButtonComponent>()!;
        buttonNode.AddComponent<UIMaterialComponent>()!.SetBatchSolidColor(ColorF4.DarkGray);
        SceneNode ringNode = new(buttonNode) { Name = "Focus ring" };
        ringNode.SetTransform<UIBoundableTransform>();
        UIMaterialComponent ring = ringNode.AddComponent<UIMaterialComponent>()!;
        ring.SetBatchBorderStyle(ColorF4.Transparent, ColorF4.Transparent, 2);
        button.FocusRingMaterial = ring;
        root.IsActiveSelf = true;

        ring.BatchBorderColor.ShouldBe((Vector4)ColorF4.Transparent);
        button.IsFocused = true;
        ring.BatchBorderColor.ShouldBe((Vector4)ColorF4.White);
        SceneNode nextRingNode = new(buttonNode) { Name = "Replacement focus ring" };
        nextRingNode.SetTransform<UIBoundableTransform>();
        UIMaterialComponent nextRing = nextRingNode.AddComponent<UIMaterialComponent>()!;
        nextRing.SetBatchBorderStyle(ColorF4.Transparent, ColorF4.Transparent, 2);
        button.FocusRingMaterial = nextRing;
        ring.BatchBorderColor.ShouldBe((Vector4)ColorF4.Transparent);
        nextRing.BatchBorderColor.ShouldBe((Vector4)ColorF4.White);
        button.IsFocused = false;
        nextRing.BatchBorderColor.ShouldBe((Vector4)ColorF4.Transparent);
        button.IsFocused = true;
        button.CancelForRecycle();
        nextRing.BatchBorderColor.ShouldBe((Vector4)ColorF4.Transparent);
        button.FocusRingMaterial.ShouldBeNull();
        button.FocusRingMaterial = nextRing;
        button.IsFocused = true;
        nextRing.BatchBorderColor.ShouldBe((Vector4)ColorF4.White);
        root.IsActiveSelf = false;
        nextRing.BatchBorderColor.ShouldBe((Vector4)ColorF4.Transparent);
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
    public void NumericInput_UsesBoundsCultureAndCheckedStepsAtCommit()
    {
        SceneNode root = new("Numeric policy", new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode field = new(root) { Name = "Number" };
        field.SetTransform<UIBoundableTransform>();
        field.AddComponent<UITextComponent>();
        UIIntInputComponent input = field.AddComponent<UIIntInputComponent>()!;
        UIValueState<string> binding = new("9");
        input.Binding = binding;
        input.Minimum = 0;
        input.Maximum = 10;
        input.Step = 2;
        root.IsActiveSelf = true;

        input.TryStep(1).ShouldBeTrue();
        input.Text.ShouldBe("10");
        binding.Value.ShouldBe("10");
        input.TryStep(1).ShouldBeFalse();
        input.Text = "11";
        input.PostValidateInput(input.Text).ShouldBeFalse();
        input.UserSubmit();
        binding.Value.ShouldBe("10");
        input.UserCancel();
        input.Text.ShouldBe("10");
        Should.Throw<ArgumentOutOfRangeException>(() => input.Minimum = 11);

        UIFloatInputComponent floating = new() { Culture = CultureInfo.GetCultureInfo("fr-FR") };
        floating.PostValidateInput("1,5").ShouldBeTrue();
        floating.PostValidateInput("NaN").ShouldBeFalse();
        floating.PostValidateInput("Infinity").ShouldBeFalse();

        SceneNode largeField = new(root) { Name = "Large number" };
        largeField.SetTransform<UIBoundableTransform>();
        largeField.AddComponent<UITextComponent>();
        UIULongInputComponent large = largeField.AddComponent<UIULongInputComponent>()!;
        large.Text = ulong.MaxValue.ToString(CultureInfo.InvariantCulture);
        large.TryStep(1).ShouldBeFalse();
        large.Text.ShouldBe(ulong.MaxValue.ToString(CultureInfo.InvariantCulture));

        SceneNode wideField = new(root) { Name = "Wide exponent" };
        wideField.SetTransform<UIBoundableTransform>();
        wideField.AddComponent<UITextComponent>();
        UIDoubleInputComponent wide = wideField.AddComponent<UIDoubleInputComponent>()!;
        wide.Culture = CultureInfo.InvariantCulture;
        wide.Text = "1E+100";
        wide.Step = 1e100;
        wide.TryStep(1).ShouldBeTrue();
        double.Parse(wide.Text, CultureInfo.InvariantCulture).ShouldBe(2e100);

        SceneNode tinyField = new(root) { Name = "Tiny exponent" };
        tinyField.SetTransform<UIBoundableTransform>();
        tinyField.AddComponent<UITextComponent>();
        UIFloatInputComponent tiny = tinyField.AddComponent<UIFloatInputComponent>()!;
        tiny.Culture = CultureInfo.InvariantCulture;
        tiny.Text = "1E-30";
        tiny.Step = 1e-30f;
        tiny.TryStep(1).ShouldBeTrue();
        float.Parse(tiny.Text, CultureInfo.InvariantCulture).ShouldBe(2e-30f);
    }

    [Test]
    public void NumericAltScrub_PreviewsThenCommitsOrCancelsOneBindingWrite()
    {
        SceneNode root = new("Numeric scrub", new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        UICanvasInputComponent canvasInput = root.AddComponent<UICanvasInputComponent>()!;
        SceneNode field = new(root) { Name = "Number" };
        field.SetTransform<UIBoundableTransform>();
        field.AddComponent<UITextComponent>();
        UIIntInputComponent input = field.AddComponent<UIIntInputComponent>()!;
        UIValueState<string> binding = new("5");
        int writes = 0;
        binding.ValueChanged += _ => writes++;
        input.Binding = binding;
        input.Minimum = 0;
        input.Maximum = 10;
        root.IsActiveSelf = true;
        var alt = typeof(UICanvasInputComponent).GetMethod("OnAltLeftStateChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        input.PointerPressed(Vector2.Zero);
        input.MouseMoved(Vector2.Zero, new Vector2(24, 0));
        input.IsScrubbing.ShouldBeFalse();
        binding.Value.ShouldBe("5");
        input.PointerReleased(new Vector2(24, 0), true);

        alt.Invoke(canvasInput, [true]);
        input.PointerPressed(Vector2.Zero);
        input.IsScrubbing.ShouldBeTrue();
        input.MouseMoved(Vector2.Zero, new Vector2(24, 0));
        input.Text.ShouldBe("8");
        binding.Value.ShouldBe("5");
        input.PointerCancelled();
        input.Text.ShouldBe("5");
        writes.ShouldBe(0);

        input.PointerPressed(Vector2.Zero);
        input.MouseMoved(Vector2.Zero, new Vector2(24, 0));
        input.UserCancel();
        input.MouseMoved(new Vector2(24, 0), new Vector2(32, 0));
        input.PointerReleased(new Vector2(32, 0), true);
        input.Text.ShouldBe("5");
        writes.ShouldBe(0);

        input.PointerPressed(Vector2.Zero);
        input.MouseMoved(Vector2.Zero, new Vector2(24, 0));
        input.UserSubmit();
        input.MouseMoved(new Vector2(24, 0), new Vector2(32, 0));
        input.PointerReleased(new Vector2(32, 0), true);
        binding.Value.ShouldBe("8");
        writes.ShouldBe(1);

        SceneNode signedField = new(root) { Name = "Signed number" };
        signedField.SetTransform<UIBoundableTransform>();
        signedField.AddComponent<UITextComponent>();
        UISByteInputComponent signed = signedField.AddComponent<UISByteInputComponent>()!;
        signed.Text = "-100";
        signed.Step = 100;
        signed.PointerPressed(Vector2.Zero);
        signed.MouseMoved(Vector2.Zero, new Vector2(16, 0));
        signed.Text.ShouldBe("100");
        signed.EnableAltScrub = false;
        signed.IsScrubbing.ShouldBeFalse();
        signed.Text.ShouldBe("-100");

        signed.EnableAltScrub = true;
        signed.Text = "100";
        signed.Step = 1;
        signed.PointerPressed(Vector2.Zero);
        signed.MouseMoved(Vector2.Zero, new Vector2(-1200, 0));
        signed.Text.ShouldBe("-50");
        signed.PointerCancelled();
        signed.Text.ShouldBe("100");
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
    public void RangeDrag_UsesPressedWidthAfterNewLayoutIsAccepted()
    {
        UICanvasTransform canvas = new();
        canvas.SetSize(new Vector2(240, 40));
        SceneNode root = new("Resized range", canvas) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode node = new(root) { Name = "Range" };
        UIBoundableTransform track = node.SetTransform<UIBoundableTransform>();
        track.MinAnchor = Vector2.Zero;
        track.MaxAnchor = Vector2.Zero;
        track.Width = 100;
        track.Height = 20;
        UIRangeSliderComponent slider = node.AddComponent<UIRangeSliderComponent>()!;
        root.IsActiveSelf = true;
        canvas.UpdateLayoutAsync();
        SpinWait.SpinUntil(() => canvas.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
        track.ActualWidth.ShouldBe(100);

        UIPointerGeometry pressed = new(new Vector2(100, 20));
        slider.PointerPressed(new Vector2(50, 10), pressed);
        track.Width = 200;
        canvas.UpdateLayoutAsync();
        SpinWait.SpinUntil(() => canvas.PumpLayoutResults() && track.ActualWidth == 200,
            TimeSpan.FromSeconds(5)).ShouldBeTrue();
        slider.MouseMoved(new Vector2(50, 10), new Vector2(75, 10), pressed);
        slider.Value.ShouldBe(0.75, 0.00001);
        slider.PointerReleased(new Vector2(75, 10), true, pressed);
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
    public void Scrollbar_DragsWithinAcceptedExtentAndLeavesDirectionalScrollAvailable()
    {
        UICanvasTransform canvas = new();
        canvas.SetSize(new Vector2(100, 80));
        SceneNode root = new("Scrollbar canvas", canvas) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode scrollNode = new(root) { Name = "Scroll" };
        UIScrollableTransform scroll = scrollNode.SetTransform<UIScrollableTransform>();
        UIScrollRegionComponent region = scrollNode.AddComponent<UIScrollRegionComponent>()!;
        SceneNode contentNode = new(scrollNode) { Name = "Content" };
        UIBoundableTransform content = contentNode.SetTransform<UIBoundableTransform>();
        content.MinAnchor = Vector2.Zero;
        content.MaxAnchor = Vector2.Zero;
        content.Width = 240;
        content.Height = 80;
        SceneNode trackNode = new(root) { Name = "Horizontal track" };
        UIBoundableTransform track = trackNode.SetTransform<UIBoundableTransform>();
        track.MinAnchor = Vector2.Zero;
        track.MaxAnchor = Vector2.Zero;
        track.Width = 100;
        track.Height = 10;
        UIScrollbarComponent scrollbar = trackNode.AddComponent<UIScrollbarComponent>()!;
        scrollbar.Horizontal = true;
        scrollbar.ScrollTarget = scroll;
        root.IsActiveSelf = true;
        canvas.UpdateLayoutAsync();
        SpinWait.SpinUntil(() => canvas.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
        scroll.ContentExtent.X.ShouldBe(240);
        track.ActualWidth.ShouldBe(100);

        scrollbar.PointerPressed(new Vector2(90, 5));
        scroll.ScrollOffset.X.ShouldBeGreaterThan(0);
        scrollbar.ThumbTransform.ShouldNotBeNull().Width!.Value.ShouldBeLessThan(100f);
        scrollbar.MouseMoved(new Vector2(90, 5), new Vector2(0, 5));
        scrollbar.PointerReleased(new Vector2(0, 5), true);
        scroll.ScrollOffset.X.ShouldBe(0);
        region.TryConsumeFocusDirection(Vector2.UnitX).ShouldBeTrue();
        scroll.ScrollOffset.X.ShouldBe(48);

        scrollbar.AccessibilityAdjust(1).ShouldBeTrue();
        ulong beforeAcceptedThumb = canvas.CurrentLayoutGeneration;
        canvas.UpdateLayoutAsync();
        bool acceptedReady = SpinWait.SpinUntil(() =>
        {
            canvas.PumpLayoutResults();
            UIBoundableTransform? thumb = scrollbar.ThumbTransform;
            return canvas.CurrentLayoutGeneration > beforeAcceptedThumb && thumb is not null &&
                scrollbar.TryCapturePointerGeometry(new Vector2(100, 10), thumb.ActualSize,
                    thumb.ActualLocalBottomLeftTranslation, scroll.ScrollOffset,
                    scroll.MaxScrollOffset, out _);
        }, TimeSpan.FromSeconds(5));
        if (!acceptedReady)
        {
            UIBoundableTransform? thumb = scrollbar.ThumbTransform;
            float travel = 100 - (thumb?.ActualWidth ?? 0);
            float expectedStart = scroll.MaxScrollOffset.X > 0
                ? Math.Clamp(scroll.ScrollOffset.X / scroll.MaxScrollOffset.X, 0, 1) * travel
                : float.NaN;
            Assert.Fail($"Accepted scrollbar geometry did not settle: generation={canvas.CurrentLayoutGeneration}, " +
                $"before={beforeAcceptedThumb}, trackActual={track.ActualSize}, thumbActual={thumb?.ActualSize}, " +
                $"thumbActualTranslation={thumb?.ActualLocalBottomLeftTranslation}, " +
                $"thumbRequestedTranslation={thumb?.Translation}, expectedStart={expectedStart}, " +
                $"scrollOffset={scroll.ScrollOffset}, maxOffset={scroll.MaxScrollOffset}, " +
                $"viewport={scroll.ViewportExtent}, content={scroll.ContentExtent}, " +
                $"contentChildActual={content.ActualLocalBottomLeftTranslation}, " +
                $"canInteract={scrollbar.CanInteract}, readOnly={scrollbar.IsReadOnly}, " +
                $"scrollable={scroll.Scrollable}, scrollableX={scroll.ScrollableX}, " +
                $"targetMatches={ReferenceEquals(scrollbar.ScrollTarget, scroll)}, thumbVisibility={thumb?.Visibility}.");
        }
        UIBoundableTransform acceptedThumb = scrollbar.ThumbTransform.ShouldNotBeNull();
        scroll.ContentExtent.X.ShouldBe(240);
        scrollbar.TryCapturePointerGeometry(new Vector2(100, 10), acceptedThumb.ActualSize,
            acceptedThumb.ActualLocalBottomLeftTranslation, scroll.ScrollOffset, scroll.MaxScrollOffset,
            out UIScrollbarPointerMetrics frozen).ShouldBeTrue();
        UIPointerGeometry pressed = new(new Vector2(100, 10), frozen);
        float grabX = frozen.ThumbStart + frozen.ThumbLength * 0.5f;
        scrollbar.PointerPressed(new Vector2(grabX, 5), pressed);
        track.Width = 200;
        canvas.UpdateLayoutAsync();
        SpinWait.SpinUntil(() => canvas.PumpLayoutResults() && track.ActualWidth == 200,
            TimeSpan.FromSeconds(5)).ShouldBeTrue();
        float movedX = grabX + 10;
        scrollbar.MouseMoved(new Vector2(grabX, 5), new Vector2(movedX, 5), pressed);
        float expected = Math.Clamp((frozen.ThumbStart + 10) /
            (frozen.TrackLength - frozen.ThumbLength), 0, 1) * frozen.MaximumOffset;
        scroll.ScrollOffset.X.ShouldBe(expected, 0.01f);
        scrollbar.MinimumThumbPixels = 16;
        float beforeInvalidMove = scroll.ScrollOffset.X;
        scrollbar.MouseMoved(new Vector2(movedX, 5), new Vector2(movedX + 10, 5), pressed);
        scroll.ScrollOffset.X.ShouldBe(beforeInvalidMove);
        scrollbar.PointerReleased(new Vector2(movedX + 10, 5), true, pressed);
        SceneNode ownedThumb = scrollbar.ThumbTransform.ShouldNotBeNull().SceneNode.ShouldNotBeNull();
        scrollbar.Destroy(true);
        ownedThumb.IsDestroyed.ShouldBeTrue();
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
    public void ComboTypeAhead_SkipsDisabledOptionsAndUsesVirtualPrefixIndex()
    {
        SceneNode root = new("Combo typeahead", new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode node = new(root) { Name = "Combo" };
        node.SetTransform<UIBoundableTransform>();
        UIComboBoxComponent combo = node.AddComponent<UIComboBoxComponent>()!;
        combo.SetOptions([new(1, "Alpha"), new(2, "Beta", false),
            new(3, "Bravo"), new(4, "Birch")]);
        root.IsActiveSelf = true;

        combo.TypeAhead('b').ShouldBeTrue();
        combo.SelectedId.ShouldBe(3UL);
        combo.TypeAhead('i').ShouldBeTrue();
        combo.SelectedId.ShouldBe(4UL);

        SearchOnlyComboOptions virtualOptions = new();
        combo.VirtualOptions = virtualOptions;
        combo.TypeAhead('r').ShouldBeTrue();
        combo.SelectedId.ShouldBe(12345UL);
        virtualOptions.SearchCount.ShouldBe(1);
        virtualOptions.GetCount.ShouldBe(1);
    }

    private sealed class SearchOnlyComboOptions : IUIComboOptionSource, IUIComboPrefixSearch
    {
        public int Count => 1_000_000;
        public ulong Revision => 1;
        public int SearchCount { get; private set; }
        public int GetCount { get; private set; }
        public int IndexOfId(ulong id) => id == 12345 ? 12345 : -1;
        public int FindPrefix(string prefix, int startIndex)
        {
            SearchCount++;
            return prefix.Equals("r", StringComparison.OrdinalIgnoreCase) ? 12345 : -1;
        }
        public UIComboOption GetOption(int index)
        {
            GetCount++;
            if (index != 12345)
                throw new InvalidOperationException("The combo scanned the virtual source.");
            return new UIComboOption(12345, "Ruby");
        }
    }

    [Test]
    public void ComboLargeProvider_ReportsMissingIndicesWithoutScanning()
    {
        UIComboBoxComponent combo = CreateActiveCombo("Combo missing indices");
        UnindexedComboOptions source = new();
        combo.VirtualOptions = source;

        combo.SupportsTypeAhead.ShouldBeFalse();
        combo.SupportsAdjacentSelection.ShouldBeFalse();
        combo.TypeAhead('x').ShouldBeFalse();
        combo.LastTypeAheadStatus.ShouldBe(EUIComboLookupStatus.IndexRequired);
        combo.AccessibilityAdjust(1).ShouldBeFalse();
        combo.LastNavigationStatus.ShouldBe(EUIComboLookupStatus.IndexRequired);
        source.GetCount.ShouldBe(0);
    }

    [Test]
    public void ComboLargeProvider_UsesBoundedEnabledIndicesAndRejectsStaleResults()
    {
        UIComboBoxComponent combo = CreateActiveCombo("Combo indexed options");
        IndexedComboOptions source = new();
        combo.VirtualOptions = source;

        combo.SupportsTypeAhead.ShouldBeTrue();
        combo.SupportsAdjacentSelection.ShouldBeTrue();
        combo.TypeAhead('x').ShouldBeTrue();
        combo.SelectedId.ShouldBe(111UL);
        combo.LastTypeAheadStatus.ShouldBe(EUIComboLookupStatus.Selected);
        source.DisabledNavigationResult = true;
        combo.AccessibilityAdjust(1).ShouldBeFalse();
        combo.LastNavigationStatus.ShouldBe(EUIComboLookupStatus.InvalidProviderResult);
        combo.SelectedId.ShouldBe(111UL);
        source.DisabledNavigationResult = false;
        combo.AccessibilityAdjust(1).ShouldBeTrue();
        combo.SelectedId.ShouldBe(222UL);
        combo.LastNavigationStatus.ShouldBe(EUIComboLookupStatus.Selected);
        source.GetCount.ShouldBe(3);
        source.NavigationCount.ShouldBe(2);

        source.InvalidSelectedIndex = true;
        combo.TypeAhead('x').ShouldBeFalse();
        combo.LastTypeAheadStatus.ShouldBe(EUIComboLookupStatus.InvalidProviderResult);
        combo.AccessibilityAdjust(1).ShouldBeFalse();
        combo.LastNavigationStatus.ShouldBe(EUIComboLookupStatus.InvalidProviderResult);
        source.InvalidSelectedIndex = false;

        combo.TypeAhead('q').ShouldBeFalse();
        combo.LastTypeAheadStatus.ShouldBe(EUIComboLookupStatus.NoMatch);
        source.DisabledResult = true;
        combo.TypeAhead('x').ShouldBeFalse();
        combo.LastTypeAheadStatus.ShouldBe(EUIComboLookupStatus.InvalidProviderResult);
        combo.SelectedId.ShouldBe(222UL);

        source.DisabledResult = false;
        source.ChangeRevisionDuringSearch = true;
        combo.TypeAhead('x').ShouldBeFalse();
        combo.LastTypeAheadStatus.ShouldBe(EUIComboLookupStatus.ProviderChanged);
        combo.SelectedId.ShouldBe(222UL);
    }

    private UIComboBoxComponent CreateActiveCombo(string name)
    {
        SceneNode root = new(name, new UICanvasTransform()) { IsActiveSelf = false };
        _ownedRoots.Add(root);
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode node = new(root) { Name = "Combo" };
        node.SetTransform<UIBoundableTransform>();
        UIComboBoxComponent combo = node.AddComponent<UIComboBoxComponent>()!;
        root.IsActiveSelf = true;
        return combo;
    }

    private sealed class UnindexedComboOptions : IUIComboOptionSource
    {
        public int Count => int.MaxValue;
        public ulong Revision => 1;
        public int GetCount { get; private set; }
        public int IndexOfId(ulong id) => -1;
        public UIComboOption GetOption(int index)
        {
            GetCount++;
            throw new InvalidOperationException("The combo scanned an unindexed provider.");
        }
    }

    private sealed class IndexedComboOptions : IUIComboOptionSource,
        IUIComboPrefixSearch, IUIComboEnabledNavigation
    {
        private ulong _revision = 1;
        public int Count => int.MaxValue;
        public ulong Revision => _revision;
        public int GetCount { get; private set; }
        public int NavigationCount { get; private set; }
        public bool DisabledResult { get; set; }
        public bool DisabledNavigationResult { get; set; }
        public bool ChangeRevisionDuringSearch { get; set; }
        public bool InvalidSelectedIndex { get; set; }
        public int IndexOfId(ulong id) => InvalidSelectedIndex ? -2 : id switch
        {
            111 => 1_234_567,
            222 => 1_234_568,
            _ => -1
        };
        public int FindPrefix(string prefix, int startIndex)
        {
            if (ChangeRevisionDuringSearch)
                _revision++;
            return prefix.Equals("x", StringComparison.OrdinalIgnoreCase) ? 1_234_567 : -1;
        }
        public int FindNextEnabled(int startIndex, int direction)
        {
            NavigationCount++;
            return startIndex == 1_234_567 && direction == 1 ? 1_234_568 : -1;
        }
        public UIComboOption GetOption(int index)
        {
            GetCount++;
            return index switch
            {
                1_234_567 => new(111, "Xylophone", !DisabledResult),
                1_234_568 => new(222, "Yarrow", !DisabledNavigationResult),
                _ => throw new InvalidOperationException("The combo scanned outside the index.")
            };
        }
    }

    [Test]
    public void VirtualComboRowsUseIntrinsicBackgroundsWithoutPrivateMaterials()
    {
        using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
            EngineMaterialConstructionTarget.DesktopGlsl);
        UIComboBoxComponent combo = new() { VirtualOptions = new SingleComboOptionSource() };
        object adapter = typeof(UIComboBoxComponent)
            .GetField("_virtualAdapter", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!
            .GetValue(combo)!;
        IVirtualListProvider provider = (IVirtualListProvider)adapter;
        UIBoundableTransform first = provider.CreateRow();
        UIBoundableTransform second = provider.CreateRow();
        try
        {
            UIMaterialComponent firstBackground = first.SceneNode!.GetComponent<UIMaterialComponent>()!;
            UIMaterialComponent secondBackground = second.SceneNode!.GetComponent<UIMaterialComponent>()!;
            firstBackground.Mesh.ShouldBeNull();
            secondBackground.Mesh.ShouldBeNull();
            firstBackground.SupportsBatchedRendering.ShouldBeTrue();
            secondBackground.SupportsBatchedRendering.ShouldBeTrue();
            firstBackground.SetBatchSolidColor(new Vector4(1, 0, 0, 1));
            firstBackground.Mesh.ShouldBeNull();
            secondBackground.Mesh.ShouldBeNull();
        }
        finally
        {
            first.SceneNode!.Destroy(true);
            second.SceneNode!.Destroy(true);
            combo.Destroy(true);
        }
    }

    private sealed class SingleComboOptionSource : IUIComboOptionSource
    {
        public int Count => 1;
        public ulong Revision => 1;
        public UIComboOption GetOption(int index) => index == 0
            ? new UIComboOption(1, "First") : throw new ArgumentOutOfRangeException(nameof(index));
        public int IndexOfId(ulong id) => id == 1 ? 0 : -1;
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
