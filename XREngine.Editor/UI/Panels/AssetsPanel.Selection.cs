using XREngine.Editor.UI;

namespace XREngine.Editor;

public partial class AssetsPanel
{
    private void SelectAssetRow(ulong? id)
    {
        if (!AcceptsActiveInput || _table.SelectedId != id)
            return;
        ulong intent = ++_selectionIntent;
        _renameMenuDraft = null;
        if (id is not { } itemId || !_paths.TryGetValue(itemId, out string? path))
        {
            string? previous = SelectedPath;
            SelectedPath = null;
            if (previous is not null && NativeAssetSelection.SelectedPath is { } inspectedPath &&
                NativeAssetPathPolicy.PathsEqual(previous, inspectedPath))
                NativeAssetSelection.Clear();
            return;
        }

        SelectedPath = path;
        if (Directory.Exists(path))
        {
            if (NativeAssetSelection.SelectedPath is not null)
                NativeAssetSelection.Clear();
            return;
        }
        // Selection already inspected this file before the same click activates it.
        // A scene selection clears the shared asset path, so a later click restores it.
        if (NativeAssetSelection.SelectedPath is { } selected && NativeAssetPathPolicy.PathsEqual(selected, path))
            return;

        EditorDropObjectIdentity owner = new(this);
        var transform = Transform;
        EditorDropObjectIdentity transformOwner = new(transform);
        var parent = transform.Parent;
        ulong sourceRevision = _tableRows.Revision;
        NativeAssetSelection.Select(path);
        if (AcceptsActiveInput && _selectionIntent == intent && owner.IsCurrent && transformOwner.IsCurrent &&
            ReferenceEquals(Transform, transform) && ReferenceEquals(transform.Parent, parent) &&
            _tableRows.Revision == sourceRevision && _table.SelectedId == itemId &&
            SelectedPath is { } current && NativeAssetPathPolicy.PathsEqual(current, path) &&
            _paths.TryGetValue(itemId, out string? mapped) && NativeAssetPathPolicy.PathsEqual(mapped, path) &&
            NativeAssetSelection.SelectedPath is { } inspected && NativeAssetPathPolicy.PathsEqual(inspected, path))
            AssetSelected?.Invoke(path);
    }

    private void ActivateAssetRow(ulong id)
    {
        if (!AcceptsActiveInput || _table.SelectedId != id || !_paths.TryGetValue(id, out string? path))
            return;
        if (Directory.Exists(path))
        {
            Folder = path;
            return;
        }
        SelectAssetRow(id);
    }
}
