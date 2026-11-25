using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Editor.Services;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public partial class AssetsPanel
{
    private IUICanvasInputSource? _menuInput;
    private Action<UIInteractableComponent>? _menuRightClick;
    private NativeAssetMenuRequest? _menuRequest;
    private UIContextMenuComponent? _assetMenu;
    private SceneNode? _assetMenuNode;
    private bool _showingAssetMenu;
    private NativeAssetRenameDraft? _renameMenuDraft;
    private ulong _assetMenuIntent;
    private UICanvasTransform? _assetMenuCanvas;
    private ObjectConstructionIdentity? _assetMenuIdentity;
    private ObjectConstructionIdentity? _assetMenuNodeIdentity;
    private ObjectConstructionIdentity? _assetMenuTransformIdentity;
    private Action? _assetMenuDismissed;

    private void UpdateAssetMenu()
    {
        IUICanvasInputSource? input = BoundableTransform.GetCanvasComponent()?.GetInputComponent();
        if (!ReferenceEquals(input, _menuInput))
        {
            ReleaseAssetMenu();
            if (!AcceptsActiveInput || input is null ||
                !ReferenceEquals(BoundableTransform.GetCanvasComponent()?.GetInputComponent(), input)) return;
            SubscribeAssetMenuInput(input);
        }
        if (_showingAssetMenu || _menuRequest is not { } request) return;
        if (!IsCurrentAssetMenu(request)) { DismissAssetMenu(); return; }
        if (!request.AwaitingShow) return;
        if (_assetMenu is not null && !OwnsAssetMenu()) { ReleaseAssetMenu(); return; }
        ShowAssetMenu(request);
    }

    private void SubscribeAssetMenuInput(IUICanvasInputSource input)
    {
        _menuInput = input;
        _menuRightClick = target =>
        {
            if (AcceptsActiveInput && ReferenceEquals(_menuInput, input) &&
                ReferenceEquals(BoundableTransform.GetCanvasComponent()?.GetInputComponent(), input))
                RequestAssetMenu(target, input);
        };
        input.RightClick += _menuRightClick;
    }

    private void ShowAssetMenu(NativeAssetMenuRequest request)
    {
        _showingAssetMenu = true;
        try
        {
            if (_assetMenu is null)
            {
                SceneNode node = new("Asset row menu", new UIBoundableTransform()) { IsActiveSelf = false };
                _assetMenuNode = node;
                _assetMenuCanvas = request.Canvas;
                _assetMenuNodeIdentity = ObjectConstructionIdentity.Capture(node);
                _assetMenuTransformIdentity = ObjectConstructionIdentity.Capture(node.Transform);
                UIContextMenuComponent menu = node.AddComponent<UIContextMenuComponent>()!;
                if (!IsCurrentAssetMenu(request) || !ReferenceEquals(_assetMenuNode, node))
                {
                    menu.Destroy(true);
                    node.Destroy(false);
                    return;
                }
                _assetMenu = menu;
                _assetMenuIdentity = ObjectConstructionIdentity.Capture(menu);
                _assetMenuDismissed = () =>
                {
                    if (ReferenceEquals(_assetMenu, menu) && OwnsAssetMenu()) AssetMenuDismissed();
                };
                menu.Dismissed += _assetMenuDismissed;
                ((UIBoundableTransform)node.Transform).Visibility = EVisibility.Collapsed;
                node.Transform.SetParent(request.Canvas, false, EParentAssignmentMode.Immediate);
                if (!IsCurrentAssetMenu(request) || !ReferenceEquals(_assetMenu, menu)) return;
                node.IsActiveSelf = true;
            }
            if (!IsCurrentAssetMenu(request) || _assetMenu is not { } popup ||
                !OwnsAssetMenu() || !ReferenceEquals(_assetMenuNode?.Transform.Parent, request.Canvas)) return;
            bool directory = Directory.Exists(request.Path);
            bool importable = !directory && File.Exists(request.Path) &&
                !string.Equals(Path.GetExtension(request.Path), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase);
            popup.Show(request.Position,
                new ContextMenuItem(Path.GetFileName(request.Path), null, enabled: false),
                ContextMenuItem.Separator(),
                new ContextMenuItem(directory ? "Open Folder" : "Inspect", () => OpenMenuAsset(request)),
                new ContextMenuItem(directory ? "Open in Explorer" : "Show in Explorer",
                    () => RevealMenuAsset(request)),
                new ContextMenuItem("Rename", () => PrepareMenuRename(request)),
                new ContextMenuItem("Reimport", () => ReimportMenuAsset(request), enabled: importable),
                CreateAssetSubmenu(request, directory));
            if (IsCurrentAssetMenu(request) && popup.IsOpen)
                request.AwaitingShow = false;
        }
        finally
        {
            _showingAssetMenu = false;
            if (ReferenceEquals(_menuRequest, request) && request.AwaitingShow)
                DismissAssetMenu();
            else if (!ReferenceEquals(_menuRequest, request) && OwnsAssetMenu())
                _assetMenu?.Hide();
        }
    }

    private void RequestAssetMenu(UIInteractableComponent target, IUICanvasInputSource input)
    {
        ulong intent = ++_assetMenuIntent;
        NativeAssetMenuRequest? request = null;
        for (TransformBase? current = target.Transform; current is not null && !ReferenceEquals(current, Transform); current = current.Parent)
        {
            if (current.SceneNode?.GetComponent<UIVirtualTableRowComponent>() is not { } row ||
                current.Parent is not UIListTransform list || !ReferenceEquals(list.VirtualProvider, _table)) continue;
            if (list.TryGetVirtualRowLease(row.BoundableTransform, out var lease) &&
                _paths.TryGetValue(lease.ItemId, out string? path) &&
                BoundableTransform.GetCanvasTransform() is { } canvas &&
                input.TryGetPresentedPointerPosition(target, out var position))
                request = new(this, row, list, _table, lease, canvas, input, position, path);
            break;
        }
        DismissAssetMenu(invalidateIntent: false);
        if (intent != _assetMenuIntent || !AcceptsActiveInput || request is null || !request.IsCurrent()) return;
        // Show on the next owner tick. The old menu still has a callback in this
        // right-click dispatch and must not dismiss the replacement.
        _menuRequest = request;
    }

    private bool IsCurrentAssetMenu(NativeAssetMenuRequest request)
        => AcceptsActiveInput && ReferenceEquals(_menuRequest, request) &&
            ReferenceEquals(_menuInput, request.Input) && request.IsCurrent() &&
            _paths.TryGetValue(request.Lease.ItemId, out string? path) && NativeAssetPathPolicy.PathsEqual(path, request.Path);

    private void OpenMenuAsset(NativeAssetMenuRequest request)
    {
        if (!SelectMenuAsset(request)) return;
        ActivateAssetRow(request.Lease.ItemId);
    }

    private void RevealMenuAsset(NativeAssetMenuRequest request)
    {
        if (!SelectMenuAsset(request)) return;
        bool directory = Directory.Exists(request.Path);
        if (!directory && !File.Exists(request.Path)) return;
        if (!IsCurrentAssetMenu(request) || request.AwaitingShow ||
            _table.SelectedId != request.Lease.ItemId) return;
        EditorFileBrowser.OpenPath(request.Path, directory);
    }

    private bool SelectMenuAsset(NativeAssetMenuRequest request)
    {
        if (!IsCurrentAssetMenu(request) || request.AwaitingShow) return false;
        _table.SelectedId = request.Lease.ItemId;
        if (!IsCurrentAssetMenu(request) || request.AwaitingShow || _table.SelectedId != request.Lease.ItemId) return false;
        SelectedPath = request.Path;
        return true;
    }

    private void PrepareMenuRename(NativeAssetMenuRequest request)
    {
        if (!SelectMenuAsset(request) || _nameInput is not { } field) return;
        _renameMenuDraft = new NativeAssetRenameDraft(request, field);
        ulong focusIntent = request.Input.FocusIntentRevision;
        ObjectConstructionIdentity fieldIdentity = ObjectConstructionIdentity.Capture(field);
        ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(field.SceneNode);
        ObjectConstructionIdentity transformIdentity = ObjectConstructionIdentity.Capture(field.Transform);
        bool CurrentField() => IsCurrentAssetMenu(request) && ReferenceEquals(_nameInput, field) &&
            fieldIdentity.IsCurrent && nodeIdentity.IsCurrent && transformIdentity.IsCurrent &&
            !field.IsDestroyed && !field.IsDestroyQueued &&
            ReferenceEquals(field.SceneNode, nodeIdentity.Target) && ReferenceEquals(field.Transform, transformIdentity.Target);
        field.Text = Path.GetFileName(request.Path);
        if (!CurrentField()) return;
        field.SelectionAnchor = 0;
        if (!CurrentField()) return;
        field.CursorPosition = field.Text.Length;
        if (CurrentField() && request.Input.FocusIntentRevision == focusIntent && !request.Input.HasPendingFocusRequest)
            request.Input.TryFocusPresented(field);
    }

    private void SubmitMenuRename(UITextInputComponent field)
    {
        NativeAssetRenameDraft? draft = _renameMenuDraft;
        _renameMenuDraft = null;
        if (AcceptsActiveInput && ReferenceEquals(_nameInput, field) &&
            draft?.CanSubmit(field, SelectedPath, _paths) == true)
            RenameSelection();
    }

    private void CancelMenuRename(UITextInputComponent field)
    {
        _renameMenuDraft = null;
        if (AcceptsActiveInput && ReferenceEquals(_nameInput, field)) field.Text = string.Empty;
    }

    // Groups creatable asset types by category. The menu is built once per show, not per frame.
    private ContextMenuItem CreateAssetSubmenu(NativeAssetMenuRequest request, bool directory)
    {
        bool available = Engine.Assets?.CreationRecovery is not null && !UseEngineAssets;
        IReadOnlyList<(Type Type, string DisplayName, string Category)> choices =
            available ? EditorImGuiUI.GetNativeAssetTypeChoices() : [];
        if (choices.Count == 0)
            return new ContextMenuItem("Create New...", null, enabled: false);
        var categories = new List<ContextMenuItem>();
        var items = new List<ContextMenuItem>();
        string? category = null;
        foreach ((Type type, string displayName, string itemCategory) in choices)
        {
            if (category is not null && !string.Equals(category, itemCategory, StringComparison.OrdinalIgnoreCase))
            {
                categories.Add(ContextMenuItem.Submenu(category, [.. items]));
                items.Clear();
            }
            category = itemCategory;
            items.Add(new ContextMenuItem(displayName, () => CreateMenuAsset(request, directory, type, displayName)));
        }
        if (category is not null && items.Count > 0)
            categories.Add(ContextMenuItem.Submenu(category, [.. items]));
        return ContextMenuItem.Submenu("Create New...", [.. categories]);
    }

    private void CreateMenuAsset(NativeAssetMenuRequest request, bool directory, Type type, string displayName)
    {
        if (!SelectMenuAsset(request)) return;
        string folder = directory ? request.Path : Path.GetDirectoryName(request.Path) ?? CurrentFolder;
        try
        {
            string created = NativeAssetCreation.CreateAsset(RootFolder, folder, type, displayName);
            if (!AcceptsActiveInput) return;
            SelectedPath = created;
            NativeAssetSelection.Select(created);
            if (AcceptsActiveInput) RefreshRows();
        }
        catch (Exception exception)
        {
            Debug.UI($"[NativeAssets] Could not create {displayName}: {exception.Message}");
        }
    }

    private void ReimportMenuAsset(NativeAssetMenuRequest request)
    {
        if (!SelectMenuAsset(request)) return;
        _ = ImportSelectionAsync();
    }

    private void AssetMenuDismissed()
    {
        if (_menuRequest is { AwaitingShow: false }) _menuRequest = null;
    }

    private void DismissAssetMenu(bool invalidateIntent = true)
    {
        if (invalidateIntent) _assetMenuIntent++;
        _menuRequest = null;
        if (!_showingAssetMenu && OwnsAssetMenu()) _assetMenu?.Hide();
    }

    private bool OwnsAssetMenu()
        => _assetMenu is { IsDestroyed: false, IsDestroyQueued: false } menu &&
            _assetMenuNode is { IsDestroyed: false, IsDestroyQueued: false } node &&
            _assetMenuIdentity is { IsCurrent: true } && _assetMenuNodeIdentity is { IsCurrent: true } &&
            _assetMenuTransformIdentity is { IsCurrent: true } transform &&
            ReferenceEquals(menu.SceneNode, node) && ReferenceEquals(node.Transform, transform.Target) &&
            ReferenceEquals(node.Transform.Parent, _assetMenuCanvas);

    private void ReleaseAssetMenu()
    {
        IUICanvasInputSource? input = _menuInput;
        Action<UIInteractableComponent>? callback = _menuRightClick;
        UIContextMenuComponent? menu = _assetMenu;
        Action? dismissed = _assetMenuDismissed;
        SceneNode? node = _assetMenuNode;
        UICanvasTransform? canvas = _assetMenuCanvas;
        ObjectConstructionIdentity? menuIdentity = _assetMenuIdentity;
        ObjectConstructionIdentity? nodeIdentity = _assetMenuNodeIdentity;
        ObjectConstructionIdentity? transformIdentity = _assetMenuTransformIdentity;
        TransformBase? parent = node?.Transform.Parent;
        bool ownedPopup = menu is null || (menuIdentity is { IsCurrent: true } && ReferenceEquals(menu.SceneNode, node));
        bool OwnedNode() => ownedPopup && node is not null && nodeIdentity is { IsCurrent: true } &&
            transformIdentity is { IsCurrent: true } transform && !node.IsDestroyed && !node.IsDestroyQueued &&
            ReferenceEquals(node.Transform, transform.Target) && ReferenceEquals(node.Transform.Parent, parent) &&
            (parent is null || ReferenceEquals(parent, canvas));
        _assetMenuIntent++;
        _menuInput = null;
        _menuRightClick = null;
        _menuRequest = null;
        _assetMenu = null;
        _assetMenuDismissed = null;
        _assetMenuNode = null;
        _assetMenuCanvas = null;
        _assetMenuIdentity = null;
        _assetMenuNodeIdentity = null;
        _assetMenuTransformIdentity = null;
        _renameMenuDraft = null;
        if (input is not null && callback is not null) input.RightClick -= callback;
        try
        {
            if (menu is not null)
            {
                if (dismissed is not null) menu.Dismissed -= dismissed;
                if (OwnedNode() && menuIdentity is { IsCurrent: true } && ReferenceEquals(menu.SceneNode, node))
                    menu.Destroy(true);
            }
        }
        finally
        {
            if (OwnedNode())
            {
                try { ((UIBoundableTransform)node!.Transform).Visibility = EVisibility.Collapsed; }
                finally { if (OwnedNode()) node!.Destroy(false); }
            }
        }
    }
}
