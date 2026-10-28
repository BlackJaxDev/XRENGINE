using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using XREngine;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public static partial class CollectionTypes
    {
        private const float RowHeaderWidth = 150.0f;
        private const float ButtonHeight = 24.0f;
        private const float ButtonWidth = 80.0f;

        public static bool TryCreateEditor(Type propType, out Action<SceneNode, PropertyInfo, object?[]?>? editor)
            => TryCreateEditorCore(propType, null, out editor);

        private static bool TryCreateEditorCore(Type propType, Func<bool>? ownerIsCurrent,
            out Action<SceneNode, PropertyInfo, object?[]?>? editor,
            Action<RuntimeComponentCreationReceipt>? retainFailedReceipt = null)
        {
            if (!IsSupportedCollectionType(propType))
            {
                editor = null;
                return false;
            }

            editor = (node, prop, objects) =>
            {
                if (ownerIsCurrent?.Invoke() == false) return;
                if (prop.DeclaringType == typeof(AnimStateMachine) &&
                    prop.Name == nameof(AnimStateMachine.Layers) &&
                    prop.PropertyType == typeof(EventList<AnimLayer>))
                {
                    EngineTypes.CreateStateMachineLayerDetail(node, prop, objects);
                    return;
                }
                if (ownerIsCurrent?.Invoke() == false) return;
                if (objects?.Any(target => target is IEditorListElementBinding or
                    IEditorLinkedElementBinding or IEditorDictionaryElementBinding) == true &&
                    !CollectionEditorContext.CanCreateNestedList(propType, objects))
                {
                    CollectionEditorContext.AddNestedAttemptLabel(node,
                        "Nested editing supports direct fixed-height BCL lists with scalar values.",
                        () => ownerIsCurrent?.Invoke() != false);
                    return;
                }
                if (ownerIsCurrent?.Invoke() == false) return;
                if (!EnsureVerticalLayout(node, ownerIsCurrent)) return;
                if (ownerIsCurrent?.Invoke() == false) return;
                ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(node);
                TransformBase nodeTransform = node.Transform;
                ObjectConstructionIdentity nodeTransformIdentity = ObjectConstructionIdentity.Capture(nodeTransform);
                var container = node.NewChild();
                ObjectConstructionIdentity containerIdentity = ObjectConstructionIdentity.Capture(container);
                if (ownerIsCurrent?.Invoke() == false || !nodeIdentity.IsCurrent ||
                    !nodeTransformIdentity.IsCurrent || node.IsDestroyed || node.IsDestroyQueued ||
                    container.IsDestroyed || container.IsDestroyQueued ||
                    !ReferenceEquals(node.Transform, nodeTransform) ||
                    !ReferenceEquals(container.Transform.Parent, nodeTransform))
                    return;
                bool ContainerCreationCurrent() => ownerIsCurrent?.Invoke() != false &&
                    nodeIdentity.IsCurrent && nodeTransformIdentity.IsCurrent &&
                    containerIdentity.IsCurrent && !node.IsDestroyed && !node.IsDestroyQueued &&
                    !container.IsDestroyed && !container.IsDestroyQueued &&
                    ReferenceEquals(node.Transform, nodeTransform) &&
                    ReferenceEquals(container.Transform.Parent, nodeTransform);
                if (!EnsureVerticalLayout(container, ContainerCreationCurrent) ||
                    !ContainerCreationCurrent()) return;
                TransformBase containerTransform = container.Transform;
                ObjectConstructionIdentity containerTransformIdentity =
                    ObjectConstructionIdentity.Capture(containerTransform);
                bool CoreCurrent() => ContainerCreationCurrent() &&
                    containerTransformIdentity.IsCurrent &&
                    ReferenceEquals(container.Transform, containerTransform);
                var context = new CollectionEditorContext(prop, objects, propType, container,
                    ownerIsCurrent is null ? null : CoreCurrent);
                if (!CoreCurrent())
                {
                    context.Retire();
                    return;
                }
                InspectorListDetailRefreshComponent? lifetime;
                RuntimeComponentCreationReceipt? lifetimeReceipt = null;
                void RetainFailedLifetime(Exception error)
                {
                    if (lifetimeReceipt is not { } receipt) return;
                    receipt.RecordFailure(error);
                    if (receipt.Component is { IsDestroyed: false, IsDestroyQueued: false } &&
                        !receipt.TryAbortUnchangedComponent())
                    {
                        context.RetainFailedComponentCreation(receipt);
                        retainFailedReceipt?.Invoke(receipt);
                    }
                }
                try
                {
                    RuntimeComponentCreationReceipt receipt = new(node, CoreCurrent);
                    lifetimeReceipt = receipt;
                    lifetime = node.AddComponentGuarded(
                        static () => new InspectorListDetailRefreshComponent(), receipt);
                }
                catch (Exception error)
                {
                    RetainFailedLifetime(error);
                    context.Retire();
                    Debug.LogException(error, "Could not attach a guarded collection detail lifetime.");
                    return;
                }
                if (lifetime is null || lifetime.IsRetired || lifetime.IsDestroyed || lifetime.IsDestroyQueued ||
                    node.IsDestroyed || node.IsDestroyQueued || !CoreCurrent() ||
                    !ReferenceEquals(lifetime.SceneNode, node))
                {
                    RetainFailedLifetime(new InvalidOperationException(
                        "The guarded collection detail lifetime could not retain its owner."));
                    context.Retire();
                    return;
                }
                if (!CoreCurrent())
                {
                    context.Retire();
                    return;
                }
                lifetime.Refresh = context.RefreshList;
                lifetime.Release = context.Retire;
                if (!CoreCurrent())
                {
                    lifetime.Refresh = null;
                    lifetime.Release = null;
                    context.Retire();
                    return;
                }
                context.Rebuild();
            };
            return true;
        }

        private static bool IsSupportedCollectionType(Type type)
            => typeof(IDictionary).IsAssignableFrom(type)
            || ImplementsGenericInterface(type, typeof(IDictionary<,>))
            || typeof(IList).IsAssignableFrom(type)
            || ImplementsGenericInterface(type, typeof(IList<>))
            || IsLinkedListType(type)
            || typeof(ICollection).IsAssignableFrom(type)
            || ImplementsGenericInterface(type, typeof(ICollection<>));

        private static bool EnsureVerticalLayout(SceneNode node, Func<bool>? isCurrent = null)
        {
            if (isCurrent?.Invoke() == false) return false;
            UIListTransform layout = isCurrent is not null && node.Transform is UIListTransform existing
                ? existing
                : node.SetTransform<UIListTransform>();
            bool Current() => isCurrent?.Invoke() != false && ReferenceEquals(node.Transform, layout) &&
                !node.IsDestroyed && !node.IsDestroyQueued && !layout.IsDestroyed && !layout.IsDestroyQueued;
            if (!Current()) return false;
            layout.DisplayHorizontal = false;
            if (!Current()) return false;
            layout.ItemSpacing = 6.0f;
            if (!Current()) return false;
            layout.ItemAlignment = EListAlignment.TopOrLeft;
            if (!Current()) return false;
            layout.Padding = new Vector4(0.0f);
            return Current();
        }

        private sealed partial class CollectionEditorContext(PropertyInfo property, object?[]? objects,
            Type declaredType, SceneNode container, Func<bool>? ownerIsCurrent = null)
        {
            private readonly PropertyInfo _property = property;
            private readonly List<object> _owners = CollectOwners(objects);
            private readonly Type _declaredType = declaredType;
            private readonly SceneNode _container = container;
            private readonly Func<bool>? _ownerIsCurrent = ownerIsCurrent;
            private readonly EditorListMutationContext _listContext = new();
            private readonly List<RuntimeComponentCreationReceipt> _failedComponentCreations = [];
            private readonly List<DetachedLeafRetirementReceipt> _failedPaneLeafRetirements = [];
            private EditorListRowSource _listRows => _listContext.Rows;
            private readonly Dictionary<UIBoundableTransform, EditorListRowCellState> _listCells = [];
            private readonly Dictionary<UIBoundableTransform, EditorVirtualCollectionRowCellState> _virtualCells = [];
            private UIListTransform? _virtualList;
            private UITextComponent? _listCountLabel;
            private Type? _listElementType;
            private IList? _boundList;
            private EditorListSourceLease[] _headerLeases = [];
            private ulong _headerGeneration;
            private Func<bool>? _nestedListCurrent;
            private Func<bool>? _nestedCountLabelCurrent;

            public void RetainFailedComponentCreation(RuntimeComponentCreationReceipt receipt)
            {
                if (!_failedComponentCreations.Contains(receipt))
                    _failedComponentCreations.Add(receipt);
            }

            private void RetainFailedPaneLeafRetirement(DetachedLeafRetirementReceipt receipt)
            {
                if (!_failedPaneLeafRetirements.Contains(receipt))
                    _failedPaneLeafRetirements.Add(receipt);
            }

            public void Retire()
            {
                if (!_listContext.IsActive) return;
                _headerGeneration++;
                _headerLeases = [];
                _listContext.Retire();
                RetireNestedListDetail();
                UIListTransform? list = _virtualList;
                bool detached = list is null;
                try
                {
                    bool mayDetach = _ownerIsCurrent is null || _nestedListCurrent?.Invoke() == true;
                    if (list is not null && mayDetach)
                    {
                        IVirtualListProvider? provider = list.VirtualProvider;
                        list.DetachVirtualProviderForOwnerTeardown();
                        detached = provider is null || !ReferenceEquals(list.VirtualProvider, provider);
                    }
                    else if (list is not null)
                        detached = false;
                }
                catch (Exception error)
                {
                    detached = list is not null && list.VirtualProvider is null;
                    Debug.LogException(error, "Could not detach retired list rows.");
                }
                finally
                {
                    _nestedListCurrent = null;
                    _nestedCountLabelCurrent = null;
                    foreach (EditorListRowCellState cell in _listCells.Values)
                    {
                        if (cell.Bindings is { } bindings)
                            foreach (IEditorListElementBinding binding in bindings)
                                try { binding.ReleaseLease(); }
                                catch (Exception error) { Debug.LogException(error, "Could not release a retired list row."); }
                        if (cell.LastBindings is { } cached)
                            foreach (IEditorListElementBinding binding in cached)
                                try { binding.ReleaseLease(); }
                                catch (Exception error) { Debug.LogException(error, "Could not release a cached list row."); }
                    }
                    if (detached)
                    {
                        _listCells.Clear();
                        _virtualCells.Clear();
                        _previewCells.Clear();
                        _virtualList = null;
                    }
                }
            }

            public void Rebuild()
            {
                if (!_listContext.IsActive || !IsOwnerCurrent()) return;
                _headerLeases = [];
                ulong buildGeneration = ++_headerGeneration;
                RetireNestedListDetail();
                if (!IsCurrentBuild(buildGeneration)) return;
                if (_virtualList is not null)
                    _virtualList.VirtualProvider = null;
                if (!IsCurrentBuild(buildGeneration)) return;
                _virtualList = null;
                _listCountLabel = null;
                _nestedListCurrent = null;
                _nestedCountLabelCurrent = null;
                _listCells.Clear();
                _virtualCells.Clear();
                _previewCells.Clear();
                foreach (var child in _container.Transform.Children.ToArray())
                {
                    if (child.SceneNode is not { } old) continue;
                    ObjectConstructionIdentity oldIdentity = ObjectConstructionIdentity.Capture(old);
                    TransformBase oldTransform = old.Transform;
                    ObjectConstructionIdentity oldTransformIdentity = ObjectConstructionIdentity.Capture(oldTransform);
                    if (!ReferenceEquals(oldTransform.Parent, _container.Transform)) continue;
                    old.IsActiveSelf = false;
                    if (!IsCurrentBuild(buildGeneration)) return;
                    if (!oldIdentity.IsCurrent || !oldTransformIdentity.IsCurrent ||
                        !ReferenceEquals(old.Transform, oldTransform) ||
                        !ReferenceEquals(oldTransform.Parent, _container.Transform)) continue;
                    old.Destroy(false);
                    if (!IsCurrentBuild(buildGeneration)) return;
                }

                if (_owners.Count == 0)
                {
                    AddInfoLabel(_container, "No valid targets.");
                    return;
                }

                object? sample = GetSampleCollection();
                if (!IsCurrentBuild(buildGeneration)) return;
                if (sample is null)
                {
                    AddInfoLabel(_container, "Collection is null.");
                    return;
                }

                if (sample is IDictionary dictionary)
                {
                    var (keyType, valueType) = ResolveDictionaryTypes(_declaredType, sample.GetType());
                    RenderDictionary(dictionary, keyType, valueType);
                    return;
                }

                if (IsLinkedListInstance(sample, out var linkedListElementType))
                {
                    RenderLinkedList(sample, linkedListElementType);
                    return;
                }

                if (sample is IList list)
                {
                    var elementType = ResolveElementType(_declaredType, sample.GetType());
                    RenderList(elementType);
                    return;
                }

                if (sample is ICollection collection)
                {
                    RenderGenericCollection(collection);
                    return;
                }

                AddInfoLabel(_container, $"Unsupported collection type '{sample.GetType().Name}'.");
            }

            public bool IsCurrentBuild(ulong generation)
                => _listContext.IsActive && _headerGeneration == generation && IsOwnerCurrent();

            private bool IsOwnerCurrent()
                => _ownerIsCurrent?.Invoke() != false;

            private bool KeepRenderedBranch(SceneNode branch, ulong generation)
            {
                if (_ownerIsCurrent is not null && !_ownerIsCurrent()) return false;
                if (IsCurrentBuild(generation)) return true;
                ObjectConstructionIdentity branchIdentity = ObjectConstructionIdentity.Capture(branch);
                TransformBase branchTransform = branch.Transform;
                ObjectConstructionIdentity transformIdentity = ObjectConstructionIdentity.Capture(branchTransform);
                if (!ReferenceEquals(branchTransform.Parent, _container.Transform)) return false;
                try { branch.IsActiveSelf = false; }
                catch (Exception error) { Debug.LogException(error, "Could not deactivate a retired collection branch."); }
                if (!branchIdentity.IsCurrent || !transformIdentity.IsCurrent ||
                    !ReferenceEquals(branch.Transform, branchTransform) ||
                    !ReferenceEquals(branchTransform.Parent, _container.Transform)) return false;
                try
                {
                    if (!branch.IsDestroyed && !branch.IsDestroyQueued)
                        branch.Destroy(false);
                }
                catch (Exception error) { Debug.LogException(error, "Could not retire a collection branch."); }
                return false;
            }

            private object? GetSampleCollection()
            {
                if (_nestedSources is { Length: > 0 }) return _nestedSources[0].Inner;
                foreach (var owner in _owners)
                {
                    try
                    {
                        var value = _property.GetValue(owner);
                        if (value is not null)
                            return value;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, $"Failed to read property '{_property.Name}'.");
                    }
                }
                return null;
            }

            private void RenderDictionary(IDictionary template, Type keyType, Type valueType)
            {
                ulong generation = _headerGeneration;
                EditorDictionarySourceLease[] sources = CaptureDictionarySources(generation);
                bool canMutate = sources.Length == _owners.Count && sources.Length != 0 &&
                    ReferenceEquals(sources[0].Dictionary, template) &&
                    sources.All(source => source.Adapter.KeyType == keyType &&
                        source.Adapter.ValueType == valueType);
                if (!IsCurrentBuild(generation)) return;
                // Multiline labels and read-only values use measured virtual rows.
                if (canMutate && CanUseVirtualCollectionEditor(valueType))
                {
                    EditorDictionarySnapshot primary = sources[0].Displayed;
                    bool hasPlan = EditorDictionaryRowPlan.TryCreate(
                        sources.Select(source => source.Displayed).ToArray(), out var plan);
                    if (!IsCurrentBuild(generation)) return;
                    if (hasPlan && plan is not null && sources.All(source => source.IsCurrent()))
                    {
                        if (!IsCurrentBuild(generation)) return;
                        RenderVirtualDictionary(keyType, valueType, sources, primary, plan, generation);
                        return;
                    }
                }
                List<object?> keys = template.Keys.Cast<object?>().ToList();
                if (!IsCurrentBuild(generation)) return;
                SceneNode branch = _container.NewChild();
                try
                {
                EnsureVerticalLayout(branch);
                if (!KeepRenderedBranch(branch, generation)) return;
                AddInfoLabel(branch, $"Count: {keys.Count}");
                if (!KeepRenderedBranch(branch, generation)) return;
                var header = branch.NewChild();
                var headerLayout = header.SetTransform<UIListTransform>();
                headerLayout.DisplayHorizontal = true;
                headerLayout.ItemSpacing = 6.0f;

                if (canMutate)
                {
                    CreateInlineButton(header, "Add Entry", () =>
                    {
                        if (!IsCurrentBuild(generation)) return;
                        AddDictionaryEntry(sources, generation);
                    });
                }
                else
                {
                    AddInfoLabel(header, "Dictionary editing needs exact int or ordinal string keys and auto-property owners.");
                }
                if (!KeepRenderedBranch(branch, generation)) return;

                var listNode = branch.NewChild();
                EnsureVerticalLayout(listNode);
                if (!KeepRenderedBranch(branch, generation)) return;
                if (keys.Count == 0)
                {
                    AddInfoLabel(listNode, "<empty>");
                    return;
                }

                foreach (var key in keys)
                {
                    bool rowEditable = canMutate && key is not null &&
                        sources.All(source => source.Adapter.IndexOf(source.Displayed, key) >= 0);
                    object? visibleValue;
                    try { visibleValue = key is null ? null : template[key]; }
                    catch (Exception error)
                    {
                        Debug.LogException(error, "Could not read a dictionary value.");
                        visibleValue = "<unavailable>";
                    }
                    if (!KeepRenderedBranch(branch, generation)) return;
                    BuildDictionaryRow(listNode, key, keyType, valueType, rowEditable,
                        sources, visibleValue);
                    if (!KeepRenderedBranch(branch, generation)) return;
                }
                }
                finally { if (!IsCurrentBuild(generation)) KeepRenderedBranch(branch, generation); }
            }

            private void BuildDictionaryRow(SceneNode parent, object? key, Type keyType,
                Type valueType, bool canMutate, EditorDictionarySourceLease[] sources,
                object? visibleValue)
            {
                ulong generation = _headerGeneration;
                var row = parent.NewChild();
                var splitter = row.SetTransform<UIDualSplitTransform>();
                splitter.VerticalSplit = false;
                splitter.FirstFixedSize = true;
                splitter.FixedSize = RowHeaderWidth;

                var labelNode = row.NewChild();
                var labelList = labelNode.SetTransform<UIListTransform>();
                labelList.DisplayHorizontal = true;
                labelList.ItemSpacing = 4.0f;

                labelNode.NewChild<UITextComponent>(out var label);
                string keyText = key?.ToString() ?? "<null>";
                if (!IsCurrentBuild(generation)) return;
                label.Text = keyText;
                if (!IsCurrentBuild(generation)) return;
                label.FontSize = EditorUI.Styles.PropertyNameFontSize;
                label.HorizontalAlignment = EHorizontalAlignment.Left;
                label.VerticalAlignment = EVerticalAlignment.Center;
                label.Color = EditorUI.Styles.PropertyNameTextColor;
                label.BoundableTransform.Margins = new Vector4(4.0f, 2.0f, 4.0f, 2.0f);

                if (canMutate)
                {
                    CreateInlineButton(labelNode, "Remove", () =>
                    {
                        if (IsCurrentBuild(generation) && key is not null)
                            RemoveDictionaryEntry(sources, key, generation);
                    });
                }

                var valueNode = row.NewChild();
                if (!canMutate)
                {
                    string text = visibleValue?.ToString() ?? "<null>";
                    if (!IsCurrentBuild(generation)) return;
                    AddInfoLabel(valueNode, text);
                    return;
                }
                var bindingType = typeof(DictionaryValueBinding<,>).MakeGenericType(keyType, valueType);
                var bindingProperty = bindingType.GetProperty(nameof(DictionaryValueBinding<object, object>.Value))!;
                var targets = CreateDictionaryBindings(bindingType, key!, generation, sources);
                if (!IsCurrentBuild(generation)) return;
                BuildValueEditor(valueNode, valueType, bindingProperty, targets);
            }

            private object[] CreateDictionaryBindings(Type bindingType, object key,
                ulong generation, EditorDictionarySourceLease[] sources,
                Func<bool>? rowReadIsCurrent = null, Func<bool>? rowWriteIsCurrent = null)
            {
                List<object> bindings = new();
                foreach (EditorDictionarySourceLease source in sources)
                {
                    if (!IsCurrentBuild(generation)) break;
                    int index = source.Adapter.IndexOf(source.Displayed, key);
                    if (index < 0) return [];
                    var instance = Activator.CreateInstance(bindingType, source,
                        source.Displayed.Keys[index], (Action)Rebuild,
                        rowReadIsCurrent, rowWriteIsCurrent);
                    if (!IsCurrentBuild(generation)) break;
                    if (instance is not null)
                        bindings.Add(instance);
                }
                return bindings.ToArray();
            }

            private void RemoveDictionaryEntry(EditorDictionarySourceLease[] sources,
                object key, ulong generation)
            {
                if (!IsCurrentBuild(generation)) return;
                try { EditorDictionaryMutation.Remove(sources, key, Rebuild); }
                catch (Exception error) { Debug.LogException(error, "Could not remove a dictionary entry."); }
            }

            private EditorDictionarySourceLease[] CaptureDictionarySources(ulong generation)
            {
                List<EditorDictionarySourceLease> sources = [];
                foreach (var owner in _owners)
                {
                    if (!IsCurrentBuild(generation)) return [];
                    try
                    {
                        sources.Add(EditorDictionaryMutation.Capture(owner, _property,
                            () => IsCurrentBuild(generation)));
                    }
                    catch (Exception error)
                    {
                        Debug.LogException(error, "Dictionary editing is unavailable for this source.");
                        return [];
                    }
                }
                return [.. sources];
            }

            private void AddDictionaryEntry(EditorDictionarySourceLease[] sources, ulong generation)
            {
                if (!IsCurrentBuild(generation)) return;
                try { EditorDictionaryMutation.Add(sources, Rebuild); }
                catch (Exception error) { Debug.LogException(error, "Could not add a dictionary entry."); }
            }

            private void RenderList(Type elementType)
            {
                ulong buildGeneration = _headerGeneration;
                IList? sampleList = GetListSample();
                if (!IsCurrentBuild(buildGeneration)) return;
                int? sampleCount = sampleList?.Count;
                if (_listRows.Count != sampleCount)
                    _listRows.Reset(sampleCount ?? 0);
                _listElementType = elementType;
                _boundList = sampleList;
                if (sampleCount is int count)
                {
                    UITextComponent? countLabel;
                    if (_ownerIsCurrent is not null)
                    {
                        countLabel = AddNestedAttemptLabel(_container, $"Count: {count}",
                            () => IsCurrentBuild(buildGeneration));
                        if (countLabel is null) return;
                    }
                    else
                    {
                        _container.NewChild<UITextComponent>(out UITextComponent label);
                        label.Text = $"Count: {count}";
                        countLabel = label;
                    }
                    _listCountLabel = countLabel;
                    if (_ownerIsCurrent is not null)
                    {
                        ObjectConstructionIdentity labelIdentity = ObjectConstructionIdentity.Capture(countLabel);
                        SceneNode labelNode = countLabel.SceneNode!;
                        ObjectConstructionIdentity labelNodeIdentity = ObjectConstructionIdentity.Capture(labelNode);
                        TransformBase containerTransform = _container.Transform;
                        ObjectConstructionIdentity containerTransformIdentity =
                            ObjectConstructionIdentity.Capture(containerTransform);
                        _nestedCountLabelCurrent = () => IsCurrentBuild(buildGeneration) &&
                            labelIdentity.IsCurrent && labelNodeIdentity.IsCurrent &&
                            containerTransformIdentity.IsCurrent &&
                            !labelNode.IsDestroyed && !labelNode.IsDestroyQueued &&
                            ReferenceEquals(_container.Transform, containerTransform) &&
                            ReferenceEquals(countLabel.SceneNode, labelNode) &&
                            ReferenceEquals(labelNode.Transform.Parent, containerTransform);
                    }
                    if (!IsCurrentBuild(buildGeneration)) return;
                }

                bool canMutate = HasMutableList();
                if (!IsCurrentBuild(buildGeneration)) return;
                EditorListSourceLease[] leases = canMutate && _nestedSources is null
                    ? _owners.Select(owner => EditorListSourceLease.Capture(owner, _property)).ToArray()
                    : [];
                if (!IsCurrentBuild(buildGeneration)) return;
                _headerLeases = leases;
                SceneNode? header;
                if (_ownerIsCurrent is not null)
                {
                    header = CreateNestedAttemptHeader(_container,
                        () => IsCurrentBuild(buildGeneration));
                    if (header is null) return;
                }
                else
                {
                    header = _container.NewChild();
                    var headerLayout = header.SetTransform<UIListTransform>();
                    headerLayout.DisplayHorizontal = true;
                    headerLayout.ItemSpacing = 6.0f;
                }

                if (canMutate)
                {
                    ulong headerGeneration = _headerGeneration;
                    Action add = () =>
                    {
                        if (_listContext.IsActive && headerGeneration == _headerGeneration)
                            AddListElement(elementType);
                    };
                    if (_ownerIsCurrent is not null)
                    {
                        if (CreateNestedAttemptButton(header, "Add", add,
                            () => IsCurrentBuild(buildGeneration)) is null) return;
                    }
                    else CreateInlineButton(header, "Add", add);
                }
                else
                {
                    string message = _nestedSources is not null &&
                        CanResizeNestedLists(EEditorListMutation.Set)
                            ? "Fixed-size nested list; values can be edited."
                            : "List is read-only.";
                    if (_ownerIsCurrent is not null)
                    {
                        if (AddNestedAttemptLabel(header, message,
                            () => IsCurrentBuild(buildGeneration)) is null) return;
                    }
                    else AddInfoLabel(header, message);
                }
                if (!IsCurrentBuild(buildGeneration)) return;

                if (sampleList is null || sampleList.Count == 0)
                {
                    if (_ownerIsCurrent is not null)
                        AddNestedAttemptLabel(_container, "<empty>",
                            () => IsCurrentBuild(buildGeneration));
                    else AddInfoLabel(_container, "<empty>");
                    return;
                }
                UIListTransform list;
                if (_ownerIsCurrent is not null)
                {
                    UIListTransform? nestedList = CreateNestedVirtualList(buildGeneration);
                    if (nestedList is null) return;
                    list = nestedList;
                }
                else
                {
                    SceneNode scrollNode = _container.NewChild();
                    UIScrollableTransform scroll = scrollNode.SetTransform<UIScrollableTransform>();
                    scroll.MinAnchor = Vector2.Zero;
                    scroll.MaxAnchor = Vector2.One;
                    scroll.Height = 300;
                    scrollNode.AddComponent<UIScrollRegionComponent>();
                    if (!IsCurrentBuild(buildGeneration)) return;
                    SceneNode listNode = scrollNode.NewChild();
                    list = listNode.SetTransform<UIListTransform>();
                    list.MinAnchor = Vector2.Zero;
                    list.MaxAnchor = Vector2.One;
                    list.ItemSize = 28;
                    list.VirtualOverscan = 2;
                    list.ItemAlignment = EListAlignment.TopOrLeft;
                    _virtualList = list;
                    UIVirtualTableProvider<int> provider = new(_listRows,
                        new UIVirtualTableColumn<int>("value", "Element", 300,
                            CreateVirtualListCell, BindVirtualListCell, UnbindVirtualListCell,
                            BindVirtualListCell));
                    // The bound list can change in place, so each read uses the current list.
                    provider.ItemExtent = CreateMeasuredRowExtent(elementType, index =>
                        _boundList is { } bound && index < bound.Count ? bound[index] : null);
                    list.VirtualProvider = provider;
                    if (!IsCurrentBuild(buildGeneration))
                    {
                        if (ReferenceEquals(list.VirtualProvider, provider))
                            list.DetachVirtualProviderForOwnerTeardown();
                        return;
                    }
                }
                _listContext.RefreshRequested = () =>
                {
                    if (!_listContext.IsActive) return;
                    RetireNestedListDetailIfStale();
                    if (_headerLeases.Length != 0)
                    {
                        EditorListSourceLease[] refreshed = _owners.Select(owner =>
                            EditorListSourceLease.Capture(owner, _property)).ToArray();
                        if (!_listContext.IsActive) return;
                        _headerLeases = refreshed;
                    }
                    if (_listCountLabel is { } countLabel)
                    {
                        if (_ownerIsCurrent is null)
                            countLabel.Text = $"Count: {_listRows.Count}";
                        else if (_nestedCountLabelCurrent?.Invoke() == true)
                        {
                            ulong intent = countLabel.TextWriteIntentRevision;
                            Func<bool> current = _nestedCountLabelCurrent!;
                            try
                            {
                                countLabel.SetTextGuarded($"Count: {_listRows.Count}", current,
                                    static require =>
                                    {
                                        if (!require()) throw new InvalidOperationException(
                                            "The nested collection count label is no longer owned.");
                                    });
                            }
                            catch (InvalidOperationException) when (!current() ||
                                countLabel.TextWriteIntentRevision != unchecked(intent + 1)) { }
                        }
                    }
                    if (!_listContext.IsActive) return;
                    if (_virtualList is { } currentList &&
                        (_ownerIsCurrent is null || _nestedListCurrent?.Invoke() == true))
                    {
                        InvalidateMeasuredRowExtents();
                        currentList.InvalidateMeasure();
                    }
                };
            }

            public void RefreshList()
            {
                if (!_listContext.IsActive || !IsOwnerCurrent()) return;
                RetireNestedListDetailIfStale();
                if (_nestedSources is { } nestedPaths && nestedPaths.Any(path => !path.IsInnerCurrent()))
                {
                    if (!nestedPaths.All(path => path.RefreshInnerSnapshot())) return;
                    _listContext.RevisionChanged();
                }
                IList? current = GetListSample();
                if (!ReferenceEquals(current, _boundList))
                {
                    _listContext.RevisionChanged();
                    _listRows.Reset(current?.Count ?? 0);
                    Rebuild();
                    return;
                }
                if (current is null) return;
                bool changed = current.Count != _listRows.Count;
                if (!changed)
                    foreach (EditorListRowCellState cell in _listCells.Values)
                    {
                        if (cell.Index < 0 || cell.Bindings is not { } bindings) continue;
                        if (!AreListBindingsCurrent(bindings, cell.Index, cell.RowId))
                        {
                            changed = true;
                            break;
                        }
                        if (changed) break;
                    }
                if (changed)
                {
                    _listContext.RevisionChanged();
                    _listRows.Reset(current.Count);
                }
                else
                    _listRows.Refresh();
                InvalidateMeasuredRowExtents();
                _virtualList?.InvalidateMeasure();
            }

            private UIBoundableTransform CreateVirtualListCell(SceneNode tableRow)
            {
                if (!_listContext.IsActive || (_ownerIsCurrent is not null && !_ownerIsCurrent()))
                    throw new InvalidOperationException("The list detail view was retired.");
                if (_ownerIsCurrent is not null)
                    return CreateNestedVirtualListCell(tableRow);
                SceneNode cell = new(tableRow);
                UIListTransform layout = cell.SetTransform<UIListTransform>();
                layout.DisplayHorizontal = true;
                layout.ItemAlignment = EListAlignment.TopOrLeft;
                layout.ItemSpacing = 4;
                SceneNode labelNode = cell.NewChild<UITextComponent>(out UITextComponent label);
                labelNode.GetTransformAs<UIBoundableTransform>(true)!.Width = 60;
                SceneNode removeNode = cell.NewChild();
                removeNode.GetTransformAs<UIBoundableTransform>(true)!.Width = 55;
                SceneNode valueNode = cell.NewChild();
                valueNode.SetTransform<UIBoundableTransform>().Width = 180;
                var (_, nestedButton, _, _) = NativeUIElements.CreateButton(
                    valueNode, "Open nested list", width: 180, height: 24);
                nestedButton.IsEnabled = false;
                nestedButton.SceneNode.IsActiveSelf = false;
                EditorListRowCellState state = new()
                {
                    Label = label, RemoveHost = removeNode, ValueNode = valueNode,
                    NestedButton = nestedButton
                };
                _listCells.Add(layout, state);
                return layout;
            }

            private UIBoundableTransform CreateNestedVirtualListCell(SceneNode tableRow)
            {
                bool Current() => _listContext.IsActive && IsCurrentBuild(_headerGeneration) &&
                    _nestedListCurrent?.Invoke() == true;
                if (!Current()) throw new InvalidOperationException("The nested list detail view was retired.");
                ObjectConstructionIdentity rowIdentity = ObjectConstructionIdentity.Capture(tableRow);
                TransformBase rowTransform = tableRow.Transform;
                ObjectConstructionIdentity rowTransformIdentity = ObjectConstructionIdentity.Capture(rowTransform);
                TransformBase? originalRowParent = rowTransform.Parent;
                SceneNode? createdCell = CreateNestedAttemptNode(tableRow, Current);
                if (createdCell is null)
                    throw new InvalidOperationException("The nested row owner changed during cell creation.");
                SceneNode cell = createdCell;
                ObjectConstructionIdentity cellIdentity = ObjectConstructionIdentity.Capture(cell);
                bool CellOwned() => rowIdentity.IsCurrent && rowTransformIdentity.IsCurrent &&
                    cellIdentity.IsCurrent && !tableRow.IsDestroyed && !tableRow.IsDestroyQueued &&
                    !cell.IsDestroyed && !cell.IsDestroyQueued &&
                    ReferenceEquals(tableRow.Transform, rowTransform) &&
                    ReferenceEquals(rowTransform.Parent, originalRowParent) &&
                    ReferenceEquals(cell.Transform.Parent, rowTransform);
                if (!Current() || !CellOwned())
                    throw new InvalidOperationException("The nested row owner changed during cell creation.");
                UIListTransform? layout = CreateNestedAttemptTransform<UIListTransform>(cell, Current, replace: true);
                if (layout is null) throw new InvalidOperationException("The nested row owner changed during layout creation.");
                ObjectConstructionIdentity layoutIdentity = ObjectConstructionIdentity.Capture(layout);
                bool CellCurrent() => Current() && CellOwned() && layoutIdentity.IsCurrent &&
                    ReferenceEquals(cell.Transform, layout);
                if (!CellCurrent()) throw new InvalidOperationException("The nested row owner changed during layout setup.");
                layout.DisplayHorizontal = true;
                if (!CellCurrent()) throw new InvalidOperationException("The nested row owner changed during layout setup.");
                layout.ItemAlignment = EListAlignment.TopOrLeft;
                if (!CellCurrent()) throw new InvalidOperationException("The nested row owner changed during layout setup.");
                layout.ItemSpacing = 4;
                if (!CellCurrent()) throw new InvalidOperationException("The nested row owner changed during layout setup.");

                UITextComponent? label = AddNestedAttemptLabel(cell, " ", CellCurrent);
                if (label is null || !CellCurrent()) throw new InvalidOperationException("The nested row owner changed during label creation.");
                UIBoundableTransform? labelBounds = CreateNestedAttemptTransform<UIBoundableTransform>(label.SceneNode!, CellCurrent);
                if (labelBounds is null || !CellCurrent()) throw new InvalidOperationException("The nested row owner changed during label layout.");
                labelBounds.Width = 60;
                if (!CellCurrent()) throw new InvalidOperationException("The nested row owner changed during label layout.");

                SceneNode? createdRemoveHost = CreateNestedAttemptNode(cell, CellCurrent);
                if (createdRemoveHost is null) throw new InvalidOperationException("The nested row owner changed during remove-host creation.");
                SceneNode removeHost = createdRemoveHost;
                ObjectConstructionIdentity removeHostIdentity = ObjectConstructionIdentity.Capture(removeHost);
                if (!CellCurrent() || !removeHostIdentity.IsCurrent ||
                    !ReferenceEquals(removeHost.Transform.Parent, layout))
                    throw new InvalidOperationException("The nested row owner changed during remove-host creation.");
                UIBoundableTransform? removeBounds = CreateNestedAttemptTransform<UIBoundableTransform>(removeHost, CellCurrent);
                if (removeBounds is null || !CellCurrent() || !ReferenceEquals(removeHost.Transform, removeBounds))
                    throw new InvalidOperationException("The nested row owner changed during remove-host layout.");
                removeBounds.Width = 55;
                if (!CellCurrent()) throw new InvalidOperationException("The nested row owner changed during remove-host layout.");

                SceneNode? createdValueNode = CreateNestedAttemptNode(cell, CellCurrent);
                if (createdValueNode is null) throw new InvalidOperationException("The nested row owner changed during value-host creation.");
                SceneNode valueNode = createdValueNode;
                ObjectConstructionIdentity valueNodeIdentity = ObjectConstructionIdentity.Capture(valueNode);
                if (!CellCurrent() || !valueNodeIdentity.IsCurrent ||
                    !ReferenceEquals(valueNode.Transform.Parent, layout))
                    throw new InvalidOperationException("The nested row owner changed during value-host creation.");
                UIBoundableTransform? valueBounds = CreateNestedAttemptTransform<UIBoundableTransform>(valueNode, CellCurrent, replace: true);
                if (valueBounds is null || !CellCurrent() || !ReferenceEquals(valueNode.Transform, valueBounds))
                    throw new InvalidOperationException("The nested row owner changed during value-host layout.");
                valueBounds.Width = 180;
                if (!CellCurrent()) throw new InvalidOperationException("The nested row owner changed during value-host layout.");
                UIButtonComponent? nestedButton = CreateNestedAttemptButton(valueNode,
                    "Open nested list", static () => { }, CellCurrent);
                if (nestedButton is null || !CellCurrent()) throw new InvalidOperationException("The nested row owner changed during button creation.");
                nestedButton.IsEnabled = false;
                if (!CellCurrent()) throw new InvalidOperationException("The nested row owner changed during button setup.");
                nestedButton.SceneNode.IsActiveSelf = false;
                if (!CellCurrent()) throw new InvalidOperationException("The nested row owner changed during button setup.");
                EditorListRowCellState state = new()
                {
                    Label = label,
                    RemoveHost = removeHost,
                    ValueNode = valueNode,
                    NestedButton = nestedButton
                };
                _listCells.Add(layout, state);
                return layout;
            }

            private void BindVirtualListCell(UIBoundableTransform cell, int index, ulong rowId)
            {
                if (!_listContext.IsActive) return;
                if (!_listCells.TryGetValue(cell, out EditorListRowCellState? state)) return;
                state.CleanupIntent++;
                ulong generation = _headerGeneration;
                try { BindVirtualListCellCore(cell, state, index, rowId, generation); }
                finally
                {
                    if (!IsCurrentCellBinding(cell, state, generation))
                    {
                        if (state.Bindings is { } bindings)
                            foreach (IEditorListElementBinding binding in bindings)
                                try { binding.ReleaseLease(); }
                                catch (Exception error) { Debug.LogException(error, "Could not retire a new list binding."); }
                        if (state.LastBindings is { } cached)
                            foreach (IEditorListElementBinding binding in cached)
                                try { binding.ReleaseLease(); }
                                catch (Exception error) { Debug.LogException(error, "Could not retire a cached list binding."); }
                        if (_ownerIsCurrent is null || _ownerIsCurrent())
                        {
                            state.Bindings = null;
                            state.LastBindings = null;
                            if (state.RemoveButton is { } remove &&
                                ReferenceEquals(remove.SceneNode.Transform.Parent, state.RemoveHost.Transform))
                            {
                                try { remove.SceneNode.Destroy(false); }
                                catch (Exception error) { Debug.LogException(error, "Could not retire a list action."); }
                            }
                            if (state.EditorNode is { } editor &&
                                ReferenceEquals(editor.Transform.Parent, state.ValueNode.Transform))
                            {
                                try { editor.Destroy(false); }
                                catch (Exception error) { Debug.LogException(error, "Could not retire a list editor."); }
                            }
                        }
                    }
                }
            }

            private bool IsCurrentCellBinding(UIBoundableTransform cell,
                EditorListRowCellState state, ulong generation)
                => IsCurrentBuild(generation) && _listCells.TryGetValue(cell, out EditorListRowCellState? current) &&
                    ReferenceEquals(current, state);

            private void BindVirtualListCellCore(UIBoundableTransform cell,
                EditorListRowCellState state, int index, ulong rowId, ulong generation)
            {
                bool RowCurrent()
                {
                    if (!IsCurrentCellBinding(cell, state, generation) || state.Index != index ||
                        state.RowId != rowId || _virtualList is not { } currentList ||
                        cell.Parent is not UIBoundableTransform currentRow ||
                        !ReferenceEquals(currentRow.Parent, currentList) ||
                        !currentList.TryGetVirtualRowLease(currentRow, out UIVirtualRowLease currentLease) ||
                        currentLease.ItemId != rowId)
                        return false;
                    return true;
                }
                IEditorListElementBinding[]? previousBindings = state.Bindings ?? state.LastBindings;
                bool sameSource = !IsNestedListType(_listElementType!) && state.RowId == rowId &&
                    (state.Index == index || state.LastIndex == index) &&
                    AreListBindingsCurrent(previousBindings, index, rowId);
                if (!IsCurrentCellBinding(cell, state, generation)) return;
                if (sameSource)
                {
                    SceneNode? sameEditor = state.EditorNode;
                    ObjectConstructionIdentity? editorIdentity = sameEditor is null
                        ? null : ObjectConstructionIdentity.Capture(sameEditor);
                    TransformBase? editorTransform = sameEditor?.Transform;
                    ObjectConstructionIdentity? editorTransformIdentity = editorTransform is null
                        ? null : ObjectConstructionIdentity.Capture(editorTransform);
                    TransformBase? valueTransform = state.ValueNode.Transform;
                    ObjectConstructionIdentity valueTransformIdentity = ObjectConstructionIdentity.Capture(valueTransform);
                    bool SameEditorCurrent() => sameEditor is null ||
                        ReferenceEquals(state.EditorNode, sameEditor) && editorIdentity is { IsCurrent: true } &&
                        editorTransformIdentity is { IsCurrent: true } && valueTransformIdentity.IsCurrent &&
                        !sameEditor.IsDestroyed && !sameEditor.IsDestroyQueued &&
                        ReferenceEquals(sameEditor.Transform, editorTransform) &&
                        ReferenceEquals(state.ValueNode.Transform, valueTransform) &&
                        ReferenceEquals(editorTransform?.Parent, valueTransform);
                    if (!SameEditorCurrent()) return;
                    state.Index = index;
                    state.Bindings = previousBindings;
                    state.LastBindings = null;
                    foreach (IEditorListElementBinding binding in previousBindings!) binding.AcceptLease();
                    if (state.RemoveButton is not null) state.RemoveButton.SceneNode.IsActiveSelf = true;
                    if (!RowCurrent() || !SameEditorCurrent()) return;
                    if (sameEditor is not null) sameEditor.IsActiveSelf = true;
                    return;
                }
                if (previousBindings is not null)
                    foreach (IEditorListElementBinding binding in previousBindings) binding.ReleaseLease();
                state.LastBindings = null;
                bool sameButtonLease = state.RemoveButton is not null && state.RowId == rowId &&
                    state.BoundRevision == _listContext.Revision;
                state.Index = index;
                state.RowId = rowId;
                state.BoundRevision = _listContext.Revision;
                if (!SetNestedListCellLabel(cell, state, $"[{index}]", RowCurrent)) return;
                if (!RowCurrent()) return;
                if (state.NestedAction is { } oldNestedAction && state.NestedButton is { } currentNestedButton)
                {
                    currentNestedButton.UnregisterClickActions(oldNestedAction);
                    state.NestedAction = null;
                    currentNestedButton.IsEnabled = false;
                    if (!RowCurrent()) return;
                    currentNestedButton.SceneNode.IsActiveSelf = false;
                    if (!RowCurrent()) return;
                }
                if (!RowCurrent()) return;
                Type elementType = _listElementType!;
                if (!sameButtonLease && state.RemoveButton is { } oldButton)
                {
                    oldButton.SceneNode.IsActiveSelf = false;
                    if (!RowCurrent()) return;
                    oldButton.SceneNode.Destroy(false);
                    if (!RowCurrent()) return;
                    state.RemoveButton = null;
                }
                if (state.RemoveButton is null)
                {
                    ulong capturedRevision = _listContext.Revision;
                    if (_ownerIsCurrent is not null)
                    {
                        UIButtonComponent? remove = CreateNestedAttemptButton(state.RemoveHost, "Remove",
                            () =>
                            {
                                if (RowCurrent() && state.Index == index && state.RowId == rowId &&
                                    _listContext.Revision == capturedRevision && state.Index < _listRows.Count &&
                                    _listRows.GetItemId(state.Index) == rowId && state.Bindings is { } bound)
                                    RemoveListElement(bound);
                            }, RowCurrent, width: null, fillParent: true);
                        if (remove is null || !RowCurrent()) return;
                        state.RemoveButton = remove;
                    }
                    else
                    {
                        SceneNode removeNode = state.RemoveHost.NewChild<UIButtonComponent>(out UIButtonComponent remove);
                        state.RemoveButton = remove;
                        if (!RowCurrent()) return;
                        removeNode.GetTransformAs<UIBoundableTransform>(true)!.MaxAnchor = Vector2.One;
                        removeNode.NewChild<UITextComponent>(out UITextComponent removeText);
                        removeText.Text = "Remove";
                        if (!RowCurrent()) return;
                        remove.InteractAction += _ =>
                        {
                            if (state.Index >= 0 && state.RowId == rowId &&
                                _listContext.Revision == capturedRevision &&
                                state.Index < _listRows.Count &&
                                _listRows.GetItemId(state.Index) == rowId &&
                                state.Bindings is { } bound)
                                RemoveListElement(bound);
                        };
                    }
                }
                state.RemoveButton.SceneNode.IsActiveSelf = true;
                if (!RowCurrent()) return;
                state.RemoveButton.IsReadOnly = !HasMutableList();
                if (!RowCurrent()) return;
                bool rowEditable = _nestedSources is not null
                    ? CanSetNestedIndex(index)
                    : EditorListMutation.SupportsSource(_property) &&
                        !_owners.Any(owner => _property.GetValue(owner) is not IList list ||
                            !EditorListMutation.Supports(list, EEditorListMutation.Set) || index >= list.Count);
                if (!RowCurrent()) return;
                if (!rowEditable)
                {
                    if (state.EditorNode is not null)
                    {
                        state.EditorNode.IsActiveSelf = false;
                        if (!RowCurrent()) return;
                        state.EditorNode.Destroy(false);
                        if (!RowCurrent()) return;
                        state.EditorNode = null;
                    }
                    if (!SetNestedListCellLabel(cell, state,
                        $"[{index}] Read-only or missing list item", RowCurrent)) return;
                    return;
                }
                Type bindingType = (_nestedSources is null ? typeof(ListElementBinding<>) :
                    typeof(EditorNestedListElementBinding<>)).MakeGenericType(elementType);
                PropertyInfo bindingProperty = bindingType.GetProperty("Value")!;
                object[] targets = _nestedSources is null
                    ? CreateListBindings(bindingType, index)
                    : CreateNestedListBindings(index, rowId, RowCurrent);
                if (!RowCurrent())
                {
                    foreach (IEditorListElementBinding binding in targets.OfType<IEditorListElementBinding>())
                        binding.ReleaseLease();
                    return;
                }
                state.Bindings = targets.Cast<IEditorListElementBinding>().ToArray();
                if (IsNestedListType(elementType))
                {
                    SceneNode? oldEditor = state.EditorNode;
                    if (oldEditor is not null &&
                        ReferenceEquals(oldEditor.Transform.Parent, state.ValueNode.Transform))
                        oldEditor.Destroy(false);
                    if (!RowCurrent()) return;
                    if (ReferenceEquals(state.EditorNode, oldEditor)) state.EditorNode = null;
                    state.ReusableEditor = false;
                    UIButtonComponent nestedButton = state.NestedButton!;
                    bool canOpenNested = CanCreateNestedList(elementType, targets.Cast<object?>().ToArray());
                    if (canOpenNested)
                    {
                        Action<UIInteractableComponent> openAction = _ =>
                        {
                            if (IsCurrentCellBinding(cell, state, generation) &&
                                state.Index == index && state.RowId == rowId && state.Bindings is { } currentBindings &&
                                currentBindings.All(binding => binding.IsLeased) && RowCurrent() &&
                                AreListBindingsCurrent(currentBindings, index, rowId))
                                OpenNestedListDetail(elementType, bindingProperty, targets,
                                    () => RowCurrent() &&
                                        state.Index == index && state.RowId == rowId &&
                                        state.Bindings is { } bound && bound.All(binding => binding.IsLeased) &&
                                        AreListBindingsCurrent(bound, index, rowId),
                                    generation);
                        };
                        state.NestedAction = openAction;
                        nestedButton.RegisterClickActions(openAction);
                        if (!RowCurrent()) return;
                        nestedButton.IsEnabled = true;
                        if (!RowCurrent()) return;
                        nestedButton.SceneNode.IsActiveSelf = true;
                    }
                    else
                    {
                        nestedButton.IsEnabled = false;
                        if (!RowCurrent()) return;
                        nestedButton.SceneNode.IsActiveSelf = false;
                    }
                    if (!RowCurrent()) return;
                    return;
                }
                bool reusable = CanRebindListEditor(elementType);
                if (state.EditorNode is not null && !state.ReusableEditor)
                {
                    state.EditorNode.IsActiveSelf = false;
                    if (!RowCurrent()) return;
                    state.EditorNode.Destroy(false);
                    if (!RowCurrent()) return;
                    state.EditorNode = null;
                }
                if (!RowCurrent()) return;
                if (state.EditorNode is null)
                {
                    SceneNode? createdEditor = _ownerIsCurrent is null
                        ? state.ValueNode.NewChild()
                        : CreateNestedAttemptNode(state.ValueNode, RowCurrent, () => IsCurrentBuild(generation));
                    if (createdEditor is null || !RowCurrent()) return;
                    SceneNode editor = createdEditor;
                    state.EditorNode = editor;
                    UIBoundableTransform? editorLayout = _ownerIsCurrent is null
                        ? editor.SetTransform<UIBoundableTransform>()
                        : CreateNestedAttemptTransform<UIBoundableTransform>(editor, RowCurrent, replace: true);
                    if (editorLayout is null || !RowCurrent() || !ReferenceEquals(editor.Transform, editorLayout)) return;
                    editorLayout.MaxAnchor = Vector2.One;
                    if (!RowCurrent()) return;
                    if (_ownerIsCurrent is null)
                        BuildValueEditor(editor, elementType, bindingProperty, targets);
                    else if (!BuildNestedScalarValueEditor(editor, elementType, bindingProperty,
                        targets, RowCurrent, RetainFailedComponentCreation))
                    {
                        if (!RowCurrent()) return;
                        if (ReferenceEquals(editor.Transform.Parent, state.ValueNode.Transform))
                            editor.Destroy(false);
                        state.EditorNode = null;
                        return;
                    }
                    if (!RowCurrent()) return;
                    state.ReusableEditor = reusable;
                }
                else
                {
                    SceneNode oldEditor = state.EditorNode;
                    bool rebound;
                    bool editorOwnershipCurrent = true;
                    if (_ownerIsCurrent is null)
                        rebound = TryRebindListEditor(oldEditor, elementType, bindingProperty,
                            targets.Cast<object?>().ToArray());
                    else
                        rebound = TryRebindOwnedNestedListEditor(oldEditor, state, elementType, bindingProperty,
                            targets.Cast<object?>().ToArray(), RowCurrent, out editorOwnershipCurrent);
                    if (!editorOwnershipCurrent) return;
                    if (!RowCurrent()) return;
                    if (rebound)
                    {
                        oldEditor.IsActiveSelf = true;
                        return;
                    }
                    ObjectConstructionIdentity editorIdentity = ObjectConstructionIdentity.Capture(oldEditor);
                    TransformBase editorTransform = oldEditor.Transform;
                    ObjectConstructionIdentity editorTransformIdentity = ObjectConstructionIdentity.Capture(editorTransform);
                    TransformBase valueTransform = state.ValueNode.Transform;
                    ObjectConstructionIdentity valueTransformIdentity = ObjectConstructionIdentity.Capture(valueTransform);
                    bool FallbackOwnershipCurrent() => editorIdentity.IsCurrent && editorTransformIdentity.IsCurrent &&
                        valueTransformIdentity.IsCurrent && ReferenceEquals(state.EditorNode, oldEditor) &&
                        !oldEditor.IsDestroyed && !oldEditor.IsDestroyQueued &&
                        ReferenceEquals(oldEditor.Transform, editorTransform) &&
                        ReferenceEquals(state.ValueNode.Transform, valueTransform) &&
                        ReferenceEquals(editorTransform.Parent, valueTransform);
                    if (!FallbackOwnershipCurrent()) return;
                    oldEditor.IsActiveSelf = false;
                    if (!RowCurrent() || !FallbackOwnershipCurrent()) return;
                    state.EditorNode = null;
                    oldEditor.Destroy(false);
                    if (!RowCurrent() || state.EditorNode is not null) return;
                    SceneNode? createdEditor = _ownerIsCurrent is null
                        ? state.ValueNode.NewChild()
                        : CreateNestedAttemptNode(state.ValueNode, RowCurrent, () => IsCurrentBuild(generation));
                    if (createdEditor is null || !RowCurrent()) return;
                    SceneNode replacement = createdEditor;
                    state.EditorNode = replacement;
                    UIBoundableTransform? editorLayout = _ownerIsCurrent is null
                        ? replacement.SetTransform<UIBoundableTransform>()
                        : CreateNestedAttemptTransform<UIBoundableTransform>(replacement, RowCurrent, replace: true);
                    if (editorLayout is null || !RowCurrent() || !ReferenceEquals(replacement.Transform, editorLayout)) return;
                    editorLayout.MaxAnchor = Vector2.One;
                    if (!RowCurrent()) return;
                    if (_ownerIsCurrent is null)
                        BuildValueEditor(replacement, elementType, bindingProperty, targets);
                    else if (!BuildNestedScalarValueEditor(replacement, elementType, bindingProperty,
                        targets, RowCurrent, RetainFailedComponentCreation))
                    {
                        if (!RowCurrent()) return;
                        if (ReferenceEquals(replacement.Transform.Parent, state.ValueNode.Transform))
                            replacement.Destroy(false);
                        state.EditorNode = null;
                        return;
                    }
                    if (!RowCurrent()) return;
                    state.ReusableEditor = reusable;
                }
            }

            private bool AreListBindingsCurrent(IEditorListElementBinding[]? bindings, int index, ulong rowId)
            {
                if (bindings is null || bindings.Length != _owners.Count ||
                    index >= _listRows.Count || _listRows.GetItemId(index) != rowId)
                    return false;
                foreach (IEditorListElementBinding binding in bindings)
                    try
                    {
                        if (binding is IEditorNestedListElementBinding nested)
                        {
                            if (!nested.IsRowCurrent || binding.ContextRevision != _listContext.Revision ||
                                binding.Index != index || binding.RowId != rowId ||
                                index >= binding.Collection.Count ||
                                !EditorListMutation.SameValue(binding.Collection[index], binding.DisplayedValue))
                                return false;
                            continue;
                        }
                        IList list = binding.Collection;
                        if (binding.ContextRevision != _listContext.Revision ||
                            binding.Index != index || binding.RowId != rowId ||
                            !ReferenceEquals(binding.OwnerProperty.GetValue(binding.Owner), list) ||
                            !ReferenceEquals(binding.CollectionState, EditorListMutation.GetStateToken(list)) ||
                            !ReferenceEquals(binding.OwnerDocument, EditorListMutation.DocumentOf(binding.Owner)) ||
                            !ReferenceEquals(binding.OwnerWorld, EditorListMutation.WorldOf(binding.Owner)) ||
                            !ReferenceEquals(binding.OwnerNode, EditorListMutation.NodeOf(binding.Owner)) ||
                            index >= list.Count ||
                            !EditorListMutation.SameValue(list[index], binding.DisplayedValue))
                            return false;
                    }
                    catch { return false; }
                return true;
            }

            private static bool CanRebindListEditor(Type valueType)
                => Type.GetTypeCode(valueType) != TypeCode.Object || valueType.IsEnum ||
                    valueType == typeof(Vector2) || valueType == typeof(Vector3) ||
                    valueType == typeof(Vector4) || valueType == typeof(Quaternion) ||
                    valueType == typeof(LayerMask) || valueType == typeof(ColorF3) ||
                    valueType == typeof(ColorF4) || valueType == typeof(System.Drawing.Color);

            private static bool TryRebindListEditor(SceneNode editor, Type valueType,
                PropertyInfo property, object?[] targets, Func<bool>? ownerIsCurrent = null)
            {
                if (ownerIsCurrent?.Invoke() == false) return false;
                if (valueType == typeof(Vector2) || valueType == typeof(Vector3) ||
                    valueType == typeof(Vector4) || valueType == typeof(Quaternion) ||
                    valueType == typeof(LayerMask))
                {
                    DataTransformerBase? transformer = editor.Components.OfType<DataTransformerBase>().FirstOrDefault();
                    if (transformer is null) return false;
                    ObjectConstructionIdentity editorIdentity = ObjectConstructionIdentity.Capture(editor);
                    TransformBase editorTransform = editor.Transform;
                    ObjectConstructionIdentity transformIdentity = ObjectConstructionIdentity.Capture(editorTransform);
                    TransformBase? parent = editorTransform.Parent;
                    bool Current() => ownerIsCurrent?.Invoke() != false && editorIdentity.IsCurrent &&
                        transformIdentity.IsCurrent && !editor.IsDestroyed && !editor.IsDestroyQueued &&
                        ReferenceEquals(editor.Transform, editorTransform) &&
                        ReferenceEquals(editorTransform.Parent, parent) &&
                        editor.Components.Contains(transformer);
                    if (!Current()) return false;
                    transformer.Property = property;
                    if (!Current()) return false;
                    transformer.Targets = targets;
                    if (!Current()) return false;
                    object?[] transformerTargets = [transformer];
                    for (int index = 0; index < editorTransform.Children.Count; index++)
                    {
                        if (!Current()) return false;
                        TransformBase childTransform = editorTransform.Children[index];
                        SceneNode? child = childTransform.SceneNode;
                        if (child is null || !ReferenceEquals(childTransform.Parent, editorTransform)) return false;
                        ObjectConstructionIdentity childIdentity = ObjectConstructionIdentity.Capture(child);
                        bool ChildCurrent() => Current() && childIdentity.IsCurrent &&
                            !child.IsDestroyed && !child.IsDestroyQueued &&
                            ReferenceEquals(child.Transform, childTransform) &&
                            ReferenceEquals(childTransform.Parent, editorTransform);
                        if (!ChildCurrent()) return false;
                        PropertyInfo transformedProperty = transformer.GetTransformedProperty(child.Name!);
                        NativeUIElements.RebindPropertyEditor(child, transformedProperty,
                            transformerTargets, ChildCurrent);
                        if (!ChildCurrent()) return false;
                    }
                    return Current();
                }
                if (valueType == typeof(ColorF3) || valueType == typeof(ColorF4) ||
                    valueType == typeof(System.Drawing.Color))
                {
                    UIColorSelectorComponent? color = editor.Components.OfType<UIColorSelectorComponent>().FirstOrDefault();
                    if (color is null) return false;
                    color.Binding = new EditorColorPropertyBinding(property, targets);
                    if (ownerIsCurrent?.Invoke() == false) return false;
                    color.IsReadOnly = !property.CanWrite;
                    return ownerIsCurrent?.Invoke() != false;
                }
                if (valueType.IsEnum && !valueType.IsDefined(typeof(FlagsAttribute), inherit: false))
                {
                    InspectorEnumEditorComponent? enumEditor = editor.Components
                        .OfType<InspectorEnumEditorComponent>().FirstOrDefault();
                    if (enumEditor is null) return false;
                    enumEditor.Bind(property, targets, ownerIsCurrent);
                    return ownerIsCurrent?.Invoke() != false;
                }
                if (CanRebindListEditor(valueType))
                {
                    NativeUIElements.RebindPropertyEditor(editor, property, targets, ownerIsCurrent);
                    return ownerIsCurrent?.Invoke() != false;
                }
                return false;
            }

            private void UnbindVirtualListCell(UIBoundableTransform cell)
            {
                if (!_listCells.TryGetValue(cell, out EditorListRowCellState? state)) return;
                ulong cleanupIntent = ++state.CleanupIntent;
                ObjectConstructionIdentity cellIdentity = ObjectConstructionIdentity.Capture(cell);
                SceneNode? cellNode = cell.SceneNode;
                SceneNode valueNode = state.ValueNode;
                ObjectConstructionIdentity valueNodeIdentity = ObjectConstructionIdentity.Capture(valueNode);
                TransformBase valueTransform = valueNode.Transform;
                ObjectConstructionIdentity valueTransformIdentity = ObjectConstructionIdentity.Capture(valueTransform);
                SceneNode? editor = state.EditorNode;
                ObjectConstructionIdentity? editorIdentity = editor is null
                    ? null : ObjectConstructionIdentity.Capture(editor);
                TransformBase? editorTransform = editor?.Transform;
                ObjectConstructionIdentity? editorTransformIdentity = editorTransform is null
                    ? null : ObjectConstructionIdentity.Capture(editorTransform);
                bool CleanupCurrent() => state.CleanupIntent == cleanupIntent &&
                    _listCells.TryGetValue(cell, out EditorListRowCellState? current) &&
                    ReferenceEquals(current, state) && cellIdentity.IsCurrent &&
                    !cell.IsDestroyed && !cell.IsDestroyQueued &&
                    ReferenceEquals(cell.SceneNode, cellNode) && valueNodeIdentity.IsCurrent &&
                    valueTransformIdentity.IsCurrent && !valueNode.IsDestroyed &&
                    !valueNode.IsDestroyQueued && ReferenceEquals(valueNode.Transform, valueTransform) &&
                    ReferenceEquals(valueNode.Parent, cellNode);
                bool EditorOwned() => editor is not null && editorTransform is not null &&
                    editorIdentity?.IsCurrent == true && editorTransformIdentity?.IsCurrent == true &&
                    !editor.IsDestroyed && !editor.IsDestroyQueued &&
                    ReferenceEquals(state.EditorNode, editor) &&
                    ReferenceEquals(editor.Transform, editorTransform) &&
                    ReferenceEquals(editorTransform.Parent, valueTransform);
                state.LastIndex = state.Index;
                state.Index = -1;
                state.LastBindings = state.Bindings;
                state.Bindings = null;
                if (state.LastBindings is { } oldBindings)
                    foreach (IEditorListElementBinding binding in oldBindings) binding.ReleaseLease();
                if (!CleanupCurrent()) return;
                if (state.NestedAction is { } nestedAction && state.NestedButton is { } nestedButton)
                {
                    state.NestedButton.UnregisterClickActions(nestedAction);
                    if (!CleanupCurrent()) return;
                    state.NestedAction = null;
                    nestedButton.IsEnabled = false;
                    if (!CleanupCurrent()) return;
                    nestedButton.SceneNode.IsActiveSelf = false;
                    if (!CleanupCurrent()) return;
                }
                if (state.RemoveButton is { } button)
                {
                    button.SceneNode.IsActiveSelf = false;
                    if (!CleanupCurrent()) return;
                }
                if (editor is not null)
                {
                    if (!EditorOwned())
                    {
                        if (ReferenceEquals(state.EditorNode, editor)) state.EditorNode = null;
                        state.ReusableEditor = false;
                        return;
                    }
                    if (state.ReusableEditor)
                    {
                        editor.IsActiveSelf = false;
                        if (!CleanupCurrent()) return;
                        if (!EditorOwned() && ReferenceEquals(state.EditorNode, editor))
                        {
                            state.EditorNode = null;
                            state.ReusableEditor = false;
                        }
                    }
                    else
                    {
                        editor.IsActiveSelf = false;
                        if (!CleanupCurrent()) return;
                        if (!EditorOwned())
                        {
                            if (ReferenceEquals(state.EditorNode, editor)) state.EditorNode = null;
                            state.ReusableEditor = false;
                            return;
                        }
                        editor.Destroy(false);
                        if (!CleanupCurrent()) return;
                        if (ReferenceEquals(state.EditorNode, editor)) state.EditorNode = null;
                    }
                }
            }

            private IList? GetListSample()
            {
                if (_nestedSources is { Length: > 0 }) return _nestedSources[0].Inner;
                foreach (var owner in _owners)
                {
                    if (_property.GetValue(owner) is IList list)
                        return list;
                }
                return null;
            }

            private object[] CreateListBindings(Type bindingType, int index)
            {
                List<object> bindings = new();
                try
                {
                    foreach (var owner in _owners)
                    {
                        if (!_listContext.IsActive) break;
                        var instance = Activator.CreateInstance(bindingType, _property, owner, index,
                            _listRows.GetItemId(index), _listContext);
                        if (instance is not null)
                            bindings.Add(instance);
                    }
                }
                catch
                {
                    foreach (IEditorListElementBinding binding in bindings.OfType<IEditorListElementBinding>())
                        binding.ReleaseLease();
                    throw;
                }
                if (!_listContext.IsActive)
                {
                    foreach (IEditorListElementBinding binding in bindings.OfType<IEditorListElementBinding>())
                        binding.ReleaseLease();
                    return [];
                }
                return bindings.ToArray();
            }

            private bool HasMutableList()
            {
                if (_nestedSources is not null)
                    return CanResizeNestedLists(EEditorListMutation.Insert);
                if (_owners.Count == 0 || !EditorListMutation.SupportsSource(_property)) return false;
                foreach (var owner in _owners)
                {
                    if (_property.GetValue(owner) is not IList list ||
                        !EditorListMutation.Supports(list, EEditorListMutation.Insert))
                        return false;
                }
                return true;
            }

            private void AddListElement(Type elementType)
            {
                if (!_listContext.IsActive) return;
                if (_nestedSources is not null)
                {
                    AddNestedListElement(elementType);
                    return;
                }
                try
                {
                    EditorListMutation.Apply(_owners.Select(owner => (owner, _property)).ToArray(),
                        EEditorListMutation.Insert, -1,
                        _ => EditorListMutation.CreateDefaultElement(elementType), _listContext,
                        expectedSources: _headerLeases);
                    if (_virtualList is null) Rebuild();
                }
                catch (Exception error) { Debug.LogException(error, "Could not add a list item."); }
            }

            private void RemoveListElement(IReadOnlyList<IEditorListElementBinding> bindings)
            {
                if (!_listContext.IsActive) return;
                if (_nestedSources is not null)
                {
                    RemoveNestedListElements(bindings.OfType<IEditorNestedListElementBinding>().ToArray());
                    return;
                }
                try
                {
                    EditorListMutation.Remove(bindings);
                    if (_listRows.Count == 0) Rebuild();
                }
                catch (Exception error) { Debug.LogException(error, "Could not remove a list item."); }
            }

            private void RenderLinkedList(object sample, Type elementType)
            {
                ulong generation = _headerGeneration;
                int count = sample is ICollection collection ? collection.Count : 0;
                EditorLinkedSourceLease[] sources = CaptureLinkedSources(generation);
                bool canMutate = sources.Length == _owners.Count && sources.Length != 0 &&
                    ReferenceEquals(sources[0].List, sample) &&
                    sources.All(source => source.Displayed.Nodes.Length == count);
                if (!IsCurrentBuild(generation)) return;
                List<object?> values = EnumerateLinkedListValues(sample).ToList();
                if (!IsCurrentBuild(generation)) return;
                if (canMutate && CanUseVirtualCollectionEditor(elementType))
                {
                    RenderVirtualLinkedList(elementType, sources, values, generation);
                    return;
                }
                SceneNode branch = _container.NewChild();
                try
                {
                EnsureVerticalLayout(branch);
                if (!KeepRenderedBranch(branch, generation)) return;
                AddInfoLabel(branch, $"Count: {count}");
                if (!KeepRenderedBranch(branch, generation)) return;
                var header = branch.NewChild();
                var headerLayout = header.SetTransform<UIListTransform>();
                headerLayout.DisplayHorizontal = true;
                headerLayout.ItemSpacing = 6.0f;

                if (canMutate)
                {
                    CreateInlineButton(header, "Add", () =>
                    {
                        if (IsCurrentBuild(generation)) AddLinkedListElement(sources, generation);
                    });
                }
                else
                {
                    AddInfoLabel(header, "LinkedList editing needs exact BCL lists and auto-property owners.");
                }
                if (!KeepRenderedBranch(branch, generation)) return;

                var listNode = branch.NewChild();
                EnsureVerticalLayout(listNode);
                if (!KeepRenderedBranch(branch, generation)) return;

                if (values.Count == 0)
                {
                    AddInfoLabel(listNode, "<empty>");
                    return;
                }

                for (int i = 0; i < values.Count; i++)
                {
                    BuildLinkedListRow(listNode, elementType, i, canMutate, sources, values[i]);
                    if (!KeepRenderedBranch(branch, generation)) return;
                }
                }
                finally { if (!IsCurrentBuild(generation)) KeepRenderedBranch(branch, generation); }
            }

            private IEnumerable<object?> EnumerateLinkedListValues(object linkedList)
            {
                if (linkedList is not IEnumerable enumerable)
                    yield break;

                foreach (var value in enumerable)
                    yield return value;
            }

            private EditorLinkedSourceLease[] CaptureLinkedSources(ulong generation)
            {
                List<EditorLinkedSourceLease> sources = [];
                foreach (var owner in _owners)
                {
                    if (!IsCurrentBuild(generation)) return [];
                    try
                    {
                        sources.Add(EditorLinkedMutation.Capture(owner, _property,
                            () => IsCurrentBuild(generation)));
                    }
                    catch (Exception error)
                    {
                        Debug.LogException(error, "Linked-list editing is unavailable for this source.");
                        return [];
                    }
                }
                return [.. sources];
            }

            private void BuildLinkedListRow(SceneNode parent, Type elementType, int index,
                bool canMutate, EditorLinkedSourceLease[] sources, object? visibleValue)
            {
                ulong generation = _headerGeneration;
                var row = parent.NewChild();
                var splitter = row.SetTransform<UIDualSplitTransform>();
                splitter.VerticalSplit = false;
                splitter.FirstFixedSize = true;
                splitter.FixedSize = RowHeaderWidth;

                var labelNode = row.NewChild();
                var labelLayout = labelNode.SetTransform<UIListTransform>();
                labelLayout.DisplayHorizontal = true;
                labelLayout.ItemSpacing = 4.0f;

                labelNode.NewChild<UITextComponent>(out var label);
                label.Text = $"Node {index}";
                label.FontSize = EditorUI.Styles.PropertyNameFontSize;
                label.HorizontalAlignment = EHorizontalAlignment.Left;
                label.VerticalAlignment = EVerticalAlignment.Center;
                label.Color = EditorUI.Styles.PropertyNameTextColor;
                label.BoundableTransform.Margins = new Vector4(4.0f, 2.0f, 4.0f, 2.0f);

                if (canMutate)
                {
                    CreateInlineButton(labelNode, "Remove", () =>
                    {
                        if (IsCurrentBuild(generation)) RemoveLinkedListElement(sources, index, generation);
                    });
                }

                var valueNode = row.NewChild();
                if (!canMutate)
                {
                    AddInfoLabel(valueNode, visibleValue?.ToString() ?? "<null>");
                    return;
                }
                var bindingType = typeof(LinkedListElementBinding<>).MakeGenericType(elementType);
                var bindingProperty = bindingType.GetProperty(nameof(LinkedListElementBinding<object>.Value))!;
                var targets = CreateLinkedListBindings(bindingType, index, generation, sources);
                if (!IsCurrentBuild(generation)) return;
                BuildValueEditor(valueNode, elementType, bindingProperty, targets);
            }

            private object[] CreateLinkedListBindings(Type bindingType, int index, ulong generation,
                EditorLinkedSourceLease[] sources, Func<bool>? rowReadIsCurrent = null,
                Func<bool>? rowWriteIsCurrent = null)
            {
                List<object> bindings = new();
                foreach (EditorLinkedSourceLease source in sources)
                {
                    if (!IsCurrentBuild(generation)) break;
                    if (index >= source.Displayed.Nodes.Length) return [];
                    var instance = Activator.CreateInstance(bindingType, source, index,
                        (Action)Rebuild, rowReadIsCurrent, rowWriteIsCurrent);
                    if (!IsCurrentBuild(generation)) break;
                    if (instance is not null)
                        bindings.Add(instance);
                }
                return bindings.ToArray();
            }

            private void AddLinkedListElement(EditorLinkedSourceLease[] sources, ulong generation)
            {
                if (!IsCurrentBuild(generation)) return;
                try { EditorLinkedMutation.AddLast(sources, Rebuild); }
                catch (Exception error) { Debug.LogException(error, "Could not add a linked-list node."); }
            }

            private void RemoveLinkedListElement(EditorLinkedSourceLease[] sources, int index,
                ulong generation)
            {
                if (!IsCurrentBuild(generation)) return;
                try { EditorLinkedMutation.Remove(sources, index, Rebuild); }
                catch (Exception error) { Debug.LogException(error, "Could not remove a linked-list node."); }
            }


        }

        private static void BuildValueEditor(SceneNode node, Type valueType, PropertyInfo bindingProperty, object[] targets)
        {
            if (bindingProperty is null || targets.Length == 0)
            {
                AddInfoLabel(node, "<No editable targets>");
                return;
            }

            var editor = CreateNew(valueType);
            if (editor is null)
            {
                AddInfoLabel(node, $"No editor for {valueType.Name}.");
                return;
            }

            editor(node, bindingProperty, targets.Cast<object?>().ToArray());
        }

        private static List<object> CollectOwners(object?[]? objects)
        {
            List<object> owners = new();
            if (objects is null)
                return owners;

            foreach (var obj in objects)
                if (obj is not null)
                    owners.Add(obj);
            return owners;
        }

        private static List<object?> BuildBuffer(IEnumerable source)
        {
            List<object?> buffer = new();
            foreach (var item in source)
                buffer.Add(item);
            return buffer;
        }

        private static bool ImplementsGenericInterface(Type type, Type genericDefinition)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == genericDefinition)
                return true;

            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == genericDefinition)
                    return true;
            }

            return false;
        }

        private static bool IsLinkedListType(Type type)
        {
            while (type is not null && type != typeof(object))
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(LinkedList<>))
                    return true;
                type = type.BaseType!;
            }
            return false;
        }

        private static bool IsLinkedListInstance(object value, out Type elementType)
        {
            var type = value.GetType();
            while (type is not null && type != typeof(object))
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(LinkedList<>))
                {
                    elementType = type.GetGenericArguments()[0];
                    return true;
                }
                type = type.BaseType!;
            }

            elementType = typeof(object);
            return false;
        }

        private static (Type KeyType, Type ValueType) ResolveDictionaryTypes(Type declaredType, Type runtimeType)
        {
            if (TryGetDictionaryArgs(declaredType, out var key, out var value))
                return (key, value);
            if (TryGetDictionaryArgs(runtimeType, out key, out value))
                return (key, value);
            return (typeof(object), typeof(object));
        }

        private static bool TryGetDictionaryArgs(Type type, out Type key, out Type value)
        {
            if (type.IsGenericType)
            {
                var def = type.GetGenericTypeDefinition();
                if (def == typeof(IDictionary<,>) || def == typeof(Dictionary<,>))
                {
                    var args = type.GetGenericArguments();
                    key = args[0];
                    value = args[1];
                    return true;
                }
            }

            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                {
                    var args = iface.GetGenericArguments();
                    key = args[0];
                    value = args[1];
                    return true;
                }
            }

            key = typeof(object);
            value = typeof(object);
            return false;
        }

        private static Type ResolveElementType(Type declaredType, Type runtimeType)
        {
            if (declaredType.IsArray)
                return declaredType.GetElementType() ?? typeof(object);

            if (declaredType.IsGenericType)
            {
                var args = declaredType.GetGenericArguments();
                if (args.Length == 1)
                    return args[0];
            }

            var enumerableIface = declaredType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerableIface is not null)
                return enumerableIface.GetGenericArguments()[0];

            if (runtimeType.IsGenericType)
            {
                var args = runtimeType.GetGenericArguments();
                if (args.Length == 1)
                    return args[0];
            }

            enumerableIface = runtimeType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerableIface is not null)
                return enumerableIface.GetGenericArguments()[0];

            return typeof(object);
        }

        private static void CreateInlineButton(SceneNode parent, string text, Action onClick)
        {
            NativeUIElements.CreateButton(
                parent,
                text,
                _ => onClick(),
                width: ButtonWidth,
                height: ButtonHeight,
                margins: new Vector4(2.0f),
                fontSize: EditorUI.Styles.PropertyInputFontSize ?? 14.0f);
        }

        private static object? ConvertValue(object? value, Type targetType)
        {
            if (targetType is null)
                return value;

            targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (value is null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            if (targetType.IsInstanceOfType(value))
                return value;

            try
            {
                if (targetType.IsEnum)
                {
                    if (value is string s && Enum.IsDefined(targetType, s))
                        return Enum.Parse(targetType, s, true);
                    return Enum.ToObject(targetType, value);
                }

                return Convert.ChangeType(value, targetType);
            }
            catch
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }
        }

        private sealed class ListElementBinding<T> : IEditorListElementBinding
        {
            private readonly PropertyInfo _property;
            private readonly object _owner;
            private readonly object? _ownerDocument;
            private readonly object? _ownerWorld;
            private readonly SceneNode? _ownerNode;
            private readonly int _index;
            private readonly IList _collection;
            private object _collectionState;
            private object? _displayedValue;
            private readonly ulong _rowId;
            private readonly EditorListMutationContext _context;
            private ulong _revision;
            private bool _leased = true;

            public ListElementBinding(PropertyInfo property, object owner, int index,
                ulong rowId, EditorListMutationContext context)
            {
                _property = property;
                _owner = owner;
                _ownerDocument = EditorMutationDirtyState.ResolveDocument(owner);
                _ownerWorld = owner is global::XREngine.XRWorldObjectBase worldOwner
                    ? worldOwner.World : null;
                _ownerNode = owner is XREngine.Components.XRComponent component
                    ? component.SceneNode : null;
                _index = index;
                _collection = property.GetValue(owner) as IList
                    ?? throw new InvalidOperationException("The list changed before row binding.");
                _collectionState = EditorListMutation.GetStateToken(_collection);
                _displayedValue = index >= 0 && index < _collection.Count ? _collection[index] : null;
                _rowId = rowId;
                _context = context;
                _revision = context.Revision;
            }

            public object Owner => _owner;
            public object? OwnerDocument => _ownerDocument;
            public object? OwnerWorld => _ownerWorld;
            public SceneNode? OwnerNode => _ownerNode;
            public PropertyInfo OwnerProperty => _property;
            public IList Collection => _collection;
            public object CollectionState => _collectionState;
            public object? DisplayedValue => _displayedValue;
            public int Index => _index;
            public ulong RowId => _rowId;
            public ulong ContextRevision => _revision;
            public EditorListMutationContext Context => _context;
            public bool IsLeased => _leased;
            public void AcceptLease() => _leased = true;
            public void ReleaseLease() => _leased = false;

            public void AcceptCommitted(object? value, object state, ulong revision)
            {
                _displayedValue = value;
                _collectionState = state;
                _revision = revision;
            }

            public T? Value
            {
                get
                {
                    if (_context.Revision != _revision ||
                        !ReferenceEquals(_property.GetValue(_owner), _collection))
                        return default;
                    IList list = _collection;
                    if (_index < 0 || _index >= list.Count)
                        return default;
                    return list[_index] is T value ? value : (T?)ConvertValue(list[_index], typeof(T));
                }
                set
                {
                    EditorListMutation.Set([this], _ => value);
                }
            }
        }

        private sealed class DictionaryValueBinding<TKey, TValue> : IEditorDictionaryElementBinding
        {
            private readonly EditorDictionarySourceLease _source;
            private readonly object _key;
            private readonly Action _refresh;
            private object? _displayedValue;
            private readonly Func<bool>? _rowReadIsCurrent;
            private readonly Func<bool>? _rowWriteIsCurrent;

            public DictionaryValueBinding(EditorDictionarySourceLease source, object key, Action refresh)
                : this(source, key, refresh, null, null)
            {
            }

            public DictionaryValueBinding(EditorDictionarySourceLease source, object key, Action refresh,
                Func<bool>? rowIsCurrent)
                : this(source, key, refresh, rowIsCurrent, rowIsCurrent)
            {
            }

            public DictionaryValueBinding(EditorDictionarySourceLease source, object key, Action refresh,
                Func<bool>? rowReadIsCurrent, Func<bool>? rowWriteIsCurrent)
            {
                _source = source;
                _key = key;
                _refresh = refresh;
                int index = source.Adapter.IndexOf(source.Displayed, key);
                _displayedValue = source.Displayed.Values[index];
                _rowReadIsCurrent = rowReadIsCurrent;
                _rowWriteIsCurrent = rowWriteIsCurrent;
            }

            EditorDictionarySourceLease IEditorDictionaryElementBinding.Source => _source;
            object IEditorDictionaryElementBinding.Key => _key;
            object? IEditorDictionaryElementBinding.DisplayedValue => _displayedValue;
            bool IEditorDictionaryElementBinding.IsCurrent => (_rowWriteIsCurrent?.Invoke() ?? true) &&
                _source.IsCurrent() &&
                _source.Adapter.IndexOf(_source.Displayed, _key) >= 0;
            Action IEditorDictionaryElementBinding.Refresh => _refresh;
            void IEditorDictionaryElementBinding.AcceptCommitted(object? value, object state)
                => _displayedValue = value;

            public TValue? Value
            {
                get => (_rowReadIsCurrent?.Invoke() ?? true) && _source.IsViewCurrent()
                    ? (TValue?)_displayedValue : default;
                set => EditorDictionaryMutation.Set([this], _ => value, _refresh);
            }
        }

        private sealed class LinkedListElementBinding<T> : IEditorLinkedElementBinding
        {
            private readonly EditorLinkedSourceLease _source;
            private readonly object _node;
            private object? _displayedValue;
            private readonly Action _refresh;
            private readonly Func<bool>? _rowReadIsCurrent;
            private readonly Func<bool>? _rowWriteIsCurrent;

            public LinkedListElementBinding(EditorLinkedSourceLease source, int index, Action refresh)
                : this(source, index, refresh, null, null)
            {
            }

            public LinkedListElementBinding(EditorLinkedSourceLease source, int index, Action refresh,
                Func<bool>? rowIsCurrent)
                : this(source, index, refresh, rowIsCurrent, rowIsCurrent)
            {
            }

            public LinkedListElementBinding(EditorLinkedSourceLease source, int index, Action refresh,
                Func<bool>? rowReadIsCurrent, Func<bool>? rowWriteIsCurrent)
            {
                _source = source;
                _node = source.Displayed.Nodes[index];
                _displayedValue = source.Displayed.Values[index];
                _refresh = refresh;
                _rowReadIsCurrent = rowReadIsCurrent;
                _rowWriteIsCurrent = rowWriteIsCurrent;
            }

            EditorLinkedSourceLease IEditorLinkedElementBinding.Source => _source;
            object IEditorLinkedElementBinding.Node => _node;
            object? IEditorLinkedElementBinding.DisplayedValue => _displayedValue;
            bool IEditorLinkedElementBinding.IsCurrent => (_rowWriteIsCurrent?.Invoke() ?? true) && _source.IsCurrent() &&
                Array.IndexOf(_source.Displayed.Nodes, _node) >= 0;
            Action IEditorLinkedElementBinding.Refresh => _refresh;
            void IEditorLinkedElementBinding.AcceptCommitted(object? value, object state)
                => _displayedValue = value;

            public T? Value
            {
                get => (_rowReadIsCurrent?.Invoke() ?? true) && _source.IsViewCurrent()
                    ? (T?)_displayedValue : default;
                set => EditorLinkedMutation.Set([this], _ => value, _refresh);
            }
        }

    }
}
