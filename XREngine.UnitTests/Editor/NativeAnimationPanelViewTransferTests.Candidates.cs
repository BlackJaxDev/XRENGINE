using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Editor;

public sealed partial class NativeAnimationPanelViewTransferTests
{
    private UITextInputComponent SearchInput => Get<UITextInputComponent>(_target, "_searchInput");

    [Test]
    public void CapturedEmptySelectionCannotFollowANewCandidateRoot()
    {
        SceneNode[] previous = Selection.SceneNodes;
        SceneNode root = new("Later root");
        try
        {
            Selection.SceneNodes = [];
            InvokePanel(_source, "RefreshRows");
            NativeAnimationPanelViewReceipt captured = Capture();
            Assert.That(captured.Values.Candidates.Root, Is.Null);
            Selection.SceneNode = root;
            Assert.That(captured.IsCurrent, Is.False);
            Assert.Throws<InvalidOperationException>(() => Restore(captured));
        }
        finally { Selection.SceneNodes = previous; root.Destroy(true); }
    }

    [Test]
    public void EmptyClipStillCapturesItsCurrentSceneCandidateContext()
    {
        SceneNode[] previous = Selection.SceneNodes;
        SceneNode root = new("Root without a clip");
        try
        {
            Selection.SceneNode = root;
            SelectClip(null);
            // The fixture setter bypasses the asset selection event. Reject the old
            // presentation before refreshing both panels to the new empty view.
            Assert.That(Get<object?>(_source, "_displayedClip"), Is.SameAs(_clip));
            Assert.That(_source.TryCaptureViewReceipt(out _), Is.False);
            InvokePanel(_source, "RefreshRows");
            InvokePanel(_target, "RefreshRows");
            NativeAnimationPanelViewReceipt captured = Capture();
            Assert.That(captured.Values.Clip, Is.Null);
            Assert.That(captured.Values.Candidates.Root, Is.SameAs(root));
            Assert.That(Restore(captured).IsCurrent, Is.True);
        }
        finally { Selection.SceneNodes = previous; root.Destroy(true); }
    }

    [Test]
    public void CommittedQueryAndSelectedPathReturnAfterDeferredDiscovery()
    {
        SceneNode[] previous = Selection.SceneNodes;
        SceneNode root = new("Candidate root");
        try
        {
            Selection.SceneNode = root;
            InvokePanel(_source, "RefreshRows");
            var candidates = Get<IReadOnlyList<EditorImGuiUI.AnimationClipPathCandidate>>(_source, "_candidates");
            Assert.That(candidates, Is.Not.Empty);
            var selected = candidates[0];
            CommitSearch(_source, selected.DisplayPath);
            Set(_source, "_selectedCandidate", selected);
            NativeAnimationPanelViewReceipt captured = Capture();

            NativeDockPanelStateReceipt applied = Restore(captured);

            Assert.That(applied.IsCurrent, Is.True);
            Assert.That(SearchInput.Text, Is.EqualTo(selected.DisplayPath));
            Assert.That(SearchInput.TextComponent.Text, Is.EqualTo(selected.DisplayPath));
            Assert.That(Get<object?>(_target, "_selectedCandidate"), Is.Null,
                "Selection must wait for fresh destination discovery.");
            Set(_target, "_lastRefresh", DateTime.MinValue);
            InvokePanel(_target, "RefreshTick");
            var restored = Get<EditorImGuiUI.AnimationClipPathCandidate>(_target, "_selectedCandidate");
            Assert.That(restored.Id, Is.EqualTo(selected.Id));
            Assert.That(restored, Is.Not.SameAs(selected));
            Assert.That(Get<string>(_target, "_pathSearch"), Is.EqualTo(selected.DisplayPath));
            Assert.That(Get<int>(_target, "_candidateMatchCount"), Is.GreaterThan(0));
        }
        finally { Selection.SceneNodes = previous; root.Destroy(true); }
    }

    [TestCase("query")]
    [TestCase("page")]
    [TestCase("selected")]
    [TestCase("input")]
    public void CapturedCandidateViewRejectsLaterSourceIntent(string change)
    {
        SceneNode[] previous = Selection.SceneNodes;
        SceneNode root = new("Candidate root");
        try
        {
            Selection.SceneNode = root;
            InvokePanel(_source, "RefreshRows");
            NativeAnimationPanelViewReceipt captured = Capture();
            if (change == "query") CommitSearch(_source, "changed");
            else if (change == "page") Set(_source, "_candidatePage", 1);
            else if (change == "input") Get<UITextInputComponent>(_source, "_searchInput").Text = "unsubmitted";
            else Set(_source, "_selectedCandidate",
                Get<IReadOnlyList<EditorImGuiUI.AnimationClipPathCandidate>>(_source, "_candidates")[0]);
            Assert.That(captured.IsCurrent, Is.False);
            Assert.Throws<InvalidOperationException>(() => Restore(captured));
        }
        finally { Selection.SceneNodes = previous; root.Destroy(true); }
    }

    [TestCase("text")]
    [TestCase("display")]
    [TestCase("caret")]
    public void EarlierToolbarCallbackCannotOverwriteNewerSearchIntent(string change)
    {
        CommitSearch(_source, "source query");
        SearchInput.Text = "destination draft";
        NativeAnimationPanelViewReceipt captured = Capture();
        bool edited = false;
        TimeInput.PropertyChanged += (_, property) =>
        {
            if (edited || property.PropertyName != nameof(UITextInputComponent.Text)) return;
            edited = true;
            if (change == "text") SearchInput.Text = "newer draft";
            else if (change == "display") SearchInput.TextComponent.Text = "newer display";
            else SearchInput.CursorPosition = SearchInput.CursorPosition;
        };
        Assert.Throws<InvalidOperationException>(() => Restore(captured));
        Assert.That(edited, Is.True);
        Assert.That(SearchInput.Text, Is.EqualTo(change == "text" ? "newer draft" : "destination draft"));
        if (change == "display") Assert.That(SearchInput.TextComponent.Text, Is.EqualTo("newer display"));
    }

    [Test]
    public void DeferredCandidateResolutionPreservesPageAndUsesFreshStableId()
    {
        var old = Candidate("selected", "match old");
        List<EditorImGuiUI.AnimationClipPathCandidate> fresh = [];
        for (int i = 0; i < 260; i++) fresh.Add(Candidate(i.ToString(), "match " + i));
        fresh[150] = Candidate(old.Id, "match fresh");
        NativeAnimationCandidateView view = new(null, null, "match", 1, old.Id);
        var resolved = AnimationPanel.ResolveCandidateView(view, fresh, 128);
        Assert.That(resolved.Page, Is.EqualTo(1));
        Assert.That(resolved.Selected, Is.SameAs(fresh[150]));
        Assert.That(resolved.Selected, Is.Not.SameAs(old));
        var missing = AnimationPanel.ResolveCandidateView(view with { Query = "no match", SelectedId = "missing" }, fresh, 128);
        Assert.That(missing.Page, Is.Zero);
        Assert.That(missing.Selected, Is.Null);
        var clamped = AnimationPanel.ResolveCandidateView(view with { Page = 99 }, fresh, 128);
        Assert.That(clamped.Page, Is.EqualTo(2));
    }

    [Test]
    public void SearchEditAfterRestoreInvalidatesTheAppliedView()
    {
        CommitSearch(_source, "source query");
        NativeDockPanelStateReceipt applied = Restore(Capture());
        Assert.That(applied.IsCurrent, Is.True);
        SearchInput.Text = "newer draft";
        Assert.That(applied.IsCurrent, Is.False);
    }

    [Test]
    public void SearchVetoKeepsTheDestinationDraftAndRejectsRestore()
    {
        CommitSearch(_source, "source query");
        SearchInput.Text = "destination draft";
        NativeAnimationPanelViewReceipt captured = Capture();
        SearchInput.PropertyChanging += (_, property) =>
        {
            if (property.PropertyName == nameof(UITextInputComponent.Text))
                ((XREngine.Data.Core.XRPropertyChangingEventArgs<string>)property).AllowChange = false;
        };
        Assert.Throws<InvalidOperationException>(() => Restore(captured));
        Assert.That(SearchInput.Text, Is.EqualTo("destination draft"));
        Assert.That(SearchInput.TextComponent.Text, Is.EqualTo("destination draft"));
    }

    [Test]
    public void ChangedRootDiscardsTheDeferredSelectedPath()
    {
        SceneNode[] previous = Selection.SceneNodes;
        SceneNode first = new("First root"), second = new("Second root");
        try
        {
            Selection.SceneNode = first;
            InvokePanel(_source, "RefreshRows");
            var candidate = Get<IReadOnlyList<EditorImGuiUI.AnimationClipPathCandidate>>(_source, "_candidates")[0];
            Set(_source, "_selectedCandidate", candidate);
            Restore(Capture());
            Selection.SceneNode = second;
            Set(_target, "_lastRefresh", DateTime.MinValue);
            InvokePanel(_target, "RefreshTick");
            Assert.That(Get<SceneNode>(_target, "_candidateRoot"), Is.SameAs(second));
            Assert.That(Get<object?>(_target, "_selectedCandidate"), Is.Null);
            Assert.That(Get<int>(_target, "_candidatePage"), Is.Zero);
        }
        finally { Selection.SceneNodes = previous; first.Destroy(true); second.Destroy(true); }
    }

    private static EditorImGuiUI.AnimationClipPathCandidate Candidate(string id, string text)
        => new(id, text, "Probe", [], XREngine.Animation.EAnimationMemberType.Property, typeof(float), 0f);

    private static void CommitSearch(AnimationPanel panel, string query)
    {
        UITextInputComponent input = Get<UITextInputComponent>(panel, "_searchInput");
        input.Text = query;
        try { typeof(AnimationPanel).GetMethod("SubmitPathSearch", Fields)!.Invoke(panel, [input]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
}
