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
            PropertyInfo property, object?[]? targets)
        {
            EnsureVerticalLayout(node);
            var context = new FloatAnimationDetail(node, property, targets);
            InspectorListDetailRefreshComponent lifetime =
                node.AddComponent<InspectorListDetailRefreshComponent>()!;
            lifetime.Refresh = context.RequestRefresh;
            lifetime.Release = context.Retire;
            InspectorFloatCurveTickComponent tick =
                node.AddComponent<InspectorFloatCurveTickComponent>()!;
            tick.Refresh = context.RefreshIfDirty;
            context.Build();
        }

        private sealed class FloatAnimationDetail
        {
            private const int MaximumKeys = 8192;
            private const int SampleCount = 97;
            private readonly SceneNode _root;
            private readonly PropertyInfo _property;
            private readonly object?[]? _targets;
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
                object?[]? targets)
            {
                _root = root;
                _property = property;
                _targets = targets?.ToArray();
                _plotValidator = IsPlotCurrent;
            }

            public void Build()
            {
                if (_targets is not null)
                    foreach (object? target in _targets)
                        if (target is XRBase owner)
                            owner.PropertyChanged += OuterChanged;
                SceneNode card = CreateInspectorCard(_root);
                AddSectionHeader(card, "Float animation");
                _status = AddInfoLabel(card, "Loading curve...");

                SceneNode fields = card.NewChild();
                UIListTransform fieldLayout = fields.SetTransform<UIListTransform>();
                fieldLayout.DisplayHorizontal = true;
                fieldLayout.ItemSpacing = 5;
                _time = AddInput(fields, "Key time", 100, SubmitTime);
                _value = AddInput(fields, "Key value", 110, SubmitValue);
                _length = AddInput(fields, "Length", 100, SubmitLength);

                SceneNode actions = card.NewChild();
                UIListTransform actionLayout = actions.SetTransform<UIListTransform>();
                actionLayout.DisplayHorizontal = true;
                actionLayout.ItemSpacing = 5;
                CreateInspectorButton(actions, "Add key", AddKey);
                CreateInspectorButton(actions, "Remove key", RemoveKey);
                CreateInspectorButton(actions, "Set length", () => SetLength(false));
                CreateInspectorButton(actions, "Stretch length", () => SetLength(true));

                _plot = new UIRetainedPlot(card, "FloatAnimationPlot");
                _plot.Root.GetTransformAs<UIBoundableTransform>(true)!.Height = 160;
                SceneNode scrollNode = card.NewChild();
                UIScrollableTransform scroll = scrollNode.SetTransform<UIScrollableTransform>();
                scroll.Height = 240;
                scroll.MinAnchor = Vector2.Zero;
                scroll.MaxAnchor = Vector2.One;
                scrollNode.AddComponent<UIScrollRegionComponent>();
                SceneNode listNode = scrollNode.NewChild();
                _list = listNode.SetTransform<UIListTransform>();
                _list.ItemSize = 28;
                _list.VirtualOverscan = 2;
                _list.ItemAlignment = EListAlignment.TopOrLeft;
                _table = new UIVirtualTableProvider<FloatKeyframe>(_rows,
                    UIVirtualTableColumn<FloatKeyframe>.Text("time", "Time", 105,
                        key => key.Second.ToString("G6", CultureInfo.InvariantCulture)),
                    UIVirtualTableColumn<FloatKeyframe>.Text("value", "Value", 105,
                        key => key.OutValue.ToString("G6", CultureInfo.InvariantCulture)));
                _table.AccessibleRowName = key =>
                    $"Key at {key.Second.ToString("G6", CultureInfo.InvariantCulture)}";
                _table.SelectionChanged += SelectKey;
                _table.Activated += ActivateKey;
                _list.VirtualProvider = _table;
                RequestRefresh();
                RefreshIfDirty();
            }

            private static UITextInputComponent AddInput(SceneNode parent,
                string name, float width, Action<UITextInputComponent> submit)
            {
                SceneNode host = parent.NewChild();
                UIListTransform layout = host.SetTransform<UIListTransform>();
                layout.DisplayHorizontal = false;
                layout.Width = width;
                AddInfoLabel(host, name);
                SceneNode inputHost = host.NewChild();
                var (input, _, _, _) =
                    NativeUIElements.CreateTextInputField<UITextInputComponent>(inputHost);
                input.AccessibilityLabel = name;
                input.SingleLineMode = true;
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
                    _targets, out _source, out string reason);
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
            }

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
