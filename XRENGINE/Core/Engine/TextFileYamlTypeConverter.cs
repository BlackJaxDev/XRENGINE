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

        emitter.Emit(new Scalar(backingPath));
        return true;
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
