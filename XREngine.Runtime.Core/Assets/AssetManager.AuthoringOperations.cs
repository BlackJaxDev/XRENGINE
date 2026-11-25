using System.IO;
using XREngine.Core.Files;

namespace XREngine;

public partial class AssetManager
{
    private AssetAuthoringTicket CreateAuthoringTicket(AuthoringContext context)
        => new(this, context, context.AssetsRoot, context.MetadataRoot, context.CacheRoot,
            context.ProjectRoot);

    /// <summary>Checks whether a deferred authoring job still belongs to this project context.</summary>
    public bool IsCurrentAuthoringTicket(AssetAuthoringTicket ticket)
    {
        lock (_metadataLock)
            return TryGetCurrentTicketContext(ticket, out _);
    }

    /// <summary>Checks a deferred source before import work starts.</summary>
    public bool IsLiveAuthoringSource(AssetAuthoringTicket ticket, string sourcePath)
    {
        if (!TryNormalizePath(sourcePath, out sourcePath))
            return false;
        lock (_metadataLock)
            return TryGetCurrentTicketContext(ticket, out AuthoringContext context)
                && TryMapMetadataPath(context, sourcePath, out _, out _)
                && context.Files.FileExists(sourcePath)
                && TryGetCurrentTicketContext(ticket, out _);
    }

    /// <summary>Moves a source's import options without replacing another options file.</summary>
    public bool TryMoveImportOptions(AssetAuthoringTicket ticket, string oldSourcePath,
        string newSourcePath, string oldOptionsPath, string newOptionsPath)
    {
        if (!TryNormalizePath(oldSourcePath, out oldSourcePath) ||
            !TryNormalizePath(newSourcePath, out newSourcePath) ||
            !TryNormalizePath(oldOptionsPath, out oldOptionsPath) ||
            !TryNormalizePath(newOptionsPath, out newOptionsPath))
            return false;
        lock (_metadataLock)
        {
            if (!TryGetCurrentTicketContext(ticket, out AuthoringContext context) ||
                context.Files is not IAssetAuthoringPathBackend authoring ||
                !TryMapMetadataPath(context, oldSourcePath, out _, out _) ||
                !TryMapMetadataPath(context, newSourcePath, out _, out _) ||
                IsLivePath(context, oldSourcePath) || !context.Files.FileExists(newSourcePath) ||
                !IsExpectedOptionsPath(context, authoring, oldSourcePath, oldOptionsPath) ||
                !IsExpectedOptionsPath(context, authoring, newSourcePath, newOptionsPath) ||
                !Path.GetFileName(oldOptionsPath).AsSpan(Path.GetFileName(oldSourcePath).Length)
                    .SequenceEqual(Path.GetFileName(newOptionsPath).AsSpan(Path.GetFileName(newSourcePath).Length)) ||
                !context.Files.FileExists(oldOptionsPath) || context.Files.FileExists(newOptionsPath) ||
                context.Files.DirectoryExists(newOptionsPath) ||
                !TryGetCurrentTicketContext(ticket, out _))
                return false;

            string parent = Path.GetDirectoryName(newOptionsPath)!;
            context.Files.CreateDirectory(parent);
            if (!TryGetCurrentTicketContext(ticket, out _) ||
                IsLivePath(context, oldSourcePath) || !authoring.FileExists(newSourcePath) ||
                authoring.HasLinkedComponent(parent) ||
                authoring.HasLinkedComponent(oldOptionsPath) ||
                authoring.HasLinkedComponent(newOptionsPath) ||
                !TryGetCurrentTicketContext(ticket, out _))
                return false;
            authoring.MoveFile(oldOptionsPath, newOptionsPath, overwrite: false);
            return true;
        }
    }

    /// <summary>Removes a deleted source's import options from the captured cache.</summary>
    public bool TryDeleteImportOptions(AssetAuthoringTicket ticket, string sourcePath, string optionsPath)
    {
        if (!TryNormalizePath(sourcePath, out sourcePath) ||
            !TryNormalizePath(optionsPath, out optionsPath))
            return false;
        lock (_metadataLock)
        {
            if (!TryGetCurrentTicketContext(ticket, out AuthoringContext context) ||
                context.Files is not IAssetAuthoringPathBackend authoring ||
                !TryMapMetadataPath(context, sourcePath, out _, out _) ||
                IsLivePath(context, sourcePath) ||
                !IsExpectedOptionsPath(context, authoring, sourcePath, optionsPath) ||
                !authoring.FileExists(optionsPath) || IsLivePath(context, sourcePath) ||
                !TryGetCurrentTicketContext(ticket, out _))
                return false;
            authoring.DeleteFile(optionsPath);
            return true;
        }
    }

    /// <summary>Publishes one staged generated root after a final context and path check.</summary>
    public bool TryPublishGeneratedRoot(AssetAuthoringTicket ticket, string stagedPath, string rootAssetPath)
    {
        if (!TryNormalizePath(stagedPath, out stagedPath) ||
            !TryNormalizePath(rootAssetPath, out rootAssetPath))
            return false;
        lock (_metadataLock)
        {
            if (!TryGetCurrentTicketContext(ticket, out AuthoringContext context) ||
                context.Files is not IAssetAuthoringPathBackend authoring ||
                !TryMapMetadataPath(context, rootAssetPath, out _, out _) ||
                !TryMapMetadataPath(context, stagedPath, out _, out _) ||
                !string.Equals(Path.GetExtension(rootAssetPath), ".asset", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(stagedPath), ".publish", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(stagedPath), Path.GetDirectoryName(rootAssetPath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !authoring.FileExists(stagedPath))
                return false;

            if (authoring.HasLinkedComponent(stagedPath) ||
                authoring.HasLinkedComponent(rootAssetPath) ||
                !TryGetCurrentTicketContext(ticket, out _))
                return false;

            if (authoring.FileExists(rootAssetPath))
            {
                if (!TryGetCurrentTicketContext(ticket, out _))
                    return false;
                authoring.ReplaceFile(stagedPath, rootAssetPath);
            }
            else if (!authoring.DirectoryExists(rootAssetPath))
            {
                if (!TryGetCurrentTicketContext(ticket, out _))
                    return false;
                authoring.MoveFile(stagedPath, rootAssetPath, overwrite: false);
            }
            else
                return false;
            return true;
        }
    }

    private bool TryGetCurrentTicketContext(AssetAuthoringTicket ticket, out AuthoringContext context)
    {
        context = ticket.Context as AuthoringContext ?? null!;
        return context is not null && ReferenceEquals(ticket.Owner, this) &&
            ReferenceEquals(_gameWatcherContext, context) && !_authoringContextRebinding &&
            IsCurrentAuthoringContext(context);
    }

    private static bool IsExpectedOptionsPath(AuthoringContext context,
        IAssetAuthoringPathBackend authoring, string sourcePath, string optionsPath)
    {
        if (context.CacheRoot is null ||
            !TryMapMetadataPath(context, sourcePath, out _, out string relativeSource))
            return false;
        string expectedDirectory = Path.Combine(context.CacheRoot,
            Path.GetDirectoryName(relativeSource) ?? string.Empty);
        string optionsName = Path.GetFileName(optionsPath);
        string prefix = Path.GetFileName(sourcePath) + ".";
        if (!string.Equals(Path.GetDirectoryName(optionsPath), expectedDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !optionsName.StartsWith(prefix, StringComparison.Ordinal) ||
            !optionsName.EndsWith(".import.yaml", StringComparison.OrdinalIgnoreCase))
            return false;

        return !authoring.HasLinkedComponent(sourcePath) && !authoring.HasLinkedComponent(optionsPath);
    }

    private static bool TryNormalizePath(string path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try { fullPath = Path.GetFullPath(path); return true; }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }
}
