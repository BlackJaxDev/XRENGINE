using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Physics;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed partial class NativeAnimationPanelViewTransferTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private UITestShaderScope _shaders = null!;
    private object? _previousSelection;
    private SceneNode _root = null!;
    private AnimationPanel _source = null!;
    private AnimationPanel _target = null!;
    private NativeToolPanelStateBinding _sourceBinding = null!;
    private NativeToolPanelStateBinding _targetBinding = null!;
    private PropAnimFloat _animation = null!;
    private FloatKeyframe _key = null!;
    private AnimationMember _member = null!;
    private AnimationClip _clip = null!;

    [SetUp]
    public void SetUp()
    {
        _shaders = new UITestShaderScope();
        _previousSelection = NativeAssetSelection.SelectedTarget;
        _animation = new PropAnimFloat(3, false, true);
        _key = new FloatKeyframe(0.75f, 2f, 2f, 0f, 0f, EVectorInterpType.Linear);
        _animation.Keyframes.Add(_key);
        _member = new AnimationMember("Value", EAnimationMemberType.Property, _animation);
        _clip = new AnimationClip(new AnimationMember("Root", EAnimationMemberType.Group, _member))
            { LengthInSeconds = 3 };
        SelectClip(_clip);
        _root = new SceneNode("Tools", new UIBoundableTransform());
        _root.SetWorldContext(new TestWorldContext());
        SceneNode sourceSlot = _root.NewChildWithTransform<UIBoundableTransform>(out _, "Source");
        SceneNode targetSlot = _root.NewChildWithTransform<UIBoundableTransform>(out _, "Target");
        sourceSlot.NewChild<AnimationPanel>(out _source);
        targetSlot.NewChild<AnimationPanel>(out _target);
        NativeDockLayout layout = new();
        _sourceBinding = NativeToolPanelState.Bind(_source, sourceSlot, layout, 3,
            () => { }, () => { })!;
        _targetBinding = NativeToolPanelState.Bind(_target, targetSlot, layout, 3,
            () => { }, () => { })!;
        Assert.That(_sourceBinding.IsCurrent && _targetBinding.IsCurrent, Is.True);
        Set(_source, "_selectedMember", _member);
        Set(_source, "_selectedKey", _key);
        TimeInput.Text = "prior time";
        ValueInput.Text = "prior value";
    }

    [TearDown]
    public void TearDown()
    {
        SelectClip(_previousSelection);
        _root?.Destroy(true);
        _shaders?.Dispose();
    }

    [Test]
    public void AcceptedPairRestoresTimeValueAndLocalViewWithoutChangingGlobalSelection()
    {
        Set(_source, "_graphView", false);
        Set(_source, "_plotZoom", 2f);
        Set(_source, "_plotWindowStart", 0.4f);
        Set(_source, "_stretchLengthEdits", true);
        Set(_source, "_valueChannel", 0);
        NativeAnimationPanelViewReceipt receipt = Capture();

        NativeDockPanelStateReceipt applied = Restore(receipt);

        Assert.Multiple(() =>
        {
            Assert.That(applied.IsCurrent, Is.True);
            Assert.That(NativeAssetSelection.SelectedTarget, Is.SameAs(_clip));
            Assert.That(Get<AnimationMember>(_target, "_selectedMember"), Is.SameAs(_member));
            Assert.That(Get<Keyframe>(_target, "_selectedKey"), Is.SameAs(_key));
            Assert.That(Get<bool>(_target, "_graphView"), Is.False);
            Assert.That(Get<float>(_target, "_plotZoom"), Is.EqualTo(2f));
            Assert.That(Get<float>(_target, "_plotWindowStart"), Is.EqualTo(0.4f));
            Assert.That(TimeInput.Text, Is.EqualTo("0.75"));
            Assert.That(ValueInput.Text, Is.EqualTo("2"));
            Assert.That(TimeInput.TextComponent.Text, Is.EqualTo("0.75"));
            Assert.That(ValueInput.TextComponent.Text, Is.EqualTo("2"));
        });
    }

    [Test]
    public void OwnedProjectionDefersSceneCandidatesUntilNextOrdinaryRefresh()
    {
        SceneNode[] previousSelection = Selection.SceneNodes;
        SceneNode candidateRoot = new("Candidate scene root");
        try
        {
            Selection.SceneNode = candidateRoot;
            InvokePanel(_target, "RefreshRows");
            Assert.That(HasCandidateSection(), Is.True);
            NativeAnimationPanelViewReceipt receipt = Capture();

            NativeDockPanelStateReceipt applied = Restore(receipt);

            Assert.That(applied.IsCurrent, Is.True);
            Assert.That(HasCandidateSection(), Is.False,
                "Owned row projection must defer scene candidate discovery.");
            Set(_target, "_lastRefresh", DateTime.MinValue);
            InvokePanel(_target, "RefreshTick");
            Assert.That(HasCandidateSection(), Is.True);
            Assert.That(Get<AnimationMember>(_target, "_selectedMember"), Is.SameAs(_member));
            Assert.That(Get<Keyframe>(_target, "_selectedKey"), Is.SameAs(_key));
        }
        finally
        {
            Selection.SceneNodes = previousSelection;
            candidateRoot.Destroy(true);
        }

        bool HasCandidateSection() =>
            Get<List<(ulong Id, string Text)>>(_target, "_rows")
                .Exists(row => row.Id == 22 &&
                    row.Text.StartsWith("Scene path candidates:", StringComparison.Ordinal));
    }

    [TestCase("time")]
    [TestCase("value")]
    public void ToolbarVetoKeepsTheRejectedInputAndItsDisplay(string field)
    {
        NativeAnimationPanelViewReceipt receipt = Capture();
        UITextInputComponent input = field == "time" ? TimeInput : ValueInput;
        string prior = input.Text;
        string? priorDisplay = input.TextComponent.Text;
        int vetoes = 0;
        input.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName != nameof(UITextInputComponent.Text)) return;
            vetoes++;
            ((XRPropertyChangingEventArgs<string>)change).AllowChange = false;
        };

        Assert.Throws<InvalidOperationException>(() => Restore(receipt));
        Assert.That(vetoes, Is.GreaterThan(0));
        Assert.That(input.Text, Is.EqualTo(prior));
        Assert.That(input.TextComponent.Text, Is.EqualTo(priorDisplay));
    }

    [TestCase("time")]
    [TestCase("value")]
    public void InputPrechangeNestedTextWinsOverOldRestore(string field)
    {
        NativeAnimationPanelViewReceipt receipt = Capture();
        UITextInputComponent input = field == "time" ? TimeInput : ValueInput;
        bool nested = false;
        input.PropertyChanging += (_, change) =>
        {
            if (nested || change.PropertyName != nameof(UITextInputComponent.Text)) return;
            nested = true;
            input.Text = "newer input";
        };

        Assert.Throws<InvalidOperationException>(() => Restore(receipt));
        Assert.That(nested, Is.True);
        Assert.That(input.Text, Is.EqualTo("newer input"));
        Assert.That(input.TextComponent.Text, Is.EqualTo("newer input"));
    }

    [TestCase("time")]
    [TestCase("value")]
    public void InputPrechangeReparentRetainsItsNewOwner(string field)
    {
        NativeAnimationPanelViewReceipt receipt = Capture();
        UITextInputComponent input = field == "time" ? TimeInput : ValueInput;
        SceneNode foreign = _root.NewChild("Foreign owner");
        bool moved = false;
        input.PropertyChanging += (_, change) =>
        {
            if (moved || change.PropertyName != nameof(UITextInputComponent.Text)) return;
            moved = true;
            input.SceneNode!.Transform.SetParent(foreign.Transform, false,
                EParentAssignmentMode.Immediate);
        };

        Assert.Throws<InvalidOperationException>(() => Restore(receipt));
        Assert.That(moved, Is.True);
        Assert.That(input.SceneNode!.Parent, Is.SameAs(foreign));
        Assert.That(input.Text, Is.Not.EqualTo(field == "time" ? "0.75" : "2"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ValueCallbackChangingAcceptedTimeStopsTheSecondPublication(bool selectedKey)
    {
        if (!selectedKey) Set(_source, "_selectedKey", null);
        NativeAnimationPanelViewReceipt receipt = Capture();
        bool changed = false;
        ValueInput.PropertyChanging += (_, change) =>
        {
            if (changed || change.PropertyName != nameof(UITextInputComponent.Text)) return;
            changed = true;
            TimeInput.Text = "newer time";
        };

        Assert.Throws<InvalidOperationException>(() => Restore(receipt));
        Assert.That(changed, Is.True);
        Assert.That(TimeInput.Text, Is.EqualTo("newer time"));
        Assert.That(TimeInput.TextComponent.Text, Is.EqualTo("newer time"));
    }

    [TestCase("time")]
    [TestCase("value")]
    public void SiblingDisplayPrechangeKeepsItsNewerText(string field)
    {
        NativeAnimationPanelViewReceipt receipt = Capture();
        UITextInputComponent input = field == "time" ? TimeInput : ValueInput;
        UITextComponent display = input.TextComponent;
        bool nested = false;
        display.PropertyChanging += (_, change) =>
        {
            if (nested || change.PropertyName != nameof(UITextComponent.Text)) return;
            nested = true;
            display.Text = "newer display";
        };

        Assert.Throws<InvalidOperationException>(() => Restore(receipt));
        Assert.That(nested, Is.True);
        Assert.That(display.Text, Is.EqualTo("newer display"));
    }

    [TestCase("source")]
    [TestCase("destination")]
    [TestCase("clip")]
    [TestCase("key")]
    [TestCase("track")]
    [TestCase("root")]
    [TestCase("rows")]
    public void FirstInputCallbackCannotPublishAStaleView(string mutation)
    {
        if (mutation is "track" or "root") Set(_source, "_selectedKey", null);
        NativeAnimationPanelViewReceipt receipt = Capture();
        ulong targetEpochBefore = Get<ulong>(_target, "_authoringEpoch");
        bool changed = false;
        TimeInput.PropertyChanging += (_, change) =>
        {
            if (changed || change.PropertyName != nameof(UITextInputComponent.Text)) return;
            changed = true;
            switch (mutation)
            {
                case "source":
                    _source.BeginStateIntent();
                    Set(_source, "_selectedKey", null);
                    break;
                case "destination":
                    _target.BeginStateIntent();
                    Set(_target, "_graphView", false);
                    break;
                case "clip":
                    SelectClip(new AnimationClip());
                    break;
                case "key":
                    _animation.Keyframes.Remove(_key);
                    break;
                case "track":
                    _animation.Keyframes = new KeyframeTrack<FloatKeyframe>();
                    break;
                case "root":
                    _clip.RootMember = new AnimationMember("Replacement", EAnimationMemberType.Group);
                    break;
                case "rows":
                    InvokePanel(_target, "RefreshRows");
                    break;
            }
        };

        Assert.Throws<InvalidOperationException>(() => Restore(receipt));
        Assert.That(changed, Is.True);
        Assert.That(receipt.IsCurrent, Is.EqualTo(mutation is "destination" or "rows"));
        if (mutation == "destination")
            Assert.That(Get<bool>(_target, "_graphView"), Is.False);
        if (mutation == "source")
            Assert.That(Get<Keyframe?>(_source, "_selectedKey"), Is.Null);
        if (mutation == "clip")
            Assert.That(NativeAssetSelection.SelectedTarget, Is.Not.SameAs(_clip));
        if (mutation == "key")
            Assert.That(_animation.Keyframes, Does.Not.Contain(_key));
        if (mutation == "track")
            Assert.That(_animation.Keyframes, Is.Not.SameAs(receipt.Values.Track));
        if (mutation == "root")
            Assert.That(_clip.RootMember?.MemberName, Is.EqualTo("Replacement"));
        if (mutation == "rows")
            Assert.That(Get<ulong>(_target, "_authoringEpoch"),
                Is.GreaterThan(targetEpochBefore));
    }

    [Test]
    public void ScrollCallbackRefreshInvalidatesTheOldOffsetWrite()
    {
        NativeAnimationPanelViewReceipt receipt = Capture();
        UIListTransform list = (UIListTransform)typeof(NativeVirtualTextPanel)
            .GetField("_list", Fields)!.GetValue(_target)!;
        float before = list.ContentScrollOffset;
        bool refreshed = false;
        list.PropertyChanging += (_, change) =>
        {
            if (refreshed || change.PropertyName != nameof(UIListTransform.ContentScrollOffset)) return;
            refreshed = true;
            InvokePanel(_target, "RefreshRows");
        };
        NativeDockPanelStateValues requested = _sourceBinding.CaptureReceipt().Values
            with { ScrollOffset = before + 32 };

        Assert.Throws<InvalidOperationException>(() => _targetBinding.Restore(
            requested, () => receipt.IsCurrent, receipt.Values));
        Assert.That(refreshed, Is.True);
        Assert.That(list.ContentScrollOffset, Is.EqualTo(before));
        Assert.That(receipt.IsCurrent, Is.True);
    }

    [TestCase("time")]
    [TestCase("value")]
    [TestCase("timeDisplay")]
    [TestCase("valueDisplay")]
    public void FinalScrollCallbackTextOnlyEditRetainsNewerToolbarIntent(string target)
    {
        NativeAnimationPanelViewReceipt receipt = Capture();
        UIListTransform list = (UIListTransform)typeof(NativeVirtualTextPanel)
            .GetField("_list", Fields)!.GetValue(_target)!;
        float requestedOffset = list.ContentScrollOffset + 32;
        float offsetAtChanging = float.NaN;
        ulong epochAtChanging = 0;
        bool changed = false;
        list.PropertyChanging += (_, change) =>
        {
            if (changed || change.PropertyName != nameof(UIListTransform.ContentScrollOffset)) return;
            changed = true;
            offsetAtChanging = list.ContentScrollOffset;
            epochAtChanging = Get<ulong>(_target, "_authoringEpoch");
            switch (target)
            {
                case "time": TimeInput.Text = "newer time"; break;
                case "value": ValueInput.Text = "newer value"; break;
                case "timeDisplay": TimeInput.TextComponent.Text = "newer display"; break;
                default: ValueInput.TextComponent.Text = "newer display"; break;
            }
        };
        NativeDockPanelStateValues requested = _sourceBinding.CaptureReceipt().Values
            with { ScrollOffset = requestedOffset };

        Assert.Throws<InvalidOperationException>(() => _targetBinding.Restore(
            requested, () => receipt.IsCurrent, receipt.Values));
        Assert.That(changed, Is.True);
        Assert.That(list.ContentScrollOffset, Is.EqualTo(offsetAtChanging));
        Assert.That(Get<ulong>(_target, "_authoringEpoch"), Is.EqualTo(epochAtChanging));
        Assert.That(receipt.IsCurrent, Is.True);
        switch (target)
        {
            case "time":
                Assert.That(TimeInput.Text, Is.EqualTo("newer time"));
                Assert.That(TimeInput.TextComponent.Text, Is.EqualTo("newer time"));
                break;
            case "value":
                Assert.That(ValueInput.Text, Is.EqualTo("newer value"));
                Assert.That(ValueInput.TextComponent.Text, Is.EqualTo("newer value"));
                break;
            case "timeDisplay":
                Assert.That(TimeInput.TextComponent.Text, Is.EqualTo("newer display"));
                break;
            default:
                Assert.That(ValueInput.TextComponent.Text, Is.EqualTo("newer display"));
                break;
        }
    }

    [TestCase("scroll")]
    [TestCase("bounds")]
    public void EarlyRowProjectionCallbackCannotCommitStaleScrollOrBounds(string boundary)
    {
        NativeAnimationPanelViewReceipt receipt = Capture();
        UIListTransform list = (UIListTransform)typeof(NativeVirtualTextPanel)
            .GetField("_list", Fields)!.GetValue(_target)!;
        if (boundary == "scroll") list.ContentScrollOffset = 100_000;
        else list.UpperVirtualBound = -1;
        float scrollBefore = list.ContentScrollOffset;
        float upperBefore = list.UpperVirtualBound;
        float lowerBefore = list.LowerVirtualBound;
        string property = boundary == "scroll" ? nameof(UIListTransform.ContentScrollOffset)
            : nameof(UIListTransform.UpperVirtualBound);
        bool early = false;
        list.PropertyChanging += (_, change) =>
        {
            if (early || change.PropertyName != property) return;
            early = true;
            Assert.That(TimeInput.Text, Is.EqualTo("prior time"),
                "The callback must run during row projection, before toolbar publication.");
            TimeInput.Text = "newer early time";
        };

        Assert.Throws<InvalidOperationException>(() => Restore(receipt));
        Assert.That(early, Is.True);
        Assert.That(TimeInput.Text, Is.EqualTo("newer early time"));
        Assert.That(TimeInput.TextComponent.Text, Is.EqualTo("newer early time"));
        Assert.That(list.ContentScrollOffset, Is.EqualTo(scrollBefore));
        Assert.That(list.UpperVirtualBound, Is.EqualTo(upperBefore));
        Assert.That(list.LowerVirtualBound, Is.EqualTo(lowerBefore));
    }

    [TestCase("time", false)]
    [TestCase("time", true)]
    [TestCase("value", false)]
    [TestCase("value", true)]
    public void CaretPrechangeCannotContinueAfterInputTextOrParentChanges(string field,
        bool reparent)
    {
        NativeAnimationPanelViewReceipt receipt = Capture();
        UITextInputComponent input = field == "time" ? TimeInput : ValueInput;
        input.CursorPosition = 8;
        input.SelectionAnchor = 6;
        string? oldDisplay = input.TextComponent.Text;
        SceneNode foreign = _root.NewChild("Caret callback owner");
        bool changed = false;
        input.PropertyChanging += (_, change) =>
        {
            if (changed || change.PropertyName != nameof(UITextInputComponent.CursorPosition)) return;
            changed = true;
            if (reparent)
                input.SceneNode!.Transform.SetParent(foreign.Transform, false,
                    EParentAssignmentMode.Immediate);
            else
                input.Text = "newer caret text";
        };

        Assert.Throws<InvalidOperationException>(() => Restore(receipt));
        Assert.That(changed, Is.True);
        if (reparent)
        {
            Assert.That(input.SceneNode!.Parent, Is.SameAs(foreign));
            Assert.That(input.TextComponent.Text, Is.EqualTo(oldDisplay));
        }
        else
        {
            Assert.That(input.Text, Is.EqualTo("newer caret text"));
            Assert.That(input.TextComponent.Text, Is.EqualTo("newer caret text"));
        }
    }

    [Test]
    public void PendingValueRefreshRejectsBeforePublishingASecondField()
    {
        NativeAnimationPanelViewReceipt receipt = Capture();
        bool pending = false;
        TimeInput.PropertyChanged += (_, change) =>
        {
            if (pending || change.PropertyName != nameof(UITextInputComponent.Text)) return;
            pending = true;
            Set(_target, "_valueInputRefreshPending", true);
        };

        Assert.Throws<InvalidOperationException>(() => Restore(receipt));
        Assert.That(pending, Is.True);
        Assert.That(Get<bool>(_target, "_valueInputRefreshPending"), Is.True);
    }

    [Test]
    public void SelectedMemberWithoutKeyPinsItsTrackUntilFreshCapture()
    {
        Set(_source, "_selectedKey", null);
        NativeAnimationPanelViewReceipt old = Capture();
        Assert.That(old.Values.Key, Is.Null);
        Assert.That(old.Values.Track, Is.SameAs(_animation.Keyframes));
        _animation.Keyframes = new KeyframeTrack<FloatKeyframe>();
        Assert.That(old.IsCurrent, Is.False);
        NativeAnimationPanelViewReceipt fresh = Capture();
        Assert.That(fresh.Values.Track, Is.SameAs(_animation.Keyframes));
    }

    [Test]
    public void InactivePanelCannotCompensateItsOldClipAfterSelectionChanges()
    {
        NativeAnimationPanelViewValues prior = _source.CapturePriorView();
        Assert.That(prior.Clip, Is.SameAs(_clip));
        _source.SceneNode!.IsActiveSelf = false;
        AnimationClip current = new(new AnimationMember("Other", EAnimationMemberType.Group));
        SelectClip(current);
        Assert.That(_source.CanRestorePriorView(prior), Is.False);
        Assert.That(NativeAssetSelection.SelectedTarget, Is.SameAs(current));
    }

    private UITextInputComponent TimeInput => Get<UITextInputComponent>(_target, "_timeInput");
    private UITextInputComponent ValueInput => Get<UITextInputComponent>(_target, "_valueInput");

    private NativeAnimationPanelViewReceipt Capture()
    {
        Assert.That(_source.TryCaptureViewReceipt(out NativeAnimationPanelViewReceipt receipt), Is.True);
        return receipt;
    }

    private NativeDockPanelStateReceipt Restore(NativeAnimationPanelViewReceipt receipt)
        => _targetBinding.Restore(_sourceBinding.CaptureReceipt().Values,
            () => receipt.IsCurrent, receipt.Values);

    private static T Get<T>(AnimationPanel panel, string field)
        => (T)typeof(AnimationPanel).GetField(field, Fields)!.GetValue(panel)!;

    private static void Set(AnimationPanel panel, string field, object? value)
        => typeof(AnimationPanel).GetField(field, Fields)!.SetValue(panel, value);

    private static void InvokePanel(AnimationPanel panel, string method)
    {
        try { typeof(AnimationPanel).GetMethod(method, Fields)!.Invoke(panel, null); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }

    private static void SelectClip(object? clip)
        => typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
            .SetValue(null, clip);
}
