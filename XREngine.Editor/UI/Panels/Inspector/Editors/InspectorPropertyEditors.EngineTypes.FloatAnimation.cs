using System.Globalization;
using System.Numerics;
using System.Reflection;
using XREngine.Animation;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public static partial class EngineTypes
    {
        private static void CreateFloatAnimationDetail(SceneNode node,
            PropertyInfo property, object?[]? targets,
            InspectorFloatTransitionOwnership? transitionPath = null)
        {
            ObjectConstructionIdentity rootIdentity = ObjectConstructionIdentity.Capture(node);
            EnsureVerticalLayout(node);
            if (!rootIdentity.IsCurrent || node.IsDestroyed || node.IsDestroyQueued) return;
            var context = new FloatAnimationDetail(node, property, targets,
                transitionPath);
            InspectorListDetailRefreshComponent lifetime =
                node.AddComponent<InspectorListDetailRefreshComponent>()!;
            if (!rootIdentity.IsCurrent || lifetime is null || lifetime.IsRetired)
            { context.Retire(); return; }
            lifetime.Refresh = context.RequestRefresh;
            lifetime.Release = context.Retire;
            if (!context.CanBuild || lifetime.IsRetired) { context.Retire(); return; }
            InspectorFloatCurveTickComponent tick =
                node.AddComponent<InspectorFloatCurveTickComponent>()!;
            if (!context.CanBuild || lifetime.IsRetired || tick is null)
            { context.Retire(); return; }
            tick.Refresh = context.RefreshIfDirty;
            if (!context.CanBuild || lifetime.IsRetired) { context.Retire(); return; }
            context.Build();
        }

        private sealed class FloatAnimationDetail
        {
            private const int MaximumKeys = 8192;
            private const int SampleCount = 97;
            private readonly SceneNode _root;
            private readonly ObjectConstructionIdentity _rootIdentity;
            private readonly PropertyInfo _property;
            private readonly object?[]? _targets;
            private readonly InspectorFloatTransitionOwnership? _transitionPath;
            private readonly NativeTableRows<FloatKeyframe> _rows = new();
            private readonly Dictionary<FloatKeyframe, ulong> _ids =
                new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<ulong, FloatKeyframe> _keys = [];
            private readonly float[] _samples = new float[SampleCount];
            private InspectorFloatCurveSource? _source;
            private UIRetainedPlot? _plot;
            private UIListTransform? _list;
            private UIVirtualTableProvider<FloatKeyframe>? _table;
            private UITextComponent? _status;
            private UITextInputComponent? _time;
            private UITextInputComponent? _value;
            private UITextInputComponent? _length;
            private UIButtonComponent? _inModeButton;
            private UIButtonComponent? _outModeButton;
            private UITextComponent? _inModeLabel;
            private UITextComponent? _outModeLabel;
            private FloatKeyframe? _selected;
            private float _plotWidth;
            private float _plotHeight;
            private bool _dirty = true;
            private bool _refreshing;
            private bool _retired;
            private bool _stretch;
            private ulong _nextId;
            private ulong _revision;
            private InspectorFloatCurveSource? _activePlotSource;
            private ulong _activePlotRevision;
            private readonly Func<bool> _plotValidator;

            public FloatAnimationDetail(SceneNode root, PropertyInfo property,
                object?[]? targets, InspectorFloatTransitionOwnership? transitionPath)
            {
                _root = root;
                _rootIdentity = ObjectConstructionIdentity.Capture(root);
                _property = property;
                _targets = targets?.ToArray();
                _transitionPath = transitionPath;
                _plotValidator = IsPlotCurrent;
            }

            public void Build()
            {
                if (!ContinueBuild()) return;
                if (_targets is not null)
                    foreach (object? target in _targets)
                        if (target is XRBase owner)
                            owner.PropertyChanged += OuterChanged;
                if (!ContinueBuild()) return;
                SceneNode card = CreateInspectorCard(_root);
                if (!ContinueBuild()) return;
                AddSectionHeader(card, "Float animation");
                if (!ContinueBuild()) return;
                _status = AddInfoLabel(card, "Loading curve...");
                if (!ContinueBuild()) return;

                SceneNode fields = card.NewChild();
                if (!ContinueBuild()) return;
                UIListTransform fieldLayout = fields.SetTransform<UIListTransform>();
                fieldLayout.DisplayHorizontal = true;
                fieldLayout.ItemSpacing = 5;
                _time = AddInput(fields, "Key time", 100, SubmitTime);
                if (!ContinueBuild()) return;
                _value = AddInput(fields, "Key value", 110, SubmitValue);
                if (!ContinueBuild()) return;
                _length = AddInput(fields, "Length", 100, SubmitLength);
                if (!ContinueBuild()) return;

                SceneNode actions = card.NewChild();
                if (!ContinueBuild()) return;
                UIListTransform actionLayout = actions.SetTransform<UIListTransform>();
                actionLayout.DisplayHorizontal = true;
                actionLayout.ItemSpacing = 5;
                CreateInspectorButton(actions, "Add key", AddKey);
                if (!ContinueBuild()) return;
                CreateInspectorButton(actions, "Remove key", RemoveKey);
                if (!ContinueBuild()) return;
                CreateInspectorButton(actions, "Set length", () => SetLength(false));
                if (!ContinueBuild()) return;
                CreateInspectorButton(actions, "Stretch length", () => SetLength(true));
                if (!ContinueBuild()) return;
                SceneNode modes = card.NewChild();
                if (!ContinueBuild()) return;
                UIListTransform modeLayout = modes.SetTransform<UIListTransform>();
                if (!ContinueBuild()) return;
                modeLayout.DisplayHorizontal = true;
                if (!ContinueBuild()) return;
                modeLayout.ItemSpacing = 5;
                if (!ContinueBuild()) return;
                var (_, inModeButton, _, inModeLabel) = NativeUIElements.CreateButton(
                    modes, "In: —", _ => CycleInterpolation(false), width: 130);
                if (!ContinueBuild()) return;
                _inModeButton = inModeButton;
                _inModeLabel = inModeLabel;
                _inModeButton.IsEnabled = false;
                if (!ContinueBuild()) return;
                var (_, outModeButton, _, outModeLabel) = NativeUIElements.CreateButton(
                    modes, "Out: —", _ => CycleInterpolation(true), width: 130);
                if (!ContinueBuild()) return;
                _outModeButton = outModeButton;
                _outModeLabel = outModeLabel;
                _outModeButton.IsEnabled = false;
                if (!ContinueBuild()) return;

                _plot = new UIRetainedPlot(card, "FloatAnimationPlot");
                if (!ContinueBuild()) return;
                _plot.Root.GetTransformAs<UIBoundableTransform>(true)!.Height = 160;
                if (!ContinueBuild()) return;
                SceneNode scrollNode = card.NewChild();
                if (!ContinueBuild()) return;
                UIScrollableTransform scroll = scrollNode.SetTransform<UIScrollableTransform>();
                if (!ContinueBuild()) return;
                scroll.Height = 240;
                if (!ContinueBuild()) return;
                scroll.MinAnchor = Vector2.Zero;
                if (!ContinueBuild()) return;
                scroll.MaxAnchor = Vector2.One;
                if (!ContinueBuild()) return;
                scrollNode.AddComponent<UIScrollRegionComponent>();
                if (!ContinueBuild()) return;
                SceneNode listNode = scrollNode.NewChild();
                if (!ContinueBuild()) return;
                _list = listNode.SetTransform<UIListTransform>();
                if (!ContinueBuild()) return;
                _list.ItemSize = 28;
                if (!ContinueBuild()) return;
                _list.VirtualOverscan = 2;
                if (!ContinueBuild()) return;
                _list.ItemAlignment = EListAlignment.TopOrLeft;
                if (!ContinueBuild()) return;
                _table = new UIVirtualTableProvider<FloatKeyframe>(_rows,
                    UIVirtualTableColumn<FloatKeyframe>.Text("time", "Time", 105,
                        key => key.Second.ToString("G6", CultureInfo.InvariantCulture)),
                    UIVirtualTableColumn<FloatKeyframe>.Text("value", "Value", 105,
                        key => key.OutValue.ToString("G6", CultureInfo.InvariantCulture)));
                _table.AccessibleRowName = key =>
                    $"Key at {key.Second.ToString("G6", CultureInfo.InvariantCulture)}";
                _table.SelectionChanged += SelectKey;
                _table.Activated += ActivateKey;
                if (!ContinueBuild()) return;
                ObjectConstructionIdentity listIdentity =
                    ObjectConstructionIdentity.Capture(listNode);
                ObjectConstructionIdentity transformIdentity =
                    ObjectConstructionIdentity.Capture(_list);
                ObjectConstructionIdentity scrollIdentity =
                    ObjectConstructionIdentity.Capture(scrollNode);
                ObjectConstructionIdentity scrollTransformIdentity =
                    ObjectConstructionIdentity.Capture(scrollNode.Transform);
                ObjectConstructionIdentity cardIdentity =
                    ObjectConstructionIdentity.Capture(card);
                bool ListOwned() => listIdentity.IsCurrent && transformIdentity.IsCurrent &&
                    scrollIdentity.IsCurrent && scrollTransformIdentity.IsCurrent &&
                    cardIdentity.IsCurrent && _rootIdentity.IsCurrent &&
                    ReferenceEquals(_list, listNode.Transform) &&
                    ReferenceEquals(listNode.Parent, scrollNode) &&
                    ReferenceEquals(listNode.Transform.Parent, scrollNode.Transform) &&
                    ReferenceEquals(scrollNode.Parent, card) &&
                    ReferenceEquals(scrollNode.Transform.Parent, card.Transform) &&
                    ReferenceEquals(card.Parent, _root);
                if (!ListOwned()) { Retire(); return; }
                _list.VirtualProvider = _table;
                if (!CanBuild)
                {
                    if (ListOwned()) _list.DetachVirtualProviderForOwnerTeardown();
                    return;
                }
                RequestRefresh();
                RefreshIfDirty();
            }

            public bool CanBuild => !_retired && _rootIdentity.IsCurrent &&
                !_root.IsDestroyed && !_root.IsDestroyQueued;

            private bool ContinueBuild()
            {
                if (CanBuild) return true;
                Retire();
                return false;
            }

            private UITextInputComponent? AddInput(SceneNode parent,
                string name, float width, Action<UITextInputComponent> submit)
            {
                if (!ContinueBuild()) return null;
                SceneNode host = parent.NewChild();
                if (!ContinueBuild()) return null;
                UIListTransform layout = host.SetTransform<UIListTransform>();
                if (!ContinueBuild()) return null;
                layout.DisplayHorizontal = false;
                layout.Width = width;
                AddInfoLabel(host, name);
                if (!ContinueBuild()) return null;
                SceneNode inputHost = host.NewChild();
                if (!ContinueBuild()) return null;
                var (input, _, _, _) =
                    NativeUIElements.CreateTextInputField<UITextInputComponent>(inputHost);
                if (!ContinueBuild()) return null;
                input.AccessibilityLabel = name;
                if (!ContinueBuild()) return null;
                input.SingleLineMode = true;
                if (!ContinueBuild()) return null;
                input.Submitted += submit;
                return input;
            }

            public void RequestRefresh()
            {
                _revision++;
                _dirty = true;
            }

            private bool IsCurrent(InspectorFloatCurveSource? source, ulong revision)
                => !_retired && !_root.IsDestroyed && !_root.IsDestroyQueued &&
                    _revision == revision && ReferenceEquals(_source, source) &&
                    (source is null || source.IsCurrentFast());

            private bool IsPlotCurrent()
                => _plot is not null && _activePlotSource is not null &&
                    IsCurrent(_activePlotSource, _activePlotRevision);

            public void RefreshIfDirty()
            {
                if (_refreshing || _retired || _root.IsDestroyed || _root.IsDestroyQueued)
                    return;
                _refreshing = true;
                try
                {
                    if (_source is { } source && !source.IsCurrentFast())
                        RequestRefresh();
                    if (!_dirty && _plot is { } current &&
                        (current.Width != _plotWidth || current.Height != _plotHeight))
                        RequestRefresh();
                    if (!_dirty)
                        return;
                    _dirty = false;
                    if (_source is null || !_source.IsCurrentFast())
                        RebindSource();
                    if (_retired || _dirty) return;
                    RefreshRows();
                    if (_retired || _dirty) return;
                    RefreshPlot();
                }
                finally { _refreshing = false; }
            }

            private void RebindSource()
            {
                _activePlotSource = null;
                Unsubscribe();
                bool found = InspectorFloatCurveSource.TryCapture(_property,
                    _targets, out _source, out string reason, _transitionPath);
                ulong revision = _revision;
                if (_status is not null)
                    _status.Text = found ? "Select a key to edit its time and value." : reason;
                if (!IsCurrent(_source, revision)) return;
                if (!found || _source is null)
                {
                    _selected = null;
                    return;
                }
                _source.Curve.PropertyChanged += CurveChanged;
                _source.Curve.LengthChanged += AnimationChanged;
                _source.Curve.LoopChanged += AnimationChanged;
                _source.Curve.DefaultValueChanged += VectorChanged;
                _source.Curve.ConstrainKeyframedFPSChanged += VectorChanged;
                _source.Curve.LerpConstrainedFPSChanged += VectorChanged;
                _source.Curve.IsBakedChanged += BakedChanged;
                _source.Curve.BakedFPSChanged += BakedChanged;
                _source.Curve.BakedFrameCountChanged += BakedChanged;
                _source.Curve.BakedValueCompressionAlgorithmChanged += BakedChanged;
                _source.Track.Changed += TrackChanged;
                _source.Track.LengthChanged += TrackLengthChanged;
            }

            private void Unsubscribe()
            {
                if (_source is { } source)
                {
                    source.Curve.PropertyChanged -= CurveChanged;
                    source.Curve.LengthChanged -= AnimationChanged;
                    source.Curve.LoopChanged -= AnimationChanged;
                    source.Curve.DefaultValueChanged -= VectorChanged;
                    source.Curve.ConstrainKeyframedFPSChanged -= VectorChanged;
                    source.Curve.LerpConstrainedFPSChanged -= VectorChanged;
                    source.Curve.IsBakedChanged -= BakedChanged;
                    source.Curve.BakedFPSChanged -= BakedChanged;
                    source.Curve.BakedFrameCountChanged -= BakedChanged;
                    source.Curve.BakedValueCompressionAlgorithmChanged -= BakedChanged;
                    source.Track.Changed -= TrackChanged;
                    source.Track.LengthChanged -= TrackLengthChanged;
                }
                _source = null;
            }

            private void OuterChanged(object? sender, IXRPropertyChangedEventArgs change)
            {
                if (change.PropertyName == _property.Name)
                    RequestRefresh();
            }

            private void CurveChanged(object? sender, IXRPropertyChangedEventArgs change)
            {
                if (change.PropertyName is nameof(BaseAnimation.CurrentTime) or
                    nameof(PropAnimFloat.CurrentPosition) or
                    nameof(PropAnimFloat.CurrentVelocity) or
                    nameof(PropAnimFloat.CurrentAcceleration))
                    return;
                RequestRefresh();
            }

            private void AnimationChanged(BaseAnimation animation) => RequestRefresh();

            private void VectorChanged(PropAnimVector<float, FloatKeyframe> animation)
                => RequestRefresh();

            private void BakedChanged(BasePropAnimBakeable animation)
                => RequestRefresh();

            private void TrackChanged(BaseKeyframeTrack track) => RequestRefresh();

            private void TrackLengthChanged(float previous, BaseKeyframeTrack track)
                => RequestRefresh();

            private void RefreshRows()
            {
                InspectorFloatCurveSource? source = _source;
                ulong revision = _revision;
                _keys.Clear();
                if (source is null || !IsCurrent(source, revision))
                {
                    _rows.Replace([]);
                    if (!IsCurrent(source, revision)) return;
                    _table!.SelectedId = null;
                    if (!IsCurrent(source, revision)) return;
                    RefreshModeControls(source, revision);
                    if (!IsCurrent(source, revision)) return;
                    _list?.InvalidateMeasure();
                    return;
                }
                List<(ulong Id, FloatKeyframe Item)> snapshot = [];
                foreach (FloatKeyframe key in source.Track)
                {
                    if (snapshot.Count == MaximumKeys || !IsCurrent(source, revision))
                        break;
                    if (!_ids.TryGetValue(key, out ulong id))
                        _ids.Add(key, id = ++_nextId);
                    _keys[id] = key;
                    snapshot.Add((id, key));
                }
                if (!IsCurrent(source, revision))
                {
                    RequestRefresh();
                    return;
                }
                if (_ids.Count > MaximumKeys * 2)
                {
                    HashSet<FloatKeyframe> current = [.. snapshot.Select(item => item.Item)];
                    foreach (FloatKeyframe key in _ids.Keys.Where(key => !current.Contains(key)).ToArray())
                        _ids.Remove(key);
                }
                _rows.Replace(snapshot);
                if (!IsCurrent(source, revision)) return;
                if (_selected is not null && !snapshot.Any(item =>
                        ReferenceEquals(item.Item, _selected)))
                    _selected = null;
                _table!.SelectedId = _selected is { } selected &&
                    _ids.TryGetValue(selected, out ulong selectedId) ? selectedId : null;
                if (!IsCurrent(source, revision)) return;
                if (_status is not null)
                    _status.Text = source.Track.Count > MaximumKeys
                        ? $"Showing first {MaximumKeys} keys."
                        : _selected is null ? "Select a key to edit its time and value."
                        : "Editing selected key.";
                if (!IsCurrent(source, revision)) return;
                RefreshInputs(source, revision);
                if (!IsCurrent(source, revision)) return;
                _list?.InvalidateMeasure();
            }

            private void RefreshPlot()
            {
                _activePlotSource = null;
                if (_plot is not { } plot)
                    return;
                try
                {
                _plotWidth = plot.Width;
                _plotHeight = plot.Height;
                plot.Begin();
                if (_source is not { } source || !source.IsCurrentFast() ||
                    _plotWidth <= 20 || _plotHeight <= 20)
                {
                    plot.End();
                    return;
                }
                _activePlotSource = source;
                _activePlotRevision = _revision;
                float length = source.Curve.LengthInSeconds;
                if (!float.IsFinite(length) || length <= 0)
                {
                    plot.End();
                    return;
                }
                float minimum = float.PositiveInfinity;
                float maximum = float.NegativeInfinity;
                for (int index = 0; index < SampleCount; index++)
                {
                    float value = source.Curve.GetValue(length * index / (SampleCount - 1));
                    if (!_plotValidator()) { RequestRefresh(); return; }
                    _samples[index] = value;
                    if (float.IsFinite(value))
                    {
                        minimum = Math.Min(minimum, value);
                        maximum = Math.Max(maximum, value);
                    }
                }
                if (!float.IsFinite(minimum) || !float.IsFinite(maximum))
                {
                    plot.End();
                    return;
                }
                float span = Math.Max(0.001f, maximum - minimum);
                for (int index = 1; index < SampleCount; index++)
                {
                    if (!_plotValidator()) { RequestRefresh(); return; }
                    float a = _samples[index - 1];
                    float b = _samples[index];
                    if (!float.IsFinite(a) || !float.IsFinite(b)) continue;
                    plot.AddLine(new Vector2(8 + (index - 1) * (_plotWidth - 16) /
                        (SampleCount - 1), 8 + (a - minimum) / span * (_plotHeight - 16)),
                        new Vector2(8 + index * (_plotWidth - 16) /
                        (SampleCount - 1), 8 + (b - minimum) / span * (_plotHeight - 16)),
                        new Vector4(0.22f, 0.67f, 0.92f, 1), validate: _plotValidator);
                }
                if (_plotValidator()) plot.End(_plotValidator);
                }
                finally { _activePlotSource = null; }
            }

            private void SelectKey(ulong? id)
            {
                _selected = id is { } value && _keys.TryGetValue(value,
                    out FloatKeyframe? key) ? key : null;
                RequestRefresh();
            }

            private void ActivateKey(ulong id) => SelectKey(id);

            private void RefreshInputs(InspectorFloatCurveSource source, ulong revision)
            {
                if (_time is not null)
                    _time.Text = _selected?.Second.ToString("G9",
                        CultureInfo.InvariantCulture) ?? "0";
                if (!IsCurrent(source, revision)) return;
                if (_value is not null)
                    _value.Text = _selected?.OutValue.ToString("G9",
                        CultureInfo.InvariantCulture) ?? "0";
                if (!IsCurrent(source, revision)) return;
                if (_length is not null)
                    _length.Text = source.Curve.LengthInSeconds.ToString("G9",
                        CultureInfo.InvariantCulture);
                if (!IsCurrent(source, revision)) return;
                RefreshModeControls(source, revision);
            }

            private void RefreshModeControls(InspectorFloatCurveSource? source,
                ulong revision)
            {
                if (!IsCurrent(source, revision)) return;
                FloatKeyframe? key = _selected;
                bool enabled = source is not null && key is not null &&
                    IsPresentedKey(source, key);
                if (_inModeLabel is not null)
                    _inModeLabel.Text = enabled ? $"In: {key!.InterpolationTypeIn}" : "In: —";
                if (!IsCurrent(source, revision)) return;
                if (_inModeButton is not null) _inModeButton.IsEnabled = enabled;
                if (!IsCurrent(source, revision)) return;
                if (_outModeLabel is not null)
                    _outModeLabel.Text = enabled ? $"Out: {key!.InterpolationTypeOut}" : "Out: —";
                if (!IsCurrent(source, revision)) return;
                if (_outModeButton is not null) _outModeButton.IsEnabled = enabled;
            }

            private bool IsPresentedKey(InspectorFloatCurveSource source,
                FloatKeyframe key)
                => ReferenceEquals(key.OwningTrack, source.Track) &&
                    key.TryCaptureAuthoredState(out _) &&
                    _ids.TryGetValue(key, out ulong id) && _keys.TryGetValue(id,
                        out FloatKeyframe? current) && ReferenceEquals(current, key);

            private bool CanEdit(out InspectorFloatCurveSource source)
            {
                source = _source!;
                return !_retired && _root.IsActiveInHierarchy &&
                    source is not null && source.IsCurrent();
            }

            private static bool Parse(UITextInputComponent? input, out float value)
                => float.TryParse(input?.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

            private void SubmitTime(UITextInputComponent input)
            {
                if (CanEdit(out var source) && _selected is { } key &&
                    Parse(input, out float value))
                    InspectorFloatCurveActions.SetTime(source, key, value);
                RequestRefresh();
            }

            private void SubmitValue(UITextInputComponent input)
            {
                if (CanEdit(out var source) && _selected is { } key &&
                    Parse(input, out float value))
                    InspectorFloatCurveActions.SetValue(source, key, value);
                RequestRefresh();
            }

            private void CycleInterpolation(bool outgoing)
            {
                if (CanEdit(out var source) && _selected is { } key &&
                    IsPresentedKey(source, key))
                {
                    EVectorInterpType current = outgoing ? key.InterpolationTypeOut :
                        key.InterpolationTypeIn;
                    EVectorInterpType? next = current switch
                    {
                        EVectorInterpType.Step => EVectorInterpType.Linear,
                        EVectorInterpType.Linear => EVectorInterpType.Smooth,
                        EVectorInterpType.Smooth => EVectorInterpType.Hermite,
                        EVectorInterpType.Hermite => EVectorInterpType.Step,
                        _ => null
                    };
                    if (next is { } mode)
                        InspectorFloatCurveActions.SetInterpolation(source, key,
                            outgoing, mode);
                }
                RequestRefresh();
            }

            private void SubmitLength(UITextInputComponent input) => SetLength(_stretch);

            private void SetLength(bool stretch)
            {
                _stretch = stretch;
                if (CanEdit(out var source) && Parse(_length, out float value))
                    InspectorFloatCurveActions.SetLength(source, value, stretch);
                RequestRefresh();
            }

            private void AddKey()
            {
                if (CanEdit(out var source) && Parse(_time, out float second) &&
                    InspectorFloatCurveActions.Add(source, second, out FloatKeyframe? key))
                    _selected = key;
                RequestRefresh();
            }

            private void RemoveKey()
            {
                if (CanEdit(out var source) && _selected is { } key &&
                    InspectorFloatCurveActions.Remove(source, key))
                    _selected = null;
                RequestRefresh();
            }

            public void Retire()
            {
                if (_retired) return;
                _retired = true;
                _activePlotSource = null;
                if (_root.GetComponent<InspectorFloatCurveTickComponent>() is { } tick)
                    tick.Refresh = null;
                Unsubscribe();
                if (_targets is not null)
                    foreach (object? target in _targets)
                        if (target is XRBase owner)
                            owner.PropertyChanged -= OuterChanged;
                if (_list is not null)
                    _list.DetachVirtualProviderForOwnerTeardown();
                if (_table is not null)
                {
                    _table.SelectionChanged -= SelectKey;
                    _table.Activated -= ActivateKey;
                }
                if (_time is not null) _time.Submitted -= SubmitTime;
                if (_value is not null) _value.Submitted -= SubmitValue;
                if (_length is not null) _length.Submitted -= SubmitLength;
                _rows.Replace([]);
                _keys.Clear();
                _ids.Clear();
            }
        }
    }
}
