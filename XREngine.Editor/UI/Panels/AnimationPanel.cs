using System.Collections;
using System.Globalization;
using System.Numerics;
using XREngine.Animation;
using XREngine.Components.Animation;
using XREngine.Components;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Edits clip keys and shows playback for the selected component.</summary>
public class AnimationPanel : NativeVirtualTextPanel
{
    private static readonly Vector4[] PlotPalette = [new(0.93f, 0.39f, 0.32f, 1),
        new(0.22f, 0.67f, 0.92f, 1), new(0.37f, 0.80f, 0.49f, 1), new(0.92f, 0.77f, 0.24f, 1)];
    private readonly List<(ulong Id, string Text)> _rows = [];
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
    private UIRetainedPlot? _plot;
    private readonly float[,] _plotSamples = new float[4, UIRetainedPlot.MaximumSegments / 4 + 1];
    private bool _graphView = true;
    private ulong _nextId = 32;
    private DateTime _lastRefresh;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        Selection.SelectionChanged += SelectionChanged;
        NativeAssetSelection.SelectionChanged += AssetSelectionChanged;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
        RefreshRows();
    }

    protected override void OnComponentDeactivated()
    {
        Selection.SelectionChanged -= SelectionChanged;
        NativeAssetSelection.SelectionChanged -= AssetSelectionChanged;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
        base.OnComponentDeactivated();
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
        input.Cancelled += _ => RefreshTimeInput();
        SceneNode valueNode = commands.NewChild();
        valueNode.SetTransform<UIBoundableTransform>().Width = 150;
        var (valueInput, _, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(valueNode);
        _valueInput = valueInput;
        valueInput.SingleLineMode = true;
        valueInput.Submitted += SubmitValue;
        valueInput.Cancelled += _ => RefreshValueInput();
        SceneNode searchNode = commands.NewChild();
        searchNode.SetTransform<UIBoundableTransform>().Width = 160;
        var (searchInput, _, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(searchNode);
        _searchInput = searchInput;
        searchInput.SingleLineMode = true;
        searchInput.Submitted += field => { _pathSearch = field.Text.Trim(); _candidatePage = 0; RefreshRows(); };
        searchInput.Cancelled += field => field.Text = _pathSearch;
    }

    protected override float FooterHeight => 180;

    protected override void CreateFooter(SceneNode parent)
        => _plot = new UIRetainedPlot(parent, "AnimationPlot");

    private static AnimationClipComponent? SelectedComponent()
        => Selection.SceneNode?.GetComponent<AnimationClipComponent>();

    private static AnimationClip? CurrentClip()
        => NativeAssetSelection.SelectedTarget as AnimationClip ?? SelectedComponent()?.Animation;

    private void Step(float seconds)
    {
        if (SelectedComponent() is { } component)
            Seek(component.PlaybackTime + seconds);
    }

    private void Seek(float seconds)
    {
        if (SelectedComponent() is not { Animation: { } clip } component)
            return;
        component.EvaluateAtTime(Math.Clamp(seconds, 0, clip.LengthInSeconds), dispatchEvents: false);
        RefreshRows();
    }

    private void AddKey()
    {
        if (CurrentClip() is not { } clip || _selectedMember is not { } member)
            return;
        float second = SelectedComponent()?.PlaybackTime ?? 0.0f;
        if (_timeInput is not null && float.TryParse(_timeInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float entered))
            second = entered;
        if (AnimationClipAuthoringActions.TryAddKey(clip, member, second, out Keyframe? key))
        {
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
        if (CurrentClip() is { } clip && _selectedKey is { } key)
            AnimationClipAuthoringActions.SetKeyTime(clip, key, second);
        else
            Seek(second);
        RefreshRows();
    }

    private void RefreshTimeInput()
    {
        if (_timeInput is not null)
            _timeInput.Text = (_selectedKey?.Second ?? SelectedComponent()?.PlaybackTime ?? 0.0f).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void SubmitValue(UITextInputComponent input)
    {
        if (CurrentClip() is not { } clip || _selectedKey is not { } key || !TryParseValue(input.Text, key.ValueType, out object? value))
        {
            RefreshValueInput();
            return;
        }
        AnimationClipAuthoringActions.SetKeyValue(clip, key, value!);
        RefreshRows();
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
    private void AssetSelectionChanged(object? _) => RefreshRows();

    private void RefreshTick()
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan interval = SelectedComponent()?.IsPlaying == true ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1);
        if (now - _lastRefresh < interval)
            return;
        _lastRefresh = now;
        RefreshRows();
    }

    protected override void OnRowActivated(ulong id)
    {
        if (id is >= 8 and <= 25)
        {
            ActivateCommand(id);
            RefreshRows();
            return;
        }
        if (_memberRows.TryGetValue(id, out AnimationMember? member))
        {
            _selectedMember = member;
            _selectedKey = null;
        }
        else if (_keyRows.TryGetValue(id, out Keyframe? key))
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
        _rows.Clear();
        _memberRows.Clear();
        _keyRows.Clear();
        _pathRows.Clear();
        AnimationClip? clip = CurrentClip();
        if (!ReferenceEquals(clip, _displayedClip))
        {
            _displayedClip = clip;
            _selectedMember = null;
            _selectedKey = null;
            _ids.Clear();
            _nextId = 32;
        }
        if (clip is null)
        {
            _rows.Add((1, "Select an AnimationClip asset or a scene node with an AnimationClipComponent."));
            SetRows(_rows);
            return;
        }
        if (_selectedMember is { } selectedMember &&
            !AnimationClipAuthoringActions.ContainsMember(clip, selectedMember))
        {
            _selectedMember = null;
            _selectedKey = null;
        }
        if (_selectedKey is { } selectedKey &&
            !AnimationClipAuthoringActions.ContainsKey(clip, selectedKey))
            _selectedKey = null;
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
        if (SelectedComponent() is { } component && ReferenceEquals(component.Animation, clip))
        {
            _rows.Add((3, $"Time: {component.PlaybackTime:F3} s | State: {(component.IsPaused ? "Paused" : component.IsPlaying ? "Playing" : "Stopped")}"));
            if (component.PlaybackCapabilityDiagnostic.Length > 0)
                _rows.Add((4, component.PlaybackCapabilityDiagnostic));
        }

        _rows.Add((5, "Members (select a row to edit its keys)"));
        if (clip.RootMember is { } root)
            AddMemberRows(root, 0);
        else
            _rows.Add((0, "This clip has no animation members."));

        if (_selectedMember is { Animation: { } animation } selected)
        {
            _rows.Add((7, "Keys for " + (selected.MemberName ?? "<unnamed>")));
            if (animation is IEnumerable enumerable)
                foreach (object? item in enumerable)
                    if (item is Keyframe key)
                    {
                        ulong id = IdFor(key);
                        _keyRows[id] = key;
                        string marker = ReferenceEquals(_selectedKey, key) ? "> " : "  ";
                        string frame = key.AuthoredFrameIndex >= 0 ? $" | frame {key.AuthoredFrameIndex}" : string.Empty;
                        _rows.Add((id, $"{marker}{key.Second:0.###} s{frame} | {FormatKeyValue(key)}"));
                    }
        }
        if (_candidateRoot is not null)
        {
            _candidateMatchCount = 0;
            foreach (EditorImGuiUI.AnimationClipPathCandidate candidate in _candidates)
                if (_pathSearch.Length == 0 || candidate.DisplayPath.IndexOf(_pathSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                    _candidateMatchCount++;
            int pages = Math.Max(1, (_candidateMatchCount + 127) / 128);
            _candidatePage = Math.Clamp(_candidatePage, 0, pages - 1);
            _rows.Add((22, $"Scene path candidates: {_candidateMatchCount} matches, page {_candidatePage + 1}/{pages}. Third toolbar field filters."));
            _rows.Add((24, "Previous path page"));
            _rows.Add((25, "Next path page"));
            int matched = 0;
            foreach (EditorImGuiUI.AnimationClipPathCandidate candidate in _candidates)
            {
                if (_pathSearch.Length > 0 && candidate.DisplayPath.IndexOf(_pathSearch, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (matched++ < _candidatePage * 128)
                    continue;
                if (matched > (_candidatePage + 1) * 128)
                    break;
                ulong id = IdFor(candidate);
                _pathRows[id] = candidate;
                string marker = ReferenceEquals(candidate, _selectedCandidate) ? "> " : "  ";
                _rows.Add((id, marker + candidate.DisplayPath + " [" + candidate.ValueType.Name + "]"));
            }
        }
        SetRows(_rows);
        RefreshPlot(clip);
    }

    private void AddMemberRows(AnimationMember member, int depth)
    {
        ulong id = IdFor(member);
        _memberRows[id] = member;
        string indent = new(' ', Math.Min(depth, 16) * 2);
        string marker = ReferenceEquals(_selectedMember, member) ? "> " : "  ";
        string suffix = member.Animation is null ? string.Empty : $" [{member.Animation.GetType().Name}]";
        _rows.Add((id, $"{marker}{indent}{member.MemberName} ({member.MemberType}){suffix}"));
        foreach (AnimationMember child in member.Children)
            AddMemberRows(child, depth + 1);
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
            case 14: SelectedComponent()?.Pause(); break;
            case 15: SelectedComponent()?.Resume(); break;
            case 16: _graphView = !_graphView; break;
            case 17: RebuildCandidates(); break;
            case 18:
                if (CurrentClip() is { } clip && _selectedCandidate is { } candidate)
                    EditorImGuiUI.AddAnimationClipPath(clip, candidate);
                break;
            case 19: RemoveSelectedMember(); break;
            case 20: SelectedComponent()?.Play(); break;
            case 21: SelectedComponent()?.StopPlayback(); break;
            case 24: _candidatePage = Math.Max(0, _candidatePage - 1); break;
            case 25: _candidatePage++; break;
        }
    }

    private void RebuildCandidates()
    {
        _candidateRoot = Selection.SceneNode;
        _candidates = _candidateRoot is null ? [] : EditorImGuiUI.DiscoverAnimationClipPaths(_candidateRoot);
        _selectedCandidate = null;
        _candidatePage = 0;
        _ids.Clear();
        _nextId = 32;
    }

    private void RemoveSelectedMember()
    {
        if (CurrentClip() is not { } clip || _selectedMember is not { } member)
            return;
        if (!AnimationClipAuthoringActions.RemoveMember(clip, member))
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
        plot.Begin();
        float width = plot.Width;
        float height = plot.Height;
        if (width < 24 || height < 24 || clip.LengthInSeconds <= 0 || _selectedMember?.Animation is not { } animation)
        {
            plot.End();
            return;
        }
        if (!_graphView)
        {
            float y = height * 0.5f;
            plot.AddLine(new Vector2(8, y), new Vector2(width - 8, y), new Vector4(0.4f, 0.45f, 0.5f, 1));
            if (animation is IEnumerable keys)
                foreach (object? item in keys)
                    if (item is Keyframe key)
                    {
                        float x = 8 + Math.Clamp(key.Second / clip.LengthInSeconds, 0, 1) * (width - 16);
                        Vector4 color = ReferenceEquals(key, _selectedKey) ? Vector4.One : new Vector4(0.93f, 0.77f, 0.27f, 1);
                        plot.AddLine(new Vector2(x, y - 9), new Vector2(x, y + 9), color, 3);
                    }
            plot.End();
            return;
        }

        int count = Math.Clamp((int)(width / 6), 2, UIRetainedPlot.MaximumSegments / 4 + 1);
        int channels = 0;
        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        for (int index = 0; index < count; index++)
        {
            float second = clip.LengthInSeconds * index / (count - 1);
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
        id = _nextId++;
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
