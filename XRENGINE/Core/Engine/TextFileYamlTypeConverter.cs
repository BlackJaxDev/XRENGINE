using System;
using System.IO;
using XREngine.Core.Files;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

[YamlTypeConverter]
public sealed class TextFileYamlTypeConverter : IWriteOnlyYamlTypeConverter
{
    [ThreadStatic]
    private static bool _skip;

    public bool Accepts(Type type)
        => !_skip && type == typeof(TextFile);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        => throw new NotSupportedException($"{nameof(TextFileYamlTypeConverter)} is write-only; reading is handled by {nameof(XRAssetDeserializer)}.");

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is null)
        {
            emitter.Emit(new Scalar("~"));
            return;
        }

        if (value is not TextFile textFile)
            throw new YamlException($"Expected {nameof(TextFile)} but got '{value.GetType()}'.");

        if (TryWriteBackingPath(emitter, textFile))
            return;

        if (!HasNonAssetBackingFile(textFile) && TryWriteAsReference.TryEmitReference(emitter, textFile))
            return;

        bool previous = _skip;
        _skip = true;
        try
        {
            serializer(value, value.GetType());
        }
        finally
        {
            _skip = previous;
        }
    }

    private static bool TryWriteBackingPath(IEmitter emitter, TextFile textFile)
    {
        if (!TryGetUnchangedBackingPath(textFile, out string? backingPath))
            return false;

        // Emit a workspace-portable form when the file lives under a known asset root so the
        // serialized scalar survives moving the project to a different machine layout. Falls
        // back to the absolute path when the file is outside all known roots; the reader's
        // rebasing logic can still recover absolute paths that contain a known segment.
        string scalarPath = MakePortableAssetPath(backingPath!);
        emitter.Emit(new Scalar(scalarPath));
        return true;
    }

    private static string MakePortableAssetPath(string absolutePath)
    {
        string? relative = TryMakeRelativeUnderRoot(absolutePath, Engine.Assets.GameAssetsPath)
                           ?? TryMakeRelativeUnderRoot(absolutePath, Engine.Assets.EngineAssetsPath);
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

    private static bool TryGetUnchangedBackingPath(TextFile textFile, out string? backingPath)
    {
        backingPath = null;

        string? candidatePath = GetPreferredBackingPath(textFile);
        if (string.IsNullOrWhiteSpace(candidatePath))
            return false;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidatePath);
        }
        catch
        {
            return false;
        }

        if (string.Equals(Path.GetExtension(fullPath), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!File.Exists(fullPath))
            return false;

        string currentText = textFile.Text ?? string.Empty;
        string diskText;
        try
        {
            diskText = File.ReadAllText(fullPath, TextFile.GetEncoding(fullPath));
        }
        catch
        {
            return false;
        }

        if (!string.Equals(diskText, currentText, StringComparison.Ordinal))
            return false;

        backingPath = fullPath;
        return true;
    }

    private static bool HasNonAssetBackingFile(TextFile textFile)
        => TryGetNonAssetBackingPath(textFile, out _);

    private static bool TryGetNonAssetBackingPath(TextFile textFile, out string? backingPath)
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

        if (string.Equals(Path.GetExtension(backingPath), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
        {
            backingPath = null;
            return false;
        }

        return File.Exists(backingPath);
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
