using System.Numerics;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Browses the game or engine asset directory with recycled rows.</summary>
public class AssetsPanel : NativeVirtualTextPanel
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

    public string? SelectedPath { get; private set; }
    public event Action<string>? AssetSelected;
    public event Action? ViewStateChanged;
    public string Folder
    {
        get => _folder ?? string.Empty;
        set { _folder = string.IsNullOrWhiteSpace(value) ? null : value; RefreshRows(); ViewStateChanged?.Invoke(); }
    }
    public bool UseEngineAssets
    {
        get => _engineFiles;
        set { _engineFiles = value; _folder = null; RefreshRows(); ViewStateChanged?.Invoke(); }
    }
    public string Search
    {
        get => _search;
        set
        {
            _search = value ?? string.Empty;
            if (_searchInput is not null)
                _searchInput.Text = _search;
            RefreshRows();
            ViewStateChanged?.Invoke();
        }
    }

    public AssetsPanel()
    {
        _table = new UIVirtualTableProvider<AssetRow>(_tableRows,
            UIVirtualTableColumn<AssetRow>.Text("kind", "Type", 76, row => row.Path.Length == 0 ? "Error" : row.IsDirectory ? "Folder" : "Asset"),
            UIVirtualTableColumn<AssetRow>.Text("name", "Name", 420, row => row.Name));
        _table.Activated += ActivateAssetRow;
        _table.RowPointerMoved += DragPointerMoved;
        _table.RowPointerLeft += source => _dragStarts.Remove(source);
        RowProviderOverride = _table;
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        SelectedPath = NativeAssetSelection.SelectedPath;
        Engine.Assets.GameFileCreated += FileChanged;
        Engine.Assets.GameFileDeleted += FileChanged;
        Engine.Assets.GameFileRenamed += FileRenamed;
        Engine.Assets.EngineFileCreated += FileChanged;
        Engine.Assets.EngineFileDeleted += FileChanged;
        Engine.Assets.EngineFileRenamed += FileRenamed;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
        Engine.Assets.GameFileCreated -= FileChanged;
        Engine.Assets.GameFileDeleted -= FileChanged;
        Engine.Assets.GameFileRenamed -= FileRenamed;
        Engine.Assets.EngineFileCreated -= FileChanged;
        Engine.Assets.EngineFileDeleted -= FileChanged;
        Engine.Assets.EngineFileRenamed -= FileRenamed;
        base.OnComponentDeactivated();
    }

    protected override void CreateCommands(SceneNode commands)
    {
        AddCommand("Up", () => Folder = Path.GetDirectoryName(CurrentFolder) ?? string.Empty);
        AddCommand("Game", () => UseEngineAssets = false);
        AddCommand("Engine", () => UseEngineAssets = true);
        AddCommand("Refresh", RefreshRows);
        SceneNode searchNode = commands.NewChild();
        searchNode.SetTransform<UIBoundableTransform>().Width = 144;
        var (input, _, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(searchNode);
        _searchInput = input;
        input.SingleLineMode = true;
        input.Text = _search;
        input.Submitted += field => Search = field.Text.Trim();
        input.Cancelled += field => field.Text = _search;
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
        DateTime now = DateTime.UtcNow;
        if (Interlocked.Exchange(ref _pendingRefresh, 0) == 0 && now - _lastRefresh < TimeSpan.FromSeconds(10))
            return;
        _lastRefresh = now;
        RefreshRows();
    }

    private void FileChanged(AssetFileChangeEventArgs _) => Interlocked.Exchange(ref _pendingRefresh, 1);
    private void FileRenamed(AssetFileRenameEventArgs _) => Interlocked.Exchange(ref _pendingRefresh, 1);

    protected override void RefreshRows()
    {
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

    private void ActivateAssetRow(ulong id)
    {
        if (!_paths.TryGetValue(id, out string? path))
            return;
        if (Directory.Exists(path))
        {
            Folder = path;
            return;
        }
        SelectedPath = path;
        NativeAssetSelection.Select(path);
        AssetSelected?.Invoke(path);
    }

    private void DragPointerMoved(ulong id, UIVirtualTableRowComponent source, Vector2 localPoint)
    {
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
        => _paths.TryGetValue(id, out string? path) && File.Exists(path) &&
           EditorDragDropUtility.BeginDrag(source, EditorDragDropUtility.CreateAssetPayload(path), point);
}
