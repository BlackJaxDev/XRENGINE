using XREngine.Core.Files;
using XREngine.Core.Attributes;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI.Components;

[RequireComponents(typeof(UIScrollingTransform))]
public partial class UIProjectBrowserComponent : UIComponent
{
    private Dictionary<string, FileInfo> _fileCache = [];
    private EArrangement _arrangement = EArrangement.List;
    private float _gridItemSize = 100.0f;
    private bool _showHiddenFiles = false;
    private EFileDetails _displayedFileDetails = EFileDetails.Name | EFileDetails.Size | EFileDetails.Date | EFileDetails.Type;
    private bool _showEngineFiles = false;
    private EProjectBrowserFileEventFamily _subscribedFamily;
    private bool _ownerActive;
    private bool _terminal;

    public Dictionary<string, FileInfo> FileCache
    {
        get => _fileCache;
        //private set => SetField(ref _fileCache, value);
    }
    public EArrangement Arrangement
    {
        get => _arrangement;
        set => SetField(ref _arrangement, value);
    }
    public float GridItemSize
    {
        get => _gridItemSize;
        set => SetField(ref _gridItemSize, value);
    }
    public bool ShowHiddenFiles
    {
        get => _showHiddenFiles;
        set => SetField(ref _showHiddenFiles, value);
    }
    public EFileDetails DisplayedFileDetails
    {
        get => _displayedFileDetails;
        set => SetField(ref _displayedFileDetails, value);
    }
    public bool ShowEngineFiles
    {
        get => _showEngineFiles;
        set => SetField(ref _showEngineFiles, value);
    }

    public string RootFolderPath => ShowEngineFiles 
        ? Engine.Assets.EngineAssetsPath
        : Engine.Assets.GameAssetsPath;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (_terminal || !IsActiveInHierarchy)
            return;
        _ownerActive = true;
        _fileCache = GetCurrentFiles();
        LinkCallbacks();
    }

    protected override void OnComponentDeactivated()
    {
        _ownerActive = false;
        try { UnlinkCallbacks(); }
        finally { base.OnComponentDeactivated(); }
    }

    protected override void OnDestroying()
    {
        _terminal = true;
        _ownerActive = false;
        try { UnlinkCallbacks(); }
        finally { base.OnDestroying(); }
    }

    private void LinkCallbacks()
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy)
            return;
        EProjectBrowserFileEventFamily family = ShowEngineFiles
            ? EProjectBrowserFileEventFamily.Engine : EProjectBrowserFileEventFamily.Game;
        if (_subscribedFamily == family)
            return;
        UnlinkCallbacks();
        _subscribedFamily = family;
        try
        {
            if (family == EProjectBrowserFileEventFamily.Engine)
            {
                Engine.Assets.EngineFileChanged += FileChanged;
                Engine.Assets.EngineFileDeleted += FileDeleted;
                Engine.Assets.EngineFileRenamed += FileRenamed;
                Engine.Assets.EngineFileCreated += FileCreated;
            }
            else
            {
                Engine.Assets.GameFileChanged += FileChanged;
                Engine.Assets.GameFileDeleted += FileDeleted;
                Engine.Assets.GameFileRenamed += FileRenamed;
                Engine.Assets.GameFileCreated += FileCreated;
            }
        }
        catch { UnlinkCallbacks(); throw; }
    }
    private void UnlinkCallbacks()
    {
        EProjectBrowserFileEventFamily family = _subscribedFamily;
        _subscribedFamily = EProjectBrowserFileEventFamily.None;
        if (family == EProjectBrowserFileEventFamily.Engine)
        {
            Engine.Assets.EngineFileChanged -= FileChanged;
            Engine.Assets.EngineFileDeleted -= FileDeleted;
            Engine.Assets.EngineFileRenamed -= FileRenamed;
            Engine.Assets.EngineFileCreated -= FileCreated;
        }
        else if (family == EProjectBrowserFileEventFamily.Game)
        {
            Engine.Assets.GameFileChanged -= FileChanged;
            Engine.Assets.GameFileDeleted -= FileDeleted;
            Engine.Assets.GameFileRenamed -= FileRenamed;
            Engine.Assets.GameFileCreated -= FileCreated;
        }
    }

    private void FileCreated(AssetFileChangeEventArgs args)
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
        string path = args.FullPath;
        if (_fileCache.ContainsKey(path))
            return;

        _fileCache.Add(path, new FileInfo(args.FullPath));
    }

    private void FileRenamed(AssetFileRenameEventArgs args)
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
        string prevPath = args.OldFullPath;
        if (!_fileCache.ContainsKey(prevPath))
            return;

        _fileCache.Remove(prevPath);
        _fileCache.Add(args.FullPath, new FileInfo(args.FullPath));
    }

    private void FileDeleted(AssetFileChangeEventArgs args)
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
        string path = args.FullPath;
        if (!_fileCache.ContainsKey(path))
            return;

        _fileCache.Remove(path);
    }

    private void FileChanged(AssetFileChangeEventArgs args)
    {

    }

    public Dictionary<string, FileInfo> GetCurrentFiles()
    {
        DirectoryInfo dir = new(RootFolderPath);
        if (!dir.Exists)
            return [];
        FileInfo[] files = dir.GetFiles();
        if (!ShowHiddenFiles)
            files = files.Where(f => (f.Attributes & FileAttributes.Hidden) == 0).ToArray();
        return files.ToDictionary(static f => f.FullName);
    }

    protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
    {
        bool change = base.OnPropertyChanging(propName, field, @new);
        if (change)
        {
            switch (propName)
            {
                case nameof(ShowEngineFiles):
                    UnlinkCallbacks();
                    break;
            }
        }
        return change;
    }
    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(ShowHiddenFiles):
            case nameof(ShowEngineFiles):
                if (!_terminal && _ownerActive && IsActiveInHierarchy)
                {
                    LinkCallbacks();
                    _fileCache = GetCurrentFiles();
                }
                break;
            case nameof(Arrangement):
                switch (Arrangement)
                {
                    case EArrangement.List:
                        ImmediateChild?.SetTransform<UIListTransform>();
                        break;
                    case EArrangement.Grid:
                        ImmediateChild?.SetTransform<UIGridTransform>();
                        break;
                }
                break;
        }
    }

    public SceneNode? ImmediateChild => SceneNode.FirstChild;
}
