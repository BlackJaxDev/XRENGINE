using System.Collections;
using System.Globalization;
using System.Numerics;
using XREngine.Animation;
using XREngine.Components.Animation;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Edits clip keys and shows playback for the selected component.</summary>
public partial class AnimationPanel : NativeVirtualTextPanel
{
    private const int RowPageSize = 128;
    private const int MaxRetainedRowIds = 8192;
    private const ulong LastCommandRowId = 63;
    private const ulong FirstDynamicRowId = 1024;
    private const ulong HoverRowId = 67;
    private static readonly Vector4[] PlotPalette = [new(0.93f, 0.39f, 0.32f, 1),
        new(0.22f, 0.67f, 0.92f, 1), new(0.37f, 0.80f, 0.49f, 1), new(0.92f, 0.77f, 0.24f, 1)];
    private readonly List<(ulong Id, string Text)> _rows = [];
    private readonly NativeTextList _projectedRows = new();
    private readonly Dictionary<ulong, int> _memberDepths = [];
    private readonly Dictionary<object, ulong> _ids = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ulong, AnimationMember> _memberRows = [];
    private readonly List<AnimationMember> _visiblePlotMembers = [];
    private readonly Dictionary<ulong, Keyframe> _keyRows = [];
    private readonly Dictionary<ulong, EditorImGuiUI.AnimationClipPathCandidate> _pathRows = [];
    private AnimationClip? _displayedClip;
    private AnimationMember? _selectedMember;
    private Keyframe? _selectedKey;
    private UITextInputComponent? _timeInput;
    private UITextInputComponent? _valueInput;
    private bool _refreshingValueInput;
    private bool _valueInputRefreshPending;
    private NativeAnimationSearchInputComponent? _searchInput;
    private NativeAnimationSearchStopReceipt? _searchDraft;
    private SceneNode? _candidateRoot;
    private IReadOnlyList<EditorImGuiUI.AnimationClipPathCandidate> _candidates = [];
    private EditorImGuiUI.AnimationClipPathCandidate? _selectedCandidate;
    private string _pathSearch = string.Empty;
    private int _candidatePage;
    private int _candidateMatchCount;
    private int _memberPage;
    private int _memberCount;
    private int _keyPage;
    private int _keyCount;
    private int _statusRowIndex = -1;
    private int _hoverRowIndex = -1;
    private AnimationMember? _hoveredKeyMember;
    private Keyframe? _hoveredKey;
    private bool _projectionDirty = true;
    private bool _deferredCandidateRefresh;
    private bool _sourceTopologyDirty = true;
    private ulong _sourceRevision;
    private ulong _authoringEpoch;
    private ulong _buildingSourceRevision;
    private AnimationClip? _observedClip;
    private readonly Dictionary<AnimationMember,
        EventList<AnimationMember>> _observedMembers =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<BaseKeyframeTrack> _observedTracks =
        new(ReferenceEqualityComparer.Instance);
    private UIRetainedPlot? _plot;
    private AnimationPlotInputComponent? _plotInput;
    private AnimationPlotPointerSnapshot? _plotSnapshot;
    private ulong _plotViewRevision;
    private ulong _plotBuildRevision;
    private bool _plotBuilding;
    private bool _plotRefreshPending;
    private int _plotRetryDepth;
    private AnimationMember[] _plotBuildMembers = [];
    private BasePropAnim?[] _plotBuildAnimations = [];
    private AnimationMember? _plotBuildSelectedMember;
    private BasePropAnim? _plotBuildSelectedAnimation;
    private float _plotBuildClipLength;
    private readonly Func<bool> _plotValidator;
    private UIRetainedPlot? _activePlotBuildPlot;
    private AnimationClip? _activePlotBuildClip;
    private ulong _activePlotBuildSourceRevision;
    private ulong _activePlotBuildViewRevision;
    private ulong _activePlotBuildRevision;
    private bool _plotBuildHidden;
    private readonly List<(AnimationMember Member, Keyframe Key,
        BaseKeyframeTrack? Track, float Second, Vector2 Point)> _plotKeyHits = [];
    private float _plottedWidth;
    private float _plottedHeight;
    private float _plottedStart;
    private float _plottedDuration;
    private readonly NativeAnimationClipViewMemory _clipViews = new();
    private float _plotZoom = 1;
    private float _plotWindowStart;
    // The value-axis view is transient: a clip change, a view reset, and a view restore return it to the full range.
    private float _plotValueZoom = AnimationPlotValueZoom.MinimumZoom;
    private float _plotValueCenter = AnimationPlotValueZoom.DefaultCenter;
    private readonly float[,] _plotSamples = new float[4, UIRetainedPlot.MaximumSegments / 4 + 1];
    private bool _graphView = true;
    private bool _stretchLengthEdits;
    private int _valueChannel = -1;
    private ulong _nextId = FirstDynamicRowId;
    private DateTime _lastRefresh;
    private bool _ownerSubscriptionsActive;
    private bool _destroying;
    private ulong _searchLifecycleRevision;
    internal ulong AuthoringEpoch => _authoringEpoch;
    internal string CommittedPathSearch => _pathSearch;
    internal AnimationClip? CurrentSearchClip => CurrentClip();

    public AnimationPanel()
    {
        _plotValidator = ValidateActivePlotBuild;
        RowProviderOverride = _projectedRows;
        _projectedRows.Activated = OnRowActivated;
    }

    protected override void OnComponentActivated()
    {
        ulong activation = ++_searchLifecycleRevision;
        _authoringEpoch++;
        if (_destroying)
            return;
        ResumeSearchDraft(_searchInput, beforePanelActivation: false);
        if (_destroying || _searchLifecycleRevision != activation || IsDestroyed || IsDestroyQueued)
            return;
        base.OnComponentActivated();
        if (_destroying || _searchLifecycleRevision != activation || IsDestroyed || IsDestroyQueued)
            return;
        if (!_ownerSubscriptionsActive)
        {
            _ownerSubscriptionsActive = true;
            Selection.SelectionChanged += SelectionChanged;
            NativeAssetSelection.SelectionChanged += AssetSelectionChanged;
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
        }
        RefreshRows();
    }

    protected override void OnComponentDeactivated()
    {
        ulong deactivation = ++_searchLifecycleRevision;
        _authoringEpoch++;
        CaptureSearchDraft();
        if (_searchLifecycleRevision != deactivation)
            return;
        ReleaseOwnerSubscriptions();
        if (_searchLifecycleRevision != deactivation)
            return;
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        _searchLifecycleRevision++;
        _authoringEpoch++;
        _destroying = true;
        _clipViews.Clear();
        _searchDraft?.Dispose();
        _searchDraft = null;
        UITextInputComponent? time = _timeInput;
        UITextInputComponent? value = _valueInput;
        NativeAnimationSearchInputComponent? search = _searchInput;
        if (search is not null && ReferenceEquals(search.Panel, this))
            search.Panel = null;
        _timeInput = null;
        _valueInput = null;
        _searchInput = null;
        _projectedRows.Activated = null;
        List<Exception> failures = [];
        void Attempt(Action release)
        {
            try { release(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        Attempt(ReleaseOwnerSubscriptions);
        Attempt(ReleaseVirtualTextOwnerCallbacks);
        Attempt(() => _projectedRows.Replace([]));
        if (time is not null)
        {
            Attempt(() => time.Submitted -= SubmitTime);
            Attempt(() => time.Cancelled -= CancelTime);
        }
        if (value is not null)
        {
            Attempt(() => value.Submitted -= SubmitValue);
            Attempt(() => value.Cancelled -= CancelValue);
        }
        if (search is not null)
        {
            Attempt(() => search.Submitted -= SubmitPathSearch);
            Attempt(() => search.Cancelled -= CancelPathSearch);
        }
        _plotKeyHits.Clear();
        _plotSnapshot = null;
        _plotInput = null;
        _hoveredKeyMember = null;
        _hoveredKey = null;
        Attempt(() => base.OnDestroying());
        if (failures.Count != 0)
            throw new AggregateException("Animation panel teardown failed.", failures);
    }

    private void ReleaseOwnerSubscriptions()
    {
        ReleaseProjectionSources();
        _plotSnapshot = null;
        if (!_ownerSubscriptionsActive)
            return;
        _ownerSubscriptionsActive = false;
        Selection.SelectionChanged -= SelectionChanged;
        NativeAssetSelection.SelectionChanged -= AssetSelectionChanged;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
    }

    protected override void CreateCommands(SceneNode commands)
    {
        SceneNode timeNode = commands.NewChild();
        timeNode.SetTransform<UIBoundableTransform>().Width = 110;
        var (input, _, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(timeNode);
        _timeInput = input;
        input.SingleLineMode = true;
        input.Text = "0";
        input.Submitted += SubmitTime;
        input.Cancelled += CancelTime;
        SceneNode valueNode = commands.NewChild();
        valueNode.SetTransform<UIBoundableTransform>().Width = 150;
        var (valueInput, _, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(valueNode);
        _valueInput = valueInput;
        valueInput.SingleLineMode = true;
        valueInput.Submitted += SubmitValue;
        valueInput.Cancelled += CancelValue;
        SceneNode searchNode = commands.NewChild();
        searchNode.SetTransform<UIBoundableTransform>().Width = 160;
        var (searchInput, _, _, _) = NativeUIElements.CreateTextInputField<NativeAnimationSearchInputComponent>(searchNode);
        _searchInput = searchInput;
        searchInput.Panel = this;
        searchInput.SingleLineMode = true;
        searchInput.Text = _pathSearch;
        searchInput.Submitted += SubmitPathSearch;
        searchInput.Cancelled += CancelPathSearch;
    }

    private void CaptureSearchDraft()
    {
        NativeAnimationSearchStopReceipt? previous = _searchDraft;
        _searchDraft = null;
        previous?.Dispose();
        IUICanvasInputSource? canvasInput = BoundableTransform.GetCanvasComponent()?.GetInputComponent();
        if (_destroying || !NativeAnimationSearchStopReceipt.TryCreate(this, _searchInput,
            canvasInput, out NativeAnimationSearchStopReceipt receipt))
            return;
        _searchDraft = receipt;
        if (receipt.TryReleaseFocus() && !_destroying && _authoringEpoch == receipt.PanelEpoch)
            return;
        if (ReferenceEquals(_searchDraft, receipt))
            _searchDraft = null;
        receipt.Dispose();
    }

    internal void ResumeSearchDraft(UITextInputComponent? input, bool beforePanelActivation)
    {
        NativeAnimationSearchStopReceipt? receipt = _searchDraft;
        if (receipt is null || input is null || !IsActiveInHierarchy)
            return;
        _searchDraft = null;
        ulong expectedEpoch = beforePanelActivation ? receipt.PanelEpoch : unchecked(receipt.PanelEpoch + 1);
        receipt.TryResume(this, input, expectedEpoch);
    }

    private void CancelTime(UITextInputComponent _) { if (!_destroying) RefreshTimeInput(); }
    private void CancelValue(UITextInputComponent _) { if (!_destroying) RefreshValueInput(); }
    private void SubmitPathSearch(UITextInputComponent field)
    {
        if (_destroying)
            return;
        BeginStateIntent();
        SetField(ref _pendingCandidateView, null, publishNotifications: false);
        SetField(ref _pathSearch, field.Text.Trim(), publishNotifications: false);
        SetField(ref _candidatePage, 0, publishNotifications: false);
        RefreshRows();
    }
    private void CancelPathSearch(UITextInputComponent field)
    {
        if (!_destroying)
            field.Text = _pathSearch;
    }

    protected override float FooterHeight => 180;

    protected override void CreateFooter(SceneNode parent)
    {
        _plot = new UIRetainedPlot(parent, "AnimationPlot");
        _plotInput = _plot.Root.AddComponent<AnimationPlotInputComponent>()!;
        _plotInput.Panel = this;
    }

    private static AnimationClipComponent? SelectedComponent()
        => Selection.SceneNode?.GetComponent<AnimationClipComponent>();

    private static AnimationClip? CurrentClip()
        => NativeAssetSelection.SelectedTarget as AnimationClip ?? SelectedComponent()?.Animation;

    private static AnimationClipComponent? SelectedMatchingComponent()
    {
        AnimationClipComponent? component = SelectedComponent();
        return component is not null && ReferenceEquals(component.Animation, CurrentClip())
            ? component : null;
    }

    private void Step(float seconds)
    {
        if (SelectedMatchingComponent() is { } component)
            Seek(component.PlaybackTime + seconds);
    }

    private void Seek(float seconds)
    {
        if (SelectedComponent() is not { Animation: { } clip } component ||
            !ReferenceEquals(CurrentClip(), clip))
            return;
        component.EvaluateAtTime(Math.Clamp(seconds, 0, clip.LengthInSeconds), dispatchEvents: false);
        RefreshRows();
    }

    private void AddKey()
    {
        if (CurrentClip() is not { } clip || _selectedMember is not { } member)
            return;
        float second = SelectedMatchingComponent()?.PlaybackTime ?? 0.0f;
        if (_timeInput is not null && float.TryParse(_timeInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float entered))
            second = entered;
        if (AnimationClipAuthoringActions.TryAddKey(clip, member, second, out Keyframe? key))
        {
            if (ReferenceEquals(CurrentClip(), clip) &&
                ReferenceEquals(_selectedMember, member) && key is not null &&
                MemberContainsKey(member, key))
                _selectedKey = key;
            RefreshTimeInput();
            RefreshValueInput();
            RefreshRows();
        }
    }

    private void RemoveKey()
    {
        if (CurrentClip() is not { } clip || _selectedMember is not { } member || _selectedKey is not { } key)
            return;
        if (AnimationClipAuthoringActions.RemoveKey(clip, member, key))
        {
            if (ReferenceEquals(CurrentClip(), clip) &&
                ReferenceEquals(_selectedMember, member) &&
                ReferenceEquals(_selectedKey, key))
                _selectedKey = null;
            RefreshValueInput();
            RefreshRows();
        }
    }

    private void SubmitTime(UITextInputComponent input)
    {
        if (!float.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float second))
        {
            RefreshTimeInput();
            return;
        }
        if (_selectedKey is not null)
        {
            if (TryGetCurrentKeyDraft(out AnimationClip? clip,
                    out _, out Keyframe? key))
                AnimationClipAuthoringActions.SetKeyTime(clip!, key!, second);
            else
                RefreshTimeInput();
        }
        else
            Seek(second);
        RefreshRows();
    }

    private void RefreshTimeInput()
    {
        if (_timeInput is not null)
            _timeInput.Text = (_selectedKey?.Second ?? SelectedMatchingComponent()?.PlaybackTime ?? 0.0f).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void SubmitValue(UITextInputComponent input)
    {
        if (_valueInputRefreshPending || _refreshingValueInput)
        {
            RefreshValueInput();
            return;
        }
        ulong epoch = _authoringEpoch;
        ulong sourceRevision = _sourceRevision;
        int channel = _valueChannel;
        if (!TryGetCurrentKeyDraft(out AnimationClip? clip,
                out AnimationMember? member, out Keyframe? key) ||
            clip is null || member is null || key is null ||
            !TryParseSelectedValue(input.Text, key!, KeyChannelPart.OutValue, out object? value) ||
            !CanApplySelectedValue(epoch, sourceRevision, channel, input, clip!, member!, key!))
        {
            if (ReferenceEquals(_valueInput, input))
                RefreshValueInput();
            return;
        }
        AnimationClipAuthoringActions.SetKeyValue(clip, key, value!);
        if (_authoringEpoch == epoch && !_destroying && IsActiveInHierarchy)
            RefreshRows();
    }

    private bool TryGetCurrentKeyDraft(out AnimationClip? clip,
        out AnimationMember? member, out Keyframe? key)
    {
        ulong epoch = _authoringEpoch;
        ulong sourceRevision = _sourceRevision;
        clip = CurrentClip();
        member = _selectedMember;
        key = _selectedKey;
        return !_destroying && clip is not null &&
            ReferenceEquals(clip, _displayedClip) &&
            member is not null && key is not null &&
            AnimationClipAuthoringActions.ContainsMember(clip, member) &&
            MemberContainsKey(member, key) &&
            _authoringEpoch == epoch && _sourceRevision == sourceRevision &&
            ReferenceEquals(CurrentClip(), clip) &&
            ReferenceEquals(_displayedClip, clip) &&
            ReferenceEquals(_selectedMember, member) &&
            ReferenceEquals(_selectedKey, key);
    }

    private static bool MemberContainsKey(AnimationMember member, Keyframe key)
    {
        if (member.Animation is not IEnumerable keys || key.OwningTrack is null)
            return false;
        foreach (object? candidate in keys)
            if (ReferenceEquals(candidate, key))
                return true;
        return false;
    }

    private void RefreshValueInput()
    {
        if (_refreshingValueInput)
        {
            _valueInputRefreshPending = true;
            return;
        }
        _refreshingValueInput = true;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                _valueInputRefreshPending = false;
                UITextInputComponent? input = _valueInput;
                if (input is null || _destroying)
                    return;
                if (!IsActiveInHierarchy)
                {
                    _valueInputRefreshPending = true;
                    return;
                }
                ulong epoch = _authoringEpoch;
                ulong sourceRevision = _sourceRevision;
                int channel = _valueChannel;
                AnimationClip? clip = CurrentClip();
                AnimationMember? member = _selectedMember;
                Keyframe? key = _selectedKey;
                string text = key is null ? string.Empty : FormatSelectedValue(key);
                bool Current() => !_destroying && IsActiveInHierarchy &&
                    _authoringEpoch == epoch && _sourceRevision == sourceRevision &&
                    _valueChannel == channel && ReferenceEquals(_valueInput, input) &&
                    ReferenceEquals(_selectedMember, member) &&
                    ReferenceEquals(_selectedKey, key) && ReferenceEquals(CurrentClip(), clip);
                if (Current())
                    try { input.Text = text; }
                    catch
                    {
                        _valueInputRefreshPending = true;
                        throw;
                    }
                if (!_valueInputRefreshPending && Current() && input.Text == text)
                    return;
            }
            _valueInputRefreshPending = true;
        }
        finally { _refreshingValueInput = false; }
    }

    private static bool TryParseValue(string text, Type type, out object? value)
    {
        value = null;
        if (type == typeof(bool))
        {
            if (!bool.TryParse(text, out bool boolean))
                return false;
            value = boolean;
            return true;
        }
        string[] parts = text.Split([',', ' ', '<', '>'], StringSplitOptions.RemoveEmptyEntries);
        int count = type == typeof(float) ? 1 : type == typeof(Vector2) ? 2 : type == typeof(Vector3) ? 3 : 4;
        if (parts.Length != count)
            return false;
        Span<float> numbers = stackalloc float[4];
        for (int index = 0; index < count; index++)
            if (!float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[index]) || !float.IsFinite(numbers[index]))
                return false;
        value = type == typeof(float) ? numbers[0]
            : type == typeof(Vector2) ? new Vector2(numbers[0], numbers[1])
            : type == typeof(Vector3) ? new Vector3(numbers[0], numbers[1], numbers[2])
            : type == typeof(Vector4) ? new Vector4(numbers[0], numbers[1], numbers[2], numbers[3])
            : type == typeof(Quaternion) ? new Quaternion(numbers[0], numbers[1], numbers[2], numbers[3])
            : null;
        return value is not null;
    }

    private void SelectionChanged(SceneNode[] _)
    {
        _candidateRoot = null;
        RefreshRows();
    }
    private void AssetSelectionChanged(object? _)
    {
        if (!_destroying)
            RefreshRows();
    }

    private void RefreshTick()
    {
        if (_destroying)
            return;
        if (_valueInputRefreshPending)
            RefreshValueInput();
        DateTime now = DateTime.UtcNow;
        TimeSpan interval = SelectedMatchingComponent()?.IsPlaying == true ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1);
        if (now - _lastRefresh < interval)
            return;
        _lastRefresh = now;
        AnimationClip? clip = CurrentClip();
        if (_deferredCandidateRefresh || _projectionDirty ||
            !ReferenceEquals(clip, _displayedClip) ||
            !ReferenceEquals(_candidateRoot, Selection.SceneNode))
        {
            RefreshRows();
            return;
        }
        if (clip is not null && SelectedComponent() is { } component &&
            ReferenceEquals(component.Animation, clip))
        {
            if (_statusRowIndex >= 0)
            {
                string status = $"Time: {component.PlaybackTime:F3} s | State: {(component.IsPaused ? "Paused" : component.IsPlaying ? "Playing" : "Stopped")}";
                if (_projectedRows.UpdateTextAt(_statusRowIndex, 3, status))
                {
                    _rows[_statusRowIndex] = (3, status);
                    SignalRowsChanged(RowCount);
                }
            }
        }
        if (clip is not null && _plot is { } plot &&
            (_plotRefreshPending || MathF.Abs(plot.Width - _plottedWidth) > 0.5f ||
             MathF.Abs(plot.Height - _plottedHeight) > 0.5f))
            RefreshPlot(clip);
    }

    protected override void OnRowActivated(ulong id)
    {
        if (_destroying || !IsActiveInHierarchy ||
            !ReferenceEquals(CurrentClip(), _displayedClip))
            return;
        if (id is >= 8 and <= 21 or >= 24 and <= LastCommandRowId)
        {
            BeginStateIntent();
            _authoringEpoch++;
            ulong epoch = _authoringEpoch;
            ActivateCommand(id);
            if (_authoringEpoch == epoch && !_destroying && IsActiveInHierarchy)
                RefreshRows();
            return;
        }
        if (_memberRows.TryGetValue(id, out AnimationMember? member) &&
            _displayedClip is { } clip &&
            AnimationClipAuthoringActions.ContainsMember(clip, member))
        {
            BeginStateIntent();
            _authoringEpoch++;
            _selectedMember = member;
            _selectedKey = null;
            _keyPage = 0;
        }
        else if (_keyRows.TryGetValue(id, out Keyframe? key) &&
            _selectedMember is { } selected && MemberContainsKey(selected, key))
        {
            BeginStateIntent();
            _authoringEpoch++;
            _selectedKey = key;
        }
        else if (_pathRows.TryGetValue(id, out EditorImGuiUI.AnimationClipPathCandidate? candidate))
        {
            BeginStateIntent();
            _authoringEpoch++;
            _selectedCandidate = candidate;
        }
        else
            return;
        RefreshTimeInput();
        RefreshValueInput();
        RefreshRows();
    }

    protected override void RefreshRows() => RefreshRowsCore(null, false);

    private void RefreshRowsOwned(Action requireCurrent)
        => RefreshRowsCore(requireCurrent, true);

    private void RefreshRowsCore(Action? requireCurrent, bool owned)
    {
        if (owned) requireCurrent!();
        else
        {
            _authoringEpoch++;
            _deferredCandidateRefresh = false;
        }
        if (_destroying)
            return;
        if (_ids.Count >= MaxRetainedRowIds)
            _ids.Clear();
        _buildingSourceRevision = _sourceRevision;
        _rows.Clear();
        _statusRowIndex = -1;
        _hoverRowIndex = -1;
        _memberDepths.Clear();
        _memberRows.Clear();
        _visiblePlotMembers.Clear();
        _keyRows.Clear();
        _pathRows.Clear();
        AnimationClip? clip = CurrentClip();
        if (!ReferenceEquals(clip, _displayedClip))
        {
            if (owned)
                throw new InvalidOperationException("The displayed clip changed during restore.");
            if (_displayedClip is { } previous)
                _clipViews.Remember(previous, new(_selectedMember, _selectedKey,
                    _memberPage, _keyPage, _plotZoom, _plotWindowStart));
            ReleaseProjectionSources();
            _plotSnapshot = null;
            _plotViewRevision++;
            _displayedClip = clip;
            _selectedMember = null;
            _selectedKey = null;
            _memberPage = 0;
            _keyPage = 0;
            _plotZoom = 1;
            _plotWindowStart = 0;
            ResetPlotValueView();
            _hoveredKeyMember = null;
            _hoveredKey = null;
            // The checks below drop a remembered member or key that the clip no longer contains.
            if (clip is not null && _clipViews.TryTake(clip, out NativeAnimationClipView view))
            {
                _selectedMember = view.Member;
                _selectedKey = view.Key;
                _memberPage = view.MemberPage;
                _keyPage = view.KeyPage;
                _plotZoom = view.PlotZoom;
                _plotWindowStart = view.PlotWindowStart;
                _valueInputRefreshPending = true;
            }
            _ids.Clear();
        }
        if (clip is null)
        {
            _rows.Add((1, "Select an AnimationClip asset or a scene node with an AnimationClipComponent."));
            SetProjectedRows();
            requireCurrent?.Invoke();
            return;
        }
        if (_selectedMember is { } selectedMember &&
            !AnimationClipAuthoringActions.ContainsMember(clip, selectedMember))
        {
            if (owned)
                throw new InvalidOperationException("The selected member changed during restore.");
            _selectedMember = null;
            _selectedKey = null;
        }
        if (_selectedKey is { } selectedKey &&
            (_selectedMember is not { } owner ||
             !MemberContainsKey(owner, selectedKey)))
        {
            if (owned)
                throw new InvalidOperationException("The selected key changed during restore.");
            _selectedKey = null;
        }
        if (_ownerSubscriptionsActive && IsActiveInHierarchy)
            ObserveProjectionSources(clip);
        requireCurrent?.Invoke();
        if (!owned && (_pendingCandidateView is not null ||
                !ReferenceEquals(_candidateRoot, Selection.SceneNode)) && !RebuildCandidates())
            return;

        _rows.Add((1, "Clip: " + (clip.Name ?? "<unnamed>")));
        _rows.Add((2, $"Length: {clip.LengthInSeconds:F3} s | Sample rate: {clip.SampleRate} fps | Loop: {clip.Looped}"));
        _rows.Add((6, "Toolbar fields: key time (seconds), then selected value (all components or one channel). Press Enter to apply."));
        _rows.Add((8, "Add key to selected member"));
        _rows.Add((9, "Remove selected key"));
        _rows.Add((10, "Seek to start"));
        _rows.Add((11, "Step back 0.1 s"));
        _rows.Add((12, "Step forward 0.1 s"));
        _rows.Add((13, "Seek to end"));
        _rows.Add((14, "Pause playback"));
        _rows.Add((15, "Resume playback"));
        _rows.Add((16, _graphView ? "Show key timeline" : "Show curve graph"));
        _rows.Add((17, "Scan paths from selected scene node"));
        _rows.Add((18, "Add selected scene path to clip"));
        _rows.Add((19, "Remove selected member path"));
        _rows.Add((62, "Normalize root to a group"));
        if (!string.IsNullOrWhiteSpace(clip.FilePath))
            _rows.Add((63, "Copy clip file path"));
        _rows.Add((20, "Play scene clip"));
        _rows.Add((21, "Stop scene clip"));
        _rows.Add((30, "Length -0.1 s"));
        _rows.Add((31, "Length +0.1 s"));
        _rows.Add((58, _stretchLengthEdits ? "Stretch keys with length: on" : "Stretch keys with length: off"));
        _rows.Add((60, "Set length from key time field"));
        _rows.Add((61, "Set sample rate from key time field"));
        _rows.Add((32, "Sample rate -1 fps"));
        _rows.Add((33, "Sample rate +1 fps"));
        _rows.Add((34, "Toggle clip loop"));
        _rows.Add((45, "Cycle clip traversal"));
        _rows.Add((46, "Cycle clip kind"));
        _rows.Add((35, "Plot zoom in"));
        _rows.Add((36, "Plot zoom out"));
        _rows.Add((37, "Plot previous window"));
        _rows.Add((38, "Fit full clip"));
        _rows.Add((39, "Plot next window"));
        float visibleDuration = clip.LengthInSeconds / MathF.Max(1, _plotZoom);
        float windowStart = Math.Clamp(_plotWindowStart, 0,
            MathF.Max(0, clip.LengthInSeconds - visibleDuration));
        float windowEnd = windowStart + visibleDuration;
        _rows.Add((66, $"Time axis: {windowStart:0.###}–{windowEnd:0.###} s | frames {MathF.Round(windowStart * clip.SampleRate)}–{MathF.Round(windowEnd * clip.SampleRate)}"));
        _hoverRowIndex = _rows.Count;
        _rows.Add((HoverRowId, FormatHoveredKey()));
        _rows.Add((26, "Previous member page"));
        _rows.Add((27, "Next member page"));
        if (SelectedComponent() is { } component && ReferenceEquals(component.Animation, clip))
        {
            _statusRowIndex = _rows.Count;
            _rows.Add((3, $"Time: {component.PlaybackTime:F3} s | State: {(component.IsPaused ? "Paused" : component.IsPlaying ? "Playing" : "Stopped")}"));
            if (component.PlaybackCapabilityDiagnostic.Length > 0)
                _rows.Add((4, component.PlaybackCapabilityDiagnostic));
        }

        _rows.Add((5, "Members (select a row to edit its keys)"));
        if (clip.RootMember is { } root)
            AddMemberRows(root, requireCurrent, owned);
        else
            _rows.Add((0, "This clip has no animation members."));

        if (_selectedMember is { Animation: not null } selectedPlotMember)
        {
            for (int index = 0; index < _visiblePlotMembers.Count; index++)
                if (ReferenceEquals(_visiblePlotMembers[index], selectedPlotMember))
                {
                    _visiblePlotMembers.RemoveAt(index);
                    break;
                }
            _visiblePlotMembers.Insert(0, selectedPlotMember);
        }
        if (!_graphView && _visiblePlotMembers.Count > 0)
        {
            int visibleLanes = Math.Min(5, _visiblePlotMembers.Count);
            _rows.Add((65, $"Timeline lanes: {visibleLanes} of {_visiblePlotMembers.Count} | selected member first"));
            for (int lane = 0; lane < visibleLanes; lane++)
                _rows.Add(((ulong)(70 + lane),
                    $"  Lane {lane + 1}: {_visiblePlotMembers[lane].MemberName}"));
        }

        if (_selectedMember is { Animation: { } animation } selected)
        {
            AddSelectedMemberDetails(selected);
            requireCurrent?.Invoke();
            _rows.Add((59, $"Value field channel: {ChannelLabel(_selectedKey)} (click to cycle)"));
            _rows.Add((7, "Keys for " + (selected.MemberName ?? "<unnamed>")));
            _rows.Add((40, "Cycle incoming interpolation"));
            _rows.Add((41, "Cycle outgoing interpolation"));
            _rows.Add((42, "Set incoming tangent from value field"));
            _rows.Add((43, "Set outgoing tangent from value field"));
            _rows.Add((44, "Set key frame from time field"));
            _rows.Add((47, "Set incoming value from value field"));
            _rows.Add((48, "Unify selected planar key"));
            if (_selectedKey is IPlanarKeyframe)
                _rows.Add((57, "Auto tangents from adjacent keys"));
            if (_selectedKey is QuaternionKeyframe)
            {
                _rows.Add((49, "Incoming: Step"));
                _rows.Add((50, "Incoming: Linear"));
                _rows.Add((51, "Incoming: Smooth"));
                _rows.Add((53, "Outgoing: Step"));
                _rows.Add((54, "Outgoing: Linear"));
                _rows.Add((55, "Outgoing: Smooth"));
            }
            else if (_selectedKey is IPlanarKeyframe)
            {
                _rows.Add((49, "Incoming: Step"));
                _rows.Add((50, "Incoming: Linear"));
                _rows.Add((51, "Incoming: Smooth"));
                _rows.Add((52, "Incoming: Hermite"));
                _rows.Add((53, "Outgoing: Step"));
                _rows.Add((54, "Outgoing: Linear"));
                _rows.Add((55, "Outgoing: Smooth"));
                _rows.Add((56, "Outgoing: Hermite"));
            }
            _rows.Add((28, "Previous key page"));
            _rows.Add((29, "Next key page"));
            _keyCount = 0;
            if (animation is IEnumerable enumerable)
                foreach (object? item in enumerable)
                    if (item is Keyframe key)
                    {
                        int index = _keyCount++;
                        if (index < _keyPage * RowPageSize ||
                            index >= (_keyPage + 1) * RowPageSize)
                            continue;
                        ulong id = IdFor(key);
                        _keyRows[id] = key;
                        _rows.Add((id, string.Empty));
                    }
            int keyPages = Math.Max(1, (_keyCount + RowPageSize - 1) / RowPageSize);
            if (_keyPage >= keyPages)
            {
                if (owned)
                    throw new InvalidOperationException("The key page changed during restore.");
                _keyPage = keyPages - 1;
                RefreshRows();
                return;
            }
        }
        else if (_selectedMember is { } selectedWithoutAnimation)
        {
            AddSelectedMemberDetails(selectedWithoutAnimation);
            requireCurrent?.Invoke();
        }
        if (!owned && _candidateRoot is not null)
        {
            _candidateMatchCount = 0;
            foreach (EditorImGuiUI.AnimationClipPathCandidate candidate in _candidates)
                if (_pathSearch.Length == 0 || candidate.DisplayPath.IndexOf(_pathSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                    _candidateMatchCount++;
            int pages = Math.Max(1, (_candidateMatchCount + RowPageSize - 1) / RowPageSize);
            _candidatePage = Math.Clamp(_candidatePage, 0, pages - 1);
            _rows.Add((22, $"Scene path candidates: {_candidateMatchCount} matches, page {_candidatePage + 1}/{pages}. Third toolbar field filters."));
            _rows.Add((24, "Previous path page"));
            _rows.Add((25, "Next path page"));
            int matched = 0;
            foreach (EditorImGuiUI.AnimationClipPathCandidate candidate in _candidates)
            {
                if (_pathSearch.Length > 0 && candidate.DisplayPath.IndexOf(_pathSearch, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (matched++ < _candidatePage * RowPageSize)
                    continue;
                if (matched > (_candidatePage + 1) * RowPageSize)
                    break;
                ulong id = IdFor(candidate);
                _pathRows[id] = candidate;
                _rows.Add((id, string.Empty));
            }
        }
        SetProjectedRows();
        requireCurrent?.Invoke();
        if (owned)
        {
            _plotRefreshPending = true;
            _deferredCandidateRefresh = _candidateRoot is not null || Selection.SceneNode is not null;
        }
        else RefreshPlot(clip);
    }

    private void AddMemberRows(AnimationMember root, Action? requireCurrent = null,
        bool owned = false)
    {
        int firstRow = _rows.Count;
        Stack<(AnimationMember Member, int Depth)> pending = new();
        HashSet<AnimationMember> visited = new(ReferenceEqualityComparer.Instance);
        pending.Push((root, 0));
        _memberCount = 0;
        while (pending.Count > 0)
        {
            requireCurrent?.Invoke();
            (AnimationMember member, int depth) = pending.Pop();
            if (!visited.Add(member))
                continue;
            int index = _memberCount++;
            if (index >= _memberPage * RowPageSize &&
                index < (_memberPage + 1) * RowPageSize)
            {
                ulong id = IdFor(member);
                _memberRows[id] = member;
                if (member.Animation is not null)
                    _visiblePlotMembers.Add(member);
                _memberDepths[id] = depth;
                _rows.Add((id, string.Empty));
            }
            for (int child = member.Children.Count - 1; child >= 0; child--)
                pending.Push((member.Children[child], depth + 1));
        }
        int pages = Math.Max(1, (_memberCount + RowPageSize - 1) / RowPageSize);
        if (_memberPage >= pages)
        {
            if (owned)
                throw new InvalidOperationException("The member page changed during restore.");
            _memberPage = pages - 1;
            _rows.RemoveRange(firstRow, _rows.Count - firstRow);
            _memberRows.Clear();
            _visiblePlotMembers.Clear();
            _memberDepths.Clear();
            AddMemberRows(root);
        }
    }

    private void AddSelectedMemberDetails(AnimationMember member)
    {
        _rows.Add((80, $"Selected: {member.MemberName} [{member.MemberType}]"));
        if (member.MemberNotFound)
            _rows.Add((81, "This member was not resolved at runtime."));
        if (member.MemberType == EAnimationMemberType.Method)
        {
            object?[] arguments = member.MethodArguments ?? [];
            _rows.Add((82, $"Method: argument {member.AnimatedMethodArgumentIndex} is animated | Cache return: {member.CacheReturnValue}"));
            int shown = Math.Min(arguments.Length, 32);
            for (int index = 0; index < shown; index++)
                _rows.Add(((ulong)(100 + index),
                    $"  Arg {index}: {(index == member.AnimatedMethodArgumentIndex ? "animated" : "static")} | {FormatDetailValue(arguments[index])}"));
            if (arguments.Length > shown)
                _rows.Add((132, $"{arguments.Length - shown} more method arguments"));
        }
        if (member.Animation is not { } animation)
        {
            _rows.Add((83, "No animation asset is attached to this member."));
            return;
        }
        _rows.Add((83, $"Attached animation: {animation.GetType().Name} | {animation.LengthInSeconds:0.###} s | Loop: {animation.Looped}"));
        if (animation is BasePropAnimBakeable bakeable)
            _rows.Add((84, bakeable.IsBaked
                ? $"Baked: {bakeable.BakedFrameCount} frames at {bakeable.BakedFramesPerSecond} fps"
                : "Keyframed animation"));
        if (member.DefaultValue is { } defaultValue)
            _rows.Add((85, "Member default: " + FormatDetailValue(defaultValue)));
    }

    private void SetProjectedRows()
    {
        int previousCount = RowCount;
        _projectedRows.ReplaceProjected(_rows, ProjectRowText);
        _projectionDirty = _buildingSourceRevision != _sourceRevision;
        SignalRowsChanged(previousCount);
    }

    private void ObserveProjectionSources(AnimationClip clip)
    {
        if (ReferenceEquals(_observedClip, clip) && !_sourceTopologyDirty)
            return;
        ReleaseProjectionSources();
        _sourceTopologyDirty = false;
        _observedClip = clip;
        clip.PropertyChanged += ProjectionClipChanged;
        if (clip.RootMember is not { } root)
            return;
        Stack<AnimationMember> pending = new();
        HashSet<AnimationMember> visited = new(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.Count > 0)
        {
            AnimationMember member = pending.Pop();
            if (!visited.Add(member))
                continue;
            EventList<AnimationMember> children = member.Children;
            _observedMembers.Add(member, children);
            member.PropertyChanged += ProjectionMemberChanged;
            children.PostModified += ProjectionChildrenChanged;
            if (TrackFor(member.Animation) is { } track &&
                _observedTracks.Add(track))
                track.Changed += ProjectionTrackChanged;
            foreach (AnimationMember child in children)
                pending.Push(child);
        }
    }

    private void ReleaseProjectionSources()
    {
        AnimationClip? clip = _observedClip;
        _observedClip = null;
        if (clip is not null)
            clip.PropertyChanged -= ProjectionClipChanged;
        foreach (var pair in _observedMembers)
        {
            pair.Key.PropertyChanged -= ProjectionMemberChanged;
            pair.Value.PostModified -= ProjectionChildrenChanged;
        }
        _observedMembers.Clear();
        foreach (BaseKeyframeTrack track in _observedTracks)
            track.Changed -= ProjectionTrackChanged;
        _observedTracks.Clear();
        _projectionDirty = true;
        _sourceTopologyDirty = true;
    }

    private void ProjectionClipChanged(object? sender,
        IXRPropertyChangedEventArgs change)
    {
        if (ReferenceEquals(sender, _observedClip))
            MarkProjectionDirty(change.PropertyName == nameof(AnimationClip.RootMember));
    }

    private void ProjectionMemberChanged(object? sender,
        IXRPropertyChangedEventArgs change)
    {
        if (sender is AnimationMember member &&
            _observedMembers.ContainsKey(member))
            MarkProjectionDirty(change.PropertyName is nameof(AnimationMember.Animation) or
                nameof(AnimationMember.Children));
    }

    private void ProjectionChildrenChanged() => MarkProjectionDirty(topologyChanged: true);

    private void ProjectionTrackChanged(BaseKeyframeTrack track)
    {
        if (_observedTracks.Contains(track))
            MarkProjectionDirty();
    }

    private void MarkProjectionDirty(bool topologyChanged = false)
    {
        if (_destroying || !_ownerSubscriptionsActive || !IsActiveInHierarchy)
            return;
        _sourceRevision++;
        _projectionDirty = true;
        _sourceTopologyDirty |= topologyChanged;
    }

    private static BaseKeyframeTrack? TrackFor(BasePropAnim? animation)
        => animation switch
        {
            PropAnimFloat value => value.Keyframes,
            PropAnimVector2 value => value.Keyframes,
            PropAnimVector3 value => value.Keyframes,
            PropAnimVector4 value => value.Keyframes,
            PropAnimQuaternion value => value.Keyframes,
            PropAnimBool value => value.Keyframes,
            PropAnimInt value => value.Keyframes,
            PropAnimString value => value.Keyframes,
            PropAnimObject value => value.Keyframes,
            PropAnimMatrix value => value.Keyframes,
            _ => null,
        };

    private string ProjectRowText(int index)
    {
        if ((uint)index >= (uint)_rows.Count)
            return string.Empty;
        (ulong id, string text) = _rows[index];
        if (text.Length != 0)
            return text;
        if (_memberRows.TryGetValue(id, out AnimationMember? member))
        {
            string indent = new(' ', Math.Min(_memberDepths[id], 16) * 2);
            string marker = ReferenceEquals(_selectedMember, member) ? "> " : "  ";
            string suffix = member.Animation is null ? string.Empty :
                $" [{member.Animation.GetType().Name}]";
            return $"{marker}{indent}{member.MemberName} ({member.MemberType}){suffix}";
        }
        if (_keyRows.TryGetValue(id, out Keyframe? key))
        {
            string marker = ReferenceEquals(_selectedKey, key) ? "> " : "  ";
            string frame = key.AuthoredFrameIndex >= 0 ?
                $" | frame {key.AuthoredFrameIndex}" : string.Empty;
            return $"{marker}{key.Second:0.###} s{frame} | {FormatKeyValue(key)}";
        }
        if (_pathRows.TryGetValue(id,
                out EditorImGuiUI.AnimationClipPathCandidate? candidate))
            return (ReferenceEquals(candidate, _selectedCandidate) ? "> " : "  ") +
                candidate.DisplayPath + " [" + candidate.ValueType.Name + "]";
        return string.Empty;
    }

    private void ActivateCommand(ulong id)
    {
        switch (id)
        {
            case 8: AddKey(); break;
            case 9: RemoveKey(); break;
            case 10: Seek(0); break;
            case 11: Step(-0.1f); break;
            case 12: Step(0.1f); break;
            case 13: Seek(CurrentClip()?.LengthInSeconds ?? 0); break;
            case 14: SelectedMatchingComponent()?.Pause(); break;
            case 15: SelectedMatchingComponent()?.Resume(); break;
            case 16: _graphView = !_graphView; _plotViewRevision++; break;
            case 17: RebuildCandidates(); break;
            case 18:
                if (CurrentClip() is { } clip &&
                    ReferenceEquals(clip, _displayedClip) &&
                    ReferenceEquals(_candidateRoot, Selection.SceneNode) &&
                    _selectedCandidate is { } candidate &&
                    CandidateBelongsToCurrentSource(candidate))
                    EditorImGuiUI.AddAnimationClipPath(clip, candidate);
                break;
            case 19: RemoveSelectedMember(); break;
            case 20: SelectedMatchingComponent()?.Play(); break;
            case 21: SelectedMatchingComponent()?.StopPlayback(); break;
            case 24: SetCandidatePage(Math.Max(0, _candidatePage - 1)); break;
            case 25: SetCandidatePage(checked(_candidatePage + 1)); break;
            case 26: _memberPage = Math.Max(0, _memberPage - 1); break;
            case 27: _memberPage++; break;
            case 28: _keyPage = Math.Max(0, _keyPage - 1); break;
            case 29: _keyPage++; break;
            case 30: EditClipSettings(clip => AnimationClipAuthoringActions.SetClipLength(
                clip, MathF.Max(0, clip.LengthInSeconds - 0.1f), _stretchLengthEdits)); break;
            case 31: EditClipSettings(clip => AnimationClipAuthoringActions.SetClipLength(
                clip, MathF.Min(3600, clip.LengthInSeconds + 0.1f), _stretchLengthEdits)); break;
            case 32: EditClipSettings(clip => AnimationClipAuthoringActions.SetClipSampleRate(
                clip, Math.Max(1, clip.SampleRate - 1))); break;
            case 33: EditClipSettings(clip => AnimationClipAuthoringActions.SetClipSampleRate(
                clip, Math.Min(960, clip.SampleRate + 1))); break;
            case 34: EditClipSettings(clip => AnimationClipAuthoringActions.SetClipLooped(
                clip, !clip.Looped)); break;
            case 45: EditClipSettings(clip => AnimationClipAuthoringActions.SetClipTraversalMethod(
                clip, NextEnumValue(clip.TraversalMethod))); break;
            case 46: EditClipSettings(clip => AnimationClipAuthoringActions.SetClipKind(
                clip, NextEnumValue(clip.ClipKind))); break;
            case 35: _plotZoom = MathF.Min(32, _plotZoom * 1.5f); _plotViewRevision++; break;
            case 36: _plotZoom = MathF.Max(1, _plotZoom / 1.5f); _plotViewRevision++; break;
            case 37: _plotWindowStart = MathF.Max(0,
                _plotWindowStart - MathF.Max(0.01f, _plottedDuration * 0.75f)); _plotViewRevision++; break;
            case 38: _plotZoom = 1; _plotWindowStart = 0; ResetPlotValueView(); _plotViewRevision++; break;
            case 39: _plotWindowStart +=
                MathF.Max(0.01f, _plottedDuration * 0.75f); _plotViewRevision++; break;
            case 40: CycleSelectedInterpolation(outgoing: false); break;
            case 41: CycleSelectedInterpolation(outgoing: true); break;
            case 42: SetSelectedTangent(outgoing: false); break;
            case 43: SetSelectedTangent(outgoing: true); break;
            case 44: SetSelectedFrame(); break;
            case 47: SetSelectedIncomingValue(); break;
            case 48: EditSelectedKey(AnimationClipAuthoringActions.UnifyKey); break;
            case 57: EditSelectedKey(AnimationClipAuthoringActions.AutoKeyTangents); break;
            case 58: SetField(ref _stretchLengthEdits, !_stretchLengthEdits); break;
            case 59: CycleValueChannel(); break;
            case 60: SetClipLengthFromField(); break;
            case 61: SetClipSampleRateFromField(); break;
            case 62: EditClipSettings(AnimationClipAuthoringActions.NormalizeRoot); break;
            case 63: CopyClipPath(); break;
            case >= 49 and <= 56: SetSelectedInterpolation(id); break;
        }
    }

    private void CycleSelectedInterpolation(bool outgoing)
    {
        if (!TryGetCurrentKeyDraft(out AnimationClip? clip, out _,
                out Keyframe? key))
            return;
        if (key is QuaternionKeyframe quaternion)
        {
            ERadialInterpType current = outgoing ?
                quaternion.InterpolationTypeOut : quaternion.InterpolationTypeIn;
            ERadialInterpType next = (ERadialInterpType)
                (((int)current + 1) % Enum.GetValues<ERadialInterpType>().Length);
            AnimationClipAuthoringActions.SetKeyInterpolation(clip!, quaternion,
                outgoing, next);
        }
        else if (key is IPlanarKeyframe planar)
        {
            EVectorInterpType? current = outgoing ?
                planar.InterpolationTypeOut :
                AnimationClipAuthoringActions.GetIncomingInterpolation(key);
            if (current is { } mode)
            {
                EVectorInterpType next = (EVectorInterpType)
                    (((int)mode + 1) % Enum.GetValues<EVectorInterpType>().Length);
                AnimationClipAuthoringActions.SetKeyInterpolation(clip!, key,
                    outgoing, next);
            }
        }
    }

    private void SetSelectedInterpolation(ulong commandId)
    {
        if (!TryGetCurrentKeyDraft(out AnimationClip? clip, out _,
                out Keyframe? key))
            return;
        bool outgoing = commandId >= 53;
        int modeIndex = (int)((commandId - 49) % 4);
        if (key is QuaternionKeyframe quaternion)
        {
            if (modeIndex < 3)
                AnimationClipAuthoringActions.SetKeyInterpolation(clip!,
                    quaternion, outgoing, (ERadialInterpType)modeIndex);
        }
        else if (key is IPlanarKeyframe)
            AnimationClipAuthoringActions.SetKeyInterpolation(clip!, key!,
                outgoing, (EVectorInterpType)modeIndex);
    }

    private void SetSelectedTangent(bool outgoing)
    {
        ulong epoch = _authoringEpoch;
        ulong sourceRevision = _sourceRevision;
        int channel = _valueChannel;
        if (!TryGetCurrentKeyDraft(out AnimationClip? clip, out AnimationMember? member,
                out Keyframe? key) || clip is null || member is null || key is null ||
            _valueInput is not { } input ||
            !TryParseSelectedValue(input.Text, key!, outgoing ?
                KeyChannelPart.OutTangent : KeyChannelPart.InTangent, out object? value) ||
            !CanApplySelectedValue(epoch, sourceRevision, channel, input,
                clip!, member, key!))
            return;
        AnimationClipAuthoringActions.SetKeyTangent(clip, key, outgoing,
            value!);
    }

    private void SetSelectedFrame()
    {
        if (!TryGetCurrentKeyDraft(out AnimationClip? clip, out _,
                out Keyframe? key) || _timeInput is not { } input ||
            !int.TryParse(input.Text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int frame))
            return;
        AnimationClipAuthoringActions.SetKeyFrame(clip!, key!, frame);
    }

    private void SetSelectedIncomingValue()
    {
        ulong epoch = _authoringEpoch;
        ulong sourceRevision = _sourceRevision;
        int channel = _valueChannel;
        if (!TryGetCurrentKeyDraft(out AnimationClip? clip, out AnimationMember? member,
                out Keyframe? key) || clip is null || member is null || key is null ||
            _valueInput is not { } input ||
            !TryParseSelectedValue(input.Text, key!, KeyChannelPart.InValue, out object? value) ||
            !CanApplySelectedValue(epoch, sourceRevision, channel, input,
                clip!, member, key!))
            return;
        AnimationClipAuthoringActions.SetKeyIncomingValue(clip, key, value!);
    }

    private void EditSelectedKey(Func<AnimationClip, Keyframe, bool> edit)
    {
        if (TryGetCurrentKeyDraft(out AnimationClip? clip, out _,
                out Keyframe? key))
            edit(clip!, key!);
    }

    /// <summary>Sets the clip length to the seconds in the key time field.</summary>
    private void SetClipLengthFromField()
    {
        if (_timeInput is not { } input ||
            !float.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds) ||
            !float.IsFinite(seconds))
            return;
        EditClipSettings(clip => AnimationClipAuthoringActions.SetClipLength(
            clip, Math.Clamp(seconds, 0, 3600), _stretchLengthEdits));
    }

    /// <summary>Sets the clip sample rate to the whole number in the key time field.</summary>
    private void SetClipSampleRateFromField()
    {
        if (_timeInput is not { } input ||
            !int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int framesPerSecond))
            return;
        EditClipSettings(clip => AnimationClipAuthoringActions.SetClipSampleRate(
            clip, Math.Clamp(framesPerSecond, 1, 960)));
    }

    private void CopyClipPath()
    {
        if (CurrentClip() is not { FilePath: { Length: > 0 } path } clip || !ReferenceEquals(clip, _displayedClip))
            return;
        RuntimeClipboardServices.Current?.SetText(path);
    }

    private void EditClipSettings(Func<AnimationClip, bool> edit)
    {
        AnimationClip? clip = CurrentClip();
        if (clip is not null && ReferenceEquals(clip, _displayedClip))
            edit(clip);
    }

    private static TEnum NextEnumValue<TEnum>(TEnum current)
        where TEnum : struct, Enum
    {
        TEnum[] values = Enum.GetValues<TEnum>();
        int index = Array.IndexOf(values, current);
        return values[(index + 1) % values.Length];
    }

    private bool CandidateBelongsToCurrentSource(
        EditorImGuiUI.AnimationClipPathCandidate candidate)
    {
        foreach (EditorImGuiUI.AnimationClipPathCandidate current in _candidates)
            if (ReferenceEquals(current, candidate))
                return true;
        return false;
    }

    private void RemoveSelectedMember()
    {
        if (CurrentClip() is not { } clip || _selectedMember is not { } member)
            return;
        if (!AnimationClipAuthoringActions.RemoveMember(clip, member))
            return;
        if (!ReferenceEquals(CurrentClip(), clip) ||
            !ReferenceEquals(_selectedMember, member))
            return;
        _selectedMember = clip.RootMember;
        _selectedKey = null;
        RefreshTimeInput();
        RefreshValueInput();
    }

    private void RefreshPlot(AnimationClip clip)
    {
        if (_plotBuilding)
        {
            _plotBuildRevision++;
            _plotRefreshPending = true;
            return;
        }
        _plotBuilding = true;
        ulong buildRevision = ++_plotBuildRevision;
        try
        {
            using IDisposable? transaction = _plotInput?.UserInterfaceCanvas?
                .CanvasTransform.BeginMutationTransaction();
            RefreshPlotCore(clip, buildRevision);
        }
        finally
        {
            _plotBuilding = false;
            if (_plotRefreshPending && !_destroying && _plotRetryDepth == 0)
            {
                _plotRefreshPending = false;
                if (CurrentClip() is { } current)
                {
                    _plotRetryDepth++;
                    try { RefreshPlot(current); }
                    finally { _plotRetryDepth--; }
                }
            }
        }
    }

    private void RefreshPlotCore(AnimationClip clip, ulong buildRevision)
    {
        if (_plot is not { } plot)
            return;
        ulong sourceRevision = _sourceRevision;
        ulong viewRevision = _plotViewRevision;
        AnimationMember[] visibleMembers = [.. _visiblePlotMembers];
        _plotBuildMembers = visibleMembers;
        _plotBuildAnimations = new BasePropAnim?[visibleMembers.Length];
        for (int index = 0; index < visibleMembers.Length; index++)
            _plotBuildAnimations[index] = visibleMembers[index].Animation;
        _plotBuildSelectedMember = _selectedMember;
        _plotBuildSelectedAnimation = _selectedMember?.Animation;
        _plotBuildClipLength = clip.LengthInSeconds;
        _activePlotBuildPlot = plot;
        _activePlotBuildClip = clip;
        _activePlotBuildSourceRevision = sourceRevision;
        _activePlotBuildViewRevision = viewRevision;
        _activePlotBuildRevision = buildRevision;
        _plotBuildHidden = false;
        _plotSnapshot = null;
        _plotInput?.BoundableTransform.RecordUIChange(
            UIChangeDomain.Visual | UIChangeDomain.HitTest);
        if (!IsPlotBuildCurrent(plot, clip, sourceRevision, viewRevision,
                buildRevision))
            return;
        UIBoundableTransform? plotRoot = plot.Root.GetTransformAs<UIBoundableTransform>(true);
        if (plotRoot is null)
            return;
        plotRoot.Visibility = EVisibility.Hidden;
        _plotBuildHidden = true;
        if (!IsPlotBuildCurrent(plot, clip, sourceRevision, viewRevision,
                buildRevision))
            return;
        plotRoot.InvalidateArrange();
        if (!IsPlotBuildCurrent(plot, clip, sourceRevision, viewRevision,
                buildRevision))
            return;
        _plotKeyHits.Clear();
        plot.Begin();
        float width = plot.Width;
        float height = plot.Height;
        _plottedWidth = width;
        _plottedHeight = height;
        if (width < 24 || height < 24 || clip.LengthInSeconds <= 0)
        {
            if (_plotValidator()) plot.End(_plotValidator);
            return;
        }
        _plottedDuration = clip.LengthInSeconds / MathF.Max(1, _plotZoom);
        _plotWindowStart = Math.Clamp(_plotWindowStart, 0,
            MathF.Max(0, clip.LengthInSeconds - _plottedDuration));
        _plottedStart = _plotWindowStart;
        if (!_graphView)
        {
            DrawTimeAxis(plot, width, height);
            if (!IsPlotBuildCurrent(plot, clip, sourceRevision, viewRevision,
                    buildRevision))
            {
                if (_plotValidator()) plot.End(_plotValidator);
                return;
            }
            DrawPlotLanes(plot, width, height, visibleMembers);
            if (_plotValidator()) plot.End(_plotValidator);
            if (IsPlotBuildCurrent(plot, clip, sourceRevision, viewRevision,
                    buildRevision))
                PublishPlotSnapshot(clip);
            return;
        }

        if (_selectedMember?.Animation is not { } animation)
        {
            if (_plotValidator()) plot.End(_plotValidator);
            return;
        }

        int count = Math.Clamp((int)(width / 8), 2,
            (UIRetainedPlot.MaximumSegments - 40) / 4 + 1);
        int channels = 0;
        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        for (int index = 0; index < count; index++)
        {
            float second = _plottedStart + _plottedDuration * index / (count - 1);
            object? value = animation.GetValueGeneric(second);
            if (!IsPlotBuildCurrent(plot, clip, sourceRevision, viewRevision,
                    buildRevision))
            {
                if (_plotValidator()) plot.End(_plotValidator);
                return;
            }
            int valueChannels = WriteChannels(value, index);
            channels = Math.Max(channels, valueChannels);
            for (int channel = 0; channel < valueChannels; channel++)
            {
                float number = _plotSamples[channel, index];
                if (!float.IsFinite(number))
                    continue;
                minimum = Math.Min(minimum, number);
                maximum = Math.Max(maximum, number);
            }
        }
        if (!float.IsFinite(minimum) || !float.IsFinite(maximum))
        {
            if (_plotValidator()) plot.End(_plotValidator);
            return;
        }
        float span = Math.Max(0.001f, maximum - minimum);
        // Ctrl+wheel narrows the value axis to a window of the auto-fit range.
        minimum += AnimationPlotValueZoom.WindowStart(_plotValueZoom, _plotValueCenter) * span;
        span /= _plotValueZoom;
        DrawTimeAxis(plot, width, height);
        if (!_plotValidator())
            return;
        float zeroY = 12 + (0 - minimum) / span * (height - 36);
        if (zeroY >= 12 && zeroY <= height - 24)
            plot.AddLine(new Vector2(8, zeroY), new Vector2(width - 8, zeroY),
                new Vector4(0.36f, 0.39f, 0.44f, 1), 1, _plotValidator);
        if (!_plotValidator())
            return;
        for (int channel = 0; channel < channels; channel++)
            for (int index = 1; index < count; index++)
            {
                // A value-axis zoom can put samples outside the value band; they stay on its edge.
                Vector2 from = new(8 + (index - 1) * (width - 16) / (count - 1),
                    Math.Clamp(12 + (_plotSamples[channel, index - 1] - minimum) / span * (height - 36), 12, height - 24));
                Vector2 to = new(8 + index * (width - 16) / (count - 1),
                    Math.Clamp(12 + (_plotSamples[channel, index] - minimum) / span * (height - 36), 12, height - 24));
                if (!_plotValidator()) return;
                plot.AddLine(from, to, PlotPalette[channel], validate: _plotValidator);
            }
        if (!_plotValidator())
            return;
        if (animation is IEnumerable graphKeys)
        {
            var frozenKeys = CapturePlotKeys(graphKeys, _plottedStart,
                _plottedStart + _plottedDuration, 24);
            if (!_plotValidator())
                return;
            foreach (var item in frozenKeys)
            {
                if (!_plotValidator())
                    return;
                Keyframe key = item.Key;
                if (_plotKeyHits.Count >= 24)
                    break;
                float x = 8 + (item.Second - _plottedStart) /
                    _plottedDuration * (width - 16);
                Vector4 color = ReferenceEquals(key, _selectedKey) ?
                    Vector4.One : new Vector4(0.93f, 0.77f, 0.27f, 1);
                plot.AddLine(new Vector2(x, height - 20),
                    new Vector2(x, height - 8), color, 3, _plotValidator);
                if (!_plotValidator())
                    return;
                _plotKeyHits.Add((_selectedMember!, key, item.Track,
                    item.Second, new Vector2(x, height - 14)));
            }
        }
        if (_plotValidator()) plot.End(_plotValidator);
        if (IsPlotBuildCurrent(plot, clip, sourceRevision, viewRevision,
                buildRevision))
            PublishPlotSnapshot(clip);
    }

    private void ResetPlotValueView()
    {
        _plotValueZoom = AnimationPlotValueZoom.MinimumZoom;
        _plotValueCenter = AnimationPlotValueZoom.DefaultCenter;
    }

    private bool IsPlotBuildCurrent(UIRetainedPlot plot, AnimationClip clip,
        ulong sourceRevision, ulong viewRevision, ulong buildRevision)
    {
        if (_destroying || !ReferenceEquals(_plot, plot) ||
            !ReferenceEquals(_plotInput?.Panel, this) ||
            !ReferenceEquals(CurrentClip(), clip) ||
            !ReferenceEquals(_displayedClip, clip) ||
            _sourceRevision != sourceRevision ||
            _plotViewRevision != viewRevision ||
            _plotBuildRevision != buildRevision ||
            (_plotBuildHidden && plot.Root.GetTransformAs<UIBoundableTransform>(true)?
                .Visibility != EVisibility.Hidden) ||
            clip.LengthInSeconds != _plotBuildClipLength ||
            !ReferenceEquals(_selectedMember, _plotBuildSelectedMember) ||
            !ReferenceEquals(_selectedMember?.Animation, _plotBuildSelectedAnimation) ||
            _plotBuildMembers.Length != _plotBuildAnimations.Length)
            return false;
        for (int index = 0; index < _plotBuildMembers.Length; index++)
            if (!ReferenceEquals(_plotBuildMembers[index].Animation,
                    _plotBuildAnimations[index]))
                return false;
        return true;
    }

    private bool ValidateActivePlotBuild()
        => _activePlotBuildPlot is { } plot &&
            _activePlotBuildClip is { } clip &&
            IsPlotBuildCurrent(plot, clip, _activePlotBuildSourceRevision,
                _activePlotBuildViewRevision, _activePlotBuildRevision);

    private void DrawTimeAxis(UIRetainedPlot plot, float width, float height)
    {
        float axisY = height - 22;
        plot.AddLine(new Vector2(8, axisY), new Vector2(width - 8, axisY),
            new Vector4(0.45f, 0.49f, 0.55f, 1), 1, _plotValidator);
        if (!_plotValidator())
            return;
        for (int tick = 0; tick <= 4; tick++)
        {
            float x = 8 + tick * (width - 16) / 4;
            plot.AddLine(new Vector2(x, axisY - 4), new Vector2(x, axisY + 4),
                new Vector4(0.45f, 0.49f, 0.55f, 1), 1, _plotValidator);
            if (!_plotValidator())
                return;
        }
    }

    private void DrawPlotLanes(UIRetainedPlot plot, float width, float height,
        AnimationMember[] visibleMembers)
    {
        int laneCount = Math.Min(5, visibleMembers.Length);
        if (laneCount == 0)
            return;
        var frozenKeys = new List<(Keyframe Key, BaseKeyframeTrack? Track,
            float Second)>[laneCount];
        for (int lane = 0; lane < laneCount; lane++)
            frozenKeys[lane] = visibleMembers[lane].Animation is IEnumerable keys
                ? CapturePlotKeys(keys, _plottedStart,
                    _plottedStart + _plottedDuration, 96) : [];
        if (!_plotValidator())
            return;
        float laneHeight = (height - 34) / laneCount;
        for (int lane = 0; lane < laneCount; lane++)
        {
            AnimationMember member = visibleMembers[lane];
            float y = 6 + (lane + 0.5f) * laneHeight;
            plot.AddLine(new Vector2(8, y), new Vector2(width - 8, y),
                new Vector4(0.32f, 0.36f, 0.42f, 1), 1, _plotValidator);
            if (!_plotValidator())
                return;
            foreach (var item in frozenKeys[lane])
            {
                Keyframe key = item.Key;
                if (_plotKeyHits.Count >= 96)
                    return;
                float x = 8 + (item.Second - _plottedStart) /
                    _plottedDuration * (width - 16);
                Vector4 color = ReferenceEquals(key, _selectedKey) ? Vector4.One :
                    ReferenceEquals(member, _selectedMember) ? PlotPalette[lane % 4] :
                    new Vector4(0.93f, 0.77f, 0.27f, 1);
                plot.AddLine(new Vector2(x, y - 6), new Vector2(x, y + 6),
                    color, 3, _plotValidator);
                if (!_plotValidator())
                    return;
                _plotKeyHits.Add((member, key, item.Track, item.Second,
                    new Vector2(x, y)));
            }
        }
    }

    private static List<(Keyframe Key, BaseKeyframeTrack? Track, float Second)>
        CapturePlotKeys(IEnumerable source, float start, float end, int limit)
    {
        List<(Keyframe, BaseKeyframeTrack?, float)> result = [];
        foreach (object? item in source)
        {
            if (item is not Keyframe key || key.Second < start || key.Second > end)
                continue;
            result.Add((key, key.OwningTrack, key.Second));
            if (result.Count == limit)
                break;
        }
        return result;
    }

    private int WriteChannels(object? value, int index)
    {
        switch (value)
        {
            case float scalar: _plotSamples[0, index] = scalar; return 1;
            case bool boolean: _plotSamples[0, index] = boolean ? 1 : 0; return 1;
            case Vector2 v: _plotSamples[0, index] = v.X; _plotSamples[1, index] = v.Y; return 2;
            case Vector3 v: _plotSamples[0, index] = v.X; _plotSamples[1, index] = v.Y; _plotSamples[2, index] = v.Z; return 3;
            case Vector4 v: _plotSamples[0, index] = v.X; _plotSamples[1, index] = v.Y; _plotSamples[2, index] = v.Z; _plotSamples[3, index] = v.W; return 4;
            case Quaternion q: _plotSamples[0, index] = q.X; _plotSamples[1, index] = q.Y; _plotSamples[2, index] = q.Z; _plotSamples[3, index] = q.W; return 4;
            default: return 0;
        }
    }

    private ulong IdFor(object item)
    {
        if (_ids.TryGetValue(item, out ulong id))
            return id;
        if (_nextId == ulong.MaxValue)
            throw new InvalidOperationException("Animation row ID space is exhausted.");
        id = _nextId;
        SetField(ref _nextId, _nextId + 1, publishNotifications: false);
        _ids.Add(item, id);
        return id;
    }

    private static string FormatKeyValue(Keyframe key) => FormatValue(key switch
    {
        IPlanarKeyframe planar => planar.OutValue,
        QuaternionKeyframe quaternion => quaternion.OutValue,
        BoolKeyframe boolean => boolean.Value,
        _ => null
    });

    private static string FormatDetailValue(object? value) => value switch
    {
        null => "<none>",
        string text => text.Length <= 120 ? text : text[..120] + "…",
        bool or float or Vector2 or Vector3 or Vector4 or Quaternion => FormatValue(value),
        byte number => number.ToString(CultureInfo.InvariantCulture),
        sbyte number => number.ToString(CultureInfo.InvariantCulture),
        short number => number.ToString(CultureInfo.InvariantCulture),
        ushort number => number.ToString(CultureInfo.InvariantCulture),
        int number => number.ToString(CultureInfo.InvariantCulture),
        uint number => number.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        ulong number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("G17", CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        _ => "<" + value.GetType().Name[..Math.Min(value.GetType().Name.Length, 64)] + ">"
    };

    private static string FormatValue(object? value) => value switch
    {
        float scalar => scalar.ToString("G9", CultureInfo.InvariantCulture),
        Vector2 v => FormattableString.Invariant($"{v.X:G9}, {v.Y:G9}"),
        Vector3 v => FormattableString.Invariant($"{v.X:G9}, {v.Y:G9}, {v.Z:G9}"),
        Vector4 v => FormattableString.Invariant($"{v.X:G9}, {v.Y:G9}, {v.Z:G9}, {v.W:G9}"),
        Quaternion q => FormattableString.Invariant($"{q.X:G9}, {q.Y:G9}, {q.Z:G9}, {q.W:G9}"),
        bool boolean => boolean.ToString(),
        _ => "<none>"
    };
}
