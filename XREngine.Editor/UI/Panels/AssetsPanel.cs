using System.Numerics;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Browses the game or engine asset directory with recycled rows.</summary>
public partial class AssetsPanel : NativeVirtualTextPanel
{
    private sealed record AssetRow(string Path, string Name, bool IsDirectory);

    private readonly List<(ulong Id, AssetRow Item)> _rows = [];
    private readonly NativeTableRows<AssetRow> _tableRows = new();
    private readonly UIVirtualTableProvider<AssetRow> _table;
    private readonly Dictionary<ulong, string> _paths = [];
    private readonly Dictionary<UIVirtualTableRowComponent, Vector2> _dragStarts = [];
    private string? _folder;
    private bool _engineFiles;
    private DateTime _lastRefresh;
    private int _pendingRefresh;
    private string _search = string.Empty;
    private UITextInputComponent? _searchInput;
    private UITextInputComponent? _nameInput;
    private IDisposable? _folderDropTarget;
    private bool _ownerSubscriptionsActive;
    private bool _destroying;
    private bool _tableCallbacksAttached;
    private bool _deactivating;
    private bool _reactivationRequested;
    private ulong _selectionIntent;
    private bool AcceptsActiveInput => !_destroying && _ownerSubscriptionsActive && IsActiveInHierarchy;

    public string? SelectedPath { get; private set; }
    public event Action<string>? AssetSelected;
    public event Action? ViewStateChanged;
    public string Folder
    {
        get => _folder ?? string.Empty;
        set
        {
            if (_destroying) return;
            _folder = string.IsNullOrWhiteSpace(value) ? null : value;
            _renameMenuDraft = null;
            _selectionIntent++;
            SelectedPath = null;
            if (!AcceptsActiveInput) return;
            RefreshRows();
            if (AcceptsActiveInput) ViewStateChanged?.Invoke();
        }
    }
    public bool UseEngineAssets
    {
        get => _engineFiles;
        set
        {
            if (_destroying) return;
            _engineFiles = value;
            _renameMenuDraft = null;
            _folder = null;
            _selectionIntent++;
            SelectedPath = null;
            if (!AcceptsActiveInput) return;
            RefreshRows();
            if (AcceptsActiveInput) ViewStateChanged?.Invoke();
        }
    }
    public string Search
    {
        get => _search;
        set
        {
            if (_destroying) return;
            _selectionIntent++;
            _search = value ?? string.Empty;
            _renameMenuDraft = null;
            if (!AcceptsActiveInput) return;
            if (_searchInput is not null)
                _searchInput.Text = _search;
            if (!AcceptsActiveInput) return;
            RefreshRows();
            if (AcceptsActiveInput) ViewStateChanged?.Invoke();
        }
    }

    public AssetsPanel()
    {
        _table = new UIVirtualTableProvider<AssetRow>(_tableRows,
            UIVirtualTableColumn<AssetRow>.Text("kind", "Type", 76, row => row.Path.Length == 0 ? "Error" : row.IsDirectory ? "Folder" : "Asset"),
            UIVirtualTableColumn<AssetRow>.Text("name", "Name", 420, row => row.Name));
        RowProviderOverride = _table;
    }

    protected override void OnComponentActivated()
    {
        if (_destroying)
            return;
        if (_deactivating)
        {
            _reactivationRequested = true;
            return;
        }
        base.OnComponentActivated();
        if (_destroying || IsDestroyed || IsDestroyQueued || !IsActiveInHierarchy || _ownerSubscriptionsActive)
            return;
        if (_searchInput is { } search && search.Text != _search)
            search.Text = _search;
        if (_destroying || IsDestroyed || IsDestroyQueued || !IsActiveInHierarchy)
            return;
        AttachTableCallbacks();
        IDisposable registration;
        try
        {
            registration = EditorDragDropUtility.RegisterDropTarget(BoundableTransform,
                DropFileIntoCurrentFolder,
                payload => AcceptsActiveInput &&
                    EditorDragDropUtility.TryGetAssetOrFilePath(payload, out string? path) &&
                    path is not null && File.Exists(path) && Directory.Exists(CurrentFolder));
        }
        catch { DetachTableCallbacks(); throw; }
        if (_destroying || IsDestroyed || IsDestroyQueued || !IsActiveInHierarchy)
        {
            registration.Dispose();
            DetachTableCallbacks();
            return;
        }
        _folderDropTarget = registration;
        _ownerSubscriptionsActive = true;
        try
        {
            SelectedPath = NativeAssetSelection.SelectedPath;
            Engine.Assets.GameFileCreated += FileChanged;
            Engine.Assets.GameFileDeleted += FileChanged;
            Engine.Assets.GameFileRenamed += FileRenamed;
            Engine.Assets.EngineFileCreated += FileChanged;
            Engine.Assets.EngineFileDeleted += FileChanged;
            Engine.Assets.EngineFileRenamed += FileRenamed;
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
        }
        catch
        {
            try { ReleaseOwnerSubscriptions(); }
            finally { DetachTableCallbacks(); }
            throw;
        }
    }

    protected override void OnComponentDeactivated()
    {
        if (_deactivating) return;
        _deactivating = true;
        try
        {
            try { DetachTableCallbacks(); }
            finally
            {
                try { ReleaseOwnerSubscriptions(); }
                finally { base.OnComponentDeactivated(); }
            }
        }
        finally
        {
            _deactivating = false;
            bool replay = _reactivationRequested;
            _reactivationRequested = false;
            if (replay && !_destroying && IsActiveInHierarchy)
                OnComponentActivated();
        }
    }

    protected override void OnDestroying()
    {
        _destroying = true;
        _reactivationRequested = false;
        AssetSelected = null;
        ViewStateChanged = null;
        try { ReleaseOwnerSubscriptions(); }
        finally
        {
            try { DetachTableCallbacks(); }
            finally
            {
                try
                {
                    if (_searchInput is { } search)
                    {
                        search.Submitted -= SubmitSearch;
                        search.Cancelled -= CancelSearch;
                    }
                    _searchInput = null;
                    if (_nameInput is { } name)
                    {
                        name.Submitted -= SubmitMenuRename;
                        name.Cancelled -= CancelMenuRename;
                    }
                    _nameInput = null;
                }
                finally { base.OnDestroying(); }
            }
        }
    }

    private void AttachTableCallbacks()
    {
        if (_tableCallbacksAttached)
            return;
        _tableCallbacksAttached = true;
        _table.SelectionChanged += SelectAssetRow;
        _table.Activated += ActivateAssetRow;
        _table.RowPointerMoved += DragPointerMoved;
        _table.RowPointerLeft += DragPointerLeft;
    }

    private void DetachTableCallbacks()
    {
        if (!_tableCallbacksAttached)
            return;
        _tableCallbacksAttached = false;
        _table.SelectionChanged -= SelectAssetRow;
        _table.Activated -= ActivateAssetRow;
        _table.RowPointerMoved -= DragPointerMoved;
        _table.RowPointerLeft -= DragPointerLeft;
        _dragStarts.Clear();
    }

    private void DragPointerLeft(UIVirtualTableRowComponent source) => _dragStarts.Remove(source);

    private void ReleaseOwnerSubscriptions()
    {
        _selectionIntent++;
        try { ReleaseAssetMenu(); }
        finally { ReleaseFileSubscriptions(); }
    }

    private void ReleaseFileSubscriptions()
    {
        IDisposable? dropTarget = _folderDropTarget;
        _folderDropTarget = null;
        bool subscribed = _ownerSubscriptionsActive;
        _ownerSubscriptionsActive = false;
        try
        {
            if (subscribed)
            {
                try { UnregisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick); }
                finally
                {
                    Engine.Assets.GameFileCreated -= FileChanged;
                    Engine.Assets.GameFileDeleted -= FileChanged;
                    Engine.Assets.GameFileRenamed -= FileRenamed;
                    Engine.Assets.EngineFileCreated -= FileChanged;
                    Engine.Assets.EngineFileDeleted -= FileChanged;
                    Engine.Assets.EngineFileRenamed -= FileRenamed;
                }
            }
        }
        finally
        {
            dropTarget?.Dispose();
        }
    }

    protected override void CreateCommands(SceneNode commands)
    {
        AddCommand("Up", () => Folder = Path.GetDirectoryName(CurrentFolder) ?? string.Empty);
        AddCommand("Game", () => UseEngineAssets = false);
        AddCommand("Engine", () => UseEngineAssets = true);
        AddCommand("Refresh", RefreshRows);
        AddCommand("New Folder", CreateFolder);
        AddCommand("Rename", RenameSelection);
        AddCommand("Import", () => _ = ImportSelectionAsync());
        SceneNode nameNode = commands.NewChild();
        nameNode.SetTransform<UIBoundableTransform>().Width = 140;
        var (nameInput, _, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(nameNode);
        _nameInput = nameInput;
        nameInput.SingleLineMode = true;
        nameInput.Submitted += SubmitMenuRename;
        nameInput.Cancelled += CancelMenuRename;
        SceneNode searchNode = commands.NewChild();
        searchNode.SetTransform<UIBoundableTransform>().Width = 144;
        var (input, _, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(searchNode);
        _searchInput = input;
        input.SingleLineMode = true;
        input.Text = _search;
        input.Submitted += SubmitSearch;
        input.Cancelled += CancelSearch;
    }

    private void SubmitSearch(UITextInputComponent field)
    {
        if (AcceptsActiveInput)
            Search = field.Text.Trim();
    }

    private void CancelSearch(UITextInputComponent field)
    {
        if (AcceptsActiveInput)
            field.Text = _search;
    }

    private string RootFolder => _engineFiles
        ? Engine.Assets.EngineAssetsPath : Engine.Assets.GameAssetsPath;

    private string CurrentFolder
    {
        get
        {
            string root = Path.GetFullPath(RootFolder);
            if (_folder is null)
                return root;
            string candidate = Path.GetFullPath(_folder);
            return Path.GetRelativePath(root, candidate) is var relative &&
                   relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                ? candidate : root;
        }
    }

    private void RefreshTick()
    {
        if (!AcceptsActiveInput)
            return;
        UpdateAssetMenu();
        if (!AcceptsActiveInput)
            return;
        DateTime now = DateTime.UtcNow;
        if (Interlocked.Exchange(ref _pendingRefresh, 0) == 0 && now - _lastRefresh < TimeSpan.FromSeconds(10))
            return;
        _lastRefresh = now;
        RefreshRows();
    }

    private void FileChanged(AssetFileChangeEventArgs _)
    {
        if (!Volatile.Read(ref _destroying) && Volatile.Read(ref _ownerSubscriptionsActive))
            Interlocked.Exchange(ref _pendingRefresh, 1);
    }
    private void FileRenamed(AssetFileRenameEventArgs _)
    {
        if (!Volatile.Read(ref _destroying) && Volatile.Read(ref _ownerSubscriptionsActive))
            Interlocked.Exchange(ref _pendingRefresh, 1);
    }

    protected override void RefreshRows()
    {
        if (_destroying)
            return;
        DismissAssetMenu();
        if (_destroying)
            return;
        _rows.Clear();
        _paths.Clear();
        string folder = CurrentFolder;
        if (!Directory.Exists(folder))
        {
            int previousCount = _tableRows.Count;
            _tableRows.Replace(_rows);
            SignalRowsChanged(previousCount);
            return;
        }
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(folder).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (_search.Length != 0 && !Path.GetFileName(directory).Contains(_search, StringComparison.OrdinalIgnoreCase))
                    continue;
                ulong id = GetPathId(directory);
                while (_paths.ContainsKey(id))
                    id++;
                _paths.Add(id, directory);
                _rows.Add((id, new AssetRow(directory, Path.GetFileName(directory), true)));
            }
            foreach (string file in Directory.EnumerateFiles(folder).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (_search.Length != 0 && !Path.GetFileName(file).Contains(_search, StringComparison.OrdinalIgnoreCase))
                    continue;
                ulong id = GetPathId(file);
                while (_paths.ContainsKey(id))
                    id++;
                _paths.Add(id, file);
                _rows.Add((id, new AssetRow(file, Path.GetFileName(file), false)));
            }
        }
        catch (IOException exception)
        {
            _rows.Add((ulong.MaxValue, new AssetRow(string.Empty, "Asset scan failed: " + exception.Message, false)));
        }
        catch (UnauthorizedAccessException exception)
        {
            _rows.Add((ulong.MaxValue, new AssetRow(string.Empty, "Asset scan failed: " + exception.Message, false)));
        }
        int count = _tableRows.Count;
        _tableRows.Replace(_rows);
        SignalRowsChanged(count);
    }

    private static ulong GetPathId(string path)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        ulong hash = offset;
        foreach (char value in path)
        {
            hash ^= char.ToUpperInvariant(value);
            hash = unchecked(hash * prime);
        }
        return hash == 0 ? 1 : hash;
    }

    private void DragPointerMoved(ulong id, UIVirtualTableRowComponent source, Vector2 localPoint)
    {
        if (!AcceptsActiveInput)
            return;
        if (!EditorDragDropUtility.IsLeftMouseButtonHeldFor(source))
        {
            _dragStarts.Remove(source);
            return;
        }
        Vector2 point = source.BoundableTransform.LocalToCanvas(localPoint);
        if (!_dragStarts.TryGetValue(source, out Vector2 start))
        {
            _dragStarts[source] = point;
            return;
        }
        if (Vector2.Distance(point, start) >= EditorDragDropUtility.DefaultActivationDistance &&
            BeginAssetDrag(id, source, point))
            _dragStarts[source] = point;
    }

    private bool BeginAssetDrag(ulong id, UIInteractableComponent source, Vector2 point)
        => AcceptsActiveInput && _paths.TryGetValue(id, out string? path) && File.Exists(path) &&
           EditorDragDropUtility.BeginDrag(source,
               string.Equals(Path.GetExtension(path), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase)
                   ? EditorDragDropUtility.CreateAssetPayload(path)
                   : EditorDragDropUtility.CreateFilePayload(path), point);

    private bool DropFileIntoCurrentFolder(EditorDragDropUtility.DragPayload payload)
    {
        if (!AcceptsActiveInput) return false;
        if (!EditorDragDropUtility.TryGetAssetOrFilePath(payload, out string? source) || source is null)
            return false;
        string? sourceRoot = FindAssetRoot(source);
        if (sourceRoot is null)
            return false;
        bool committed = false;
        try
        {
            string moved = NativeAssetFileMutations.MoveFileToDirectory(sourceRoot, RootFolder, source, CurrentFolder);
            committed = true;
            if (!AcceptsActiveInput) return true;
            SelectedPath = moved;
            NativeAssetSelection.Select(moved);
            if (!AcceptsActiveInput) return true;
            RefreshRows();
            return true;
        }
        catch (Exception exception)
        {
            Debug.UI(committed
                ? $"[NativeAssets] File moved, but the asset view could not refresh: {exception.Message}"
                : $"[NativeAssets] Could not move file: {exception.Message}");
            return committed;
        }
    }

    private static string? FindAssetRoot(string path)
    {
        foreach (string root in new[] { Engine.Assets.GameAssetsPath, Engine.Assets.EngineAssetsPath }
                     .OrderByDescending(value => value.Length))
        {
            try
            {
                NativeAssetPathPolicy.RequireUnderRoot(root, path, allowRoot: false);
                return root;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // The next registered asset root may own this path.
            }
        }
        return null;
    }

    private void CreateFolder()
    {
        if (!AcceptsActiveInput) return;
        try
        {
            string created = NativeAssetFileMutations.CreateFolder(RootFolder, CurrentFolder, _nameInput?.Text ?? string.Empty);
            if (!AcceptsActiveInput) return;
            Folder = created;
            if (AcceptsActiveInput && _nameInput is not null)
                _nameInput.Text = string.Empty;
        }
        catch (Exception exception)
        {
            Debug.UI($"[NativeAssets] Could not create folder: {exception.Message}");
        }
    }

    private void RenameSelection()
    {
        if (!AcceptsActiveInput) return;
        try
        {
            string path = SelectedPath ?? CurrentFolder;
            string renamed = NativeAssetFileMutations.Rename(RootFolder, path, _nameInput?.Text ?? string.Empty);
            if (!AcceptsActiveInput) return;
            if (Directory.Exists(renamed))
                Folder = renamed;
            else
            {
                SelectedPath = renamed;
                NativeAssetSelection.Select(renamed);
                if (!AcceptsActiveInput) return;
                RefreshRows();
            }
            if (AcceptsActiveInput && _nameInput is not null)
                _nameInput.Text = string.Empty;
        }
        catch (Exception exception)
        {
            Debug.UI($"[NativeAssets] Could not rename selection: {exception.Message}");
        }
    }

    private async Task ImportSelectionAsync()
    {
        if (!AcceptsActiveInput) return;
        string? selected = SelectedPath;
        if (selected is null || !File.Exists(selected) ||
            string.Equals(Path.GetExtension(selected), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            NativeAssetFileMutations.RequireUnderRoot(RootFolder, selected, allowRoot: false);
            if (!await Engine.Assets.ReimportThirdPartyFileAsync(selected).ConfigureAwait(false))
                Debug.UI($"[NativeAssets] Import failed for {Path.GetFileName(selected)}.");
            if (!Volatile.Read(ref _destroying) && Volatile.Read(ref _ownerSubscriptionsActive))
                Interlocked.Exchange(ref _pendingRefresh, 1);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, $"[NativeAssets] Import failed for '{selected}'.");
        }
    }
}
