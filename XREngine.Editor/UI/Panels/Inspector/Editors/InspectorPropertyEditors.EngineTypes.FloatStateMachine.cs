using System.Numerics;
using System.Reflection;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public static partial class EngineTypes
    {
        internal static void CreateStateMachineLayerDetail(SceneNode node,
            PropertyInfo property, object?[]? targets)
        {
            ObjectConstructionIdentity rootIdentity = ObjectConstructionIdentity.Capture(node);
            EnsureVerticalLayout(node);
            if (!rootIdentity.IsCurrent || node.IsDestroyed || node.IsDestroyQueued) return;
            var context = new FloatTransitionNavigator(node, property, targets);
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

        private sealed class FloatTransitionNavigator
        {
            private const int MaximumRows = 8192;
            private const int Layers = 0;
            private const int States = 1;
            private const int Transitions = 2;
            private readonly SceneNode _root;
            private readonly ObjectConstructionIdentity _rootIdentity;
            private readonly PropertyInfo _property;
            private readonly object?[]? _targets;
            private readonly NativeTableRows<(int Index, object Item, bool AnyState,
                string Label)> _rows = new();
            private readonly Dictionary<ulong, (int Index, object Item,
                bool AnyState, string Label)> _rowItems = [];
            private readonly Dictionary<object, List<ulong>> _stableIds =
                new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<object, int> _occurrences =
                new(ReferenceEqualityComparer.Instance);
            private object? _idSource;
            private int _idStage = -1;
            private SceneNode? _curveParent;
            private SceneNode? _curveHost;
            private UITextComponent? _status;
            private UIListTransform? _list;
            private UIVirtualTableProvider<(int Index, object Item, bool AnyState,
                string Label)>? _table;
            private AnimStateMachine? _machine;
            private XRAsset? _document;
            private Guid _machineIdentity;
            private Guid _documentIdentity;
            private EventList<AnimLayer>? _layers;
            private ObjectConstructionIdentity? _layersIdentity;
            private AnimLayer? _layer;
            private int _layerIndex = -1;
            private EventList<AnimState>? _states;
            private ObjectConstructionIdentity? _statesIdentity;
            private AnimStateBase? _state;
            private int _stateIndex = -1;
            private bool _anyState;
            private EventList<AnimStateTransition>? _transitions;
            private ObjectConstructionIdentity? _transitionsIdentity;
            private InspectorFloatTransitionOwnership? _curvePath;
            private int _stage;
            private ulong _nextRowId;
            private ulong _revision;
            private bool _dirty = true;
            private bool _refreshing;
            private bool _retired;

            public FloatTransitionNavigator(SceneNode root, PropertyInfo property,
                object?[]? targets)
            {
                _root = root;
                _rootIdentity = ObjectConstructionIdentity.Capture(root);
                _property = property;
                _targets = targets?.ToArray();
            }

            public void Build()
            {
                if (!ContinueBuild()) return;
                SceneNode card = CreateInspectorCard(_root);
                if (!ContinueBuild()) return;
                AddSectionHeader(card, "State-machine curves");
                if (!ContinueBuild()) return;
                _status = AddInfoLabel(card, "Select a layer.");
                if (!ContinueBuild()) return;
                CreateInspectorButton(card, "Back", Back);
                if (!ContinueBuild()) return;
                SceneNode scrollNode = card.NewChild();
                if (!ContinueBuild()) return;
                UIScrollableTransform scroll = scrollNode.SetTransform<UIScrollableTransform>();
                if (!ContinueBuild()) return;
                scroll.Height = 220;
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
                _table = new(_rows,
                    UIVirtualTableColumn<(int Index, object Item, bool AnyState,
                        string Label)>.Text("name", "Name", 300,
                        item => item.Label));
                _table.AccessibleRowName = item => item.Label;
                _table.Activated += SelectRow;
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
                _curveParent = card.NewChild();
                if (!ContinueBuild()) return;
                EnsureVerticalLayout(_curveParent);
                if (!ContinueBuild()) return;
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

            public void RequestRefresh()
            {
                _dirty = true;
                _revision++;
            }

            private bool MachineCurrent()
                => _machine is { IsDestroyed: false, IsDestroyQueued: false } machine &&
                    _document is { IsDestroyed: false, IsDestroyQueued: false } document &&
                    machine.ID == _machineIdentity &&
                    document.ID == _documentIdentity &&
                    ReferenceEquals(machine.SourceAsset, document) &&
                    ReferenceEquals(machine.Layers, _layers) &&
                    _layers is { IsDestroyed: false, IsDestroyQueued: false } &&
                    _layersIdentity?.IsCurrent == true;

            private bool CanContinue(ulong revision)
                => !_retired && _revision == revision &&
                    !_root.IsDestroyed && !_root.IsDestroyQueued &&
                    _root.IsActiveInHierarchy;

            private bool LayerCurrent()
                => MachineCurrent() && _layer is not null &&
                    _layers is { } layers &&
                    (uint)_layerIndex < (uint)layers.Count &&
                    ReferenceEquals(layers[_layerIndex], _layer) &&
                    ReferenceEquals(_layer.States, _states) &&
                    _statesIdentity?.IsCurrent == true;

            private bool StateCurrent()
                => LayerCurrent() && _state is not null &&
                    (_anyState
                        ? ReferenceEquals(_layer!.AnyState, _state)
                        : _states is { IsDestroyed: false, IsDestroyQueued: false } states &&
                            (uint)_stateIndex < (uint)states.Count &&
                            ReferenceEquals(states[_stateIndex], _state)) &&
                    ReferenceEquals(_state.Transitions, _transitions) &&
                    _transitionsIdentity?.IsCurrent == true;

            public void RefreshIfDirty()
            {
                if (_retired || _refreshing || _root.IsDestroyed ||
                    _root.IsDestroyQueued)
                    return;
                _refreshing = true;
                try
                {
                    if (_machine is not null && !MachineCurrent() ||
                        _stage >= States && !LayerCurrent() ||
                        _stage >= Transitions && !StateCurrent() ||
                        _curvePath is { } path && !path.IsCurrentFast())
                        RequestRefresh();
                    if (!_dirty) return;
                    _dirty = false;
                    if (!MachineCurrent()) BindMachine();
                    if (_retired || _dirty) return;
                    if (_stage >= States && !LayerCurrent() && !ClearLayer()) return;
                    if (_stage >= Transitions && !StateCurrent() && !ClearState()) return;
                    if (_curvePath is { } selected && !selected.IsCurrentFast())
                        RetireCurve();
                    if (_retired || _dirty) return;
                    SyncSubscriptions();
                    RefreshRows();
                }
                finally { _refreshing = false; }
            }

            private void BindMachine()
            {
                ulong revision = _revision;
                Unsubscribe();
                if (!ClearLayer() || !CanContinue(revision)) return;
                _machine = null;
                _document = null;
                _layers = null;
                _layersIdentity = null;
                if (_targets is not { Length: > 0 } targets ||
                    _property.DeclaringType != typeof(AnimStateMachine) ||
                    _property.Name != nameof(AnimStateMachine.Layers) ||
                    targets[0] is not AnimStateMachine machine ||
                    targets.Any(target => !ReferenceEquals(target, machine)) ||
                    machine.IsDestroyed || machine.IsDestroyQueued ||
                    machine.Layers is not
                        { IsDestroyed: false, IsDestroyQueued: false } layers)
                {
                    if (_status is not null)
                        _status.Text = "Select one state-machine asset.";
                    return;
                }
                _machine = machine;
                _document = machine.SourceAsset;
                _machineIdentity = machine.ID;
                _documentIdentity = _document.ID;
                _layers = layers;
                _layersIdentity = ObjectConstructionIdentity.Capture(layers);
                machine.PropertyChanged += MachineChanged;
            }

            private void SyncSubscriptions()
            {
                if (_observedLayers is not null)
                    _observedLayers.PostModified -= ActiveListChanged;
                if (_observedStates is not null)
                    _observedStates.PostModified -= ActiveListChanged;
                if (_observedTransitions is not null)
                    _observedTransitions.PostModified -= ActiveListChanged;
                _observedLayers = _layers;
                _observedStates = _stage >= States ? _states : null;
                _observedTransitions = _stage >= Transitions ? _transitions : null;
                if (_observedLayers is not null)
                    _observedLayers.PostModified += ActiveListChanged;
                if (_observedStates is not null)
                    _observedStates.PostModified += ActiveListChanged;
                if (_observedTransitions is not null)
                    _observedTransitions.PostModified += ActiveListChanged;
            }

            private EventList<AnimLayer>? _observedLayers;
            private EventList<AnimState>? _observedStates;
            private EventList<AnimStateTransition>? _observedTransitions;

            private void Unsubscribe()
            {
                if (_machine is not null)
                    _machine.PropertyChanged -= MachineChanged;
                if (_observedLayers is not null)
                    _observedLayers.PostModified -= ActiveListChanged;
                if (_observedStates is not null)
                    _observedStates.PostModified -= ActiveListChanged;
                if (_observedTransitions is not null)
                    _observedTransitions.PostModified -= ActiveListChanged;
                _observedLayers = null;
                _observedStates = null;
                _observedTransitions = null;
            }

            private void MachineChanged(object? sender,
                IXRPropertyChangedEventArgs change)
            {
                if (change.PropertyName == nameof(AnimStateMachine.Layers))
                    RequestRefresh();
            }

            private void ActiveListChanged() => RequestRefresh();

            private void RefreshRows()
            {
                ulong revision = _revision;
                List<(ulong Id, (int Index, object Item, bool AnyState,
                    string Label) Item)> items = [];
                _rowItems.Clear();
                object? currentSource = _stage == Layers ? _layers :
                    _stage == States ? _states : _transitions;
                if (!ReferenceEquals(_idSource, currentSource) || _idStage != _stage)
                {
                    _idSource = currentSource;
                    _idStage = _stage;
                    _stableIds.Clear();
                }
                _occurrences.Clear();
                if (MachineCurrent())
                {
                    if (_stage == Layers && _layers is { } layers)
                        for (int index = 0; index < Math.Min(layers.Count, MaximumRows); index++)
                        {
                            if (!MachineCurrent()) { RequestRefresh(); return; }
                            if (layers[index] is { } layer)
                                AddRow(items, index, layer, false, $"Layer {index}");
                        }
                    else if (_stage == States && LayerCurrent() && _states is { } states)
                    {
                        AddRow(items, -1, _layer!.AnyState, true, "Any State");
                        for (int index = 0; index < Math.Min(states.Count, MaximumRows - 1); index++)
                        {
                            if (!LayerCurrent()) { RequestRefresh(); return; }
                            if (states[index] is { } state)
                                AddRow(items, index, state, false,
                                    $"[{index}] {state.Name}");
                        }
                    }
                    else if (_stage == Transitions && StateCurrent() &&
                        _transitions is { } transitions)
                        for (int index = 0; index < Math.Min(transitions.Count, MaximumRows); index++)
                        {
                            if (!StateCurrent()) { RequestRefresh(); return; }
                            if (transitions[index] is { } transition)
                                AddRow(items, index, transition, false,
                                    $"[{index}] {transition.Name}");
                        }
                }
                foreach (object stale in _stableIds.Keys.Where(item =>
                    !_occurrences.ContainsKey(item)).ToArray())
                    _stableIds.Remove(stale);
                _rows.Replace(items);
                if (_retired || _revision != revision) return;
                if (_status is not null)
                {
                    int total = _stage == Layers ? _layers?.Count ?? 0 :
                        _stage == States ? (_states?.Count ?? 0) + 1 :
                        _transitions?.Count ?? 0;
                    _status.Text = _machine is null ? "Select one state-machine asset."
                        : total > MaximumRows
                            ? $"Showing first {MaximumRows} of {total} entries."
                        : _stage == Layers ? "Select a layer."
                        : _stage == States ? "Select a state."
                        : _curvePath is null ? "Select a transition."
                        : "Edit the selected transition curve.";
                }
                if (_retired || _revision != revision) return;
                _list?.InvalidateMeasure();
            }

            private void AddRow(List<(ulong Id, (int Index, object Item,
                bool AnyState, string Label) Item)> items, int index,
                object item, bool anyState, string label)
            {
                int occurrence = _occurrences.TryGetValue(item, out int count)
                    ? count : 0;
                _occurrences[item] = occurrence + 1;
                if (!_stableIds.TryGetValue(item, out List<ulong>? ids))
                    _stableIds.Add(item, ids = []);
                if (occurrence == ids.Count)
                    ids.Add(++_nextRowId);
                ulong id = ids[occurrence];
                var row = (index, item, anyState, label);
                items.Add((id, row));
                _rowItems.Add(id, row);
            }

            private void SelectRow(ulong id)
            {
                if (_retired || !_rowItems.TryGetValue(id, out var row) ||
                    !_root.IsActiveInHierarchy || !MachineCurrent())
                    return;
                ulong revision = _revision;
                if (_stage == Layers && row.Item is AnimLayer layer &&
                    _layers is { } layers && (uint)row.Index < (uint)layers.Count &&
                    ReferenceEquals(layers[row.Index], layer))
                {
                    if (!ClearLayer() || !CanContinue(revision)) return;
                    _layer = layer;
                    _layerIndex = row.Index;
                    _states = layer.States;
                    _statesIdentity = _states is null ? null :
                        ObjectConstructionIdentity.Capture(_states);
                    _stage = States;
                }
                else if (_stage == States && row.Item is AnimStateBase state &&
                    LayerCurrent() && (row.AnyState
                        ? ReferenceEquals(_layer!.AnyState, state)
                        : _states is { } states &&
                            (uint)row.Index < (uint)states.Count &&
                            ReferenceEquals(states[row.Index], state)))
                {
                    if (!ClearState() || !CanContinue(revision)) return;
                    _state = state;
                    _stateIndex = row.Index;
                    _anyState = row.AnyState;
                    _transitions = state.Transitions;
                    _transitionsIdentity = _transitions is null ? null :
                        ObjectConstructionIdentity.Capture(_transitions);
                    _stage = Transitions;
                }
                else if (_stage == Transitions && row.Item is AnimStateTransition transition &&
                    StateCurrent() &&
                    InspectorFloatTransitionOwnership.TryCapture(_machine!,
                        _layerIndex, _stateIndex, row.Index, _anyState,
                        out var path, out _))
                {
                    RetireCurve();
                    if (!CanContinue(revision)) return;
                    if (!ReferenceEquals(path!.Transition, transition)) return;
                    _curvePath = path;
                    void AbandonPath()
                    {
                        if (!ReferenceEquals(_curvePath, path)) return;
                        _curvePath = null;
                        RequestRefresh();
                    }
                    if (_curveParent is not { } curveParent ||
                        curveParent.Parent is not { } card ||
                        !ReferenceEquals(card.Parent, _root))
                    {
                        AbandonPath();
                        return;
                    }
                    ObjectConstructionIdentity parentIdentity =
                        ObjectConstructionIdentity.Capture(curveParent);
                    ObjectConstructionIdentity parentTransformIdentity =
                        ObjectConstructionIdentity.Capture(curveParent.Transform);
                    ObjectConstructionIdentity cardIdentity =
                        ObjectConstructionIdentity.Capture(card);
                    ObjectConstructionIdentity rootIdentity =
                        ObjectConstructionIdentity.Capture(_root);
                    bool ParentCurrent() => parentIdentity.IsCurrent &&
                        parentTransformIdentity.IsCurrent && cardIdentity.IsCurrent &&
                        rootIdentity.IsCurrent &&
                        ReferenceEquals(_curveParent, curveParent) &&
                        ReferenceEquals(curveParent.Parent, card) &&
                        ReferenceEquals(curveParent.Transform.Parent, card.Transform) &&
                        ReferenceEquals(card.Parent, _root) &&
                        ReferenceEquals(card.Transform.Parent, _root.Transform);
                    if (!ParentCurrent())
                    {
                        AbandonPath();
                        return;
                    }
                    SceneNode host = curveParent.NewChild();
                    if (!CanContinue(revision) || !ParentCurrent() ||
                        !path.IsCurrentFast() ||
                        !ReferenceEquals(_curvePath, path) ||
                        _curveHost is not null ||
                        !ReferenceEquals(host.Parent, curveParent) ||
                        !ReferenceEquals(host.Transform.Parent,
                            curveParent.Transform))
                    {
                        if (ParentCurrent() && !host.IsDestroyed &&
                            !host.IsDestroyQueued &&
                            ReferenceEquals(host.Parent, curveParent) &&
                            ReferenceEquals(host.Transform.Parent,
                                curveParent.Transform))
                            host.Destroy(false);
                        AbandonPath();
                        return;
                    }
                    _curveHost = host;
                    CreateFloatAnimationDetail(host,
                        typeof(AnimStateTransition).GetProperty(
                            nameof(AnimStateTransition.CustomBlendFunction))!,
                        [transition], path);
                }
                RequestRefresh();
            }

            private void Back()
            {
                if (_retired || !_root.IsActiveInHierarchy) return;
                if (_curvePath is not null) RetireCurve();
                else if (_stage == Transitions && !ClearState()) return;
                else if (_stage == States && !ClearLayer()) return;
                RequestRefresh();
            }

            private bool ClearLayer()
            {
                if (!ClearState()) return false;
                _layer = null;
                _layerIndex = -1;
                _states = null;
                _statesIdentity = null;
                _stage = Layers;
                return true;
            }

            private bool ClearState()
            {
                ulong revision = _revision;
                RetireCurve();
                if (!CanContinue(revision)) return false;
                _state = null;
                _stateIndex = -1;
                _anyState = false;
                _transitions = null;
                _transitionsIdentity = null;
                if (_stage >= Transitions) _stage = States;
                return true;
            }

            private void RetireCurve()
            {
                _curvePath = null;
                if (_curveHost is not { } host) return;
                _curveHost = null;
                if (_curveParent is not { } parent ||
                    parent.Parent is not { } card ||
                    !ReferenceEquals(card.Parent, _root) ||
                    host.Transform is not { } transform)
                    return;
                ObjectConstructionIdentity hostIdentity =
                    ObjectConstructionIdentity.Capture(host);
                ObjectConstructionIdentity transformIdentity =
                    ObjectConstructionIdentity.Capture(transform);
                ObjectConstructionIdentity parentIdentity =
                    ObjectConstructionIdentity.Capture(parent);
                ObjectConstructionIdentity parentTransformIdentity =
                    ObjectConstructionIdentity.Capture(parent.Transform);
                ObjectConstructionIdentity cardIdentity =
                    ObjectConstructionIdentity.Capture(card);
                ObjectConstructionIdentity cardTransformIdentity =
                    ObjectConstructionIdentity.Capture(card.Transform);
                ObjectConstructionIdentity rootIdentity =
                    ObjectConstructionIdentity.Capture(_root);
                bool Owned() => hostIdentity.IsCurrent && transformIdentity.IsCurrent &&
                    parentIdentity.IsCurrent && parentTransformIdentity.IsCurrent &&
                    cardIdentity.IsCurrent && cardTransformIdentity.IsCurrent &&
                    rootIdentity.IsCurrent && !host.IsDestroyed &&
                    !host.IsDestroyQueued && !transform.IsDestroyed &&
                    !transform.IsDestroyQueued && !parent.IsDestroyed &&
                    !parent.IsDestroyQueued &&
                    ReferenceEquals(_curveParent, parent) &&
                    ReferenceEquals(parent.Parent, card) &&
                    ReferenceEquals(parent.Transform.Parent, card.Transform) &&
                    ReferenceEquals(card.Parent, _root) &&
                    ReferenceEquals(card.Transform.Parent, _root.Transform) &&
                    ReferenceEquals(host.Transform, transform) &&
                    ReferenceEquals(host.Parent, parent) &&
                    ReferenceEquals(transform.Parent, parent.Transform);
                if (!Owned()) return;
                host.GetComponent<InspectorListDetailRefreshComponent>()?.Retire();
                if (!Owned()) return;
                host.IsActiveSelf = false;
                if (!Owned()) return;
                host.Destroy(false);
            }

            public void Retire()
            {
                if (_retired) return;
                _retired = true;
                RetireCurve();
                Unsubscribe();
                if (_root.GetComponent<InspectorFloatCurveTickComponent>() is { } tick)
                    tick.Refresh = null;
                _list?.DetachVirtualProviderForOwnerTeardown();
                if (_table is not null) _table.Activated -= SelectRow;
                _rows.Replace([]);
                _rowItems.Clear();
                _stableIds.Clear();
                _occurrences.Clear();
            }
        }
    }
}
