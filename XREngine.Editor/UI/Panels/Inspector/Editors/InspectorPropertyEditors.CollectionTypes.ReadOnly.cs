using System.Collections;
using System.Numerics;
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
            private readonly Dictionary<UIBoundableTransform, EditorCollectionPreviewCell> _previewCells = [];

            private void RenderGenericCollection(ICollection sample)
            {
                ulong generation = _headerGeneration;
                List<object?> buffer = BuildBuffer(sample);
                if (!IsCurrentBuild(generation)) return;
                SceneNode branch = _container.NewChild();
                try
                {
                    EnsureVerticalLayout(branch);
                    if (!KeepRenderedBranch(branch, generation)) return;
                    AddInfoLabel(branch, $"Count: {buffer.Count}");
                    if (!KeepRenderedBranch(branch, generation)) return;
                    AddInfoLabel(branch, "This collection has no guarded Undo adapter and is read-only.");
                    if (!KeepRenderedBranch(branch, generation)) return;
                    if (buffer.Count == 0)
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
                    list.ItemSize = 28;
                    list.VirtualOverscan = 2;
                    list.ItemAlignment = EListAlignment.TopOrLeft;
                    EditorListRowSource rows = new();
                    rows.Reset(buffer.Count);
                    if (!KeepRenderedBranch(branch, generation)) return;
                    _virtualList = list;
                    UIVirtualTableProvider<int> provider = new(rows,
                        new UIVirtualTableColumn<int>("index", "Element", RowHeaderWidth,
                            parent => CreatePreviewCell(parent, generation),
                            (cell, index, id) => BindPreviewCell(cell, index, id, null, branch, generation),
                            UnbindPreviewCell),
                        new UIVirtualTableColumn<int>("value", "Value", 300,
                            parent => CreatePreviewCell(parent, generation),
                            (cell, index, id) => BindPreviewCell(cell, index, id, buffer, branch, generation),
                            UnbindPreviewCell));
                    list.VirtualProvider = provider;
                    if (!IsCurrentBuild(generation) && ReferenceEquals(list.VirtualProvider, provider))
                        list.DetachVirtualProviderForOwnerTeardown();
                }
                finally { if (!IsCurrentBuild(generation)) KeepRenderedBranch(branch, generation); }
            }

            private UIBoundableTransform CreatePreviewCell(SceneNode parent, ulong generation)
            {
                if (!IsCurrentBuild(generation))
                    throw new InvalidOperationException("The collection preview was retired.");
                parent.NewChild<UITextComponent>(out var label);
                UIBoundableTransform bounds = label.BoundableTransform;
                label.FontSize = EditorUI.Styles.PropertyNameFontSize;
                label.Color = EditorUI.Styles.PropertyNameTextColor;
                label.HorizontalAlignment = EHorizontalAlignment.Left;
                label.VerticalAlignment = EVerticalAlignment.Center;
                label.ClipToBounds = true;
                bounds.Margins = new Vector4(4, 2, 4, 2);
                if (!IsCurrentBuild(generation))
                    throw new InvalidOperationException("The collection preview was retired during creation.");
                _previewCells.Add(bounds, new() { Label = label });
                return bounds;
            }

            private void BindPreviewCell(UIBoundableTransform cell, int index, ulong id,
                List<object?>? buffer, SceneNode branch, ulong generation)
            {
                if (!IsCurrentBuild(generation) || !_previewCells.TryGetValue(cell, out var state)) return;
                if (cell.Parent is not UIBoundableTransform row || _virtualList is not { } list ||
                    !ReferenceEquals(row.Parent, list) || !list.TryGetVirtualRowLease(row, out var lease) ||
                    lease.ItemId != id || list.VirtualProvider is not { } provider) return;
                ulong intent = ++state.Intent;
                state.RowId = id;
                ulong textIntent = state.Label.TextWriteIntentRevision;
                ulong providerIncarnation = list.VirtualProviderIncarnation;
                ObjectConstructionIdentity cellIdentity = ObjectConstructionIdentity.Capture(cell);
                ObjectConstructionIdentity rowIdentity = ObjectConstructionIdentity.Capture(row);
                ObjectConstructionIdentity listIdentity = ObjectConstructionIdentity.Capture(list);
                bool Current() => IsCurrentBuild(generation) && state.Intent == intent && state.RowId == id &&
                    _previewCells.TryGetValue(cell, out var current) && ReferenceEquals(current, state) &&
                    cellIdentity.IsCurrent && rowIdentity.IsCurrent && listIdentity.IsCurrent &&
                    !cell.IsDestroyed && !cell.IsDestroyQueued && !state.Label.IsDestroyed &&
                    !state.Label.IsDestroyQueued && ReferenceEquals(state.Label.SceneNode, cell.SceneNode) &&
                    ReferenceEquals(cell.Parent, row) && ReferenceEquals(row.Parent, list) &&
                    ReferenceEquals(_virtualList, list) && ReferenceEquals(list.VirtualProvider, provider) &&
                    list.VirtualProviderIncarnation == providerIncarnation &&
                    list.TryGetVirtualRowLease(row, out var currentLease) && currentLease == lease &&
                    ReferenceEquals(list.Parent?.Parent, branch.Transform) &&
                    ReferenceEquals(branch.Parent, _container);
                try
                {
                    string text = buffer is null ? $"Element {index}" : buffer[index]?.ToString() ?? "<null>";
                    if (!Current() || state.Label.TextWriteIntentRevision != textIntent) return;
                    state.Label.SetTextGuarded(text, (Func<bool>)Current, static current =>
                    {
                        if (!current()) throw new InvalidOperationException("The collection preview row changed during formatting.");
                    });
                }
                finally { if (!IsCurrentBuild(generation)) KeepRenderedBranch(branch, generation); }
            }

            private void UnbindPreviewCell(UIBoundableTransform cell)
            {
                if (!_previewCells.TryGetValue(cell, out var state)) return;
                state.Intent++;
                state.RowId = 0;
            }
        }
    }
}
