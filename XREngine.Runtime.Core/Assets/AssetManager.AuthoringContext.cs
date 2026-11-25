using XREngine.Core.Files;
using XREngine.Data;
using System.IO;

namespace XREngine;

public partial class AssetManager
{
    private long _authoringRevision;
    private AuthoringContext? _gameWatcherContext;
    private bool _authoringContextRebinding = true;
    private string? _authoringProjectRoot;

    private sealed record AuthoringContext(
        long Revision,
        IRuntimeAssetSource? Source,
        int SourceEpoch,
        IAssetFileSystem FileSystem,
        IAssetMetadataFileBackend Files,
        long BackendGeneration,
        string AssetsRoot,
        string MetadataRoot,
        string? CacheRoot,
        string? ProjectRoot,
        IRuntimeAssetAuthoringServices AuthoringServices);

    /// <summary>Invalidates work admitted for an earlier project installation.</summary>
    public void InvalidateAuthoringContext()
    {
        lock (_metadataLock)
        {
            _authoringContextRebinding = true;
            try
            {
                _authoringRevision = unchecked(_authoringRevision + 1);
                GameWatcher.Rebind();
                _gameWatcherContext = TryCaptureAuthoringContext(GameWatcher.CurrentInstallation);
            }
            finally { _authoringContextRebinding = false; }
        }
    }

    /// <summary>Publishes project asset, metadata, and cache roots as one authoring context.</summary>
    public void ConfigureAuthoringRoots(string? assetsRoot, string? metadataRoot, string? cacheRoot,
        string? projectRoot = null)
    {
        string? assets = assetsRoot is null ? null : NormalizeConfiguredDirectoryPath(assetsRoot, nameof(assetsRoot));
        string? metadata = metadataRoot is null ? null : NormalizeConfiguredDirectoryPath(metadataRoot, nameof(metadataRoot));
        string? cache = NormalizeConfiguredDirectoryPath(cacheRoot, nameof(cacheRoot));
        string? project = projectRoot is null ? null : Path.GetFullPath(projectRoot);
        lock (_metadataLock)
        {
            _authoringContextRebinding = true;
            try
            {
                // Recovery records must protect their paths before watchers start.
                OpenCreationRecovery(project);
                if (assets is not null)
                {
                    _gameAssetsPath = assets;
                    GameWatcher.Path = assets;
                }
                if (metadata is not null)
                    _gameMetadataPath = metadata;
                _gameCachePath = cache;
                _authoringProjectRoot = project;
                InvalidateAuthoringContext();
            }
            finally { _authoringContextRebinding = false; }
        }
    }

    /// <summary>Invalidates the project identity when the project closes.</summary>
    public void UnloadAuthoringProject()
    {
        lock (_metadataLock)
        {
            _authoringProjectRoot = null;
            OpenCreationRecovery(null);
            InvalidateAuthoringContext();
        }
    }

    private AuthoringContext? TryCaptureAuthoringContext(AssetChangeMonitorInstallation? installation = null)
    {
        if (string.IsNullOrWhiteSpace(_gameAssetsPath) || string.IsNullOrWhiteSpace(_gameMetadataPath))
            return null;

        IAssetFileSystem? fileSystem;
        long generation;
        if (installation is not null)
        {
            fileSystem = installation.FileSystem;
            generation = installation.Generation;
        }
        else if (!AssetFileSystemServices.TryCapture(out fileSystem, out generation))
            return null;
        if (fileSystem is not IAssetMetadataFileBackend files ||
            !AssetFileSystemServices.IsCurrent(fileSystem, generation))
            return null;

        return new AuthoringContext(
            _authoringRevision, _runtimeAssetSource, RuntimeSourceEpoch, fileSystem, files, generation,
            Path.GetFullPath(_gameAssetsPath), Path.GetFullPath(_gameMetadataPath),
            string.IsNullOrWhiteSpace(_gameCachePath) ? null : Path.GetFullPath(_gameCachePath),
            _authoringProjectRoot,
            RuntimeAssetAuthoringServices.Current);
    }

    private bool IsCurrentAuthoringContext(AuthoringContext context)
        => context.Revision == _authoringRevision
            && ReferenceEquals(context.Source, _runtimeAssetSource)
            && context.SourceEpoch == RuntimeSourceEpoch
            && ReferenceEquals(context.AuthoringServices, RuntimeAssetAuthoringServices.Current)
            && AssetFileSystemServices.IsCurrent(context.FileSystem, context.BackendGeneration);

    private bool TryAdmitGameWatcherEvent(object sender, out AuthoringContext context)
    {
        context = _gameWatcherContext!;
        if (_authoringContextRebinding)
            return false;
        if (context is null)
        {
            InvalidateAuthoringContext();
            return false;
        }
        if (!IsCurrentAuthoringContext(context))
        {
            InvalidateAuthoringContext();
            return false;
        }
        return ReferenceEquals(sender, GameWatcher.CurrentInstallation)
            && IsCurrentHostWatcherSource(context.Source, context.SourceEpoch);
    }

    private static bool TryMapMetadataPath(AuthoringContext context, string assetPath,
        out string metadataPath, out string relativePath)
    {
        metadataPath = string.Empty;
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(assetPath))
            return false;
        string fullPath;
        try { fullPath = Path.GetFullPath(assetPath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
        relativePath = Path.GetRelativePath(context.AssetsRoot, fullPath);
        if (relativePath == "." || relativePath == ".." ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
            return false;
        metadataPath = Path.Combine(context.MetadataRoot, relativePath) + ".meta";
        return true;
    }

    private static bool IsLivePath(AuthoringContext context, string path)
        => context.Files.FileExists(path) || context.Files.DirectoryExists(path);

    private bool IsAdmittedGameWatcherEvent(object sender, AuthoringContext expected,
        string path, bool shouldExist)
    {
        lock (_metadataLock)
            return TryAdmitGameWatcherEvent(sender, out AuthoringContext current)
                && ReferenceEquals(current, expected)
                && IsLivePath(expected, path) == shouldExist;
    }

    private bool IsAdmittedGameWatcherRename(object sender, AuthoringContext expected,
        string oldPath, string newPath)
    {
        lock (_metadataLock)
            return TryAdmitGameWatcherEvent(sender, out AuthoringContext current)
                && ReferenceEquals(current, expected)
                && !IsLivePath(expected, oldPath)
                && IsLivePath(expected, newPath);
    }
}
