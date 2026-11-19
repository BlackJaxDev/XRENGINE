using System.Text;
using System.Text.Json;

namespace XREngine.UnityScriptHost.Admission;

public sealed partial class UnitySourceInventoryBuilder
{
    private void ReadFileMetadata(FileStream stream, string path, string extension)
    {
        bool optionalManifest = path.EndsWith("/package.json", StringComparison.Ordinal) && !IsEmbeddedPackageManifest(path);
        long availableBytes = optionalManifest
            ? MaxOptionalMetadataBytes - _optionalMetadataBytes
            : MaxRequiredMetadataBytes - _requiredMetadataBytes;
        if (stream.Length > MaxMetadataBytes || stream.Length > availableBytes)
        {
            if (!optionalManifest)
                throw new InvalidDataException($"Metadata exceeds the inspection limit: '{path}'.");
            AddDiagnostic(new("UNITYHOST1004", path, "Loose package.json data exceeded metadata limits. Its bytes were fingerprinted without package interpretation."));
            return;
        }
        if (optionalManifest)
            _optionalMetadataBytes += stream.Length;
        else
            _requiredMetadataBytes += stream.Length;
        stream.Position = 0;
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, leaveOpen: true);
        try
        {
            ReadMetadata(path, extension, reader.ReadToEnd());
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or
            InvalidOperationException or KeyNotFoundException or DecoderFallbackException)
        {
            if (!optionalManifest)
                throw new InvalidDataException($"Invalid metadata in '{path}': {exception.Message}", exception);
            AddDiagnostic(new("UNITYHOST1004", path, "Loose package.json data is not a supported package manifest. Its bytes were fingerprinted without package interpretation."));
        }
    }

    private static bool IsEmbeddedPackageManifest(string path)
        => path.StartsWith("Packages/", StringComparison.Ordinal) && path.Count(static character => character == '/') == 2;

    private static bool IsMetadata(string path, string extension)
        => extension is ".asmdef" or ".asmref" || path.EndsWith("/package.json", StringComparison.Ordinal) ||
            path is "Packages/manifest.json" or "Packages/packages-lock.json" or "ProjectSettings/ProjectVersion.txt";

    private void ReadMetadata(string path, string extension, string text)
    {
        if (path == "ProjectSettings/ProjectVersion.txt")
        {
            foreach (string line in text.Split('\n'))
                if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                {
                    _editorVersion = line["m_EditorVersion:".Length..].Trim();
                    ValidateTextLength(_editorVersion);
                }
            return;
        }

        using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Expected a JSON object in '{path}'.");

        if (extension == ".asmdef")
        {
            var assembly = new UnityAssemblyDefinition
            {
                Path = path,
                Name = RequiredString(root, "name"),
                References = StringArray(root, "references"),
                PrecompiledReferences = StringArray(root, "precompiledReferences"),
                IncludePlatforms = StringArray(root, "includePlatforms"),
                ExcludePlatforms = StringArray(root, "excludePlatforms"),
                DefineConstraints = StringArray(root, "defineConstraints"),
                OptionalUnityReferences = StringArray(root, "optionalUnityReferences"),
                OverrideReferences = OptionalBoolean(root, "overrideReferences", false),
                AllowUnsafeCode = OptionalBoolean(root, "allowUnsafeCode", false),
                AutoReferenced = OptionalBoolean(root, "autoReferenced", true),
                NoEngineReferences = OptionalBoolean(root, "noEngineReferences", false),
                VersionDefines = ReadVersionDefines(root),
            };
            _assemblies.Add(assembly);
            foreach (string reference in assembly.PrecompiledReferences)
                AddDiagnostic(new("UNITYHOST1002", path,
                    $"Assembly '{assembly.Name}' declares precompiled reference '{reference}'. Its platform/define selection is not resolved and binary execution is not admitted."));
        }
        else if (extension == ".asmref")
        {
            _references.Add(new(path, RequiredString(root, "reference")));
        }
        else if (path.EndsWith("/package.json", StringComparison.Ordinal))
        {
            _packages.Add(new(path, RequiredString(root, "name"), RequiredString(root, "version"), IsEmbeddedPackageManifest(path),
                StringMap(root, "dependencies"), StringMap(root, "vpmDependencies")));
        }
        else
        {
            AddDiagnostic(new("UNITYHOST1003", path,
                "Project dependency manifest was fingerprinted. Registry, Git, file dependencies, and lock-file resolution are not executed or admitted."));
        }
    }

    private static string RequiredString(JsonElement root, string name)
    {
        string? value = root.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Required JSON string '{name}' is empty.");
        ValidateTextLength(value);
        return value;
    }

    private static string[] StringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
            return [];
        var result = new List<string>();
        foreach (JsonElement element in value.EnumerateArray())
        {
            if (result.Count >= MaxMetadataItems)
                throw new InvalidDataException($"JSON array '{name}' exceeds the item limit.");
            string? item = element.GetString();
            if (string.IsNullOrWhiteSpace(item))
                throw new InvalidDataException($"JSON array '{name}' contains an empty reference.");
            ValidateTextLength(item);
            result.Add(item);
        }
        return [.. result];
    }

    private static bool OptionalBoolean(JsonElement root, string name, bool fallback)
        => root.TryGetProperty(name, out JsonElement value) ? value.GetBoolean() : fallback;

    private static SortedDictionary<string, string> StringMap(JsonElement root, string name)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty(name, out JsonElement value))
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (result.Count >= MaxMetadataItems)
                    throw new InvalidDataException("The dependency count exceeds the inspection limit.");
                ValidateTextLength(property.Name);
                string? constraint = property.Value.GetString();
                if (constraint is not null)
                    ValidateTextLength(constraint);
                if (string.IsNullOrWhiteSpace(constraint) || !result.TryAdd(property.Name, constraint))
                    throw new InvalidDataException($"Invalid or duplicate dependency '{property.Name}'.");
            }
        return result;
    }

    private static UnityAssemblyVersionDefine[] ReadVersionDefines(JsonElement root)
    {
        if (!root.TryGetProperty("versionDefines", out JsonElement value))
            return [];
        var result = new List<UnityAssemblyVersionDefine>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (result.Count >= MaxMetadataItems)
                throw new InvalidDataException("The version define count exceeds the inspection limit.");
            string expression = item.TryGetProperty("expression", out JsonElement expressionValue)
                ? expressionValue.GetString() ?? string.Empty : string.Empty;
            ValidateTextLength(expression);
            result.Add(new(RequiredString(item, "name"), expression, RequiredString(item, "define")));
        }
        return [.. result];
    }

    private static void ValidateTextLength(string value)
    {
        if (value.Length > MaxMetadataStringLength)
            throw new InvalidDataException("Metadata text exceeds the inspection limit.");
    }
}
