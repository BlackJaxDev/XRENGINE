using System.Numerics;
using System.Collections;
using System.Reflection;
using XREngine.Components;
using XREngine;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public static partial class CollectionTypes
    {
        private sealed partial class CollectionEditorContext
        {
            private void RenderVirtualLinkedList(Type elementType,
                EditorLinkedSourceLease[] sources, List<object?> values, ulong generation)
            {
                Type bindingType = typeof(LinkedListElementBinding<>).MakeGenericType(elementType);
                PropertyInfo bindingProperty = bindingType.GetProperty(nameof(LinkedListElementBinding<object>.Value))!;
                EditorVirtualRowContract contract = new(
                    elementType,
                    bindingProperty,
                    values.Count,
                    "Add",
                    index => $"Node {index}",
                    (index, rowRead, rowWrite) => CreateLinkedListBindings(bindingType, index,
                        generation, sources, rowRead, rowWrite),
                    (index, rowWrite) => RemoveLinkedListElement(
                        CreateLinkedRowSources(sources, rowWrite), index, generation),
                    () => AddLinkedListElement(sources, generation),
                    RowExtent: CreateMeasuredRowExtent(elementType, index => values[index]));
                RenderVirtualRows(contract, generation);
            }

            private void RenderVirtualRows(EditorVirtualRowContract contract,
                ulong generation)
            {
                SceneNode branch = _container.NewChild();
                try
                {
                    EnsureVerticalLayout(branch);
                    if (!KeepRenderedBranch(branch, generation)) return;
                    AddInfoLabel(branch, $"Count: {contract.Count}");
                    if (!KeepRenderedBranch(branch, generation)) return;
                    SceneNode header = branch.NewChild();
                    UIListTransform headerLayout = header.SetTransform<UIListTransform>();
                    headerLayout.DisplayHorizontal = true;
                    headerLayout.ItemSpacing = 6.0f;
                    CreateInlineButton(header, contract.AddLabel, () =>
                    {
                        if (IsCurrentBuild(generation)) contract.Add();
                    });
                    if (!KeepRenderedBranch(branch, generation)) return;
                    if (contract.Count == 0)
                    {
                        AddInfoLabel(branch, "<empty>");
                        return;
                    }

                    SceneNode scrollNode = branch.NewChild();
                    UIScrollableTransform scroll = scrollNode.SetTransform<UIScrollableTransform>();
                    scroll.MinAnchor = Vector2.Zero;
                    scroll.MaxAnchor = Vector2.One;
                    scroll.Height = 300;
                    scrollNode.AddComponent<UIScrollRegionComponent>();
                    if (!KeepRenderedBranch(branch, generation)) return;

                    UIListTransform list = scrollNode.NewChild().SetTransform<UIListTransform>();
                    list.MinAnchor = Vector2.Zero;
                    list.MaxAnchor = Vector2.One;
                    list.ItemSize = EditorVirtualRowExtent.SingleLineHeight;
                    list.VirtualOverscan = 2;
                    list.ItemAlignment = EListAlignment.TopOrLeft;
                    _listRows.Reset(contract.Count);
                    if (!KeepRenderedBranch(branch, generation)) return;
                    _virtualList = list;

                    UIVirtualTableProvider<int> provider = new(_listRows,
                        new UIVirtualTableColumn<int>("collection", "Entry", RowHeaderWidth + ButtonWidth + 300,
                            parent => CreateVirtualCollectionCell(parent, generation, IsNestedListType(contract.ValueType)),
                            (cell, index, id) => BindVirtualCollectionCell(cell, index, id,
                                contract, generation, branch),
                            UnbindVirtualCollectionCell));
                    // Row items are indices, so the contract measures each row by index.
                    if (contract.RowExtent is { } rowExtent)
                        provider.ItemExtent = rowExtent;
                    list.VirtualProvider = provider;
                    if (!IsCurrentBuild(generation) && ReferenceEquals(list.VirtualProvider, provider))
                        list.DetachVirtualProviderForOwnerTeardown();
                }
                finally { if (!IsCurrentBuild(generation)) KeepRenderedBranch(branch, generation); }
            }

            /// <summary>Gets the custom editor of a type when it measures its own row height.</summary>
            private static IEditorMeasuredRowEditor? MeasuredRowEditorFor(Type valueType)
                => valueType.GetCustomAttribute<EditorComponentAttribute>() as IEditorMeasuredRowEditor;

            /// <summary>
            /// Creates the row extent of a custom editor that measures its own rows, or null when
            /// the value type uses fixed-height rows.
            /// </summary>
            private static Func<int, float>? CreateMeasuredRowExtent(Type valueType, Func<int, object?> valueAt)
            {
                if (MeasuredRowEditorFor(valueType) is not { } measured) return null;
                return index => EditorVirtualRowExtent.ForMeasured(measured.MeasureRowHeight(valueAt(index)));
            }

            /// <summary>Requests new row heights after a refresh when the rows are measured.</summary>
            private void InvalidateMeasuredRowExtents()
            {
                if (_virtualList?.VirtualProvider is UIVirtualTableProvider<int> { HasItemExtents: true } provider)
                    provider.InvalidateItemExtents();
            }

            private UIBoundableTransform CreateVirtualCollectionCell(SceneNode tableRow,
                ulong generation, bool needsNestedButton)
            {
                if (!IsCurrentBuild(generation))
                    throw new InvalidOperationException("The collection detail was retired.");
                SceneNode cellNode = new(tableRow);
                UIListTransform layout = cellNode.SetTransform<UIListTransform>();
                layout.DisplayHorizontal = true;
                layout.ItemAlignment = EListAlignment.TopOrLeft;
                layout.ItemSpacing = 4;

                SceneNode labelNode = cellNode.NewChild<UITextComponent>(out UITextComponent label);
                labelNode.GetTransformAs<UIBoundableTransform>(true)!.Width = RowHeaderWidth;
                label.FontSize = EditorUI.Styles.PropertyNameFontSize;
                label.HorizontalAlignment = EHorizontalAlignment.Left;
                label.VerticalAlignment = EVerticalAlignment.Center;
                label.Color = EditorUI.Styles.PropertyNameTextColor;
                label.BoundableTransform.Margins = new Vector4(4, 2, 4, 2);

                SceneNode removeHost = cellNode.NewChild();
                removeHost.SetTransform<UIBoundableTransform>().Width = ButtonWidth;
                var (_, removeButton, _, _) = NativeUIElements.CreateButton(
                    removeHost, "Remove", width: ButtonWidth, height: 24);
                removeButton.IsEnabled = false;

                SceneNode valueNode = cellNode.NewChild();
                valueNode.SetTransform<UIBoundableTransform>().Width = 300;
                UITextComponent readOnlyValue = AddInfoLabel(valueNode, string.Empty);
                readOnlyValue.WrapMode = Rendering.FontGlyphSet.EWrapMode.None;
                readOnlyValue.ClipToBounds = true;
                readOnlyValue.SceneNode.IsActiveSelf = false;
                UIButtonComponent? nestedButton = null;
                if (needsNestedButton)
                {
                    (_, nestedButton, _, _) = NativeUIElements.CreateButton(
                        valueNode, "Open nested list", width: 180, height: 24);
                    nestedButton.IsEnabled = false;
                    nestedButton.SceneNode.IsActiveSelf = false;
                }
                EditorVirtualCollectionRowCellState state = new()
                {
                    Label = label,
                    RemoveHost = removeHost,
                    ValueNode = valueNode,
                    ReadOnlyValue = readOnlyValue,
                    ReadOnlyControl = EditorVirtualRowControlLease.Capture(readOnlyValue, valueNode),
                    RemoveButton = removeButton,
                    NestedButton = nestedButton,
                    NestedButtonControl = nestedButton is null ? null :
                        EditorVirtualRowControlLease.Capture(nestedButton, valueNode),
                };
                _virtualCells.Add(layout, state);
                if (!IsCurrentBuild(generation))
                    throw new InvalidOperationException("The collection detail was retired during row creation.");
                return layout;
            }

            private void BindVirtualCollectionCell(UIBoundableTransform cell, int index, ulong rowId,
                EditorVirtualRowContract contract, ulong generation, SceneNode branch)
            {
                if (!IsCurrentBuild(generation) ||
                    !_virtualCells.TryGetValue(cell, out EditorVirtualCollectionRowCellState? state))
                    return;
                if (cell.Parent is not UIBoundableTransform row || _virtualList is not { } list ||
                    !ReferenceEquals(row.Parent, list) || !list.TryGetVirtualRowLease(row, out UIVirtualRowLease lease) ||
                    lease.ItemId != rowId || list.VirtualProvider is not { } provider)
                    return;

                ulong intent = ++state.Intent;
                state.Index = index;
                state.RowId = rowId;
                state.Lease = lease;
                state.IsPublished = false;
                state.IsReadOnly = true;
                state.Bindings = null;
                ulong providerIncarnation = list.VirtualProviderIncarnation;
                ObjectConstructionIdentity cellIdentity = ObjectConstructionIdentity.Capture(cell);
                SceneNode cellNode = cell.SceneNode!;
                ObjectConstructionIdentity cellNodeIdentity = ObjectConstructionIdentity.Capture(cellNode);
                ObjectConstructionIdentity rowIdentity = ObjectConstructionIdentity.Capture(row);
                SceneNode rowNode = row.SceneNode!;
                ObjectConstructionIdentity rowNodeIdentity = ObjectConstructionIdentity.Capture(rowNode);
                ObjectConstructionIdentity listIdentity = ObjectConstructionIdentity.Capture(list);
                SceneNode listNode = list.SceneNode!;
                ObjectConstructionIdentity listNodeIdentity = ObjectConstructionIdentity.Capture(listNode);
                ObjectConstructionIdentity labelIdentity = ObjectConstructionIdentity.Capture(state.Label);
                SceneNode labelNode = state.Label.SceneNode!;
                ObjectConstructionIdentity labelNodeIdentity = ObjectConstructionIdentity.Capture(labelNode);
                ObjectConstructionIdentity removeHostIdentity = ObjectConstructionIdentity.Capture(state.RemoveHost);
                ObjectConstructionIdentity valueNodeIdentity = ObjectConstructionIdentity.Capture(state.ValueNode);
                UIButtonComponent removeButton = state.RemoveButton!;
                ObjectConstructionIdentity removeButtonIdentity = ObjectConstructionIdentity.Capture(removeButton);
                SceneNode removeButtonNode = removeButton.SceneNode;
                ObjectConstructionIdentity removeButtonNodeIdentity = ObjectConstructionIdentity.Capture(removeButtonNode);
                UIButtonComponent? nestedButton = state.NestedButton;
                EditorVirtualRowControlLease? nestedButtonControl = state.NestedButtonControl;
                bool NestedButtonCurrent() => ReferenceEquals(state.NestedButton, nestedButton) &&
                    ReferenceEquals(state.NestedButtonControl, nestedButtonControl) &&
                    (nestedButton is null
                        ? nestedButtonControl is null && !IsNestedListType(contract.ValueType)
                        : nestedButtonControl?.IsCurrent(state.ValueNode) == true);
                ObjectConstructionIdentity branchIdentity = ObjectConstructionIdentity.Capture(branch);
                ObjectConstructionIdentity containerIdentity = ObjectConstructionIdentity.Capture(_container);
                SceneNode? editorNode = state.EditorNode;
                if (editorNode is not null && (!state.ReusableEditor || state.EditorControls.Length == 0))
                    throw new InvalidOperationException("An incomplete fixed-height collection editor cannot be reused.");
                ObjectConstructionIdentity? editorIdentity = editorNode is null ? null :
                    ObjectConstructionIdentity.Capture(editorNode);

                bool RowCurrent()
                {
                    if (!IsCurrentBuild(generation) || state.Intent != intent || state.Index != index ||
                        state.RowId != rowId || state.Lease != lease ||
                        !_virtualCells.TryGetValue(cell, out EditorVirtualCollectionRowCellState? current) ||
                        !ReferenceEquals(current, state) || !cellIdentity.IsCurrent ||
                        !IsLive(cellNode, cellNodeIdentity) || !IsLive(row, rowIdentity) ||
                        !IsLive(rowNode, rowNodeIdentity) || !IsLive(list, listIdentity) ||
                        !IsLive(listNode, listNodeIdentity) || !IsLive(state.Label, labelIdentity) ||
                        !IsLive(labelNode, labelNodeIdentity) || !IsLive(state.RemoveHost, removeHostIdentity) ||
                        !IsLive(state.ValueNode, valueNodeIdentity) ||
                        !state.ReadOnlyControl.IsCurrent(state.ValueNode) ||
                        !NestedButtonCurrent() ||
                        !removeButtonIdentity.IsCurrent || !removeButtonNodeIdentity.IsCurrent ||
                        !IsLive(removeButtonNode, removeButtonNodeIdentity) ||
                        !IsLive(branch, branchIdentity) || !IsLive(_container, containerIdentity) ||
                        !ReferenceEquals(state.RemoveButton, removeButton) ||
                        !ReferenceEquals(state.NestedButton, nestedButton) ||
                        cell.IsDestroyed || cell.IsDestroyQueued || row.IsDestroyed || row.IsDestroyQueued ||
                        list.IsDestroyed || list.IsDestroyQueued || state.Label.IsDestroyed ||
                        state.Label.IsDestroyQueued || removeButton.IsDestroyed || removeButton.IsDestroyQueued ||
                        !ReferenceEquals(state.Label.SceneNode, labelNode) ||
                        !ReferenceEquals(labelNode.Transform.Parent, cellNode.Transform) ||
                        !ReferenceEquals(state.RemoveHost.Parent, cell.SceneNode) ||
                        !ReferenceEquals(removeButtonNode.Transform.Parent, state.RemoveHost.Transform) ||
                        !ReferenceEquals(state.ValueNode.Parent, cell.SceneNode) ||
                        !ReferenceEquals(cell.Parent, row) ||
                        !ReferenceEquals(row.Parent, list) || !ReferenceEquals(list.VirtualProvider, provider) ||
                        list.VirtualProviderIncarnation != providerIncarnation ||
                        !list.TryGetVirtualRowLease(row, out UIVirtualRowLease currentLease) ||
                        currentLease != lease || !ReferenceEquals(list.Parent?.Parent, branch.Transform) ||
                        !ReferenceEquals(branch.Parent, _container))
                        return false;
                    if (editorNode is null) return true;
                    return editorIdentity is { } currentEditorIdentity &&
                        IsLive(editorNode, currentEditorIdentity) && ReferenceEquals(state.EditorNode, editorNode) &&
                        ReferenceEquals(editorNode.Parent, state.ValueNode) &&
                        state.EditorControls.All(control => control.IsCurrent(editorNode));
                }

                bool WriteCurrent() => state.IsPublished && !state.IsReadOnly && RowCurrent();

                try
                {
                    if (!RowCurrent()) return;
                    state.Label.SetTextGuarded(contract.RowLabel(index), RowCurrent, static current =>
                    {
                        if (!current()) throw new InvalidOperationException("The collection row changed during binding.");
                    });
                    if (!RowCurrent()) return;

                    if (state.RemoveAction is { } oldAction)
                    {
                        removeButton.UnregisterClickActions(oldAction);
                        state.RemoveAction = null;
                    }
                    if (state.NestedAction is { } oldNestedAction && nestedButton is not null)
                    {
                        nestedButton.UnregisterClickActions(oldNestedAction);
                        state.NestedAction = null;
                    }
                    if (!RowCurrent()) return;
                    removeButton.IsEnabled = false;
                    if (!RowCurrent()) return;
                    removeButton.SceneNode.IsActiveSelf = false;
                    if (!RowCurrent()) return;
                    if (nestedButton is not null)
                    {
                        nestedButton.IsEnabled = false;
                        if (!RowCurrent()) return;
                        nestedButton.SceneNode.IsActiveSelf = false;
                        if (!RowCurrent()) return;
                    }

                    state.ReadOnlyValue.SceneNode.IsActiveSelf = false;
                    if (!RowCurrent()) return;
                    if (state.ReadOnlyValue.SceneNode.IsActiveSelf || removeButton.SceneNode.IsActiveSelf)
                        throw new InvalidOperationException("The collection row rejected its presentation reset.");

                    string? readOnlyText = contract.ReadOnlyText?.Invoke(index);
                    if (!RowCurrent()) return;
                    if (readOnlyText is not null)
                    {
                        if (editorNode is not null)
                        {
                            ResetFixedValueEditor(editorNode, RowCurrent);
                            if (!RowCurrent()) return;
                            editorNode.IsActiveSelf = false;
                            if (!RowCurrent()) return;
                            if (editorNode.IsActiveSelf)
                                throw new InvalidOperationException("The collection row rejected its read-only presentation.");
                        }
                        state.ReadOnlyValue.SetTextGuarded(readOnlyText, RowCurrent, static current =>
                        {
                            if (!current()) throw new InvalidOperationException("The read-only collection row changed during binding.");
                        });
                        if (!RowCurrent()) return;
                        state.ReadOnlyValue.SceneNode.IsActiveSelf = true;
                        if (!RowCurrent()) return;
                        state.IsPublished = state.ReadOnlyValue.SceneNode.IsActiveSelf;
                        return;
                    }
                    state.IsReadOnly = false;

                    if (editorNode is not null && (!state.ReusableEditor || state.EditorControls.Length == 0))
                        throw new InvalidOperationException("An incomplete fixed-height collection editor cannot be reused.");
                    object[] targets = contract.CreateBindings(index, RowCurrent, WriteCurrent);
                    if (!RowCurrent()) return;
                    if (targets.Length == 0) return;
                    state.Bindings = targets;

                    if (IsNestedListType(contract.ValueType))
                    {
                        bool canOpenNested = CanCreateNestedList(contract.ValueType, targets);
                        IList? nestedList = GetNestedListValue(targets[0]);
                        state.ReadOnlyValue.SetTextGuarded(canOpenNested
                                ? $"Nested list ({nestedList?.Count ?? 0} items)"
                                : "Nested list details are unavailable for these values.",
                            RowCurrent, static current =>
                            {
                                if (!current()) throw new InvalidOperationException("The nested list row changed during binding.");
                            });
                        if (!RowCurrent()) return;
                        state.ReadOnlyValue.SceneNode.IsActiveSelf = true;
                        if (!RowCurrent()) return;
                        if (canOpenNested && nestedButton is not null)
                        {
                            Action<UIInteractableComponent> openAction = _ =>
                            {
                                if (WriteCurrent())
                                    OpenNestedListDetail(contract.ValueType, contract.BindingProperty,
                                        targets, WriteCurrent, generation);
                            };
                            state.NestedAction = openAction;
                            nestedButton.RegisterClickActions(openAction);
                            if (!RowCurrent()) return;
                            nestedButton.IsEnabled = true;
                            if (!RowCurrent()) return;
                            nestedButton.SceneNode.IsActiveSelf = true;
                            if (!RowCurrent()) return;
                        }
                        Action<UIInteractableComponent> removeNested = _ =>
                        {
                            if (WriteCurrent()) contract.Remove(index, WriteCurrent);
                        };
                        state.RemoveAction = removeNested;
                        removeButton.RegisterClickActions(removeNested);
                        if (!RowCurrent()) return;
                        removeButton.IsEnabled = true;
                        if (!RowCurrent()) return;
                        removeButton.SceneNode.IsActiveSelf = true;
                        if (!RowCurrent()) return;
                        state.IsReadOnly = false;
                        state.IsPublished = true;
                        return;
                    }

                    if (editorNode is null)
                    {
                        SceneNode created = state.ValueNode.NewChild();
                        ObjectConstructionIdentity createdIdentity = ObjectConstructionIdentity.Capture(created);
                        if (!RowCurrent() || !IsLive(created, createdIdentity) ||
                            !ReferenceEquals(created.Parent, state.ValueNode)) return;
                        editorNode = state.EditorNode = created;
                        editorIdentity = createdIdentity;
                        if (!RowCurrent()) return;
                    }
                    if (editorNode.Transform is not UIBoundableTransform editorBounds)
                    {
                        if (!RowCurrent()) return;
                        editorBounds = editorNode.SetTransform<UIBoundableTransform>();
                        if (!RowCurrent()) return;
                    }
                    editorBounds.MaxAnchor = Vector2.One;
                    if (!RowCurrent()) return;
                    bool needsBuild = editorNode.Components.Count == 0 && editorNode.Transform.Children.Count == 0;
                    if (needsBuild)
                    {
                        BuildValueEditor(editorNode, contract.ValueType, contract.BindingProperty, targets);
                        if (!RowCurrent()) return;
                        state.EditorControls = CaptureFixedEditorControls(editorNode, contract.ValueType);
                        if (!RowCurrent()) return;
                        state.ReusableEditor = true;
                    }
                    else if (!TryRebindListEditor(editorNode, contract.ValueType, contract.BindingProperty,
                        targets.Cast<object?>().ToArray()))
                    {
                        throw new InvalidOperationException("The collection row editor cannot be rebound.");
                    }
                    if (!RowCurrent()) return;
                    editorNode.IsActiveSelf = true;
                    if (!RowCurrent()) return;
                    Action<UIInteractableComponent> removeAction = _ =>
                    {
                        if (WriteCurrent()) contract.Remove(index, WriteCurrent);
                    };
                    state.RemoveAction = removeAction;
                    removeButton.RegisterClickActions(removeAction);
                    if (!RowCurrent()) return;
                    removeButton.IsEnabled = true;
                    if (!RowCurrent()) return;
                    removeButton.SceneNode.IsActiveSelf = true;
                    if (!RowCurrent()) return;
                    state.IsPublished = true;
                    if (!WriteCurrent()) state.IsPublished = false;
                }
                catch
                {
                    state.IsPublished = false;
                    throw;
                }
            }

            private static EditorLinkedSourceLease[] CreateLinkedRowSources(
                EditorLinkedSourceLease[] sources, Func<bool> rowIsCurrent)
            {
                EditorLinkedSourceLease[] rows = new EditorLinkedSourceLease[sources.Length];
                for (int index = 0; index < sources.Length; index++)
                {
                    EditorLinkedSourceLease source = sources[index];
                    rows[index] = new()
                    {
                        Owner = source.Owner,
                        Property = source.Property,
                        List = source.List,
                        Adapter = source.Adapter,
                        Accessor = source.Accessor,
                        Displayed = source.Displayed,
                        State = source.State,
                        World = source.World,
                        Document = source.Document,
                        SceneNode = source.SceneNode,
                        IsViewCurrent = rowIsCurrent,
                    };
                }
                return rows;
            }

            private static EditorVirtualRowControlLease[] CaptureFixedEditorControls(SceneNode editor, Type elementType)
            {
                List<EditorVirtualRowControlLease> controls = [];
                void Visit(SceneNode node)
                {
                    foreach (XRComponent component in node.Components)
                        if (component is UIInteractableComponent or DataTransformerBase or InspectorEnumEditorComponent)
                            controls.Add(EditorVirtualRowControlLease.Capture(component, editor));
                    foreach (var child in node.Transform.Children)
                        if (child.SceneNode is { } childNode)
                            Visit(childNode);
                }
                Visit(editor);
                int expected = elementType == typeof(Vector2) ? 3 :
                    elementType == typeof(Vector3) || elementType == typeof(Quaternion) ? 4 :
                    elementType == typeof(Vector4) ? 5 :
                    elementType == typeof(LayerMask) || elementType.IsEnum &&
                        !elementType.IsDefined(typeof(FlagsAttribute), inherit: false) ? 2 : 1;
                if (controls.Count != expected)
                    throw new InvalidOperationException("The fixed-height collection editor lost a control during creation.");
                return controls.ToArray();
            }

            private static bool IsLive(XRObjectBase value, ObjectConstructionIdentity identity)
                => identity.IsCurrent && !value.IsDestroyed && !value.IsDestroyQueued;

            private void UnbindVirtualCollectionCell(UIBoundableTransform cell)
            {
                if (!_virtualCells.TryGetValue(cell, out EditorVirtualCollectionRowCellState? state)) return;
                UIBoundableTransform? row = cell.Parent as UIBoundableTransform;
                UIListTransform? list = row?.Parent as UIListTransform;
                SceneNode? cellNode = cell.SceneNode;
                SceneNode? rowNode = row?.SceneNode;
                SceneNode? editor = state.EditorNode;
                UIButtonComponent? button = state.RemoveButton;
                UIButtonComponent? nestedButton = state.NestedButton;
                ObjectConstructionIdentity cellIdentity = ObjectConstructionIdentity.Capture(cell);
                ObjectConstructionIdentity? rowIdentity = row is null ? null : ObjectConstructionIdentity.Capture(row);
                ObjectConstructionIdentity? rowNodeIdentity = rowNode is null ? null : ObjectConstructionIdentity.Capture(rowNode);
                ObjectConstructionIdentity? listIdentity = list is null ? null : ObjectConstructionIdentity.Capture(list);
                ObjectConstructionIdentity? editorIdentity = editor is null ? null : ObjectConstructionIdentity.Capture(editor);
                ObjectConstructionIdentity? buttonIdentity = button is null ? null : ObjectConstructionIdentity.Capture(button);
                ObjectConstructionIdentity? buttonNodeIdentity = button is null ? null : ObjectConstructionIdentity.Capture(button.SceneNode);
                ObjectConstructionIdentity? nestedButtonIdentity = nestedButton is null ? null : ObjectConstructionIdentity.Capture(nestedButton);
                ObjectConstructionIdentity? nestedButtonNodeIdentity = nestedButton is null ? null : ObjectConstructionIdentity.Capture(nestedButton.SceneNode);
                state.Intent++;
                ulong cleanupIntent = state.Intent;
                state.IsPublished = false;
                state.IsReadOnly = true;
                state.Index = -1;
                state.RowId = 0;
                state.Lease = default;
                state.Bindings = null;
                bool CleanupCurrent() => state.Intent == cleanupIntent &&
                    _virtualCells.TryGetValue(cell, out EditorVirtualCollectionRowCellState? current) &&
                    ReferenceEquals(current, state) && cellIdentity.IsCurrent &&
                    state.ReadOnlyControl.IsCurrent(state.ValueNode) &&
                    ReferenceEquals(state.ValueNode.Parent, cellNode) &&
                    !cell.IsDestroyed && !cell.IsDestroyQueued && ReferenceEquals(cell.SceneNode, cellNode) &&
                    (row is not null && list is not null && rowIdentity?.IsCurrent == true && rowNodeIdentity?.IsCurrent == true &&
                        !row.IsDestroyed && !row.IsDestroyQueued && ReferenceEquals(cell.Parent, row) &&
                        ReferenceEquals(row.SceneNode, rowNode) && ReferenceEquals(row.Parent, list) &&
                        ReferenceEquals(_virtualList, list) && listIdentity?.IsCurrent == true) &&
                    (editor is null || editorIdentity?.IsCurrent == true && ReferenceEquals(state.EditorNode, editor) &&
                        ReferenceEquals(editor.Parent, state.ValueNode) && ReferenceEquals(state.ValueNode.Parent, cellNode)) &&
                    (editor is null || state.EditorControls.All(control => control.IsCurrent(editor))) &&
                    (button is null || buttonIdentity?.IsCurrent == true && buttonNodeIdentity?.IsCurrent == true &&
                        ReferenceEquals(state.RemoveButton, button) &&
                        ReferenceEquals(button.SceneNode.Transform.Parent, state.RemoveHost.Transform) &&
                        ReferenceEquals(state.RemoveHost.Parent, cellNode)) &&
                    (nestedButton is null || nestedButtonIdentity?.IsCurrent == true &&
                        nestedButtonNodeIdentity?.IsCurrent == true && state.NestedButtonControl!.IsCurrent(state.ValueNode) &&
                        ReferenceEquals(state.NestedButton, nestedButton) &&
                        ReferenceEquals(nestedButton.SceneNode.Transform.Parent, state.ValueNode.Transform));
                if (button is not null)
                {
                    Action<UIInteractableComponent>? action = state.RemoveAction;
                    state.RemoveAction = null;
                    if (action is not null)
                        button.UnregisterClickActions(action);
                    if (!CleanupCurrent()) return;
                    button.IsEnabled = false;
                    if (!CleanupCurrent()) return;
                    button.SceneNode.IsActiveSelf = false;
                    if (!CleanupCurrent()) return;
                }
                if (nestedButton is not null)
                {
                    Action<UIInteractableComponent>? action = state.NestedAction;
                    state.NestedAction = null;
                    if (action is not null) nestedButton.UnregisterClickActions(action);
                    if (!CleanupCurrent()) return;
                    nestedButton.IsEnabled = false;
                    if (!CleanupCurrent()) return;
                    nestedButton.SceneNode.IsActiveSelf = false;
                    if (!CleanupCurrent()) return;
                }
                if (editor is not null)
                {
                    ResetFixedValueEditor(editor, CleanupCurrent);
                    if (!CleanupCurrent()) return;
                    editor.IsActiveSelf = false;
                    if (!CleanupCurrent()) return;
                }
                if (CleanupCurrent()) state.ReadOnlyValue.SceneNode.IsActiveSelf = false;
            }

            private static void ResetFixedValueEditor(SceneNode editor, Func<bool> isCurrent)
            {
                if (editor.Components.OfType<DataTransformerBase>().FirstOrDefault() is { } transformer)
                {
                    transformer.Property = null;
                    if (!isCurrent()) return;
                    transformer.Targets = null;
                    return;
                }
                if (editor.Components.OfType<UIColorSelectorComponent>().FirstOrDefault() is { } color)
                {
                    color.Binding = null;
                    return;
                }
                ResetFixedValueEditorNode(editor, isCurrent);
            }

            private static void ResetFixedValueEditorNode(SceneNode node, Func<bool> isCurrent)
            {
                if (!isCurrent()) return;
                foreach (XRComponent component in node.Components.ToArray())
                {
                    switch (component)
                    {
                        case InspectorEnumEditorComponent enumEditor:
                            enumEditor.Bind(null, null);
                            break;
                        case UIToggleComponent toggle:
                            toggle.Binding = null;
                            if (!isCurrent()) return;
                            toggle.Property = null;
                            if (!isCurrent()) return;
                            toggle.Targets = null;
                            if (!isCurrent()) return;
                            toggle.IsReadOnly = true;
                            break;
                        case UITextInputComponent text:
                            text.Binding = null;
                            if (!isCurrent()) return;
                            text.Property = null;
                            if (!isCurrent()) return;
                            text.Targets = null;
                            if (!isCurrent()) return;
                            text.IsReadOnly = true;
                            break;
                        case UIPropertyTextDriverComponent driver:
                            driver.Property = null;
                            if (!isCurrent()) return;
                            driver.Sources = null;
                            if (!isCurrent()) return;
                            driver.IsActive = false;
                            break;
                    }
                    if (!isCurrent()) return;
                }
                foreach (var child in node.Transform.Children.ToArray())
                {
                    if (!isCurrent() || child.SceneNode is not { } childNode) return;
                    ResetFixedValueEditorNode(childNode, isCurrent);
                }
            }

            /// <summary>
            /// Checks that the virtual row host can show values of this type. A row uses either a
            /// known fixed-height editor or a custom editor that measures its own row height.
            /// </summary>
            private static bool CanUseVirtualCollectionEditor(Type valueType)
            {
                if (valueType.GetCustomAttribute<EditorComponentAttribute>() is { } custom)
                    return custom is IEditorMeasuredRowEditor;
                if (Nullable.GetUnderlyingType(valueType) is not null)
                    return false;
                if (typeof(IList).IsAssignableFrom(valueType))
                {
                    return IsNestedRowValueType(ResolveElementType(valueType, valueType), MaxNestedListDepth);
                }
                return CanRebindListEditor(valueType);
            }
        }
    }
}
