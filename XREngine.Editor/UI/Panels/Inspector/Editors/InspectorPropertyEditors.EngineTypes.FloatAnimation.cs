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
            private readonly XRBase[] _outerOwners;
            private readonly InspectorFloatTransitionOwnership? _transitionPath;
            private readonly NativeTableRows<FloatKeyframe> _rows = new();
            private readonly Dictionary<FloatKeyframe, ulong> _ids =
                new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<ulong, FloatKeyframe> _keys = [];
            private readonly float[] _samples = new float[SampleCount];
            private InspectorFloatCurveSource? _source;
            private InspectorFloatCurveLengthSelection? _lengthSelection;
            private InspectorFloatCurveLengthSelection? _presentedLengthSelection;
            private InspectorFloatCurveLengthSelection? _presentedAddSelection;
            private InspectorFloatCurveSource? _presentedLengthSource;
            private ulong _presentedLengthRevision;
            private ulong _presentedAddRevision;
            private UIRetainedPlot? _plot;
            private UIListTransform? _list;
            private UIVirtualTableProvider<FloatKeyframe>? _table;
            private UITextComponent? _status;
            private UITextInputComponent? _time;
            private UITextInputComponent? _value;
            private UITextInputComponent? _inValue;
            private UITextInputComponent? _inTangent;
            private UITextInputComponent? _outTangent;
            private UITextInputComponent? _inWeight;
            private UITextInputComponent? _outWeight;
            private UITextInputComponent? _length;
            private UIButtonComponent? _addKeyButton;
            private UIButtonComponent? _removeKeyButton;
            private UIButtonComponent? _setLengthButton;
            private UIButtonComponent? _stretchLengthButton;
            private UIButtonComponent? _inModeButton;
            private UIButtonComponent? _outModeButton;
            private UITextComponent? _inModeLabel;
            private UITextComponent? _outModeLabel;
            private UIButtonComponent? _syncValuesButton;
            private UIButtonComponent? _syncDirectionsButton;
            private UIButtonComponent? _syncMagnitudesButton;
            private UITextComponent? _syncValuesLabel;
            private UITextComponent? _syncDirectionsLabel;
            private UITextComponent? _syncMagnitudesLabel;
            private UIButtonComponent? _inWeightedButton;
            private UIButtonComponent? _outWeightedButton;
            private UITextComponent? _inWeightedLabel;
            private UITextComponent? _outWeightedLabel;
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
                _outerOwners = _targets?.OfType<XRBase>()
                    .Distinct<XRBase>(ReferenceEqualityComparer.Instance).ToArray() ?? [];
                _transitionPath = transitionPath;
                _plotValidator = IsPlotCurrent;
            }

            public void Build()
            {
                if (!ContinueBuild()) return;
                foreach (XRBase owner in _outerOwners)
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
                _value = AddInput(fields, "Out value", 110, SubmitValue);
                if (!ContinueBuild()) return;
                _length = AddInput(fields, "Length", 100, SubmitLength);
                if (!ContinueBuild()) return;

                SceneNode handles = card.NewChild();
                if (!ContinueBuild()) return;
                UIListTransform handlesLayout = handles.SetTransform<UIListTransform>();
                if (!ContinueBuild()) return;
                handlesLayout.DisplayHorizontal = true;
                if (!ContinueBuild()) return;
                handlesLayout.ItemSpacing = 5;
                if (!ContinueBuild()) return;
                _inValue = AddInput(handles, "In value", 105, SubmitIncomingValue);
                if (!ContinueBuild()) return;
                _inTangent = AddInput(handles, "In tangent", 105, SubmitIncomingTangent);
                if (!ContinueBuild()) return;
                _outTangent = AddInput(handles, "Out tangent", 105, SubmitOutgoingTangent);
                if (!ContinueBuild()) return;
                _inValue!.IsEnabled = false;
                if (!ContinueBuild()) return;
                _inTangent!.IsEnabled = false;
                if (!ContinueBuild()) return;
                _outTangent!.IsEnabled = false;
                if (!ContinueBuild()) return;

                SceneNode actions = card.NewChild();
                if (!ContinueBuild()) return;
                UIListTransform actionLayout = actions.SetTransform<UIListTransform>();
                actionLayout.DisplayHorizontal = true;
                actionLayout.ItemSpacing = 5;
                _addKeyButton = CreateInspectorButton(actions, "Add key", AddKey);
                if (!ContinueBuild()) return;
                _removeKeyButton = CreateInspectorButton(actions, "Remove key", RemoveKey);
                if (!ContinueBuild()) return;
                _setLengthButton = CreateInspectorButton(actions, "Set length", () => SetLength(false));
                if (!ContinueBuild()) return;
                _stretchLengthButton = CreateInspectorButton(actions, "Stretch length", () => SetLength(true));
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

                SceneNode syncRow = card.NewChild();
                if (!ContinueBuild()) return;
                UIListTransform syncLayout = syncRow.SetTransform<UIListTransform>();
                if (!ContinueBuild()) return;
                syncLayout.DisplayHorizontal = true;
                if (!ContinueBuild()) return;
                syncLayout.ItemSpacing = 5;
                if (!ContinueBuild()) return;
                var (_, syncValuesButton, _, syncValuesLabel) =
                    NativeUIElements.CreateButton(syncRow, "Link values: —",
                        _ => ToggleSync(0), width: 135);
                if (!ContinueBuild()) return;
                _syncValuesButton = syncValuesButton;
                _syncValuesLabel = syncValuesLabel;
                _syncValuesButton.IsEnabled = false;
                if (!ContinueBuild()) return;
                var (_, syncDirectionsButton, _, syncDirectionsLabel) =
                    NativeUIElements.CreateButton(syncRow, "Link directions: —",
                        _ => ToggleSync(1), width: 160);
                if (!ContinueBuild()) return;
                _syncDirectionsButton = syncDirectionsButton;
                _syncDirectionsLabel = syncDirectionsLabel;
                _syncDirectionsButton.IsEnabled = false;
                if (!ContinueBuild()) return;

                SceneNode syncMagnitudeRow = card.NewChild();
                if (!ContinueBuild()) return;
                UIListTransform syncMagnitudeLayout =
                    syncMagnitudeRow.SetTransform<UIListTransform>();
                if (!ContinueBuild()) return;
                syncMagnitudeLayout.DisplayHorizontal = true;
                if (!ContinueBuild()) return;
                var (_, syncMagnitudesButton, _, syncMagnitudesLabel) =
                    NativeUIElements.CreateButton(syncMagnitudeRow,
                        "Link magnitudes: —", _ => ToggleSync(2), width: 160);
                if (!ContinueBuild()) return;
                _syncMagnitudesButton = syncMagnitudesButton;
                _syncMagnitudesLabel = syncMagnitudesLabel;
                _syncMagnitudesButton.IsEnabled = false;
                if (!ContinueBuild()) return;

                SceneNode weights = card.NewChild();
                if (!ContinueBuild()) return;
                UIListTransform weightsLayout = weights.SetTransform<UIListTransform>();
                if (!ContinueBuild()) return;
                weightsLayout.DisplayHorizontal = true;
                if (!ContinueBuild()) return;
                weightsLayout.ItemSpacing = 5;
                if (!ContinueBuild()) return;
                _inWeight = AddInput(weights, "In weight", 105, SubmitIncomingWeight);
                if (!ContinueBuild()) return;
                _outWeight = AddInput(weights, "Out weight", 105, SubmitOutgoingWeight);
                if (!ContinueBuild()) return;
                _inWeight!.IsEnabled = false;
                if (!ContinueBuild()) return;
                _outWeight!.IsEnabled = false;
                if (!ContinueBuild()) return;

                SceneNode weightedModes = card.NewChild();
                if (!ContinueBuild()) return;
                UIListTransform weightedLayout =
                    weightedModes.SetTransform<UIListTransform>();
                if (!ContinueBuild()) return;
                weightedLayout.DisplayHorizontal = true;
                if (!ContinueBuild()) return;
                weightedLayout.ItemSpacing = 5;
                if (!ContinueBuild()) return;
                var (_, inWeightedButton, _, inWeightedLabel) =
                    NativeUIElements.CreateButton(weightedModes, "Weighted in: —",
                        _ => ToggleWeighted(false), width: 135);
                if (!ContinueBuild()) return;
                _inWeightedButton = inWeightedButton;
                _inWeightedLabel = inWeightedLabel;
                _inWeightedButton.IsEnabled = false;
                if (!ContinueBuild()) return;
                var (_, outWeightedButton, _, outWeightedLabel) =
                    NativeUIElements.CreateButton(weightedModes, "Weighted out: —",
                        _ => ToggleWeighted(true), width: 135);
                if (!ContinueBuild()) return;
                _outWeightedButton = outWeightedButton;
                _outWeightedLabel = outWeightedLabel;
                _outWeightedButton.IsEnabled = false;
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
                _presentedLengthSelection = null;
                _presentedAddSelection = null;
                _presentedLengthSource = null;
            }

            private bool IsCurrent(InspectorFloatCurveSource? source, ulong revision)
                => !_retired && _rootIdentity.IsCurrent &&
                    !_root.IsDestroyed && !_root.IsDestroyQueued &&
                    _revision == revision && ReferenceEquals(_source, source) &&
                    (source is null || source.IsCurrentFast()) &&
                    (_lengthSelection is null || _lengthSelection.IsCurrentFast());

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
                    if (_lengthSelection is { } group && !group.IsCurrentFast())
                        RequestRefresh();
                    if (!_dirty && _plot is { } current &&
                        (current.Width != _plotWidth || current.Height != _plotHeight))
                        RequestRefresh();
                    if (!_dirty)
                        return;
                    _dirty = false;
                    if (_source is null && _lengthSelection is null ||
                        _source is { } bound && !bound.IsCurrentFast() ||
                        _lengthSelection is { } selected && !selected.IsCurrentFast())
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
                if (!found)
                {
                    bool grouped = InspectorFloatCurveLengthSelection.TryCapture(
                        _property, _targets,
                        out InspectorFloatCurveLengthSelection? selection,
                        out string groupReason, _transitionPath);
                    if (grouped && selection is { HasDistinctCurves: true })
                        _lengthSelection = selection;
                    else if (!grouped)
                        reason = groupReason;
                }
                ulong revision = _revision;
                if (_status is not null)
                    _status.Text = found ? "Select a key to edit its time and value." :
                        _lengthSelection is { } limited &&
                            !InspectorFloatCurveAddEdit.CanAddWithinLimits(limited)
                            ? "Multiple curves selected. Add supports up to 64 curves and 100000 keys after insertion; length edits still apply to all."
                            : _lengthSelection is not null
                            ? "Multiple curves selected. Add at a common time or edit all lengths; existing keys remain read-only."
                            : reason;
                if (!IsCurrent(_source, revision)) return;
                if (!found || _source is null)
                {
                    _selected = null;
                    if (_lengthSelection is { } group)
                        foreach (InspectorFloatCurveSource member in group.Curves)
                        {
                            Subscribe(member);
                            if (!IsCurrent(null, revision)) return;
                        }
                    return;
                }
                Subscribe(_source);
            }

            private void Unsubscribe()
            {
                if (_source is { } source)
                    Unsubscribe(source);
                if (_lengthSelection is { } group)
                    foreach (InspectorFloatCurveSource member in group.Curves)
                        Unsubscribe(member);
                _source = null;
                _lengthSelection = null;
                _presentedLengthSelection = null;
                _presentedAddSelection = null;
                _presentedLengthSource = null;
            }

            private void Subscribe(InspectorFloatCurveSource source)
            {
                source.Curve.PropertyChanged += CurveChanged;
                source.Curve.LengthChanged += AnimationChanged;
                source.Curve.LoopChanged += AnimationChanged;
                source.Curve.DefaultValueChanged += VectorChanged;
                source.Curve.ConstrainKeyframedFPSChanged += VectorChanged;
                source.Curve.LerpConstrainedFPSChanged += VectorChanged;
                source.Curve.IsBakedChanged += BakedChanged;
                source.Curve.BakedFPSChanged += BakedChanged;
                source.Curve.BakedFrameCountChanged += BakedChanged;
                source.Curve.BakedValueCompressionAlgorithmChanged += BakedChanged;
                source.Track.Changed += TrackChanged;
                source.Track.LengthChanged += TrackLengthChanged;
            }

            private void Unsubscribe(InspectorFloatCurveSource source)
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
                    _selected = null;
                    bool canAdd = _lengthSelection is { } addSelection &&
                        InspectorFloatCurveAddEdit.CanAddWithinLimits(addSelection);
                    if (_time is not null) _time.Text = canAdd ? "0" : "";
                    if (!IsCurrent(source, revision)) return;
                    if (_time is not null) _time.IsEnabled = canAdd;
                    if (!IsCurrent(source, revision)) return;
                    if (_value is not null) _value.IsEnabled = false;
                    if (!IsCurrent(source, revision)) return;
                    if (_length is not null)
                    {
                        _length.Text = _lengthSelection?.CommonLength?.ToString("G9",
                            CultureInfo.InvariantCulture) ?? "";
                        if (!IsCurrent(source, revision)) return;
                        _length.IsEnabled = _lengthSelection is not null;
                        if (!IsCurrent(source, revision)) return;
                        _presentedLengthSelection = _lengthSelection;
                        _presentedLengthSource = null;
                        _presentedLengthRevision = revision;
                    }
                    RefreshActionButtons(source, revision);
                    if (!IsCurrent(source, revision)) return;
                    RefreshModeControls(source, revision);
                    if (!IsCurrent(source, revision)) return;
                    _list?.InvalidateMeasure();
                    if (!IsCurrent(source, revision)) return;
                    _presentedAddSelection = canAdd ? _lengthSelection : null;
                    _presentedAddRevision = revision;
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
                if (_time is not null) _time.IsEnabled = _selected is not null;
                if (!IsCurrent(source, revision)) return;
                if (_value is not null)
                    _value.Text = _selected?.OutValue.ToString("G9",
                        CultureInfo.InvariantCulture) ?? "0";
                if (!IsCurrent(source, revision)) return;
                if (_value is not null) _value.IsEnabled = _selected is not null;
                if (!IsCurrent(source, revision)) return;
                if (_length is not null)
                    _length.Text = source.Curve.LengthInSeconds.ToString("G9",
                        CultureInfo.InvariantCulture);
                if (!IsCurrent(source, revision)) return;
                if (_length is not null) _length.IsEnabled = true;
                if (!IsCurrent(source, revision)) return;
                _presentedLengthSelection = null;
                _presentedAddSelection = null;
                _presentedLengthSource = source;
                _presentedLengthRevision = revision;
                RefreshActionButtons(source, revision);
                if (!IsCurrent(source, revision)) return;
                RefreshModeControls(source, revision);
            }

            private void RefreshActionButtons(InspectorFloatCurveSource? source,
                ulong revision)
            {
                if (!IsCurrent(source, revision)) return;
                bool keys = source is not null;
                bool length = keys || _lengthSelection is not null;
                if (_addKeyButton is not null)
                    _addKeyButton.IsEnabled = keys ||
                        _lengthSelection is { } selection &&
                        InspectorFloatCurveAddEdit.CanAddWithinLimits(selection);
                if (!IsCurrent(source, revision)) return;
                if (_removeKeyButton is not null)
                    _removeKeyButton.IsEnabled = keys && _selected is { } selected &&
                        IsPresentedKey(source!, selected);
                if (!IsCurrent(source, revision)) return;
                if (_setLengthButton is not null) _setLengthButton.IsEnabled = length;
                if (!IsCurrent(source, revision)) return;
                if (_stretchLengthButton is not null) _stretchLengthButton.IsEnabled = length;
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
                if (!IsCurrent(source, revision)) return;
                if (_inValue is not null)
                    _inValue.Text = enabled ? key!.InValue.ToString("G9",
                        CultureInfo.InvariantCulture) : "";
                if (!IsCurrent(source, revision)) return;
                if (_inValue is not null) _inValue.IsEnabled = enabled;
                if (!IsCurrent(source, revision)) return;
                if (_inTangent is not null)
                    _inTangent.Text = enabled ? key!.InTangent.ToString("G9",
                        CultureInfo.InvariantCulture) : "";
                if (!IsCurrent(source, revision)) return;
                if (_inTangent is not null) _inTangent.IsEnabled = enabled;
                if (!IsCurrent(source, revision)) return;
                if (_outTangent is not null)
                    _outTangent.Text = enabled ? key!.OutTangent.ToString("G9",
                        CultureInfo.InvariantCulture) : "";
                if (!IsCurrent(source, revision)) return;
                if (_outTangent is not null) _outTangent.IsEnabled = enabled;
                if (!IsCurrent(source, revision)) return;
                if (_syncValuesLabel is not null)
                    _syncValuesLabel.Text = enabled
                        ? $"Link values: {OnOff(key!.SyncInOutValues)}" : "Link values: —";
                if (!IsCurrent(source, revision)) return;
                if (_syncValuesButton is not null) _syncValuesButton.IsEnabled = enabled;
                if (!IsCurrent(source, revision)) return;
                if (_syncDirectionsLabel is not null)
                    _syncDirectionsLabel.Text = enabled
                        ? $"Link directions: {OnOff(key!.SyncInOutTangentDirections)}"
                        : "Link directions: —";
                if (!IsCurrent(source, revision)) return;
                if (_syncDirectionsButton is not null)
                    _syncDirectionsButton.IsEnabled = enabled;
                if (!IsCurrent(source, revision)) return;
                if (_syncMagnitudesLabel is not null)
                    _syncMagnitudesLabel.Text = enabled
                        ? $"Link magnitudes: {OnOff(key!.SyncInOutTangentMagnitudes)}"
                        : "Link magnitudes: —";
                if (!IsCurrent(source, revision)) return;
                if (_syncMagnitudesButton is not null)
                    _syncMagnitudesButton.IsEnabled = enabled;
                if (!IsCurrent(source, revision)) return;
                bool incomingHermite = enabled &&
                    key!.InterpolationTypeIn == EVectorInterpType.Hermite;
                bool outgoingHermite = enabled &&
                    key!.InterpolationTypeOut == EVectorInterpType.Hermite;
                bool incomingWeighted = incomingHermite &&
                    (key!.WeightedMode & EKeyframeWeightedMode.In) != 0;
                bool outgoingWeighted = outgoingHermite &&
                    (key!.WeightedMode & EKeyframeWeightedMode.Out) != 0;
                if (_inWeightedLabel is not null)
                    _inWeightedLabel.Text = incomingHermite
                        ? $"Weighted in: {OnOff(incomingWeighted)}"
                        : enabled ? "In: Hermite only" : "Weighted in: —";
                if (!IsCurrent(source, revision)) return;
                if (_inWeightedButton is not null)
                    _inWeightedButton.IsEnabled = incomingHermite;
                if (!IsCurrent(source, revision)) return;
                if (_outWeightedLabel is not null)
                    _outWeightedLabel.Text = outgoingHermite
                        ? $"Weighted out: {OnOff(outgoingWeighted)}"
                        : enabled ? "Out: Hermite only" : "Weighted out: —";
                if (!IsCurrent(source, revision)) return;
                if (_outWeightedButton is not null)
                    _outWeightedButton.IsEnabled = outgoingHermite;
                if (!IsCurrent(source, revision)) return;
                if (_inWeight is not null)
                    _inWeight.Text = incomingWeighted
                        ? key!.InWeight.ToString("G9", CultureInfo.InvariantCulture) : "";
                if (!IsCurrent(source, revision)) return;
                if (_inWeight is not null) _inWeight.IsEnabled = incomingWeighted;
                if (!IsCurrent(source, revision)) return;
                if (_outWeight is not null)
                    _outWeight.Text = outgoingWeighted
                        ? key!.OutWeight.ToString("G9", CultureInfo.InvariantCulture) : "";
                if (!IsCurrent(source, revision)) return;
                if (_outWeight is not null) _outWeight.IsEnabled = outgoingWeighted;
            }

            private static string OnOff(bool value) => value ? "on" : "off";

            private bool IsPresentedKey(InspectorFloatCurveSource source,
                FloatKeyframe key)
                => ReferenceEquals(key.OwningTrack, source.Track) &&
                    key.TryCaptureAuthoredState(out _) &&
                    float.IsFinite(key.InValue) && float.IsFinite(key.OutValue) &&
                    float.IsFinite(key.InTangent) && float.IsFinite(key.OutTangent) &&
                    _ids.TryGetValue(key, out ulong id) && _keys.TryGetValue(id,
                        out FloatKeyframe? current) && ReferenceEquals(current, key);

            private bool CanEdit(out InspectorFloatCurveSource source)
            {
                source = _source!;
                return !_retired && _rootIdentity.IsCurrent &&
                    !_root.IsDestroyed && !_root.IsDestroyQueued &&
                    _root.IsActiveInHierarchy &&
                    source is not null && source.IsCurrent();
            }

            private bool CanEditLengthSelection(
                out InspectorFloatCurveLengthSelection selection)
            {
                selection = _lengthSelection!;
                return !_retired && _rootIdentity.IsCurrent &&
                    !_root.IsDestroyed && !_root.IsDestroyQueued &&
                    _root.IsActiveInHierarchy &&
                    selection is not null &&
                    ReferenceEquals(selection, _presentedLengthSelection) &&
                    _presentedLengthRevision == _revision && selection.IsCurrent();
            }

            private bool CanAddToLengthSelection(
                out InspectorFloatCurveLengthSelection selection)
            {
                selection = _lengthSelection!;
                return !_retired && _rootIdentity.IsCurrent &&
                    !_root.IsDestroyed && !_root.IsDestroyQueued &&
                    _root.IsActiveInHierarchy && selection is not null &&
                    InspectorFloatCurveAddEdit.CanAddWithinLimits(selection) &&
                    ReferenceEquals(selection, _presentedAddSelection) &&
                    _presentedAddRevision == _revision && selection.IsCurrent();
            }

            private bool CanEditLengthSource(out InspectorFloatCurveSource source)
            {
                if (!CanEdit(out source)) return false;
                return ReferenceEquals(source, _presentedLengthSource) &&
                    _presentedLengthRevision == _revision;
            }

            private static bool Parse(UITextInputComponent? input, out float value)
                => float.TryParse(input?.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

            private void SubmitTime(UITextInputComponent input)
            {
                if (CanAddToLengthSelection(out _))
                {
                    AddKey();
                    return;
                }
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

            private void SubmitIncomingValue(UITextInputComponent input)
            {
                if (CanEdit(out var source) && _selected is { } key &&
                    IsPresentedKey(source, key) && Parse(input, out float value))
                    InspectorFloatCurveActions.SetIncomingValue(source, key, value);
                RequestRefresh();
            }

            private void SubmitIncomingTangent(UITextInputComponent input)
                => SubmitTangent(input, outgoing: false);

            private void SubmitOutgoingTangent(UITextInputComponent input)
                => SubmitTangent(input, outgoing: true);

            private void SubmitTangent(UITextInputComponent input, bool outgoing)
            {
                if (CanEdit(out var source) && _selected is { } key &&
                    IsPresentedKey(source, key) && Parse(input, out float value))
                    InspectorFloatCurveActions.SetTangent(source, key, outgoing, value);
                RequestRefresh();
            }

            private void ToggleSync(int part)
            {
                if (CanEdit(out var source) && _selected is { } key &&
                    IsPresentedKey(source, key))
                {
                    switch (part)
                    {
                        case 0:
                            InspectorFloatCurveActions.SetSyncValues(source, key,
                                !key.SyncInOutValues);
                            break;
                        case 1:
                            InspectorFloatCurveActions.SetSyncDirections(source, key,
                                !key.SyncInOutTangentDirections);
                            break;
                        case 2:
                            InspectorFloatCurveActions.SetSyncMagnitudes(source, key,
                                !key.SyncInOutTangentMagnitudes);
                            break;
                    }
                }
                RequestRefresh();
            }

            private void SubmitIncomingWeight(UITextInputComponent input)
                => SubmitWeight(input, outgoing: false);

            private void SubmitOutgoingWeight(UITextInputComponent input)
                => SubmitWeight(input, outgoing: true);

            private void SubmitWeight(UITextInputComponent input, bool outgoing)
            {
                if (CanEdit(out var source) && _selected is { } key &&
                    IsPresentedKey(source, key) && Parse(input, out float weight) &&
                    weight is >= 0 and <= 1)
                    InspectorFloatCurveActions.SetWeight(source, key, outgoing, weight);
                RequestRefresh();
            }

            private void ToggleWeighted(bool outgoing)
            {
                if (CanEdit(out var source) && _selected is { } key &&
                    IsPresentedKey(source, key))
                {
                    EKeyframeWeightedMode side = outgoing
                        ? EKeyframeWeightedMode.Out : EKeyframeWeightedMode.In;
                    InspectorFloatCurveActions.SetWeightedSide(source, key,
                        outgoing, (key.WeightedMode & side) == 0);
                }
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
                if (Parse(_length, out float value))
                {
                    if (CanEditLengthSource(out var source))
                        InspectorFloatCurveActions.SetLength(source, value, stretch);
                    else if (CanEditLengthSelection(out var selection))
                        InspectorFloatCurveActions.SetLength(selection, value, stretch);
                }
                RequestRefresh();
            }

            private void AddKey()
            {
                if (CanEdit(out var source) && Parse(_time, out float second) &&
                    InspectorFloatCurveActions.Add(source, second, out FloatKeyframe? key))
                    _selected = key;
                else if (CanAddToLengthSelection(out var selection) &&
                    Parse(_time, out float groupedSecond))
                    InspectorFloatCurveActions.Add(selection, groupedSecond);
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
                foreach (XRBase owner in _outerOwners)
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
                if (_inValue is not null) _inValue.Submitted -= SubmitIncomingValue;
                if (_inTangent is not null) _inTangent.Submitted -= SubmitIncomingTangent;
                if (_outTangent is not null) _outTangent.Submitted -= SubmitOutgoingTangent;
                if (_inWeight is not null) _inWeight.Submitted -= SubmitIncomingWeight;
                if (_outWeight is not null) _outWeight.Submitted -= SubmitOutgoingWeight;
                if (_length is not null) _length.Submitted -= SubmitLength;
                _rows.Replace([]);
                _keys.Clear();
                _ids.Clear();
            }
        }
    }
}
