using System.Collections;
using System.Numerics;
using System.Reflection;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public static partial class CollectionTypes
    {
        private sealed partial class CollectionEditorContext
        {
            private readonly EditorNestedListSourceLease[]? _nestedSources =
                CaptureNestedSources(objects);
            private EditorNestedListMutationContext? _nestedMutationContext;
            private EditorNestedListMutationContext? NestedMutationContext
                => _nestedSources is null ? null : _nestedMutationContext ??=
                    new EditorNestedListMutationContext(_nestedSources, _listContext);
            private EditorNestedListPaneAttempt? _nestedDetailAttempt;
            private SceneNode? _nestedDetailRoot;
            private InspectorListDetailRefreshComponent? _nestedDetailLifetime;
            private EditorNestedListSourceLease[] _nestedDetailPathProof = [];
            private ulong _nestedDetailVersion;

            private static bool IsNestedParentBinding(object? value)
                => value is IEditorListElementBinding or IEditorLinkedElementBinding or
                    IEditorDictionaryElementBinding;

            private static EditorNestedListSourceLease[]? CaptureNestedSources(object?[]? objects)
            {
                if (objects is not { Length: > 0 } || !objects.All(IsNestedParentBinding)) return null;
                EditorNestedListSourceLease[] paths = objects
                    .Select(binding => EditorNestedListSourceLease.Capture(binding!)).ToArray();
                foreach (EditorNestedListSourceLease path in paths) path.PromoteToDetail();
                return paths;
            }

            public static bool CanCreateNestedList(Type collectionType, object?[]? bindings)
            {
                if (bindings is not { Length: > 0 } || !bindings.All(IsNestedParentBinding) ||
                    !typeof(IList).IsAssignableFrom(collectionType) ||
                    bindings.Any(binding => binding switch
                    {
                        IEditorNestedListElementBinding nested =>
                            nested.Owner is XRWorldObjectBase && nested.OwnerDocument is null,
                        IEditorListElementBinding list =>
                            list.Owner is XRWorldObjectBase && list.OwnerDocument is null ||
                            !EditorListMutation.SupportsSource(list.OwnerProperty),
                        IEditorLinkedElementBinding linked =>
                            linked.Source.Owner is XRWorldObjectBase && linked.Source.Document is null,
                        IEditorDictionaryElementBinding dictionary =>
                            dictionary.Source.Owner is XRWorldObjectBase && dictionary.Source.Document is null,
                        _ => true
                    }))
                    return false;
                IList?[] innerLists = bindings.Select(binding =>
                    binding is null ? null : GetNestedListValue(binding)).ToArray();
                if (innerLists.Length == 0 || innerLists.Any(list => list is null) ||
                    innerLists.Any(list => !EditorListMutation.Supports(list!, EEditorListMutation.Set)) ||
                    innerLists.Any(list => list!.Count != innerLists[0]!.Count) ||
                    bindings.Where((binding, index) => binding is IEditorListElementBinding outer &&
                        ReferenceEquals(outer.Collection, innerLists[index])).Any())
                    return false;
                Type elementType = ResolveElementType(collectionType, innerLists[0]!.GetType());
                if (!IsNestedRowValueType(elementType, MaxNestedListDepth))
                    return false;
                // Nested rows have a fixed height, so a multiline string cannot use them.
                return elementType != typeof(string) || innerLists.All(list => AllSingleLine(list!));
            }

            /// <summary>
            /// The maximum number of list levels below a nested row. A self-referential list type
            /// cannot make the type check recurse without end.
            /// </summary>
            private const int MaxNestedListDepth = 8;

            /// <summary>
            /// Checks that a nested row can show a value of this type with a fixed-height editor or a
            /// custom editor that measures its own rows. A list value is accepted when its own
            /// elements are accepted, so a nested row can open a deeper nested detail.
            /// </summary>
            private static bool IsNestedRowValueType(Type type, int depth)
            {
                if (MeasuredRowEditorFor(type) is not null)
                    return true;
                if (type.IsEnum)
                    return !type.IsDefined(typeof(FlagsAttribute), inherit: false);
                if (type == typeof(string) || IsNestedVectorType(type))
                    return true;
                if (IsNestedListType(type))
                    return depth > 0 && IsNestedRowValueType(ResolveElementType(type, type), depth - 1);
                return Type.GetTypeCode(type) is TypeCode.Boolean or TypeCode.Byte or TypeCode.SByte or
                    TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or
                    TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or
                    TypeCode.Decimal;
            }

            private static bool IsNestedVectorType(Type type)
                => type == typeof(Vector2) || type == typeof(Vector3) ||
                    type == typeof(Vector4) || type == typeof(Quaternion);

            private static bool AllSingleLine(IList list)
            {
                for (int index = 0; index < list.Count; index++)
                    if (list[index] is string text && text.AsSpan().IndexOfAny(LineBreaks) >= 0)
                        return false;
                return true;
            }

            private static readonly System.Buffers.SearchValues<char> LineBreaks =
                System.Buffers.SearchValues.Create("\r\n\u0085\u2028\u2029");

            private bool CanSetNestedIndex(int index)
                => _nestedSources is { Length: > 0 } paths &&
                    paths.All(path => path.IsCommandCurrent() &&
                        EditorListMutation.Supports(path.Inner, EEditorListMutation.Set) &&
                        index >= 0 && index < path.Inner.Count);

            private bool CanResizeNestedLists(EEditorListMutation operation)
                => _nestedSources is { Length: > 0 } paths &&
                    paths.All(path => path.IsCommandCurrent() &&
                        EditorListMutation.Supports(path.Inner, operation)) &&
                    paths.All(path => path.Inner.Count == paths[0].Inner.Count);

            private void AddNestedListElement(Type elementType)
            {
                if (_nestedSources is not { } paths || NestedMutationContext is not { IsActive: true } context ||
                    !CanResizeNestedLists(EEditorListMutation.Insert)) return;
                try
                {
                    ulong generation = _headerGeneration;
                    bool changed = EditorNestedListMutation.Apply(paths, EEditorListMutation.Insert, -1,
                        _ => EditorListMutation.CreateDefaultElement(elementType), context,
                        () => IsCurrentBuild(generation) && CanResizeNestedLists(EEditorListMutation.Insert));
                    if (changed && _virtualList is null && IsCurrentBuild(generation) &&
                        paths.All(path => path.IsOwnerPathCurrent()))
                        Rebuild();
                }
                catch (Exception error) { Debug.LogException(error, "Could not add a nested list item."); }
            }

            private void RemoveNestedListElements(IReadOnlyList<IEditorNestedListElementBinding> bindings)
            {
                if (_nestedSources is not { } paths || NestedMutationContext is not { IsActive: true } context ||
                    bindings.Count == 0) return;
                int index = bindings[0].Index;
                try
                {
                    EditorNestedListMutation.Apply(paths, EEditorListMutation.Remove, index, null,
                        context, () => IsCurrentBuild(_headerGeneration) && bindings.All(binding => binding.IsRowCurrent));
                }
                catch (Exception error) { Debug.LogException(error, "Could not remove a nested list item."); }
            }

            private object[] CreateNestedListBindings(int index, ulong rowId, Func<bool> rowCurrent)
            {
                if (_nestedSources is not { } paths || NestedMutationContext is not { } nested ||
                    !_listContext.IsActive || !CanSetNestedIndex(index)) return [];
                Type bindingType = typeof(EditorNestedListElementBinding<>).MakeGenericType(_listElementType!);
                object[] result = new object[paths.Length];
                for (int target = 0; target < result.Length; target++)
                    result[target] = Activator.CreateInstance(bindingType,
                        paths, nested, _listContext, index, rowId, target, rowCurrent)!;
                return result;
            }

            private static bool IsNestedListType(Type type)
                => typeof(IList).IsAssignableFrom(type);

            private static IList? GetNestedListValue(object binding)
                => binding switch
                {
                    IEditorListElementBinding list => list.DisplayedValue as IList,
                    IEditorLinkedElementBinding linked => linked.DisplayedValue as IList,
                    IEditorDictionaryElementBinding dictionary => dictionary.DisplayedValue as IList,
                    _ => null
                };

            private void OpenNestedListDetail(Type collectionType, PropertyInfo valueProperty,
                object[] targets, Func<bool> rowCurrent, ulong generation)
            {
                EditorNestedListSourceLease[] pathProof;
                if (!IsCurrentBuild(generation) || !rowCurrent() ||
                    !CanCreateNestedList(collectionType, targets.Cast<object?>().ToArray()))
                    return;
                try
                {
                    pathProof = new EditorNestedListSourceLease[targets.Length];
                    for (int index = 0; index < targets.Length; index++)
                    {
                        if (!IsCurrentBuild(generation) || !rowCurrent()) return;
                        pathProof[index] = EditorNestedListSourceLease.Capture(targets[index]);
                    }
                    foreach (EditorNestedListSourceLease path in pathProof) path.PromoteToDetail();
                }
                catch (Exception error)
                {
                    Debug.LogException(error, "Could not capture nested collection ownership.");
                    return;
                }

                ulong version = ++_nestedDetailVersion;
                RetireNestedListDetail(invalidatePendingAttempt: false);
                if (!OpenParentCurrent(generation, rowCurrent, pathProof) ||
                    version != _nestedDetailVersion || _nestedDetailAttempt is not null)
                    return;

                SceneNode container = _container;
                ObjectConstructionIdentity containerIdentity = ObjectConstructionIdentity.Capture(container);
                TransformBase containerTransform = container.Transform;
                ObjectConstructionIdentity containerTransformIdentity = ObjectConstructionIdentity.Capture(containerTransform);
                if (!OpenParentCurrent(generation, rowCurrent, pathProof)) return;
                SceneNode root = new("NestedListDetail");
                EditorNestedListPaneAttempt attempt = new(root, container, containerIdentity,
                    containerTransform, containerTransformIdentity, RetainFailedPaneLeafRetirement);
                UIListTransform layout = root.SetTransform<UIListTransform>();
                bool admittedLayout = attempt.SetUnattachedLayout(layout);
                if (!admittedLayout || !OpenParentCurrent(generation, rowCurrent, pathProof))
                {
                    attempt.DestroyRootIfOwned();
                    return;
                }
                if (!OpenParentCurrent(generation, rowCurrent, pathProof) ||
                    version != _nestedDetailVersion || !attempt.IsUnattachedRootOwned)
                {
                    attempt.DestroyRootIfOwned();
                    return;
                }
                _nestedDetailAttempt = attempt;
                _nestedDetailRoot = root;
                _nestedDetailPathProof = pathProof;

                bool published = false;
                SceneNode? detail = null;
                bool PublishedCurrent() => detail is not null &&
                    version == _nestedDetailVersion &&
                    ReferenceEquals(_nestedDetailAttempt, attempt) && attempt.IsCurrent &&
                    attempt.OwnsDetail(detail) && attempt.Lifetime is not null &&
                    _listContext.IsActive && pathProof.All(path => path.IsOwnerPathCurrent());
                bool AttemptCurrent() => published
                    ? PublishedCurrent()
                    : version == _nestedDetailVersion &&
                        ReferenceEquals(_nestedDetailAttempt, attempt) && attempt.IsCurrent &&
                        OpenParentCurrent(generation, rowCurrent, pathProof);
                bool AttemptBaseCurrent() => version == _nestedDetailVersion &&
                    ReferenceEquals(_nestedDetailAttempt, attempt) && attempt.IsDirectChildOwned &&
                    OpenParentCurrent(generation, rowCurrent, pathProof);
                bool AttemptConstructionCurrent() => version == _nestedDetailVersion &&
                    ReferenceEquals(_nestedDetailAttempt, attempt) && attempt.IsCurrent &&
                    OpenParentCurrent(generation, rowCurrent, pathProof);

                if (version != _nestedDetailVersion || !ReferenceEquals(_nestedDetailAttempt, attempt) ||
                    !attempt.IsUnattachedRootOwned || !OpenParentCurrent(generation, rowCurrent, pathProof))
                { RetireNestedListAttempt(attempt, version); return; }
                layout.SetParent(containerTransform, false, EParentAssignmentMode.Immediate);
                if (!AttemptBaseCurrent() || !ReferenceEquals(root.Transform, layout) ||
                    attempt.SetLayout(layout) is null)
                { RetireNestedListAttempt(attempt, version); return; }
                layout.DisplayHorizontal = false;
                if (!AttemptCurrent()) { RetireNestedListAttempt(attempt, version); return; }
                layout.ItemSpacing = 6.0f;
                if (!AttemptCurrent()) { RetireNestedListAttempt(attempt, version); return; }
                layout.ItemAlignment = EListAlignment.TopOrLeft;
                if (!AttemptCurrent()) { RetireNestedListAttempt(attempt, version); return; }
                layout.Padding = new Vector4(0.0f);
                if (!AttemptCurrent()) { RetireNestedListAttempt(attempt, version); return; }
                if (AddNestedAttemptLabel(root, "Nested list detail", AttemptCurrent) is null)
                { RetireNestedListAttempt(attempt, version); return; }

                if (CreateNestedAttemptButton(root, "Close nested list",
                    () => RetireNestedListAttempt(attempt, version), AttemptCurrent) is null)
                { RetireNestedListAttempt(attempt, version); return; }

                SceneNode createdDetail = root.NewChild();
                detail = createdDetail;
                if (!AttemptCurrent())
                { RetireNestedListAttempt(attempt, version); return; }
                UIListTransform innerLayout = createdDetail.SetTransform<UIListTransform>();
                if (!AttemptCurrent() || !ReferenceEquals(createdDetail.Transform, innerLayout) ||
                    !attempt.SetDetail(createdDetail))
                { RetireNestedListAttempt(attempt, version); return; }
                bool InnerOwnerCurrent() => published
                    ? PublishedCurrent()
                    : AttemptConstructionCurrent() && attempt.OwnsDetail(createdDetail);

                if (!TryCreateEditorCore(collectionType, InnerOwnerCurrent,
                    out Action<SceneNode, PropertyInfo, object?[]?>? editor,
                    RetainFailedComponentCreation) || editor is null ||
                    !InnerOwnerCurrent())
                { RetireNestedListAttempt(attempt, version); return; }
                editor(createdDetail, valueProperty, targets.Cast<object?>().ToArray());
                if (!AttemptConstructionCurrent() || !attempt.OwnsDetail(createdDetail))
                { RetireNestedListAttempt(attempt, version); return; }
                if (createdDetail.GetComponent<InspectorListDetailRefreshComponent>() is { } detailLifetime &&
                    attempt.SetLifetime(detailLifetime))
                {
                    _nestedDetailLifetime = detailLifetime;
                    published = true;
                    if (!InnerOwnerCurrent()) RetireNestedListAttempt(attempt, version);
                }
                else
                    RetireNestedListAttempt(attempt, version);
            }

            private bool OpenParentCurrent(ulong generation, Func<bool> rowCurrent,
                EditorNestedListSourceLease[] pathProof)
                => IsCurrentBuild(generation) && rowCurrent() &&
                    pathProof.All(path => path.IsOwnerPathCurrent());

            public static UITextComponent? AddNestedAttemptLabel(SceneNode parent, string text,
                Func<bool> isCurrent)
            {
                if (!isCurrent()) return null;
                SceneNode? labelNode = CreateNestedAttemptChild<UITextComponent>(parent, isCurrent,
                    out UITextComponent? label);
                if (labelNode is null || label is null || !isCurrent())
                    return null;
                ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(labelNode);
                ObjectConstructionIdentity labelIdentity = ObjectConstructionIdentity.Capture(label);
                bool LeafCurrent() => isCurrent() && nodeIdentity.IsCurrent && labelIdentity.IsCurrent &&
                    !labelNode.IsDestroyed && !labelNode.IsDestroyQueued &&
                    !label.IsDestroyed && !label.IsDestroyQueued &&
                    ReferenceEquals(label.SceneNode, labelNode) &&
                    ReferenceEquals(labelNode.Transform.Parent, parent.Transform);
                if (!LeafCurrent()) return null;
                try
                {
                    label.SetTextGuarded(text, (Func<bool>)LeafCurrent, static current =>
                    {
                        if (!current())
                            throw new InvalidOperationException("The nested detail label owner changed during assignment.");
                    });
                }
                catch (InvalidOperationException) when (!LeafCurrent())
                {
                    return null;
                }
                if (!LeafCurrent()) return null;
                label.FontSize = EditorUI.Styles.PropertyInputFontSize ?? 14.0f;
                if (!LeafCurrent()) return null;
                label.Color = EditorUI.Styles.PropertyInputTextColor;
                if (!LeafCurrent()) return null;
                label.HorizontalAlignment = EHorizontalAlignment.Left;
                if (!LeafCurrent()) return null;
                label.VerticalAlignment = EVerticalAlignment.Center;
                if (!LeafCurrent()) return null;
                label.WrapMode = FontGlyphSet.EWrapMode.Word;
                if (!LeafCurrent()) return null;
                UIBoundableTransform? bounds = CreateNestedAttemptTransform<UIBoundableTransform>(labelNode, LeafCurrent);
                if (bounds is null || !LeafCurrent()) return null;
                ObjectConstructionIdentity boundsIdentity = ObjectConstructionIdentity.Capture(bounds);
                bool BoundsCurrent() => LeafCurrent() && boundsIdentity.IsCurrent &&
                    !bounds.IsDestroyed && !bounds.IsDestroyQueued && ReferenceEquals(labelNode.Transform, bounds);
                if (!BoundsCurrent()) return null;
                bounds.Margins = new Vector4(6.0f, 2.0f, 6.0f, 2.0f);
                return BoundsCurrent() ? label : null;
            }

            private bool SetNestedListCellLabel(UIBoundableTransform cell,
                EditorListRowCellState state, string text, Func<bool> isCurrent)
            {
                if (_ownerIsCurrent is null)
                {
                    state.Label.Text = text;
                    return isCurrent();
                }
                UITextComponent label = state.Label;
                if (string.Equals(label.Text, text, StringComparison.Ordinal))
                    return isCurrent();
                SceneNode? cellNode = cell.SceneNode;
                SceneNode? labelNode = label.SceneNode;
                if (cellNode is null || labelNode is null) return false;
                ObjectConstructionIdentity cellIdentity = ObjectConstructionIdentity.Capture(cell);
                ObjectConstructionIdentity cellNodeIdentity = ObjectConstructionIdentity.Capture(cellNode);
                ObjectConstructionIdentity labelIdentity = ObjectConstructionIdentity.Capture(label);
                ObjectConstructionIdentity labelNodeIdentity = ObjectConstructionIdentity.Capture(labelNode);
                TransformBase cellTransform = cellNode.Transform;
                ObjectConstructionIdentity cellTransformIdentity = ObjectConstructionIdentity.Capture(cellTransform);
                bool LeafCurrent() => isCurrent() && cellIdentity.IsCurrent && cellNodeIdentity.IsCurrent &&
                    labelIdentity.IsCurrent && labelNodeIdentity.IsCurrent && cellTransformIdentity.IsCurrent &&
                    !cellNode.IsDestroyed && !cellNode.IsDestroyQueued &&
                    !labelNode.IsDestroyed && !labelNode.IsDestroyQueued &&
                    !label.IsDestroyed && !label.IsDestroyQueued &&
                    ReferenceEquals(cellNode.Transform, cellTransform) &&
                    ReferenceEquals(label.SceneNode, labelNode) &&
                    ReferenceEquals(labelNode.Transform.Parent, cellTransform);
                if (!LeafCurrent()) return false;
                ulong intent = label.TextWriteIntentRevision;
                try
                {
                    label.SetTextGuarded(text, (Func<bool>)LeafCurrent, static current =>
                    {
                        if (!current()) throw new InvalidOperationException(
                            "The nested virtual row label owner changed during assignment.");
                    });
                    return LeafCurrent();
                }
                catch (InvalidOperationException) when (!LeafCurrent() ||
                    label.TextWriteIntentRevision != unchecked(intent + 1))
                {
                    return LeafCurrent();
                }
            }

            private static SceneNode? CreateNestedAttemptChild<TComponent>(SceneNode parent,
                Func<bool> isCurrent, out TComponent? component)
                where TComponent : XRComponent, new()
            {
                component = null;
                if (!isCurrent()) return null;
                ObjectConstructionIdentity parentIdentity = ObjectConstructionIdentity.Capture(parent);
                TransformBase parentTransform = parent.Transform;
                ObjectConstructionIdentity parentTransformIdentity = ObjectConstructionIdentity.Capture(parentTransform);
                SceneNode child = parent.NewChild();
                ObjectConstructionIdentity childIdentity = ObjectConstructionIdentity.Capture(child);
                bool ParentCurrent() => isCurrent() && parentIdentity.IsCurrent &&
                    parentTransformIdentity.IsCurrent && !parent.IsDestroyed && !parent.IsDestroyQueued &&
                    ReferenceEquals(parent.Transform, parentTransform) && !child.IsDestroyed && !child.IsDestroyQueued &&
                    ReferenceEquals(child.Transform.Parent, parentTransform);
                UIBoundableTransform? childBounds = CreateNestedAttemptTransform<UIBoundableTransform>(child,
                    ParentCurrent, replace: true);
                if (childBounds is null || !ParentCurrent()) return null;
                TransformBase childTransform = child.Transform;
                ObjectConstructionIdentity childTransformIdentity = ObjectConstructionIdentity.Capture(childTransform);
                bool ChildOwned() => ParentCurrent() &&
                    childIdentity.IsCurrent && childTransformIdentity.IsCurrent &&
                    !child.IsDestroyed && !child.IsDestroyQueued &&
                    ReferenceEquals(parent.Transform, parentTransform) &&
                    ReferenceEquals(child.Transform, childBounds) &&
                    ReferenceEquals(child.Transform, childTransform) &&
                    ReferenceEquals(childTransform.Parent, parentTransform);
                if (!ChildOwned())
                {
                    DestroyNestedAttemptChildIfOwned(child, ChildOwned);
                    return null;
                }
                RuntimeComponentCreationReceipt receipt = new(child, () => isCurrent() && ChildOwned());
                try
                {
                    component = child.AddComponentGuarded(static () => new TComponent(), receipt);
                }
                catch (Exception failure)
                {
                    receipt.RecordFailure(failure);
                    bool aborted = receipt.Component is null || receipt.TryAbortUnchangedComponent();
                    if (!aborted)
                        Debug.LogException(receipt.CleanupFailure is { } cleanup
                            ? new AggregateException(failure, cleanup) : failure,
                            "Could not abort a failed nested UI component creation.");
                    DestroyNestedAttemptChildIfOwned(child, ChildOwned);
                    if (failure is InvalidOperationException && (!isCurrent() || !ChildOwned())) return null;
                    throw;
                }
                if (component is null || !isCurrent() || !ChildOwned() ||
                    !ReferenceEquals(component.SceneNode, child) ||
                    component.IsDestroyed || component.IsDestroyQueued)
                {
                    if (receipt.Component is not null)
                    {
                        InvalidOperationException failure = new("The nested UI component owner changed after construction.");
                        receipt.RecordFailure(failure);
                        if (!receipt.TryAbortUnchangedComponent())
                            Debug.LogException(receipt.CleanupFailure is { } cleanup
                                ? new AggregateException(failure, cleanup) : failure,
                                "Could not abort a nested UI component after its owner changed.");
                    }
                    component = null;
                    DestroyNestedAttemptChildIfOwned(child, ChildOwned);
                    return null;
                }
                return child;
            }

            private static SceneNode? CreateNestedAttemptNode(SceneNode parent, Func<bool> isCurrent,
                Func<bool>? cleanupCurrent = null)
            {
                if (!isCurrent()) return null;
                ObjectConstructionIdentity parentIdentity = ObjectConstructionIdentity.Capture(parent);
                TransformBase parentTransform = parent.Transform;
                ObjectConstructionIdentity parentTransformIdentity = ObjectConstructionIdentity.Capture(parentTransform);
                SceneNode child = parent.NewChild();
                ObjectConstructionIdentity childIdentity = ObjectConstructionIdentity.Capture(child);
                TransformBase childTransform = child.Transform;
                ObjectConstructionIdentity childTransformIdentity = ObjectConstructionIdentity.Capture(childTransform);
                bool owned = parentIdentity.IsCurrent && parentTransformIdentity.IsCurrent &&
                    childIdentity.IsCurrent && childTransformIdentity.IsCurrent &&
                    !parent.IsDestroyed && !parent.IsDestroyQueued && !child.IsDestroyed && !child.IsDestroyQueued &&
                    ReferenceEquals(parent.Transform, parentTransform) && ReferenceEquals(child.Transform, childTransform) &&
                    ReferenceEquals(childTransform.Parent, parentTransform);
                if (!isCurrent() || !owned)
                {
                    if (cleanupCurrent?.Invoke() == true && owned)
                        try { child.Destroy(false); }
                        catch (Exception error) { Debug.LogException(error, "Could not retire a stale nested row child."); }
                    return null;
                }
                return child;
            }

            private static TTransform? CreateNestedAttemptTransform<TTransform>(SceneNode node,
                Func<bool> isCurrent, bool replace = false) where TTransform : TransformBase, new()
            {
                if (!isCurrent()) return null;
                ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(node);
                TransformBase priorTransform = node.Transform;
                ObjectConstructionIdentity priorTransformIdentity = ObjectConstructionIdentity.Capture(priorTransform);
                TransformBase? parentTransform = priorTransform.Parent;
                ObjectConstructionIdentity? parentIdentity = parentTransform is null
                    ? null : ObjectConstructionIdentity.Capture(parentTransform);
                TTransform? transform = replace ? node.SetTransform<TTransform>() : node.GetTransformAs<TTransform>(true);
                if (transform is null) return null;
                ObjectConstructionIdentity transformIdentity = ObjectConstructionIdentity.Capture(transform);
                return isCurrent() && nodeIdentity.IsCurrent && (replace || priorTransformIdentity.IsCurrent) &&
                    transformIdentity.IsCurrent && !node.IsDestroyed && !node.IsDestroyQueued &&
                    !transform.IsDestroyed && !transform.IsDestroyQueued && ReferenceEquals(node.Transform, transform) &&
                    (parentTransform is null
                        ? transform.Parent is null
                        : parentIdentity is { } identity && identity.IsCurrent && ReferenceEquals(transform.Parent, parentTransform))
                    ? transform : null;
            }

            private static SceneNode? CreateNestedAttemptHeader(SceneNode parent,
                Func<bool> isCurrent)
            {
                if (!isCurrent()) return null;
                ObjectConstructionIdentity parentIdentity = ObjectConstructionIdentity.Capture(parent);
                TransformBase parentTransform = parent.Transform;
                ObjectConstructionIdentity parentTransformIdentity = ObjectConstructionIdentity.Capture(parentTransform);
                SceneNode header = parent.NewChild();
                ObjectConstructionIdentity headerIdentity = ObjectConstructionIdentity.Capture(header);
                if (!isCurrent() || !parentIdentity.IsCurrent || !parentTransformIdentity.IsCurrent ||
                    !headerIdentity.IsCurrent || header.IsDestroyed || header.IsDestroyQueued ||
                    !ReferenceEquals(parent.Transform, parentTransform) ||
                    !ReferenceEquals(header.Transform.Parent, parentTransform))
                    return null;
                UIListTransform layout = header.SetTransform<UIListTransform>();
                ObjectConstructionIdentity layoutIdentity = ObjectConstructionIdentity.Capture(layout);
                bool Current() => isCurrent() && parentIdentity.IsCurrent &&
                    parentTransformIdentity.IsCurrent && headerIdentity.IsCurrent &&
                    layoutIdentity.IsCurrent && !header.IsDestroyed && !header.IsDestroyQueued &&
                    !layout.IsDestroyed && !layout.IsDestroyQueued &&
                    ReferenceEquals(parent.Transform, parentTransform) &&
                    ReferenceEquals(header.Transform, layout) &&
                    ReferenceEquals(layout.Parent, parentTransform);
                if (!Current()) return null;
                layout.DisplayHorizontal = true;
                if (!Current()) return null;
                layout.ItemSpacing = 6.0f;
                return Current() ? header : null;
            }

            private UIListTransform? CreateNestedVirtualList(ulong generation)
            {
                bool OwnerCurrent() => IsCurrentBuild(generation);
                if (!OwnerCurrent()) return null;
                ObjectConstructionIdentity containerIdentity = ObjectConstructionIdentity.Capture(_container);
                TransformBase containerTransform = _container.Transform;
                ObjectConstructionIdentity containerTransformIdentity = ObjectConstructionIdentity.Capture(containerTransform);
                SceneNode scrollNode = _container.NewChild();
                ObjectConstructionIdentity scrollIdentity = ObjectConstructionIdentity.Capture(scrollNode);
                bool ScrollNodeOwned() => containerIdentity.IsCurrent && containerTransformIdentity.IsCurrent &&
                    scrollIdentity.IsCurrent && !_container.IsDestroyed && !_container.IsDestroyQueued &&
                    !scrollNode.IsDestroyed && !scrollNode.IsDestroyQueued &&
                    ReferenceEquals(_container.Transform, containerTransform) &&
                    ReferenceEquals(scrollNode.Transform.Parent, containerTransform);
                if (!OwnerCurrent() || !ScrollNodeOwned()) return null;
                UIScrollableTransform scroll = scrollNode.SetTransform<UIScrollableTransform>();
                ObjectConstructionIdentity scrollTransformIdentity = ObjectConstructionIdentity.Capture(scroll);
                bool ScrollCurrent() => OwnerCurrent() && ScrollNodeOwned() &&
                    scrollTransformIdentity.IsCurrent && ReferenceEquals(scrollNode.Transform, scroll) &&
                    !scroll.IsDestroyed && !scroll.IsDestroyQueued;
                if (!ScrollCurrent()) return null;
                scroll.MinAnchor = Vector2.Zero;
                if (!ScrollCurrent()) return null;
                scroll.MaxAnchor = Vector2.One;
                if (!ScrollCurrent()) return null;
                scroll.Height = 300;
                if (!ScrollCurrent() || AddNestedAttemptComponent<UIScrollRegionComponent>(scrollNode,
                    ScrollCurrent) is null || !ScrollCurrent()) return null;

                SceneNode listNode = scrollNode.NewChild();
                ObjectConstructionIdentity listNodeIdentity = ObjectConstructionIdentity.Capture(listNode);
                bool ListNodeOwned() => ScrollCurrent() && listNodeIdentity.IsCurrent &&
                    !listNode.IsDestroyed && !listNode.IsDestroyQueued &&
                    ReferenceEquals(listNode.Transform.Parent, scroll);
                if (!ListNodeOwned()) return null;
                UIListTransform list = listNode.SetTransform<UIListTransform>();
                ObjectConstructionIdentity listIdentity = ObjectConstructionIdentity.Capture(list);
                UIVirtualTableProvider<int>? provider = null;
                bool ListCurrent() => ListNodeOwned() && listIdentity.IsCurrent &&
                    !list.IsDestroyed && !list.IsDestroyQueued && ReferenceEquals(listNode.Transform, list);
                if (!ListCurrent()) return null;
                list.MinAnchor = Vector2.Zero;
                if (!ListCurrent()) return null;
                list.MaxAnchor = Vector2.One;
                if (!ListCurrent()) return null;
                list.ItemSize = 28;
                if (!ListCurrent()) return null;
                list.VirtualOverscan = 2;
                if (!ListCurrent()) return null;
                list.ItemAlignment = EListAlignment.TopOrLeft;
                if (!ListCurrent()) return null;
                provider = new(_listRows,
                    new UIVirtualTableColumn<int>("value", "Element", 300,
                        CreateVirtualListCell, BindVirtualListCell, UnbindVirtualListCell,
                        BindVirtualListCell));
                if (_listElementType is { } elementType)
                    provider.ItemExtent = CreateMeasuredRowExtent(elementType, index =>
                        _boundList is { } bound && index < bound.Count ? bound[index] : null);
                if (!ListCurrent()) return null;
                _virtualList = list;
                bool NestedListCurrent() => ListCurrent() && ReferenceEquals(list.VirtualProvider, provider);
                _nestedListCurrent = NestedListCurrent;
                list.VirtualProvider = provider;
                if (!ListCurrent() || !NestedListCurrent()) return null;
                return list;
            }

            private static TComponent? AddNestedAttemptComponent<TComponent>(SceneNode node,
                Func<bool> isCurrent) where TComponent : XRComponent, new()
            {
                if (!isCurrent()) return null;
                TransformBase? maybeParentTransform = node.Transform.Parent;
                if (maybeParentTransform is null || maybeParentTransform.SceneNode is not { } parentNode)
                    return null;
                TransformBase parentTransform = maybeParentTransform;
                ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(node);
                ObjectConstructionIdentity nodeTransformIdentity = ObjectConstructionIdentity.Capture(node.Transform);
                ObjectConstructionIdentity parentIdentity = ObjectConstructionIdentity.Capture(parentNode);
                ObjectConstructionIdentity parentTransformIdentity = ObjectConstructionIdentity.Capture(parentTransform);
                TransformBase nodeTransform = node.Transform;
                bool Current() => isCurrent() && nodeIdentity.IsCurrent && nodeTransformIdentity.IsCurrent &&
                    parentIdentity.IsCurrent && parentTransformIdentity.IsCurrent &&
                    !node.IsDestroyed && !node.IsDestroyQueued && !parentNode.IsDestroyed &&
                    !parentNode.IsDestroyQueued && ReferenceEquals(node.Transform, nodeTransform) &&
                    ReferenceEquals(nodeTransform.Parent, parentTransform);
                RuntimeComponentCreationReceipt receipt = new(node, Current);
                try
                {
                    TComponent? component = node.AddComponentGuarded(static () => new TComponent(), receipt);
                    if (component is not null && Current() &&
                        ReferenceEquals(component.SceneNode, node) &&
                        !component.IsDestroyed && !component.IsDestroyQueued)
                        return component;
                    if (receipt.Component is not null)
                    {
                        InvalidOperationException failure = new("The nested component owner changed after construction.");
                        receipt.RecordFailure(failure);
                        if (!receipt.TryAbortUnchangedComponent())
                            Debug.LogException(receipt.CleanupFailure is { } cleanup
                                ? new AggregateException(failure, cleanup) : failure,
                                "Could not abort a nested component after its owner changed.");
                    }
                    return null;
                }
                catch (Exception failure)
                {
                    receipt.RecordFailure(failure);
                    bool aborted = receipt.Component is null || receipt.TryAbortUnchangedComponent();
                    if (!aborted)
                        Debug.LogException(receipt.CleanupFailure is { } cleanup
                            ? new AggregateException(failure, cleanup) : failure,
                            "Could not abort a failed nested component creation.");
                    if (failure is InvalidOperationException && !Current()) return null;
                    throw;
                }
            }

            private static UIButtonComponent? CreateNestedAttemptButton(SceneNode parent,
                string text, Action onClick, Func<bool> isCurrent,
                float? width = ButtonWidth, bool fillParent = false)
            {
                SceneNode? buttonNode = CreateNestedAttemptChild<UIButtonComponent>(parent,
                    isCurrent, out UIButtonComponent? button);
                if (buttonNode is null || button is null || !isCurrent()) return null;
                ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(buttonNode);
                ObjectConstructionIdentity buttonIdentity = ObjectConstructionIdentity.Capture(button);
                UIBoundableTransform? ownedBounds = null;
                ObjectConstructionIdentity? boundsIdentity = null;
                bool Current() => isCurrent() && nodeIdentity.IsCurrent && buttonIdentity.IsCurrent &&
                    !buttonNode.IsDestroyed && !buttonNode.IsDestroyQueued &&
                    !button.IsDestroyed && !button.IsDestroyQueued &&
                    ReferenceEquals(button.SceneNode, buttonNode) &&
                    ReferenceEquals(buttonNode.Transform.Parent, parent.Transform) &&
                    (ownedBounds is null || boundsIdentity is { } identity && identity.IsCurrent &&
                        !ownedBounds.IsDestroyed && !ownedBounds.IsDestroyQueued &&
                        ReferenceEquals(buttonNode.Transform, ownedBounds));

                UIMaterialComponent? background = AddNestedAttemptComponent<UIMaterialComponent>(buttonNode, Current);
                if (!Current() || background is null) return null;
                EditorUI.Styles.UpdateButton(button);
                if (!Current()) return null;
                background.SetBatchSolidColor(ColorF4.Transparent);
                if (!Current()) return null;
                UIBoundableTransform? bounds = CreateNestedAttemptTransform<UIBoundableTransform>(buttonNode, Current);
                if (bounds is null || !Current() || !ReferenceEquals(buttonNode.Transform, bounds)) return null;
                ownedBounds = bounds;
                boundsIdentity = ObjectConstructionIdentity.Capture(bounds);
                if (!Current()) return null;
                if (width is { } fixedWidth)
                {
                    bounds.Width = fixedWidth;
                    if (!Current()) return null;
                }
                if (fillParent)
                {
                    bounds.MaxAnchor = Vector2.One;
                    if (!Current()) return null;
                }
                bounds.Height = ButtonHeight;
                if (!Current()) return null;
                bounds.Margins = new Vector4(2.0f);
                if (!Current()) return null;

                SceneNode? labelNode = CreateNestedAttemptChild<UITextComponent>(buttonNode,
                    Current, out UITextComponent? label);
                if (labelNode is null || label is null || !Current()) return null;
                try
                {
                    label.SetTextGuarded(text, (Func<bool>)Current, static current =>
                    {
                        if (!current())
                            throw new InvalidOperationException("The nested button label owner changed during assignment.");
                    });
                }
                catch (InvalidOperationException) when (!Current()) { return null; }
                if (!Current()) return null;
                label.FontSize = EditorUI.Styles.PropertyInputFontSize ?? 14.0f;
                if (!Current()) return null;
                label.Color = EditorUI.Styles.ButtonTextColor;
                if (!Current()) return null;
                label.HorizontalAlignment = EHorizontalAlignment.Center;
                if (!Current()) return null;
                label.VerticalAlignment = EVerticalAlignment.Center;
                if (!Current()) return null;
                UIBoundableTransform? labelBounds = CreateNestedAttemptTransform<UIBoundableTransform>(labelNode, Current);
                if (labelBounds is null || !Current()) return null;
                ObjectConstructionIdentity labelBoundsIdentity = ObjectConstructionIdentity.Capture(labelBounds);
                bool LabelBoundsCurrent() => Current() && labelBoundsIdentity.IsCurrent &&
                    !labelBounds.IsDestroyed && !labelBounds.IsDestroyQueued &&
                    ReferenceEquals(labelNode.Transform, labelBounds);
                if (!LabelBoundsCurrent()) return null;
                labelBounds.Margins = new Vector4(6.0f, 0.0f, 6.0f, 0.0f);
                if (!LabelBoundsCurrent()) return null;
                button.RegisterClickActions(_ =>
                {
                    if (Current()) onClick();
                });
                return Current() ? button : null;
            }

            private static bool BuildNestedScalarValueEditor(SceneNode editor, Type valueType,
                PropertyInfo bindingProperty, object[] targets, Func<bool> isCurrent,
                Action<RuntimeComponentCreationReceipt> retainFailedReceipt)
            {
                if (!isCurrent()) return false;
                // A custom editor builds its own controls. The row rebuilds it on each bind.
                if (valueType.GetCustomAttribute<EditorComponentAttribute>() is
                    IEditorMeasuredRowEditor and EditorComponentAttribute custom)
                {
                    custom.CreateEditor(editor, bindingProperty, targets.Cast<object?>().ToArray());
                    return isCurrent();
                }
                if (valueType.IsEnum && !valueType.IsDefined(typeof(FlagsAttribute), inherit: false))
                    return BuildNestedEnumEditor(editor, bindingProperty, targets, isCurrent);
                if (valueType.IsEnum) return false;
                if (valueType == typeof(bool))
                    return NativeUIElements.TryCreateCheckboxToggleGuarded(editor, bindingProperty,
                        targets, isCurrent, out _, retainFailedReceipt: retainFailedReceipt);
                if (valueType == typeof(Vector2))
                    return BuildNestedVectorEditor<Vector2Transformer>(editor, ["X", "Y"],
                        bindingProperty, targets, isCurrent, retainFailedReceipt);
                if (valueType == typeof(Vector3))
                    return BuildNestedVectorEditor<Vector3Transformer>(editor, ["X", "Y", "Z"],
                        bindingProperty, targets, isCurrent, retainFailedReceipt);
                if (valueType == typeof(Vector4))
                    return BuildNestedVectorEditor<Vector4Transformer>(editor, ["X", "Y", "Z", "W"],
                        bindingProperty, targets, isCurrent, retainFailedReceipt);
                if (valueType == typeof(Quaternion))
                    return BuildNestedVectorEditor<QuaternionTransformer>(editor, ["Yaw", "Pitch", "Roll"],
                        bindingProperty, targets, isCurrent, retainFailedReceipt);

                bool created = Type.GetTypeCode(valueType) switch
                {
                    TypeCode.SByte => NativeUIElements.TryCreateTextInputFieldGuarded<UISByteInputComponent>(
                        editor, bindingProperty, targets, isCurrent, out _, retainFailedReceipt),
                    TypeCode.Byte => NativeUIElements.TryCreateTextInputFieldGuarded<UIByteInputComponent>(
                        editor, bindingProperty, targets, isCurrent, out _, retainFailedReceipt),
                    TypeCode.Int16 => NativeUIElements.TryCreateTextInputFieldGuarded<UIShortInputComponent>(
                        editor, bindingProperty, targets, isCurrent, out _, retainFailedReceipt),
                    TypeCode.UInt16 => NativeUIElements.TryCreateTextInputFieldGuarded<UIUShortInputComponent>(
                        editor, bindingProperty, targets, isCurrent, out _, retainFailedReceipt),
                    TypeCode.Int32 => NativeUIElements.TryCreateTextInputFieldGuarded<UIIntInputComponent>(
                        editor, bindingProperty, targets, isCurrent, out _, retainFailedReceipt),
                    TypeCode.UInt32 => NativeUIElements.TryCreateTextInputFieldGuarded<UIUIntInputComponent>(
                        editor, bindingProperty, targets, isCurrent, out _, retainFailedReceipt),
                    TypeCode.Int64 => NativeUIElements.TryCreateTextInputFieldGuarded<UILongInputComponent>(
                        editor, bindingProperty, targets, isCurrent, out _, retainFailedReceipt),
                    TypeCode.UInt64 => NativeUIElements.TryCreateTextInputFieldGuarded<UIULongInputComponent>(
                        editor, bindingProperty, targets, isCurrent, out _, retainFailedReceipt),
                    TypeCode.Single => NativeUIElements.TryCreateTextInputFieldGuarded<UIFloatInputComponent>(
                        editor, bindingProperty, targets, isCurrent, out _, retainFailedReceipt),
                    TypeCode.Double => NativeUIElements.TryCreateTextInputFieldGuarded<UIDoubleInputComponent>(
                        editor, bindingProperty, targets, isCurrent, out _, retainFailedReceipt),
                    TypeCode.Decimal => NativeUIElements.TryCreateTextInputFieldGuarded<UIDecimalInputComponent>(
                        editor, bindingProperty, targets, isCurrent, out _, retainFailedReceipt),
                    TypeCode.String => NativeUIElements.TryCreateTextInputFieldGuarded<UITextInputComponent>(
                        editor, bindingProperty, targets, isCurrent, out _, retainFailedReceipt),
                    _ => false,
                };
                return created && isCurrent();
            }

            /// <summary>
            /// Builds the same horizontal field layout as the ordinary vector editors. Each field node
            /// has the name of its transformer property, so a recycled row can rebind it by name.
            /// </summary>
            private static bool BuildNestedVectorEditor<TTransformer>(SceneNode editor, string[] fields,
                PropertyInfo bindingProperty, object[] targets, Func<bool> isCurrent,
                Action<RuntimeComponentCreationReceipt> retainFailedReceipt)
                where TTransformer : DataTransformerBase, new()
            {
                UIListTransform? layout = CreateNestedAttemptTransform<UIListTransform>(editor, isCurrent, replace: true);
                if (layout is null || !isCurrent()) return false;
                layout.DisplayHorizontal = true;
                if (!isCurrent()) return false;
                layout.Height = 25.0f;
                if (!isCurrent()) return false;
                layout.ItemAlignment = EListAlignment.TopOrLeft;
                if (!isCurrent()) return false;
                layout.ItemSpacing = 5.0f;
                if (!isCurrent()) return false;
                layout.ItemSize = null;
                if (!isCurrent()) return false;
                TTransformer? transformer = AddNestedAttemptComponent<TTransformer>(editor, isCurrent);
                if (transformer is null || !isCurrent()) return false;
                transformer.Property = bindingProperty;
                if (!isCurrent()) return false;
                transformer.Targets = targets;
                if (!isCurrent()) return false;
                object?[] transformerTargets = [transformer];
                foreach (string field in fields)
                {
                    SceneNode? child = CreateNestedAttemptNode(editor, isCurrent);
                    if (child is null || !isCurrent()) return false;
                    child.Name = field;
                    // The input field adds UI components that require a boundable transform.
                    if (CreateNestedAttemptTransform<UIBoundableTransform>(child, isCurrent, replace: true) is null)
                        return false;
                    if (!isCurrent() || !NativeUIElements.TryCreateTextInputFieldGuarded<UIFloatInputComponent>(
                        child, transformer.GetTransformedProperty(field), transformerTargets, isCurrent,
                        out _, retainFailedReceipt) || !isCurrent())
                        return false;
                }
                return isCurrent();
            }

            private static bool BuildNestedEnumEditor(SceneNode node, PropertyInfo property,
                object[] targets, Func<bool> isCurrent)
            {
                UIComboBoxComponent? combo = AddNestedAttemptComponent<UIComboBoxComponent>(node, isCurrent);
                if (!isCurrent() || combo is null) return false;
                NativePropertyPopupHost? popupHost =
                    AddNestedAttemptComponent<NativePropertyPopupHost>(node, isCurrent);
                if (!isCurrent() || popupHost is null ||
                    !popupHost.TryCreatePopup(isCurrent, out UIContextMenuComponent? popup) ||
                    popup is null || !isCurrent()) return false;
                combo.Popup = popup;
                if (!isCurrent()) return false;
                UIMaterialComponent? material = AddNestedAttemptComponent<UIMaterialComponent>(node, isCurrent);
                if (!isCurrent() || material is null) return false;
                material.SetBatchBorderStyle(ColorF4.Transparent, ColorF4.Gray, 1);
                if (!isCurrent()) return false;
                UITextComponent? label = AddNestedAttemptLabel(node, " ", isCurrent);
                if (!isCurrent() || label is null) return false;
                label.VerticalAlignment = EVerticalAlignment.Center;
                if (!isCurrent()) return false;
                InspectorEnumEditorComponent? enumEditor =
                    AddNestedAttemptComponent<InspectorEnumEditorComponent>(node, isCurrent);
                if (!isCurrent() || enumEditor is null) return false;
                enumEditor.Configure(combo, label, property, targets, isCurrent);
                if (!isCurrent()) return false;
                EditorVirtualRowControlLease comboLease = EditorVirtualRowControlLease.Capture(combo, node);
                EditorVirtualRowControlLease popupHostLease = EditorVirtualRowControlLease.Capture(popupHost, node);
                EditorVirtualRowControlLease editorLease = EditorVirtualRowControlLease.Capture(enumEditor, node);
                combo.Opening += _ =>
                {
                    if (enumEditor.IsOwnerCurrent && comboLease.IsCurrent(node) &&
                        popupHostLease.IsCurrent(node) && editorLease.IsCurrent(node) &&
                        popupHost.OwnsPopup(popup))
                        combo.PopupPosition = popupHost.GetPosition();
                };
                return isCurrent();
            }

            private bool TryRebindOwnedNestedListEditor(SceneNode editor, EditorListRowCellState state,
                Type valueType, PropertyInfo property, object?[] targets, Func<bool> rowIsCurrent,
                out bool ownershipCurrent)
            {
                ObjectConstructionIdentity editorIdentity = ObjectConstructionIdentity.Capture(editor);
                TransformBase editorTransform = editor.Transform;
                ObjectConstructionIdentity editorTransformIdentity = ObjectConstructionIdentity.Capture(editorTransform);
                SceneNode valueNode = state.ValueNode;
                ObjectConstructionIdentity valueNodeIdentity = ObjectConstructionIdentity.Capture(valueNode);
                TransformBase valueTransform = valueNode.Transform;
                ObjectConstructionIdentity valueTransformIdentity = ObjectConstructionIdentity.Capture(valueTransform);
                EditorVirtualRowControlLease[] controls = CaptureFixedEditorControls(editor, valueType);
                bool Current() => rowIsCurrent() && ReferenceEquals(state.EditorNode, editor) &&
                    editorIdentity.IsCurrent && editorTransformIdentity.IsCurrent &&
                    valueNodeIdentity.IsCurrent && valueTransformIdentity.IsCurrent &&
                    !editor.IsDestroyed && !editor.IsDestroyQueued && !valueNode.IsDestroyed && !valueNode.IsDestroyQueued &&
                    ReferenceEquals(editor.Transform, editorTransform) && ReferenceEquals(valueNode.Transform, valueTransform) &&
                    ReferenceEquals(editorTransform.Parent, valueTransform) &&
                    controls.All(control => control.IsCurrent(editor));
                ownershipCurrent = Current();
                if (!ownershipCurrent) return false;
                bool rebound = TryRebindListEditor(editor, valueType, property, targets, Current);
                ownershipCurrent = Current();
                return rebound && ownershipCurrent;
            }

            private static void DestroyNestedAttemptChildIfOwned(SceneNode child, Func<bool> isOwned)
            {
                if (!isOwned()) return;
                try { child.Destroy(false); }
                catch (Exception error) { Debug.LogException(error, "Could not retire an incomplete nested detail leaf."); }
            }

            private void RetireNestedListDetail(bool invalidatePendingAttempt = true)
            {
                if (invalidatePendingAttempt) _nestedDetailVersion++;
                EditorNestedListPaneAttempt? attempt = _nestedDetailAttempt;
                _nestedDetailAttempt = null;
                _nestedDetailRoot = null;
                _nestedDetailLifetime = null;
                _nestedDetailPathProof = [];
                if (attempt is null) return;
                attempt.RetireLifetimeIfOwned();
                attempt.DestroyRootIfOwned();
            }

            private void RetireNestedListAttempt(EditorNestedListPaneAttempt attempt, ulong version)
            {
                if (ReferenceEquals(_nestedDetailAttempt, attempt))
                {
                    if (_nestedDetailVersion == version) _nestedDetailVersion++;
                    _nestedDetailAttempt = null;
                    _nestedDetailRoot = null;
                    _nestedDetailLifetime = null;
                    _nestedDetailPathProof = [];
                }
                attempt.RetireLifetimeIfOwned();
                attempt.DestroyRootIfOwned();
            }

            private void RetireNestedListDetailIfStale()
            {
                if (_nestedDetailAttempt is { } attempt &&
                    (!attempt.IsCurrent || !attempt.IsDetailCurrent || _nestedDetailPathProof.Length == 0 ||
                     _nestedDetailPathProof.Any(path => !path.IsOwnerPathCurrent())))
                    RetireNestedListDetail();
            }
        }
    }
}
