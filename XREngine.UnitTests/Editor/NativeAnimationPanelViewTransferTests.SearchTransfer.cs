using NUnit.Framework;
using XREngine.Editor;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Editor;

public sealed partial class NativeAnimationPanelViewTransferTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void LiveTransferKeepsDraftAndSelectionSeparateFromCommittedQuery(bool reversed)
    {
        CommitSearch(_source, "committed query");
        var sourceInput = Get<UITextInputComponent>(_source, "_searchInput");
        sourceInput.Text = "unsubmitted draft";
        sourceInput.CursorPosition = reversed ? 2 : 8;
        sourceInput.SelectionAnchor = reversed ? 8 : 2;
        var source = Capture();
        var applied = Restore(source);
        Assert.That(applied.IsCurrent, Is.True);
        Assert.That(SearchInput.Text, Is.EqualTo("unsubmitted draft"));
        Assert.That((SearchInput.CursorPosition, SearchInput.SelectionAnchor),
            Is.EqualTo(reversed ? (2, 8) : (8, 2)));
        Assert.That(Get<string>(_target, "_pathSearch"), Is.EqualTo("committed query"));
        Assert.That(SearchInput.IsFocused, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SourceDraftEqualValueIntentRejectsItsCapturedTransfer(bool caret)
    {
        var sourceInput = Get<UITextInputComponent>(_source, "_searchInput");
        sourceInput.Text = "draft";
        var source = Capture();
        if (caret) sourceInput.CursorPosition = sourceInput.CursorPosition;
        else sourceInput.Text = sourceInput.Text;
        Assert.That(source.IsCurrent, Is.False);
        Assert.Throws<InvalidOperationException>(() => Restore(source));
    }

    [Test]
    public void DestinationSelectionCallbackKeepsNewerCaretAndRejectsTransfer()
    {
        var sourceInput = Get<UITextInputComponent>(_source, "_searchInput");
        sourceInput.Text = "source draft";
        sourceInput.CursorPosition = 8;
        sourceInput.SelectionAnchor = 2;
        SearchInput.Text = "destination draft";
        var source = Capture();
        bool changed = false;
        SearchInput.PropertyChanging += (_, change) =>
        {
            if (changed || change.PropertyName != nameof(UITextInputComponent.CursorPosition)) return;
            changed = true;
            SearchInput.CursorPosition = 3;
            SearchInput.SelectionAnchor = 1;
        };
        Assert.Throws<InvalidOperationException>(() => Restore(source));
        Assert.That(changed, Is.True);
        Assert.That((SearchInput.CursorPosition, SearchInput.SelectionAnchor), Is.EqualTo((3, 1)));
    }

    [Test]
    public void PostTextFailureRetainsOwnedPartialReceiptForDraftCompensation()
    {
        CommitSearch(_source, "source committed");
        var sourceInput = Get<UITextInputComponent>(_source, "_searchInput");
        sourceInput.Text = "short";
        sourceInput.CursorPosition = 4;
        sourceInput.SelectionAnchor = 1;
        CommitSearch(_target, "destination committed");
        SearchInput.Text = "destination draft";
        SearchInput.CursorPosition = 12;
        SearchInput.SelectionAnchor = 3;
        var before = _targetBinding.CaptureReceipt();
        var priorView = _target.CapturePriorView();
        var source = Capture();
        bool failed = false;
        SearchInput.PropertyChanged += (_, change) =>
        {
            if (failed || change.PropertyName != nameof(UITextInputComponent.Text)) return;
            failed = true;
            throw new ApplicationException("After the search text committed.");
        };
        NativeDockPanelStateReceipt? partial = null;
        Assert.Throws<ApplicationException>(() => _targetBinding.Restore(_sourceBinding.CaptureLiveValues(),
            () => source.IsCurrent, source.Values, value => partial = value));
        Assert.That(partial, Is.Not.Null);
        Assert.That(partial!.Value.IsCurrent, Is.True);
        var restored = _targetBinding.Restore(before.Values, static () => true, priorView,
            restorePriorSearchFocus: true);
        Assert.That(restored.IsCurrent, Is.True);
        Assert.That(SearchInput.Text, Is.EqualTo("destination draft"));
        Assert.That((SearchInput.CursorPosition, SearchInput.SelectionAnchor), Is.EqualTo((12, 3)));
        Assert.That(Get<string>(_target, "_pathSearch"), Is.EqualTo("destination committed"));
    }

    [Test]
    public void MissingDestinationCanvasDoesNotPreventDraftTransfer()
    {
        var sourceInput = Get<UITextInputComponent>(_source, "_searchInput");
        sourceInput.Text = "draft without presentation";
        sourceInput.CursorPosition = 5;
        sourceInput.SelectionAnchor = 2;
        var source = Capture();
        var requested = source.Values with { Search = source.Values.Search!.Value with { Focused = true } };
        var applied = _targetBinding.Restore(_sourceBinding.CaptureLiveValues(), () => source.IsCurrent, requested);
        Assert.That(applied.IsCurrent, Is.True);
        Assert.That(SearchInput.Text, Is.EqualTo(sourceInput.Text));
        Assert.That(SearchInput.CursorPosition, Is.EqualTo(5));
        Assert.That(SearchInput.IsFocused, Is.False);
    }
}
