using System.Collections;
using System.Globalization;
using System.Numerics;
using XREngine.Animation;
using XREngine.Components.Animation;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Edits clip keys and shows playback for the selected component.</summary>
public class AnimationPanel : NativeVirtualTextPanel
{
    private const int RowPageSize = 128;
    private const int MaxRetainedRowIds = 8192;
    private const ulong LastCommandRowId = 57;
    private const ulong FirstDynamicRowId = 80;
    private static readonly Vector4[] PlotPalette = [new(0.93f, 0.39f, 0.32f, 1),
        new(0.22f, 0.67f, 0.92f, 1), new(0.37f, 0.80f, 0.49f, 1), new(0.92f, 0.77f, 0.24f, 1)];
    private readonly List<(ulong Id, string Text)> _rows = [];
    private readonly NativeTextList _projectedRows = new();
    private readonly Dictionary<ulong, int> _memberDepths = [];
    private readonly Dictionary<object, ulong> _ids = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ulong, AnimationMember> _memberRows = [];
    private readonly Dictionary<ulong, Keyframe> _keyRows = [];
    private readonly Dictionary<ulong, EditorImGuiUI.AnimationClipPathCandidate> _pathRows = [];
    private AnimationClip? _displayedClip;
    private AnimationMember? _selectedMember;
    private Keyframe? _selectedKey;
    private UITextInputComponent? _timeInput;
    private UITextInputComponent? _valueInput;
    private UITextInputComponent? _searchInput;
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
    private bool _projectionDirty = true;
    private ulong _sourceRevision;
    private ulong _buildingSourceRevision;
    private AnimationClip? _observedClip;
    private readonly Dictionary<AnimationMember,
        EventList<AnimationMember>> _observedMembers =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<BaseKeyframeTrack> _observedTracks =
        new(ReferenceEqualityComparer.Instance);
    private UIRetainedPlot? _plot;
    private readonly List<(Keyframe Key, Vector2 Point)> _plotKeyHits = [];
    private float _plottedWidth;
    private float _plottedHeight;
    private float _plottedStart;
    private float _plottedDuration;
    private float _plotZoom = 1;
    private float _plotWindowStart;
    private readonly float[,] _plotSamples = new float[4, UIRetainedPlot.MaximumSegments / 4 + 1];
    private bool _graphView = true;
    private ulong _nextId = FirstDynamicRowId;
    private DateTime _lastRefresh;
    private bool _ownerSubscriptionsActive;
    private bool _destroying;

    public AnimationPanel()
    {
        RowProviderOverride = _projectedRows;
        _projectedRows.Activated = OnRowActivated;
    }

    protected override void OnComponentActivated()
    {
        if (_destroying)
            return;
        base.OnComponentActivated();
        if (_destroying || IsDestroyed || IsDestroyQueued)
            return;
        _ownerSubscriptionsActive = true;
        Selection.SelectionChanged += SelectionChanged;
        NativeAssetSelection.SelectionChanged += AssetSelectionChanged;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
        RefreshRows();
    }

    protected override void OnComponentDeactivated()
    {
        ReleaseOwnerSubscriptions();
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        _destroying = true;
        UITextInputComponent? time = _timeInput;
        UITextInputComponent? value = _valueInput;
        UITextInputComponent? search = _searchInput;
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
        Attempt(() => base.OnDestroying());
        if (failures.Count != 0)
            throw new AggregateException("Animation panel teardown failed.", failures);
    }

    private void ReleaseOwnerSubscriptions()
    {
        ReleaseProjectionSources();
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
        var (searchInput, _, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(searchNode);
        _searchInput = searchInput;
        searchInput.SingleLineMode = true;
        searchInput.Submitted += SubmitPathSearch;
        searchInput.Cancelled += CancelPathSearch;
    }

    private void CancelTime(UITextInputComponent _) { if (!_destroying) RefreshTimeInput(); }
    private void CancelValue(UITextInputComponent _) { if (!_destroying) RefreshValueInput(); }
    private void SubmitPathSearch(UITextInputComponent field)
    {
        if (_destroying)
            return;
        _pathSearch = field.Text.Trim();
        _candidatePage = 0;
        RefreshRows();
    }
    private void CancelPathSearch(UITextInputComponent field)
    {
        if (!_destroying)
            field.Text = _pathSearch;
    }

    protected override float FooterHeight => 180;

    protected override void CreateFooter(SceneNode parent)
        => _plot = new UIRetainedPlot(parent, "AnimationPlot");

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
        if (!TryGetCurrentKeyDraft(out AnimationClip? clip,
                out _, out Keyframe? key) ||
            !TryParseValue(input.Text, key!.ValueType, out object? value))
        {
            RefreshValueInput();
            return;
        }
        AnimationClipAuthoringActions.SetKeyValue(clip!, key, value!);
        RefreshRows();
    }

    private bool TryGetCurrentKeyDraft(out AnimationClip? clip,
        out AnimationMember? member, out Keyframe? key)
    {
        clip = CurrentClip();
        member = _selectedMember;
        key = _selectedKey;
        return clip is not null && ReferenceEquals(clip, _displayedClip) &&
            member is not null && key is not null &&
            AnimationClipAuthoringActions.ContainsMember(clip, member) &&
            MemberContainsKey(member, key);
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
        if (_valueInput is not null)
            _valueInput.Text = _selectedKey is null ? string.Empty : FormatKeyValue(_selectedKey);
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
        DateTime now = DateTime.UtcNow;
        TimeSpan interval = SelectedMatchingComponent()?.IsPlaying == true ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1);
        if (now - _lastRefresh < interval)
            return;
        _lastRefresh = now;
        AnimationClip? clip = CurrentClip();
        if (_projectionDirty || !ReferenceEquals(clip, _displayedClip) ||
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
            (MathF.Abs(plot.Width - _plottedWidth) > 0.5f ||
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
            ActivateCommand(id);
            RefreshRows();
            return;
        }
        if (_memberRows.TryGetValue(id, out AnimationMember? member) &&
            _displayedClip is { } clip &&
            AnimationClipAuthoringActions.ContainsMember(clip, member))
        {
            _selectedMember = member;
            _selectedKey = null;
            _keyPage = 0;
        }
        else if (_keyRows.TryGetValue(id, out Keyframe? key) &&
            _selectedMember is { } selected && MemberContainsKey(selected, key))
            _selectedKey = key;
        else if (_pathRows.TryGetValue(id, out EditorImGuiUI.AnimationClipPathCandidate? candidate))
            _selectedCandidate = candidate;
        else
            return;
        RefreshTimeInput();
        RefreshValueInput();
        RefreshRows();
    }

    protected override void RefreshRows()
    {
        if (_destroying)
            return;
        if (_ids.Count >= MaxRetainedRowIds)
            _ids.Clear();
        _buildingSourceRevision = _sourceRevision;
        _rows.Clear();
        _statusRowIndex = -1;
        _memberDepths.Clear();
        _memberRows.Clear();
        _keyRows.Clear();
        _pathRows.Clear();
        AnimationClip? clip = CurrentClip();
        if (!ReferenceEquals(clip, _displayedClip))
        {
            ReleaseProjectionSources();
            _displayedClip = clip;
            _selectedMember = null;
            _selectedKey = null;
            _memberPage = 0;
            _keyPage = 0;
            _plotZoom = 1;
            _plotWindowStart = 0;
            _ids.Clear();
        }
        if (clip is null)
        {
            _rows.Add((1, "Select an AnimationClip asset or a scene node with an AnimationClipComponent."));
            SetProjectedRows();
            return;
        }
        if (_selectedMember is { } selectedMember &&
            !AnimationClipAuthoringActions.ContainsMember(clip, selectedMember))
        {
            _selectedMember = null;
            _selectedKey = null;
        }
        if (_selectedKey is { } selectedKey &&
            (_selectedMember is not { } owner ||
             !MemberContainsKey(owner, selectedKey)))
            _selectedKey = null;
        if (_ownerSubscriptionsActive && IsActiveInHierarchy)
            ObserveProjectionSources(clip);
        if (!ReferenceEquals(_candidateRoot, Selection.SceneNode))
            RebuildCandidates();

        _rows.Add((1, "Clip: " + (clip.Name ?? "<unnamed>")));
        _rows.Add((2, $"Length: {clip.LengthInSeconds:F3} s | Sample rate: {clip.SampleRate} fps | Loop: {clip.Looped}"));
        _rows.Add((6, "Toolbar fields: key time (seconds), then selected key value (comma-separated components). Press Enter to apply."));
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
        _rows.Add((20, "Play scene clip"));
        _rows.Add((21, "Stop scene clip"));
        _rows.Add((30, "Length -0.1 s"));
        _rows.Add((31, "Length +0.1 s"));
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
            AddMemberRows(root);
        else
            _rows.Add((0, "This clip has no animation members."));

        if (_selectedMember is { Animation: { } animation } selected)
        {
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
                _keyPage = keyPages - 1;
                RefreshRows();
                return;
            }
        }
        if (_candidateRoot is not null)
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
        RefreshPlot(clip);
    }

    private void AddMemberRows(AnimationMember root)
    {
        int firstRow = _rows.Count;
        Stack<(AnimationMember Member, int Depth)> pending = new();
        HashSet<AnimationMember> visited = new(ReferenceEqualityComparer.Instance);
        pending.Push((root, 0));
        _memberCount = 0;
        while (pending.Count > 0)
        {
            (AnimationMember member, int depth) = pending.Pop();
            if (!visited.Add(member))
                continue;
            int index = _memberCount++;
            if (index >= _memberPage * RowPageSize &&
                index < (_memberPage + 1) * RowPageSize)
            {
                ulong id = IdFor(member);
                _memberRows[id] = member;
                _memberDepths[id] = depth;
                _rows.Add((id, string.Empty));
            }
            for (int child = member.Children.Count - 1; child >= 0; child--)
                pending.Push((member.Children[child], depth + 1));
        }
        int pages = Math.Max(1, (_memberCount + RowPageSize - 1) / RowPageSize);
        if (_memberPage >= pages)
        {
            _memberPage = pages - 1;
            _rows.RemoveRange(firstRow, _rows.Count - firstRow);
            _memberRows.Clear();
            _memberDepths.Clear();
            AddMemberRows(root);
        }
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
        if (ReferenceEquals(_observedClip, clip) && !_projectionDirty)
            return;
        ReleaseProjectionSources();
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
    }

    private void ProjectionClipChanged(object? sender,
        IXRPropertyChangedEventArgs _)
    {
        if (ReferenceEquals(sender, _observedClip))
            MarkProjectionDirty();
    }

    private void ProjectionMemberChanged(object? sender,
        IXRPropertyChangedEventArgs _)
    {
        if (sender is AnimationMember member &&
            _observedMembers.ContainsKey(member))
            MarkProjectionDirty();
    }

    private void ProjectionChildrenChanged() => MarkProjectionDirty();

    private void ProjectionTrackChanged(BaseKeyframeTrack track)
    {
        if (_observedTracks.Contains(track))
            MarkProjectionDirty();
    }

    private void MarkProjectionDirty()
    {
        if (_destroying || !_ownerSubscriptionsActive || !IsActiveInHierarchy)
            return;
        _sourceRevision++;
        _projectionDirty = true;
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
            case 16: _graphView = !_graphView; break;
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
            case 24: _candidatePage = Math.Max(0, _candidatePage - 1); break;
            case 25: _candidatePage++; break;
            case 26: _memberPage = Math.Max(0, _memberPage - 1); break;
            case 27: _memberPage++; break;
            case 28: _keyPage = Math.Max(0, _keyPage - 1); break;
            case 29: _keyPage++; break;
            case 30: EditClipSettings(clip => AnimationClipAuthoringActions.SetClipLength(
                clip, MathF.Max(0, clip.LengthInSeconds - 0.1f))); break;
            case 31: EditClipSettings(clip => AnimationClipAuthoringActions.SetClipLength(
                clip, MathF.Min(3600, clip.LengthInSeconds + 0.1f))); break;
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
            case 35: _plotZoom = MathF.Min(32, _plotZoom * 1.5f); break;
            case 36: _plotZoom = MathF.Max(1, _plotZoom / 1.5f); break;
            case 37: _plotWindowStart = MathF.Max(0,
                _plotWindowStart - MathF.Max(0.01f, _plottedDuration * 0.75f)); break;
            case 38: _plotZoom = 1; _plotWindowStart = 0; break;
            case 39: _plotWindowStart +=
                MathF.Max(0.01f, _plottedDuration * 0.75f); break;
            case 40: CycleSelectedInterpolation(outgoing: false); break;
            case 41: CycleSelectedInterpolation(outgoing: true); break;
            case 42: SetSelectedTangent(outgoing: false); break;
            case 43: SetSelectedTangent(outgoing: true); break;
            case 44: SetSelectedFrame(); break;
            case 47: SetSelectedIncomingValue(); break;
            case 48: EditSelectedKey(AnimationClipAuthoringActions.UnifyKey); break;
            case 57: EditSelectedKey(AnimationClipAuthoringActions.AutoKeyTangents); break;
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
        if (!TryGetCurrentKeyDraft(out AnimationClip? clip, out _,
                out Keyframe? key) || _valueInput is not { } input ||
            !TryParseValue(input.Text, key!.ValueType, out object? value))
            return;
        AnimationClipAuthoringActions.SetKeyTangent(clip!, key, outgoing,
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
        if (!TryGetCurrentKeyDraft(out AnimationClip? clip, out _,
                out Keyframe? key) || _valueInput is not { } input ||
            !TryParseValue(input.Text, key!.ValueType, out object? value))
            return;
        AnimationClipAuthoringActions.SetKeyIncomingValue(clip!, key, value!);
    }

    private void EditSelectedKey(Func<AnimationClip, Keyframe, bool> edit)
    {
        if (TryGetCurrentKeyDraft(out AnimationClip? clip, out _,
                out Keyframe? key))
            edit(clip!, key!);
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

    private void RebuildCandidates()
    {
        _candidateRoot = Selection.SceneNode;
        _candidates = _candidateRoot is null ? [] : EditorImGuiUI.DiscoverAnimationClipPaths(_candidateRoot);
        _selectedCandidate = null;
        _candidatePage = 0;
        _ids.Clear();
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
        if (_plot is not { } plot)
            return;
        _plotKeyHits.Clear();
        plot.Begin();
        float width = plot.Width;
        float height = plot.Height;
        _plottedWidth = width;
        _plottedHeight = height;
        if (width < 24 || height < 24 || clip.LengthInSeconds <= 0 || _selectedMember?.Animation is not { } animation)
        {
            plot.End();
            return;
        }
        _plottedDuration = clip.LengthInSeconds / MathF.Max(1, _plotZoom);
        _plotWindowStart = Math.Clamp(_plotWindowStart, 0,
            MathF.Max(0, clip.LengthInSeconds - _plottedDuration));
        _plottedStart = _plotWindowStart;
        if (!_graphView)
        {
            float y = height * 0.5f;
            plot.AddLine(new Vector2(8, y), new Vector2(width - 8, y), new Vector4(0.4f, 0.45f, 0.5f, 1));
            if (animation is IEnumerable keys)
                foreach (object? item in keys)
                    if (item is Keyframe key)
                    {
                        if (key.Second < _plottedStart ||
                            key.Second > _plottedStart + _plottedDuration)
                            continue;
                        if (_plotKeyHits.Count >= UIRetainedPlot.MaximumSegments - 1)
                            break;
                        float x = 8 + (key.Second - _plottedStart) /
                            _plottedDuration * (width - 16);
                        Vector4 color = ReferenceEquals(key, _selectedKey) ? Vector4.One : new Vector4(0.93f, 0.77f, 0.27f, 1);
                        plot.AddLine(new Vector2(x, y - 9), new Vector2(x, y + 9), color, 3);
                        _plotKeyHits.Add((key, new Vector2(x, y)));
                    }
            plot.End();
            return;
        }

        int count = Math.Clamp((int)(width / 8), 2,
            (UIRetainedPlot.MaximumSegments - 32) / 4 + 1);
        int channels = 0;
        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        for (int index = 0; index < count; index++)
        {
            float second = _plottedStart + _plottedDuration * index / (count - 1);
            object? value = animation.GetValueGeneric(second);
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
            plot.End();
            return;
        }
        float span = Math.Max(0.001f, maximum - minimum);
        for (int channel = 0; channel < channels; channel++)
            for (int index = 1; index < count; index++)
            {
                Vector2 from = new(8 + (index - 1) * (width - 16) / (count - 1),
                    12 + (_plotSamples[channel, index - 1] - minimum) / span * (height - 24));
                Vector2 to = new(8 + index * (width - 16) / (count - 1),
                    12 + (_plotSamples[channel, index] - minimum) / span * (height - 24));
                plot.AddLine(from, to, PlotPalette[channel]);
            }
        if (animation is IEnumerable graphKeys)
            foreach (object? item in graphKeys)
            {
                if (item is not Keyframe key)
                    continue;
                if (key.Second < _plottedStart ||
                    key.Second > _plottedStart + _plottedDuration)
                    continue;
                if (_plotKeyHits.Count >= 32)
                    break;
                float x = 8 + (key.Second - _plottedStart) /
                    _plottedDuration * (width - 16);
                Vector4 color = ReferenceEquals(key, _selectedKey) ?
                    Vector4.One : new Vector4(0.93f, 0.77f, 0.27f, 1);
                plot.AddLine(new Vector2(x, height - 20),
                    new Vector2(x, height - 8), color, 3);
                _plotKeyHits.Add((key, new Vector2(x, height - 14)));
            }
        plot.End();
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
