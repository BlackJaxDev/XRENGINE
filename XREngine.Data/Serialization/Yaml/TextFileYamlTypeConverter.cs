using System;
using System.IO;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

[YamlTypeConverter]
public sealed class TextFileYamlTypeConverter : IWriteOnlyYamlTypeConverter
{
    [ThreadStatic]
    private static TextFile? _inlineTextFile;

    public bool Accepts(Type type)
        => _inlineTextFile is null && type == typeof(TextFile);

    internal static bool IsWritingInline(TextFile textFile)
        => ReferenceEquals(_inlineTextFile, textFile);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        => throw new NotSupportedException($"{nameof(TextFileYamlTypeConverter)} is write-only; reading is handled by the registered XRAsset deserializer.");

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is null)
        {
            emitter.Emit(new Scalar("~"));
            return;
        }

        if (value is not TextFile textFile)
            throw new YamlException($"Expected {nameof(TextFile)} but got '{value.GetType()}'.");

        IAssetSerializationServices services = AssetSerializationServices.Current;
        bool allowReference = true;
        if (TryGetNonAssetBackingPath(textFile, services, out string? backingPath))
        {
            BackingFileState state = CompareBackingFile(backingPath!, textFile.Text ?? string.Empty, out bool unchanged);
            allowReference = state == BackingFileState.Missing;
            if (unchanged)
            {
                emitter.Emit(new Scalar(MakePortableAssetPath(backingPath!, services)));
                return;
            }
        }

        if (allowReference && TryWriteAsReference.TryEmitReference(emitter, textFile, services))
            return;

        TextFile? previous = _inlineTextFile;
        _inlineTextFile = textFile;
        try
        {
            serializer(value, value.GetType());
        }
        finally
        {
            _inlineTextFile = previous;
        }
    }

    private static string MakePortableAssetPath(string absolutePath, IAssetSerializationServices services)
    {
        string? relative = TryMakeRelativeUnderRoot(absolutePath, services.GameAssetsPath)
                           ?? TryMakeRelativeUnderRoot(absolutePath, services.EngineAssetsPath);
        return relative ?? absolutePath;
    }

    private static string? TryMakeRelativeUnderRoot(string absolutePath, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }

        // Use case-insensitive prefix match on Windows; Path.GetRelativePath alone would happily
        // emit "../.." escapes if the file is outside the root, which we don't want.
        string normalizedAbsolute = absolutePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedAbsolute.Length <= normalizedRoot.Length
            || !normalizedAbsolute.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || (normalizedAbsolute[normalizedRoot.Length] != Path.DirectorySeparatorChar
                && normalizedAbsolute[normalizedRoot.Length] != Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        string relative = Path.GetRelativePath(normalizedRoot, absolutePath);
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            return null;

        // Forward slashes for cross-platform portability.
        return relative.Replace('\\', '/');
    }

    private enum BackingFileState { Missing, Present, Unavailable }

    private static BackingFileState CompareBackingFile(string path, string currentText, out bool unchanged)
    {
        unchanged = false;
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        try
        {
            read.EnsureHostFileAccess("YAML text backing-file comparison");
        }
        catch (NotSupportedException)
        {
            // A backing path is optional. Keep text inline on hosts without file access.
            read.EnsureCurrent();
            return BackingFileState.Unavailable;
        }

        IRuntimeFileDiscovery? discovery = RuntimeFileDiscoveryServices.Capture(out long generation);
        bool exists;
        try
        {
            exists = RuntimeFileDiscoveryServices.HostFileExists(
                read, path, "YAML text backing-file comparison", discovery, generation);
        }
        catch (NotSupportedException)
        {
            // An optional comparison must retain edited text when no read provider is installed.
            using IDisposable unavailablePublication = read.BeginPublication();
            if (!RuntimeFileDiscoveryServices.IsCurrent(discovery, generation))
                throw new OperationCanceledException("AssetSource.StaleFileDiscovery: the asset file discovery installation changed during the text comparison.");
            return BackingFileState.Unavailable;
        }
        if (!exists)
            return BackingFileState.Missing;

        bool matches = false;
        try
        {
            byte[] bytes = discovery is IRuntimeHostFileReadBackend files
                ? files.ReadAllBytes(path)
                : read.ReadAllBytes(path);
            // Decode one owner's bytes with the same BOM policy as the original path read.
            // StreamReader also preserves the legacy UTF-7 preamble behavior.
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(stream, TextFile.GetEncoding(bytes, out _), detectEncodingFromByteOrderMarks: true);
            matches = string.Equals(reader.ReadToEnd(), currentText, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // An unreadable backing file must not discard the in-memory text.
        }

        using IDisposable publication = read.BeginPublication();
        if (!RuntimeFileDiscoveryServices.IsCurrent(discovery, generation))
            throw new OperationCanceledException("AssetSource.StaleFileDiscovery: the asset file discovery installation changed during the text comparison.");
        unchanged = matches;
        return BackingFileState.Present;
    }

    private static bool TryGetNonAssetBackingPath(TextFile textFile, IAssetSerializationServices services, out string? backingPath)
    {
        backingPath = GetPreferredBackingPath(textFile);
        if (string.IsNullOrWhiteSpace(backingPath))
            return false;

        try
        {
            backingPath = Path.GetFullPath(backingPath);
        }
        catch
        {
            backingPath = null;
            return false;
        }

        if (string.Equals(Path.GetExtension(backingPath), $".{services.AssetExtension}", StringComparison.OrdinalIgnoreCase))
        {
            backingPath = null;
            return false;
        }

        return true;
    }

    private static string? GetPreferredBackingPath(TextFile textFile)
    {
        if (!string.IsNullOrWhiteSpace(textFile.OriginalPath))
            return textFile.OriginalPath;

        if (!string.IsNullOrWhiteSpace(textFile.FilePath))
            return textFile.FilePath;

        return null;
    }
}
