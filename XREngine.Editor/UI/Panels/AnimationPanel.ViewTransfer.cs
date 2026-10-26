using System.Collections;
using System.Globalization;
using XREngine.Animation;
using XREngine.Components.Animation;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public partial class AnimationPanel
{
    internal bool TryCaptureViewReceipt(out NativeAnimationPanelViewReceipt receipt)
    {
        receipt = default;
        if (_destroying || IsDestroyed || IsDestroyQueued || !IsActiveInHierarchy ||
            !ReferenceEquals(_displayedClip, CurrentClip()))
            return false;
        AnimationClip? clip = _displayedClip;
        NativeAnimationPanelViewValues values = CaptureViewValues(clip);
        if (!ViewContentCurrent(values)) return false;
        if (!NativeAnimationSearchFieldReceipt.TryCapture(this, _searchInput, out var search))
            return false;
        receipt = new(this, ObjectConstructionIdentity.Capture(this), values,
            _authoringEpoch, _sourceRevision, _plotViewRevision, StateIntentRevision,
            () => ReferenceEquals(_searchInput, search.Input) && search.IsCurrent(0, values.Candidates.Query));
        return receipt.IsCurrent;
    }

    internal NativeAnimationPanelViewValues CapturePriorView()
        => CaptureViewValues(_displayedClip);

    internal bool CanRestorePriorView(NativeAnimationPanelViewValues values)
        => ReferenceEquals(CurrentClip(), values.Clip) &&
            ReferenceEquals(_displayedClip, values.Clip) &&
            ViewContentCurrent(values);

    private NativeAnimationPanelViewValues CaptureViewValues(AnimationClip? clip)
    {
        BasePropAnim? animation = _selectedMember?.Animation;
        return new(clip, clip is null ? null : ObjectConstructionIdentity.Capture(clip),
            clip?.LengthInSeconds ?? 0, clip?.SampleRate ?? 0,
            clip?.RootMember, _selectedMember, animation,
            animation is null ? null : ObjectConstructionIdentity.Capture(animation),
            _selectedKey, TrackFor(animation), _memberPage, _keyPage,
            _graphView, _plotZoom, _plotWindowStart, _stretchLengthEdits, _valueChannel, CaptureCandidateView());
    }

    internal bool IsViewCurrent(NativeAnimationPanelViewReceipt receipt)
    {
        NativeAnimationPanelViewValues values = receipt.Values;
        return !_destroying && !IsDestroyed && !IsDestroyQueued &&
            IsActiveInHierarchy && values.HasValidScalars &&
            ReferenceEquals(CurrentClip(), values.Clip) &&
            ReferenceEquals(_displayedClip, values.Clip) &&
            _authoringEpoch == receipt.AuthoringEpoch &&
            _sourceRevision == receipt.SourceRevision &&
            _plotViewRevision == receipt.PlotViewRevision &&
            StateIntentRevision == receipt.StateIntentRevision &&
            ReferenceEquals(_selectedMember, values.Member) &&
            ReferenceEquals(_selectedKey, values.Key) &&
            _memberPage == values.MemberPage && _keyPage == values.KeyPage &&
            _graphView == values.GraphView && _plotZoom == values.PlotZoom &&
            _plotWindowStart == values.PlotWindowStart &&
            _stretchLengthEdits == values.StretchLengthEdits &&
            _valueChannel == values.ValueChannel && CaptureCandidateView() == values.Candidates &&
            ViewContentCurrent(values);
    }

    private static bool ViewContentCurrent(NativeAnimationPanelViewValues values)
    {
        if (!values.HasValidScalars || !ReferenceEquals(values.Candidates.Root, Selection.SceneNode))
            return false;
        if (values.Member is not { } member)
            return values.Key is null && values.Animation is null;
        if (values.Clip is not { } clip ||
            !AnimationClipAuthoringActions.ContainsMember(clip, member) ||
            !ReferenceEquals(member.Animation, values.Animation) ||
            !ReferenceEquals(TrackFor(values.Animation), values.Track) ||
            values.Animation is BasePropAnimKeyframed && values.Track is null)
            return false;
        return values.Key is null ||
            ReferenceEquals(values.Key.OwningTrack, values.Track) &&
            MemberContainsKey(member, values.Key);
    }

    internal NativeAnimationPanelViewReceipt RestoreViewOwned(
        NativeAnimationPanelViewValues values, NativeToolPanelStateBinding binding,
        ulong intent)
    {
        binding.RequireOwnedState(intent);
        if (!ReferenceEquals(CurrentClip(), values.Clip) ||
            !ReferenceEquals(_displayedClip, values.Clip) ||
            !ViewContentCurrent(values))
            throw new InvalidOperationException("The Animation view source changed.");
        ObjectConstructionIdentity panelIdentity = ObjectConstructionIdentity.Capture(this);
        ulong expectedEpoch = _authoringEpoch;
        ulong expectedSource = _sourceRevision;
        ulong expectedView = _plotViewRevision;
        NativeAnimationPanelViewValues? expectedValues = null;
        Func<bool>? toolbarProgress = null;
        bool LocalViewCurrent()
        {
            if (_destroying || !ReferenceEquals(CurrentClip(), values.Clip) ||
                !ReferenceEquals(_displayedClip, values.Clip) ||
                _authoringEpoch != expectedEpoch ||
                _sourceRevision != expectedSource ||
                _plotViewRevision != expectedView ||
                !ViewContentCurrent(values))
                return false;
            if (expectedValues is { } expected &&
                (!ReferenceEquals(_selectedMember, expected.Member) ||
                 !ReferenceEquals(_selectedKey, expected.Key) ||
                 _memberPage != expected.MemberPage || _keyPage != expected.KeyPage ||
                 _graphView != expected.GraphView || _plotZoom != expected.PlotZoom ||
                 _plotWindowStart != expected.PlotWindowStart ||
                 _stretchLengthEdits != expected.StretchLengthEdits ||
                 _valueChannel != expected.ValueChannel || CaptureCandidateView() != expected.Candidates))
                return false;
            return true;
        }
        void RequireCurrent()
        {
            binding.RequireOwnedState(intent);
            if (!LocalViewCurrent() || toolbarProgress is { } firstToolbar && !firstToolbar())
                throw new InvalidOperationException("The Animation toolbar changed during restore.");
            binding.RequireOwnedState(intent);
            if (!LocalViewCurrent() || toolbarProgress is { } finalToolbar && !finalToolbar())
                throw new InvalidOperationException("The Animation toolbar changed during validation.");
        }
        RequireCurrent();
        if (!NativeAnimationSearchFieldReceipt.TryCapture(this, _searchInput, out var search))
            throw new InvalidOperationException("The Animation search field is unavailable during restore.");
        int searchPhase = 0;
        if (_timeInput is not { } timeInput || _valueInput is not { } valueInput ||
            timeInput.SceneNode is not { } timeNode ||
            valueInput.SceneNode is not { } valueNode ||
            timeNode.GetComponent<UITextComponent>() is not { } timeDisplay ||
            valueNode.GetComponent<UITextComponent>() is not { } valueDisplay ||
            _refreshingValueInput || _valueInputRefreshPending)
            throw new InvalidOperationException("The Animation toolbar is unavailable during restore.");
        ObjectConstructionIdentity timeIdentity = ObjectConstructionIdentity.Capture(timeInput);
        ObjectConstructionIdentity valueIdentity = ObjectConstructionIdentity.Capture(valueInput);
        ObjectConstructionIdentity timeDisplayIdentity = ObjectConstructionIdentity.Capture(timeDisplay);
        ObjectConstructionIdentity valueDisplayIdentity = ObjectConstructionIdentity.Capture(valueDisplay);
        ObjectConstructionIdentity timeNodeIdentity = ObjectConstructionIdentity.Capture(timeNode);
        ObjectConstructionIdentity valueNodeIdentity = ObjectConstructionIdentity.Capture(valueNode);
        TransformBase timeTransform = timeNode.Transform;
        TransformBase valueTransform = valueNode.Transform;
        ObjectConstructionIdentity timeTransformIdentity = ObjectConstructionIdentity.Capture(timeTransform);
        ObjectConstructionIdentity valueTransformIdentity = ObjectConstructionIdentity.Capture(valueTransform);
        SceneNode? timeParent = timeNode.Parent;
        SceneNode? valueParent = valueNode.Parent;
        ObjectConstructionIdentity? timeParentIdentity = timeParent is null ? null :
            ObjectConstructionIdentity.Capture(timeParent);
        ObjectConstructionIdentity? valueParentIdentity = valueParent is null ? null :
            ObjectConstructionIdentity.Capture(valueParent);
        ulong timeInputRevision = timeInput.TextWriteIntentRevision;
        ulong valueInputRevision = valueInput.TextWriteIntentRevision;
        ulong timeDisplayRevision = timeDisplay.TextWriteIntentRevision;
        ulong valueDisplayRevision = valueDisplay.TextWriteIntentRevision;
        bool timeFocused = timeInput.IsFocused;
        bool valueFocused = valueInput.IsFocused;
        string timePreText = timeInput.PreText;
        string timePostText = timeInput.PostText;
        string valuePreText = valueInput.PreText;
        string valuePostText = valueInput.PostText;
        AnimationClipComponent? playback = values.Key is null ? SelectedMatchingComponent() : null;
        ObjectConstructionIdentity? playbackIdentity = playback is null ? null :
            ObjectConstructionIdentity.Capture(playback);
        float sampledTime = values.Key?.Second ?? playback?.PlaybackTime ?? 0.0f;
        string expectedTime = sampledTime.ToString("0.###", CultureInfo.InvariantCulture);
        string? expectedValue = null;
        string expectedTimeDisplay = timeFocused ? expectedTime :
            string.Concat(timePreText, expectedTime, timePostText);
        string? expectedValueDisplay = null;
        bool Attached(SceneNode node, object component)
        {
            for (int index = 0; index < node.Components.Count; index++)
                if (ReferenceEquals(node.Components[index], component)) return true;
            return false;
        }
        bool UnderPanel(TransformBase child)
        {
            for (TransformBase? ancestor = child.Parent; ancestor is not null;
                 ancestor = ancestor.Parent)
                if (ReferenceEquals(ancestor, Transform)) return true;
            return false;
        }
        bool InputsCurrent() => timeIdentity.IsCurrent && valueIdentity.IsCurrent &&
            timeDisplayIdentity.IsCurrent && valueDisplayIdentity.IsCurrent &&
            timeNodeIdentity.IsCurrent && valueNodeIdentity.IsCurrent &&
            timeTransformIdentity.IsCurrent && valueTransformIdentity.IsCurrent &&
            timeParentIdentity?.IsCurrent == true && valueParentIdentity?.IsCurrent == true &&
            !timeInput.IsDestroyed && !timeInput.IsDestroyQueued &&
            !valueInput.IsDestroyed && !valueInput.IsDestroyQueued &&
            !timeDisplay.IsDestroyed && !timeDisplay.IsDestroyQueued &&
            !valueDisplay.IsDestroyed && !valueDisplay.IsDestroyQueued &&
            !timeNode.IsDestroyed && !timeNode.IsDestroyQueued &&
            !valueNode.IsDestroyed && !valueNode.IsDestroyQueued &&
            ReferenceEquals(_timeInput, timeInput) && ReferenceEquals(_valueInput, valueInput) &&
            ReferenceEquals(timeInput.SceneNode, timeNode) &&
            ReferenceEquals(valueInput.SceneNode, valueNode) &&
            ReferenceEquals(timeDisplay.SceneNode, timeNode) &&
            ReferenceEquals(valueDisplay.SceneNode, valueNode) &&
            ReferenceEquals(timeNode.GetComponent<UITextComponent>(), timeDisplay) &&
            ReferenceEquals(valueNode.GetComponent<UITextComponent>(), valueDisplay) &&
            Attached(timeNode, timeInput) && Attached(timeNode, timeDisplay) &&
            Attached(valueNode, valueInput) && Attached(valueNode, valueDisplay) &&
            ReferenceEquals(timeNode.Transform, timeTransform) &&
            ReferenceEquals(valueNode.Transform, valueTransform) &&
            ReferenceEquals(timeNode.Parent, timeParent) &&
            ReferenceEquals(valueNode.Parent, valueParent) &&
            UnderPanel(timeTransform) && UnderPanel(valueTransform) &&
            timeInput.IsFocused == timeFocused && valueInput.IsFocused == valueFocused &&
            timeInput.PreText == timePreText && timeInput.PostText == timePostText &&
            valueInput.PreText == valuePreText && valueInput.PostText == valuePostText;
        bool PlaybackCurrent() => values.Key is not null
            ? values.Key.Second == sampledTime &&
                (expectedValue is null || FormatSelectedValue(values.Key) == expectedValue)
            : ReferenceEquals(SelectedMatchingComponent(), playback) &&
                (playback is null || playbackIdentity?.IsCurrent == true &&
                    !playback.IsDestroyed && !playback.IsDestroyQueued &&
                    playback.PlaybackTime == sampledTime);
        ulong acceptedTimeInputRevision = checked(timeInputRevision + 1);
        ulong acceptedTimeDisplayRevision = checked(timeDisplayRevision + 1);
        ulong acceptedValueInputRevision = checked(valueInputRevision + 1);
        ulong acceptedValueDisplayRevision = checked(valueDisplayRevision + 1);
        int toolbarPhase = 0;
        bool TimeAccepted() =>
            timeInput.TextWriteIntentRevision == acceptedTimeInputRevision &&
            timeDisplay.TextWriteIntentRevision == acceptedTimeDisplayRevision &&
            timeInput.Text == expectedTime && timeDisplay.Text == expectedTimeDisplay;
        bool ValueAccepted() =>
            valueInput.TextWriteIntentRevision == acceptedValueInputRevision &&
            valueDisplay.TextWriteIntentRevision == acceptedValueDisplayRevision &&
            valueInput.Text == expectedValue && valueDisplay.Text == expectedValueDisplay;
        bool ToolbarPhaseCurrent()
        {
            if (!ReferenceEquals(_searchInput, search.Input) ||
                !search.IsCurrent(searchPhase, values.Candidates.Query) ||
                !InputsCurrent() || !PlaybackCurrent() || _refreshingValueInput ||
                _valueInputRefreshPending || toolbarPhase != 0 && _projectionDirty)
                return false;
            return toolbarPhase switch
            {
                0 => timeInput.TextWriteIntentRevision == timeInputRevision &&
                    timeDisplay.TextWriteIntentRevision == timeDisplayRevision &&
                    valueInput.TextWriteIntentRevision == valueInputRevision &&
                    valueDisplay.TextWriteIntentRevision == valueDisplayRevision,
                1 => timeInput.TextWriteIntentRevision == acceptedTimeInputRevision &&
                    (timeDisplay.TextWriteIntentRevision == timeDisplayRevision ||
                     timeDisplay.TextWriteIntentRevision == acceptedTimeDisplayRevision) &&
                    valueInput.TextWriteIntentRevision == valueInputRevision &&
                    valueDisplay.TextWriteIntentRevision == valueDisplayRevision,
                2 => TimeAccepted() &&
                    valueInput.TextWriteIntentRevision == valueInputRevision &&
                    valueDisplay.TextWriteIntentRevision == valueDisplayRevision,
                3 => TimeAccepted() &&
                    valueInput.TextWriteIntentRevision == acceptedValueInputRevision &&
                    (valueDisplay.TextWriteIntentRevision == valueDisplayRevision ||
                     valueDisplay.TextWriteIntentRevision == acceptedValueDisplayRevision),
                4 => TimeAccepted() && ValueAccepted(),
                _ => false,
            };
        }
        toolbarProgress = ToolbarPhaseCurrent;
        if (!ToolbarPhaseCurrent())
            throw new InvalidOperationException("The Animation toolbar source changed.");
        int memberCount = CountMembers(values.Clip?.RootMember);
        int memberPage = Math.Clamp(values.MemberPage, 0,
            Math.Max(0, (memberCount - 1) / RowPageSize));
        int keyCount = CountKeys(values.Member?.Animation);
        int keyPage = Math.Clamp(values.KeyPage, 0,
            Math.Max(0, (keyCount - 1) / RowPageSize));
        float duration = values.ClipLength / values.PlotZoom;
        float windowStart = Math.Clamp(values.PlotWindowStart, 0,
            MathF.Max(0, values.ClipLength - duration));
        RequireCurrent();
        expectedValues = values with
        {
            MemberPage = memberPage,
            KeyPage = keyPage,
            PlotWindowStart = windowStart,
        };
        // The field commit has no callbacks. Publication below checks the fixed receipt.
        SetFieldValidated(ref _selectedMember, values.Member, 0,
            static (_, _, _, _) => { }, publishNotifications: false);
        SetFieldValidated(ref _selectedKey, values.Key, 0,
            static (_, _, _, _) => { }, publishNotifications: false);
        SetFieldValidated(ref _memberPage, memberPage, 0,
            static (_, _, _, _) => { }, publishNotifications: false);
        SetFieldValidated(ref _keyPage, keyPage, 0,
            static (_, _, _, _) => { }, publishNotifications: false);
        SetFieldValidated(ref _graphView, values.GraphView, 0,
            static (_, _, _, _) => { }, publishNotifications: false);
        SetFieldValidated(ref _plotZoom, values.PlotZoom, 0,
            static (_, _, _, _) => { }, publishNotifications: false);
        SetFieldValidated(ref _plotWindowStart, windowStart, 0,
            static (_, _, _, _) => { }, publishNotifications: false);
        SetFieldValidated(ref _stretchLengthEdits, values.StretchLengthEdits, 0,
            static (_, _, _, _) => { }, publishNotifications: false);
        SetFieldValidated(ref _valueChannel, values.ValueChannel, 0,
            static (_, _, _, _) => { }, publishNotifications: false);
        StageCandidateView(values.Candidates);
        expectedValue = values.Key is null ? string.Empty : FormatSelectedValue(values.Key);
        expectedValueDisplay = valueFocused ? expectedValue :
            string.Concat(valuePreText, expectedValue, valuePostText);
        _authoringEpoch++;
        _plotViewRevision++;
        expectedEpoch++;
        expectedView++;
        _plotSnapshot = null;
        _activePlotDrag = null;
        _activePlotScrub = null;
        _plotRefreshPending = true;
        RequireCurrent();
        NativeAnimationPanelViewReceipt appliedView = new(this, panelIdentity,
            expectedValues.Value, expectedEpoch, expectedSource, expectedView,
            StateIntentRevision, toolbarProgress);
        binding.RetainAnimationRestoreView(appliedView, intent);
        RequireCurrent();
        RefreshRowsOwned(RequireCurrent);
        RequireCurrent();
        void RequireToolbar()
        {
            RequireCurrent();
            if (_projectionDirty || _refreshingValueInput || _valueInputRefreshPending ||
                !InputsCurrent() || !PlaybackCurrent())
                throw new InvalidOperationException("The Animation toolbar source changed during restore.");
            RequireCurrent();
        }
        RequireToolbar();
        if (timeInput.TextWriteIntentRevision != timeInputRevision ||
            valueInput.TextWriteIntentRevision != valueInputRevision ||
            timeDisplay.TextWriteIntentRevision != timeDisplayRevision ||
            valueDisplay.TextWriteIntentRevision != valueDisplayRevision)
            throw new InvalidOperationException("The Animation toolbar was edited during row projection.");
        toolbarPhase = 1;
        void RequireTimeWrite()
        {
            RequireToolbar();
            if (timeInput.TextWriteIntentRevision != acceptedTimeInputRevision ||
                valueInput.TextWriteIntentRevision != valueInputRevision ||
                valueDisplay.TextWriteIntentRevision != valueDisplayRevision)
                throw new InvalidOperationException("The Animation time write lost its peer.");
        }
        timeInput.SetTextGuarded(expectedTime, (Action)RequireTimeWrite,
            static require => require());
        RequireToolbar();
        if (timeInput.TextWriteIntentRevision != acceptedTimeInputRevision ||
            timeDisplay.TextWriteIntentRevision != acceptedTimeDisplayRevision ||
            valueInput.TextWriteIntentRevision != valueInputRevision ||
            valueDisplay.TextWriteIntentRevision != valueDisplayRevision ||
            timeInput.Text != expectedTime || timeDisplay.Text != expectedTimeDisplay)
            throw new InvalidOperationException("The Animation time field did not accept the restored view.");
        toolbarPhase = 2;
        RequireToolbar();
        toolbarPhase = 3;
        void RequireValueWrite()
        {
            RequireToolbar();
            if (timeInput.TextWriteIntentRevision != acceptedTimeInputRevision ||
                timeDisplay.TextWriteIntentRevision != acceptedTimeDisplayRevision ||
                timeInput.Text != expectedTime || timeDisplay.Text != expectedTimeDisplay ||
                valueInput.TextWriteIntentRevision != acceptedValueInputRevision)
                throw new InvalidOperationException("The Animation value write lost its peer.");
        }
        valueInput.SetTextGuarded(expectedValue!, (Action)RequireValueWrite,
            static require => require());
        RequireToolbar();
        if (timeInput.TextWriteIntentRevision != acceptedTimeInputRevision ||
            timeDisplay.TextWriteIntentRevision != acceptedTimeDisplayRevision ||
            valueInput.TextWriteIntentRevision != acceptedValueInputRevision ||
            valueDisplay.TextWriteIntentRevision != acceptedValueDisplayRevision ||
            timeInput.Text != expectedTime || timeDisplay.Text != expectedTimeDisplay ||
            valueInput.Text != expectedValue || valueDisplay.Text != expectedValueDisplay)
            throw new InvalidOperationException("The Animation value field did not accept the restored view.");
        toolbarPhase = 4;
        RequireCurrent();
        searchPhase = 1;
        search.Input.SetTextGuarded(values.Candidates.Query, (Action)RequireCurrent,
            static require => require());
        searchPhase = 2;
        RequireCurrent();
        return appliedView;
    }

    private static int CountKeys(BasePropAnim? animation)
    {
        int count = 0;
        if (animation is IEnumerable keys)
            foreach (object? item in keys)
                if (item is Keyframe) count++;
        return count;
    }

    private static int CountMembers(AnimationMember? root)
    {
        if (root is null) return 0;
        Stack<AnimationMember> pending = new();
        HashSet<AnimationMember> seen = new(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.Count > 0)
        {
            AnimationMember member = pending.Pop();
            if (!seen.Add(member)) continue;
            for (int index = 0; index < member.Children.Count; index++)
                pending.Push(member.Children[index]);
        }
        return seen.Count;
    }
}
