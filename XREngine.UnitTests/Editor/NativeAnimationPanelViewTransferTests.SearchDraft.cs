using System.Reflection;
using NUnit.Framework;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Editor;

public sealed partial class NativeAnimationPanelViewTransferTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void StoppedSearchDraftRetainsTextAndSelectionWithoutCommitting(bool subtree)
    {
        UITextInputComponent field = PrepareSearchDraft();
        ulong textIntent = field.TextWriteIntentRevision;
        ulong caretIntent = field.CaretWriteIntentRevision;
        int matches = Get<int>(_source, "_candidateMatchCount");
        SetSearchStopped(subtree, true);
        Assert.That(Get<object?>(_source, "_searchDraft"), Is.Not.Null);
        SetSearchStopped(subtree, false);
        Assert.Multiple(() =>
        {
            Assert.That(Get<UITextInputComponent>(_source, "_searchInput"), Is.SameAs(field));
            Assert.That(field.Text, Is.EqualTo("unsubmitted draft"));
            Assert.That(field.CursorPosition, Is.EqualTo(8));
            Assert.That(field.SelectionAnchor, Is.EqualTo(2));
            Assert.That(field.TextWriteIntentRevision, Is.EqualTo(textIntent));
            Assert.That(field.CaretWriteIntentRevision, Is.EqualTo(caretIntent));
            Assert.That(Get<string>(_source, "_pathSearch"), Is.EqualTo("committed"));
            Assert.That(Get<int>(_source, "_candidateMatchCount"), Is.EqualTo(matches));
            Assert.That(field.IsFocused, Is.False, "Continuation must not acquire focus.");
            Assert.That(Get<object?>(_source, "_searchDraft"), Is.Null);
            Assert.That(Get<bool>(_source, "_ownerSubscriptionsActive"), Is.True);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StoppedSearchDraftRejectsSceneSelectionAwayAndBack(bool subtree)
    {
        SceneNode[] previous = Selection.SceneNodes;
        SceneNode other = new("Other selection");
        try
        {
            UITextInputComponent field = PrepareSearchDraft();
            SetSearchStopped(subtree, true);
            Selection.SceneNode = other;
            Selection.SceneNodes = previous;
            SetSearchStopped(subtree, false);
            Assert.That(field.Text, Is.EqualTo("committed"));
            Assert.That(Get<string>(_source, "_pathSearch"), Is.EqualTo("committed"));
            Assert.That(Get<object?>(_source, "_searchDraft"), Is.Null);
        }
        finally { Selection.SceneNodes = previous; other.Destroy(true); }
    }

    [Test]
    public void StoppedSearchDraftRejectsAssetPublicationAwayAndBack()
    {
        UITextInputComponent field = PrepareSearchDraft();
        _source.IsActive = false;
        PublishSearchAsset(null);
        PublishSearchAsset(_clip);
        _source.IsActive = true;
        Assert.That(field.Text, Is.EqualTo("committed"));
        Assert.That(NativeAssetSelection.SelectedTarget, Is.SameAs(_clip));
    }

    [TestCase("text")]
    [TestCase("caret")]
    [TestCase("display")]
    public void StoppedSearchDraftPreservesNewerAuthoredIntent(string change)
    {
        UITextInputComponent field = PrepareSearchDraft();
        _source.IsActive = false;
        PublishSearchAsset(null);
        PublishSearchAsset(_clip);
        if (change == "text") field.Text = "newer draft";
        else if (change == "caret") field.CursorPosition = field.CursorPosition;
        else field.TextComponent.Text = "newer display";
        ulong textIntent = field.TextWriteIntentRevision;
        ulong caretIntent = field.CaretWriteIntentRevision;
        _source.IsActive = true;
        Assert.That(field.Text, Is.EqualTo(change == "text" ? "newer draft" : "unsubmitted draft"));
        Assert.That(field.TextWriteIntentRevision, Is.EqualTo(textIntent));
        Assert.That(field.CaretWriteIntentRevision, Is.EqualTo(caretIntent));
        if (change == "display") Assert.That(field.TextComponent.Text, Is.EqualTo("newer display"));
        Assert.That(Get<object?>(_source, "_searchDraft"), Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StoppedSearchDraftCannotWriteATransferredOwner(bool panel)
    {
        UITextInputComponent field = PrepareSearchDraft();
        _source.IsActive = false;
        PublishSearchAsset(null);
        PublishSearchAsset(_clip);
        SceneNode destination = _root.NewChildWithTransform<UIBoundableTransform>(out _, "Other owner");
        TransformBase moved = (panel ? _source.SceneNode : field.SceneNode).Transform;
        moved.SetParent(destination.Transform, false, EParentAssignmentMode.Immediate);
        Assert.That(moved.Parent, Is.SameAs(destination.Transform));
        ulong textIntent = field.TextWriteIntentRevision;
        _source.IsActive = true;
        Assert.That(field.Text, Is.EqualTo("unsubmitted draft"));
        Assert.That(field.TextWriteIntentRevision, Is.EqualTo(textIntent));
        Assert.That(Get<object?>(_source, "_searchDraft"), Is.Null);
    }

    [Test]
    public void StoppedSearchDraftRejectsSelectedComponentClipAwayAndBack()
    {
        SceneNode[] previous = Selection.SceneNodes;
        SceneNode source = new("Clip source");
        AnimationClipComponent component = source.AddComponent<AnimationClipComponent>()!;
        component.Animation = _clip;
        try
        {
            PublishSearchAsset(null);
            Selection.SceneNode = source;
            UITextInputComponent field = PrepareSearchDraft();
            _source.IsActive = false;
            component.Animation = null;
            component.Animation = _clip;
            _source.IsActive = true;
            Assert.That(field.Text, Is.EqualTo("committed"));
            Assert.That(component.Animation, Is.SameAs(_clip));
        }
        finally { Selection.SceneNodes = previous; source.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StoppedSearchDraftDisposesAnObsoleteViewReceipt(bool subtree)
    {
        UITextInputComponent field = PrepareSearchDraft();
        SetSearchStopped(subtree, true);
        Assert.That(Get<object?>(_source, "_searchDraft"), Is.Not.Null);
        InvokePanel(_source, "RefreshRows");
        PublishSearchAsset(null);
        PublishSearchAsset(_clip);
        SetSearchStopped(subtree, false);
        Assert.That(field.Text, Is.EqualTo("unsubmitted draft"));
        Assert.That(Get<object?>(_source, "_searchDraft"), Is.Null);
        Assert.That(Get<bool>(_source, "_ownerSubscriptionsActive"), Is.True);
    }

    [Test]
    public void StoppedSearchDraftRetiresOnPanelDestruction()
    {
        UITextInputComponent field = PrepareSearchDraft();
        _source.IsActive = false;
        Assert.That(Get<object?>(_source, "_searchDraft"), Is.Not.Null);
        field.SceneNode.Transform.SetParent(_root.Transform, false, EParentAssignmentMode.Immediate);
        _source.Destroy(true);
        Assert.That(field.IsDestroyed, Is.False);
        Assert.That(((NativeAnimationSearchInputComponent)field).Panel, Is.Null);
        Assert.That(Get<object?>(_source, "_searchDraft"), Is.Null);
    }

    [Test]
    public void StoppedSearchDraftResetCannotOverwriteAReentrantEdit()
    {
        UITextInputComponent field = PrepareSearchDraft();
        _source.IsActive = false;
        PublishSearchAsset(null);
        PublishSearchAsset(_clip);
        bool edited = false;
        field.PropertyChanging += (_, property) =>
        {
            if (edited || property.PropertyName != nameof(UITextInputComponent.Text)) return;
            edited = true;
            field.Text = "callback draft";
        };
        _source.IsActive = true;
        Assert.That(edited, Is.True);
        Assert.That(field.Text, Is.EqualTo("callback draft"));
        Assert.That(Get<string>(_source, "_pathSearch"), Is.EqualTo("committed"));
        Assert.That(Get<object?>(_source, "_searchDraft"), Is.Null);
    }

    [Test]
    public void StoppedSearchDraftResetStopsWhenItsCallbackChangesTheSourceAgain()
    {
        UITextInputComponent field = PrepareSearchDraft();
        _source.IsActive = false;
        PublishSearchAsset(null);
        PublishSearchAsset(_clip);
        bool changed = false;
        field.PropertyChanging += (_, property) =>
        {
            if (changed || property.PropertyName != nameof(UITextInputComponent.Text)) return;
            changed = true;
            PublishSearchAsset(null);
        };
        _source.IsActive = true;
        Assert.That(changed, Is.True);
        Assert.That(field.Text, Is.EqualTo("unsubmitted draft"));
        Assert.That(NativeAssetSelection.SelectedTarget, Is.Null);
        Assert.That(Get<object?>(_source, "_searchDraft"), Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StoppedSearchDraftResetRejectsComponentClipAwayAndBack(bool replaceComponent)
    {
        SceneNode[] previous = Selection.SceneNodes;
        SceneNode first = new("First clip owner"), second = new("Second clip owner");
        AnimationClipComponent firstComponent = first.AddComponent<AnimationClipComponent>()!;
        AnimationClipComponent secondComponent = second.AddComponent<AnimationClipComponent>()!;
        firstComponent.Animation = _clip;
        secondComponent.Animation = _clip;
        try
        {
            PublishSearchAsset(null);
            Selection.SceneNode = first;
            UITextInputComponent field = PrepareSearchDraft();
            _source.IsActive = false;
            AnimationClipComponent resetComponent;
            if (replaceComponent)
            {
                Selection.SceneNode = second;
                resetComponent = secondComponent;
            }
            else
            {
                firstComponent.Animation = null;
                firstComponent.Animation = _clip;
                resetComponent = firstComponent;
            }
            bool changed = false;
            field.PropertyChanging += (_, property) =>
            {
                if (changed || property.PropertyName != nameof(UITextInputComponent.Text)) return;
                changed = true;
                resetComponent.Animation = null;
                resetComponent.Animation = _clip;
            };
            _source.IsActive = true;
            Assert.That(changed, Is.True);
            Assert.That(field.Text, Is.EqualTo("unsubmitted draft"));
            Assert.That(resetComponent.Animation, Is.SameAs(_clip));
            Assert.That(Get<object?>(_source, "_searchDraft"), Is.Null);
        }
        finally
        {
            Selection.SceneNodes = previous;
            first.Destroy(true);
            second.Destroy(true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StoppedSearchDraftReleasesCanvasFocusWithoutLosingSelection(bool subtree)
    {
        var (canvas, input) = AttachSearchCanvas();
        try
        {
            UITextInputComponent field = Get<UITextInputComponent>(_source, "_searchInput");
            input.FocusedComponent = field;
            Assert.That(field.IsFocused, Is.True);
            PrepareSearchDraft();
            ulong textIntent = field.TextWriteIntentRevision;
            ulong caretIntent = field.CaretWriteIntentRevision;
            SetSearchStopped(subtree, true);
            Assert.That(input.FocusedComponent, Is.Null);
            Assert.That(field.IsFocused, Is.False);
            Assert.That(Get<object?>(_source, "_searchDraft"), Is.Not.Null);
            SetSearchStopped(subtree, false);
            Assert.That(field.Text, Is.EqualTo("unsubmitted draft"));
            Assert.That((field.CursorPosition, field.SelectionAnchor), Is.EqualTo((8, 2)));
            Assert.That(field.TextWriteIntentRevision, Is.EqualTo(textIntent));
            Assert.That(field.CaretWriteIntentRevision, Is.EqualTo(caretIntent));
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { canvas.Destroy(true); }
    }

    [Test]
    public void StoppedSearchDraftFocusReleaseCallbackKeepsItsNewerEdit()
    {
        var (canvas, input) = AttachSearchCanvas();
        try
        {
            UITextInputComponent field = Get<UITextInputComponent>(_source, "_searchInput");
            input.FocusedComponent = field;
            PrepareSearchDraft();
            bool edited = false;
            field.LostFocus += _ =>
            {
                if (edited) return;
                edited = true;
                field.Text = "release callback draft";
            };
            _source.IsActive = false;
            Assert.That(edited, Is.True);
            PublishSearchAsset(null);
            PublishSearchAsset(_clip);
            _source.IsActive = true;
            Assert.That(field.Text, Is.EqualTo("release callback draft"));
            Assert.That(Get<object?>(_source, "_searchDraft"), Is.Null);
        }
        finally { canvas.Destroy(true); }
    }

    [Test]
    public void StoppedSearchDraftDoesNotArmAfterReentrantActivation()
    {
        var (canvas, input) = AttachSearchCanvas();
        try
        {
            UITextInputComponent field = Get<UITextInputComponent>(_source, "_searchInput");
            input.FocusedComponent = field;
            PrepareSearchDraft();
            bool resumed = false;
            field.LostFocus += _ =>
            {
                if (resumed) return;
                resumed = true;
                _source.IsActive = true;
                field.Text = "reactivated draft";
            };
            _source.IsActive = false;
            Assert.That(resumed, Is.True);
            Assert.That(field.Text, Is.EqualTo("reactivated draft"));
            Assert.That(Get<object?>(_source, "_searchDraft"), Is.Null);
            Assert.That(SearchSelectionSubscriptions(typeof(Selection), "SelectionChanged"), Is.EqualTo(1));
            Assert.That(SearchSelectionSubscriptions(typeof(NativeAssetSelection), "AssetSelectionChanged"), Is.EqualTo(1));
            _source.IsActive = false;
            Assert.That(SearchSelectionSubscriptions(typeof(Selection), "SelectionChanged"), Is.Zero);
            Assert.That(SearchSelectionSubscriptions(typeof(NativeAssetSelection), "AssetSelectionChanged"), Is.Zero);
        }
        finally { canvas.Destroy(true); }
    }

    [Test]
    public void StoppedSearchDraftRejectsARegeneratedCanvasInputOwner()
    {
        var (canvas, input) = AttachSearchCanvas();
        try
        {
            UITextInputComponent field = PrepareSearchDraft();
            _source.IsActive = false;
            PublishSearchAsset(null);
            PublishSearchAsset(_clip);
            input.Generate();
            ulong textIntent = field.TextWriteIntentRevision;
            _source.IsActive = true;
            Assert.That(field.Text, Is.EqualTo("unsubmitted draft"));
            Assert.That(field.TextWriteIntentRevision, Is.EqualTo(textIntent));
            Assert.That(Get<object?>(_source, "_searchDraft"), Is.Null);
        }
        finally { canvas.Destroy(true); }
    }

    [Test]
    public void StoppedSearchDraftDoesNotCaptureAnImeComposition()
    {
        var (canvas, input) = AttachSearchCanvas();
        try
        {
            UITextInputComponent field = Get<UITextInputComponent>(_source, "_searchInput");
            input.FocusedComponent = field;
            PrepareSearchDraft();
            Assert.That(field.BeginComposition(), Is.True);
            Assert.That(field.UpdateComposition("preedit", 2), Is.True);
            _source.IsActive = false;
            Assert.That(Get<object?>(_source, "_searchDraft"), Is.Null);
        }
        finally { canvas.Destroy(true); }
    }

    private (SceneNode Canvas, UICanvasInputComponent Input) AttachSearchCanvas()
    {
        SceneNode canvas = new("Search canvas", new UICanvasTransform());
        canvas.SetWorldContext(_root.World);
        canvas.AddComponent<UICanvasComponent>();
        UICanvasInputComponent input = canvas.AddComponent<UICanvasInputComponent>()!;
        _source.SceneNode.Transform.SetParent(canvas.Transform, false, EParentAssignmentMode.Immediate);
        Assert.That(_source.SceneNode.Transform.Parent, Is.SameAs(canvas.Transform));
        return (canvas, input);
    }

    private UITextInputComponent PrepareSearchDraft()
    {
        CommitSearch(_source, "committed");
        UITextInputComponent field = Get<UITextInputComponent>(_source, "_searchInput");
        field.Text = "unsubmitted draft";
        field.CursorPosition = 8;
        field.SelectionAnchor = 2;
        return field;
    }

    private void SetSearchStopped(bool subtree, bool stopped)
    {
        if (subtree) _source.SceneNode.IsActiveSelf = !stopped;
        else _source.IsActive = !stopped;
    }

    [Test]
    public void StoppedSearchDraftDoesNotCaptureAPropertyBoundField()
    {
        SceneNode target = new("Unchanged property");
        try
        {
            UITextInputComponent field = PrepareSearchDraft();
            field.Property = typeof(SceneNode).GetProperty(nameof(SceneNode.Name));
            field.Targets = [target];
            _source.IsActive = false;
            Assert.That(Get<object?>(_source, "_searchDraft"), Is.Null);
            PublishSearchAsset(null);
            PublishSearchAsset(_clip);
            _source.IsActive = true;
            Assert.That(target.Name, Is.EqualTo("Unchanged property"));
        }
        finally { target.Destroy(true); }
    }

    private int SearchSelectionSubscriptions(Type publisher, string method)
        => (publisher.GetField("SelectionChanged", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null) as Delegate)?.GetInvocationList()
            .Count(handler => ReferenceEquals(handler.Target, _source) && handler.Method.Name == method) ?? 0;

    private static void PublishSearchAsset(object? value)
        => typeof(NativeAssetSelection).GetMethod("Publish", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [value]);
}
