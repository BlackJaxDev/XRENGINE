using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XREngine.ControlPlane;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Networking;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Serialization;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace XREngine.Editor;

internal static partial class ProjectBuilder
{
    private const int SharedPackageJsonLimit = 1024 * 1024;
    private const int SharedPackageFileLimit = 4 * 1024 * 1024;
    private const long SharedPackageTotalLimit = 64L * 1024 * 1024;

    /// <summary>Admits an explicit native input without changing ordinary local browser publication.</summary>
    private static BrowserSharedWorldPackage PrepareBrowserSharedWorldPackage(XRProject project, string worldPath,
        string assetRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string projectRoot = project.ProjectDirectory
            ?? throw new InvalidDataException("BrowserCook.SharedPackageProjectMissing: save the project before selecting a native package.");
        string selected = project.BrowserSharedWorldPackageManifestPath
            ?? throw new InvalidDataException("BrowserCook.SharedPackagePathInvalid: select a native package manifest.");
        if (Path.IsPathRooted(selected) || Uri.TryCreate(selected, UriKind.Absolute, out _))
            throw new InvalidDataException("BrowserCook.SharedPackagePathInvalid: select a project-relative native package manifest.");
        string descriptorPath = Path.GetFullPath(Path.Combine(projectRoot, selected));
        RequireSharedPackageContainedPath(descriptorPath, projectRoot);
        if (!string.Equals(Path.GetFileName(descriptorPath), "world-package.json", StringComparison.Ordinal))
            throw new InvalidDataException("BrowserCook.SharedPackagePathInvalid: select world-package.json.");
        WorldPackageManifest native = JsonSerializer.Deserialize(ReadSharedPackageFile(descriptorPath, SharedPackageJsonLimit),
            XreControlPlaneJsonContext.Default.WorldPackageManifest)
            ?? throw new InvalidDataException("BrowserCook.SharedPackageInvalid: the native manifest is empty.");
        if (native.GameBootstrapId != "world-v1"
            || native.Asset is null || native.Files is not { Count: 1 } || native.Files[0] is null
            || native.Files[0].RelativePath != native.WorldEntryPoint
            || native.Files[0].Length is < 1 or > SharedPackageFileLimit
            || !IsSharedPackageNativeWorldPath(native.WorldEntryPoint))
            throw new NotSupportedException("BrowserCook.SharedPackageProfileUnsupported: requires one self-contained base XRWorld .asset and world-v1 bootstrap.");
        RequireSharedPackageIdentityBounds(native);
        string nativeRoot = Path.GetDirectoryName(descriptorPath)!;
        string nativePath = Path.GetFullPath(Path.Combine(nativeRoot, native.WorldEntryPoint));
        RequireSharedPackageContainedPath(nativePath, projectRoot);
        byte[] nativeBytes = ReadSharedPackageFile(nativePath, SharedPackageFileLimit);
        RequireSharedPackageContainedPath(worldPath, projectRoot);
        if (!ReadSharedPackageFile(worldPath, SharedPackageFileLimit).AsSpan().SequenceEqual(nativeBytes)
            || nativeBytes.Length != native.Files[0].Length
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(nativeBytes)),
                WorldAssetIdentity.NormalizeHash(native.Files[0].Sha256), StringComparison.Ordinal))
            throw new InvalidDataException("BrowserCook.SharedPackageWorldMismatch: the native entry bytes must equal the saved browser startup world.");
        if (!WorldPackageManifestBuilder.VerifyManifest(native).Success)
            throw new InvalidDataException("BrowserCook.SharedPackageVerificationFailed: the selected native package does not match its canonical identity.");
        Stack<string> directories = new();
        directories.Push(nativeRoot);
        while (directories.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireSharedPackageContainedPath(path, nativeRoot);
                string relative = Path.GetRelativePath(nativeRoot, path).Replace('\\', '/');
                if (Directory.Exists(path) && native.WorldEntryPoint.StartsWith(relative + "/", StringComparison.Ordinal))
                    directories.Push(path);
                else if (relative != native.WorldEntryPoint && relative != "world-package.json")
                    throw new InvalidDataException("BrowserCook.SharedPackageVerificationFailed: the native package contains undeclared files or directories.");
            }
        }
        RequireSelfContainedNativeWorld(nativeBytes);
        string startupWorld = "/game/" + Path.GetRelativePath(assetRoot, worldPath).Replace('\\', '/');
        if (!string.Equals(startupWorld, "/game/" + native.WorldEntryPoint, StringComparison.Ordinal))
            throw new NotSupportedException("BrowserCook.SharedPackagePathMismatch: the saved startup path beneath Assets must match the native manifest-relative entry exactly.");
        cancellationToken.ThrowIfCancellationRequested();
        return new BrowserSharedWorldPackage(native, nativeBytes, startupWorld);
    }

    private static void RequireSharedBrowserCook(BrowserSharedWorldPackage package, string recipePath)
    {
        using JsonDocument recipe = JsonDocument.Parse(ReadSharedPackageFile(recipePath, SharedPackageJsonLimit));
        if (recipe.RootElement.GetProperty("startupWorld").GetString() != package.BrowserWorldPath)
            throw new InvalidDataException("BrowserCook.SharedPackageWorldMismatch: the cooked startup path changed.");
        foreach (JsonElement asset in recipe.RootElement.GetProperty("assets").EnumerateArray())
            if (asset.GetProperty("path").GetString() == package.BrowserWorldPath && asset.GetProperty("dependencies").GetArrayLength() != 0)
            {
                JsonElement dependencies = asset.GetProperty("dependencies");
                if (dependencies.EnumerateArray().Any(static dependency =>
                    dependency.GetString()?.StartsWith("/game/Fonts/Cooked/", StringComparison.Ordinal) == true))
                    throw new NotSupportedException("BrowserCook.SharedPackageFontsUnsupported: authored fonts require typed native package dependencies; use the ordinary browser publication profile.");
                throw new NotSupportedException("BrowserCook.SharedPackageDependenciesUnsupported: native game/engine dependency roots are not supported by the shared package profile.");
            }
    }

    /// <summary>Extends the native manifest with the exact cooked catalog and payloads inside unpublished staging.</summary>
    private static void PublishBrowserSharedWorldPackage(BrowserSharedWorldPackage? request, string outputDirectory,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return;
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(outputDirectory);
        string catalogPath = Path.Combine(root, "manifest.json");
        JsonObject catalog = JsonNode.Parse(ReadSharedPackageFile(catalogPath, SharedPackageJsonLimit)) as JsonObject
            ?? throw new InvalidDataException("BrowserCook.SharedPackageCatalogInvalid: the cooked catalog is not an object.");
        if (catalog["startupWorld"]?.GetValue<string>() != request.BrowserWorldPath || catalog.ContainsKey("worldPackage"))
            throw new InvalidDataException("BrowserCook.SharedPackageCatalogInvalid: the cooked startup binding changed.");
        catalog.Add("worldPackage", "world-package.json");
        byte[] catalogBytes = JsonSerializer.SerializeToUtf8Bytes(catalog);
        if (catalogBytes.Length > SharedPackageJsonLimit)
            throw new InvalidDataException("BrowserCook.SharedPackageBudgetExceeded: the bound catalog exceeds 1 MiB.");
        string nativePath = Path.GetFullPath(Path.Combine(root, request.Manifest.WorldEntryPoint));
        RequireSharedPackageContainedPath(nativePath, root);
        if (File.Exists(nativePath) || File.Exists(Path.Combine(root, "world-package.json")))
            throw new InvalidDataException("BrowserCook.SharedPackageOutputConflict: the native entry or descriptor already exists in browser staging.");
        Directory.CreateDirectory(Path.GetDirectoryName(nativePath)!);
        File.WriteAllBytes(nativePath, request.NativeWorldBytes);
        File.WriteAllBytes(catalogPath, catalogBytes);
        long totalBytes = 0;
        int fileCount = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSharedPackageContainedPath(file, root);
            long length = new FileInfo(file).Length;
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative != "manifest.json" && relative != request.Manifest.WorldEntryPoint
                && !Regex.IsMatch(relative, "^payload/[a-f0-9]{64}\\.bin\\z", RegexOptions.CultureInvariant))
                throw new InvalidDataException("BrowserCook.SharedPackageOutputConflict: an unrecognized file exists in browser content staging.");
            if (length is < 1 or > SharedPackageFileLimit || ++fileCount > 4096
                || (totalBytes += length) > SharedPackageTotalLimit)
                throw new InvalidDataException("BrowserCook.SharedPackageBudgetExceeded: native and browser files exceed the shared content bounds.");
        }
        Dictionary<string, string> metadata = new(request.Manifest.Metadata, StringComparer.OrdinalIgnoreCase);
        metadata.Remove("browserCatalog");
        metadata.Remove("browserStartupWorld");
        metadata.Add("browserCatalog", "manifest.json");
        metadata.Add("browserStartupWorld", request.BrowserWorldPath);
        WorldPackageManifest shared = WorldPackageManifestBuilder.CreateFromDirectory(root, request.Manifest.Asset,
            request.Manifest.PackageId, metadata, request.Manifest.WorldEntryPoint, request.Manifest.GameBootstrapId,
            request.Manifest.BuildVersion);
        RequireSharedPackageIdentityBounds(shared);
        byte[] descriptor = JsonSerializer.SerializeToUtf8Bytes(shared, XreControlPlaneJsonContext.Default.WorldPackageManifest);
        if (descriptor.Length > SharedPackageJsonLimit)
            throw new InvalidDataException("BrowserCook.SharedPackageBudgetExceeded: the shared descriptor exceeds 1 MiB.");
        cancellationToken.ThrowIfCancellationRequested();
        File.WriteAllBytes(Path.Combine(root, "world-package.json"), descriptor);
        if (!WorldPackageManifestBuilder.Verify(shared, root, cancellationToken, requireAssetContentHashMatch: true).Success)
            throw new InvalidDataException("BrowserCook.SharedPackageVerificationFailed: completed shared content did not pass canonical verification.");
    }

    private static bool IsSharedPackageNativeWorldPath(string? path)
        => !string.IsNullOrEmpty(path) && path.Length <= 512 && path.EndsWith(".asset", StringComparison.Ordinal)
            && !path.StartsWith("payload/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("manifest.json/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("world-package.json/", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(path, "^[A-Za-z0-9_./-]+\\z", RegexOptions.CultureInvariant)
            && path.Split('/').All(static part => part.Length != 0 && part is not "." and not "..");

    private static void RequireSharedPackageContainedPath(string path, string root)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative is "." or ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("BrowserCook.SharedPackagePathInvalid: a package path escapes its selected root.");
        for (string? current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("BrowserCook.SharedPackagePathInvalid: linked package files and directories are unsupported.");
            if (string.Equals(current, fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return;
        }
        throw new InvalidDataException("BrowserCook.SharedPackagePathInvalid: the selected root was not reached.");
    }

    private static byte[] ReadSharedPackageFile(string path, int limit)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length < 1 || stream.Length > limit)
            throw new InvalidDataException("BrowserCook.SharedPackageBudgetExceeded: a package source exceeds its file bound.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new IOException("BrowserCook.SharedPackageSourceChanged: a source changed during packaging.");
        return bytes;
    }

    private static void RequireSharedPackageIdentityBounds(WorldPackageManifest manifest)
    {
        if (new[] { manifest.PackageId, manifest.Asset.WorldId, manifest.Asset.RevisionId,
                manifest.BuildVersion, manifest.Asset.RequiredBuildVersion }.Any(static value => string.IsNullOrWhiteSpace(value) || value.Length > 1024)
            || manifest.Asset.AssetSchemaVersion < 1)
            throw new InvalidDataException("BrowserCook.SharedPackageIdentityInvalid: native identity fields exceed browser bounds.");
        foreach (Dictionary<string, string> values in new[] { manifest.Metadata, manifest.Asset.Metadata })
        {
            HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
            if (values is null || values.Count > 64 || values.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 1024
                || pair.Value is null || pair.Value.Length > 4096 || !keys.Add(pair.Key)))
                throw new InvalidDataException("BrowserCook.SharedPackageIdentityInvalid: native metadata exceeds browser bounds.");
        }
    }

    /// <summary>Rejects ambient path/GUID dependencies in the bounded native YAML representation.</summary>
    private static void RequireSelfContainedNativeWorld(byte[] bytes)
    {
        string text = DecodeSharedNativeWorld(bytes);
        using (StringReader boundedReader = new(text))
        {
            YamlDotNet.Core.Parser parser = new(boundedReader);
            int depth = 0, events = 0;
            while (parser.MoveNext())
            {
                if (parser.Current is YamlDotNet.Core.Events.MappingStart or YamlDotNet.Core.Events.SequenceStart)
                    depth++;
                else if (parser.Current is YamlDotNet.Core.Events.MappingEnd or YamlDotNet.Core.Events.SequenceEnd)
                    depth--;
                if (depth > 128 || ++events > 100_000)
                    throw new NotSupportedException("BrowserCook.SharedPackageDependenciesUnsupported: the native YAML exceeds its parsing bound.");
                if (parser.Current is YamlDotNet.Core.Events.NodeEvent node && !node.Tag.IsEmpty
                    && !node.Tag.Value.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal))
                    throw new NotSupportedException("BrowserCook.SharedPackageDependenciesUnsupported: custom native YAML tags require explicit shared-package inspection.");
            }
        }
        YamlStream yaml = new();
        using StringReader reader = new(text);
        yaml.Load(reader);
        if (yaml.Documents.Count != 1)
            throw new NotSupportedException("BrowserCook.SharedPackageDependenciesUnsupported: the native world must be one self-contained YAML document.");
        Dictionary<YamlNode, HashSet<(Type Type, bool OwnedTransform)>> visited = new(ReferenceEqualityComparer.Instance);
        int visits = 0;
        Stack<(YamlNode Node, Type Type, int Depth, bool OwnedTransform)> pending = new();
        pending.Push((yaml.Documents[0].RootNode, typeof(XRWorld), 0, false));
        while (pending.TryPop(out var next))
        {
            if (!visited.TryGetValue(next.Node, out HashSet<(Type Type, bool OwnedTransform)>? seenTypes))
                visited.Add(next.Node, seenTypes = []);
            if (!seenTypes.Add((next.Type, next.OwnedTransform)))
                continue;
            if (next.Depth > 128 || ++visits > 100_000)
                throw new NotSupportedException("BrowserCook.SharedPackageDependenciesUnsupported: the native YAML graph exceeds the inspection bound.");
            if (next.Node is YamlMappingNode mapping)
            {
                Type mappingType = ResolveSharedNativeMappingType(mapping, next.Type);
                if (next.Depth == 0 && mappingType != typeof(XRWorld))
                    throw new NotSupportedException("BrowserCook.SharedPackageProfileUnsupported: the native source must declare the exact base XRWorld type.");
                bool ownedDefaultTransform = next.OwnedTransform && mappingType == typeof(Transform)
                    && IsInlineDefaultTransformIdentity(mapping);
                bool inlineAssetDiscriminator = typeof(XRAsset).IsAssignableFrom(mappingType)
                    && HasLeadingNativeAssetDiscriminator(mapping);
                if (!ownedDefaultTransform && !inlineAssetDiscriminator && typeof(XRObjectBase).IsAssignableFrom(mappingType) && mapping.Children.Count > 0
                    && mapping.Children.Keys.All(static key => key is YamlScalarNode field
                        && (string.Equals(field.Value, "ID", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(field.Value, "Path", StringComparison.OrdinalIgnoreCase)
                            || field.Value is "__assetType" or "__type" or "$type")))
                    throw new NotSupportedException("BrowserCook.SharedPackageDependenciesUnsupported: native ID/Path reference envelopes require ambient lookup.");
                if (typeof(XRAsset).IsAssignableFrom(mappingType)
                    && AssetManager.YamlTypeConverters.Any(converter => converter is not IWriteOnlyYamlTypeConverter && converter.Accepts(mappingType)))
                    throw new NotSupportedException("BrowserCook.SharedPackageDependenciesUnsupported: native asset read converters require explicit self-containment inspection.");
                Type? dictionaryValue = GetSharedNativeGenericArgument(mappingType, typeof(IDictionary<,>), 1);
                foreach ((YamlNode key, YamlNode value) in mapping.Children)
                {
                    if (key is not YamlScalarNode field)
                        throw new NotSupportedException("BrowserCook.SharedPackageDependenciesUnsupported: compound native mapping keys are unsupported.");
                    if (field.Value is "__assetType" or "__type" or "$type")
                        continue;
                    if (mappingType == typeof(WorldSettings) && field.Value == nameof(WorldSettings.SkyboxTexturePath)
                        && value is YamlScalarNode { Value: { Length: > 0 } skybox } && skybox is not "~" and not "null")
                        throw new NotSupportedException("BrowserCook.SharedPackageDependenciesUnsupported: a native skybox path requires package-root support.");
                    if (field.Value == "ID" && value is YamlScalarNode id && Guid.TryParse(id.Value, out _))
                    {
                        continue;
                    }
                    Type memberType = field.Value switch
                    {
                        "$value" => mappingType,
                        "$children" when typeof(TransformBase).IsAssignableFrom(mappingType) => typeof(TransformBase[]),
                        _ => dictionaryValue ?? ResolveSharedNativeMemberType(mappingType, field.Value),
                    };
                    pending.Push((value, memberType, next.Depth + 1,
                        mappingType == typeof(SceneNode) && field.Value == nameof(SceneNode.Transform)));
                }
            }
            else if (next.Node is YamlSequenceNode sequence)
            {
                Type itemType = next.Type.IsArray ? next.Type.GetElementType()!
                    : GetSharedNativeGenericArgument(next.Type, typeof(IEnumerable<>), 0)
                        ?? throw new NotSupportedException("BrowserCook.SharedPackageDependenciesUnsupported: an unrecognized native sequence needs an explicit shared-package codec.");
                foreach (YamlNode child in sequence.Children)
                    pending.Push((child, itemType, next.Depth + 1, false));
            }
            else if (next.Node is YamlScalarNode scalar && scalar.Value is { } value)
            {
                Type scalarType = Nullable.GetUnderlyingType(next.Type) ?? next.Type;
                if (!scalarType.IsValueType && scalarType != typeof(string) && scalarType != typeof(Type)
                    && value is not "" and not "~" and not "null")
                    throw new NotSupportedException("BrowserCook.SharedPackageDependenciesUnsupported: native object and collection references must be inline; scalar GUID and path lookup are unsupported.");
                if (!value.Contains('\n') && (value.Contains('/') || value.Contains('\\')
                    || value.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)))
                    throw new NotSupportedException("BrowserCook.SharedPackageDependenciesUnsupported: path-valued native data requires explicit package-root support.");
            }
        }
    }

    private static bool IsInlineDefaultTransformIdentity(YamlMappingNode mapping)
    {
        if (mapping.Children.Count != 1)
            return false;
        foreach ((YamlNode key, YamlNode value) in mapping.Children)
            return key is YamlScalarNode { Value: "ID" }
                && value is YamlScalarNode scalar && Guid.TryParse(scalar.Value, out Guid id) && id != Guid.Empty;
        return false;
    }

    private static bool HasLeadingNativeAssetDiscriminator(YamlMappingNode mapping)
    {
        // The native nested-asset reader replays discriminator-first mappings as
        // inline objects. ID-first envelopes still use ambient reference lookup.
        foreach (YamlNode key in mapping.Children.Keys)
            return key is YamlScalarNode { Value: "__assetType" or "__type" };
        return false;
    }

    private static string DecodeSharedNativeWorld(byte[] bytes)
    {
        // SaveImmediate writes UTF-8 with a BOM. Keep the exact bytes for package
        // identity, while matching the native file reader's text interpretation.
        int offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes.AsSpan(offset));
    }

    private static Type ResolveSharedNativeMappingType(YamlMappingNode mapping, Type expected)
    {
        Type resolved = Nullable.GetUnderlyingType(expected) ?? expected;
        foreach ((YamlNode key, YamlNode value) in mapping.Children)
        {
            if (key is not YamlScalarNode { Value: "__assetType" or "__type" or "$type" })
                continue;
            if (value is not YamlScalarNode { Value: { } name }
                || AotRuntimeMetadataStore.ResolveType(name) is not { } declared || !resolved.IsAssignableFrom(declared))
                throw new NotSupportedException("BrowserCook.SharedPackageDependenciesUnsupported: the native graph has an unknown or incompatible declared type.");
            resolved = declared;
        }
        // The native transform converter explicitly defaults an untyped mapping to Transform.
        return resolved == typeof(TransformBase) ? typeof(Transform) : resolved;
    }

    private static Type ResolveSharedNativeMemberType(Type type, string? name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        foreach (PropertyInfo property in type.GetProperties(flags))
            if (property.GetIndexParameters().Length == 0 && property.GetCustomAttribute<YamlIgnoreAttribute>() is null
                && (property.GetCustomAttribute<YamlMemberAttribute>()?.Alias ?? property.Name) == name)
                return property.PropertyType;
        foreach (FieldInfo field in type.GetFields(flags))
            if (field.GetCustomAttribute<YamlIgnoreAttribute>() is null
                && (field.GetCustomAttribute<YamlMemberAttribute>()?.Alias ?? field.Name) == name)
                return field.FieldType;
        throw new NotSupportedException("BrowserCook.SharedPackageDependenciesUnsupported: an unrecognized native member needs an explicit shared-package codec.");
    }

    private static Type? GetSharedNativeGenericArgument(Type type, Type definition, int index)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == definition)
            return type.GetGenericArguments()[index];
        foreach (Type implemented in type.GetInterfaces())
            if (implemented.IsGenericType && implemented.GetGenericTypeDefinition() == definition)
                return implemented.GetGenericArguments()[index];
        return null;
    }
}
