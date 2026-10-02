using XREngine.Core.Files;
using XREngine.Scene;

namespace XREngine.Editor;

internal static partial class ProjectBuilder
{
    /// <summary>Cooks the serializer-declared external asset graph into browser catalog identities.</summary>
    private sealed class BrowserAssetDependencyCooker(string gameRoot, string? engineRoot, string sourceDirectory, CancellationToken cancellationToken) : IDisposable
    {
        private readonly string _gameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        private readonly string? _engineRoot = engineRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(engineRoot));
        private readonly Dictionary<string, (string TypeName, string Source, string[] Dependencies)> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Type> _assetTypes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);
        private int _nextSourceOrdinal;
        private readonly Publishing.BrowserMaterialCookProjection _materialProjection = new(engineRoot);

        public void Dispose() => _materialProjection.Dispose();

        public IReadOnlyDictionary<string, (string TypeName, string Source, string[] Dependencies)> Entries => _entries;

        /// <summary>Declares a publisher-generated standalone asset as a dependency of a cooked parent.</summary>
        public void AddCookedLeaf(string parentPath, string catalogPath, Type assetType, string sourceName)
        {
            if (!_entries.TryGetValue(parentPath, out var parent) || _entries.ContainsKey(catalogPath)
                || _entries.Count >= 4096 || parent.Dependencies.Length >= 64
                || !File.Exists(Path.Combine(sourceDirectory, sourceName)))
                throw new InvalidDataException($"BrowserCook.GeneratedDependencyInvalid: '{catalogPath}'.");
            string typeName = assetType.AssemblyQualifiedName
                ?? throw new InvalidOperationException($"BrowserCook.GeneratedDependencyTypeInvalid: '{catalogPath}'.");
            _entries.Add(catalogPath, (typeName, sourceName, []));
            _assetTypes.Add(catalogPath, assetType);
            _entries[parentPath] = (parent.TypeName, parent.Source,
                [.. parent.Dependencies.Append(catalogPath).Order(StringComparer.Ordinal)]);
        }

        public string Cook(XRAsset asset, string catalogPath, string sourceName)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_entries.ContainsKey(catalogPath))
                return catalogPath;
            if (!_visiting.Add(catalogPath))
                throw new InvalidDataException($"Browser asset dependency cycle includes '{catalogPath}'. Cook the cycle into one asset graph.");
            if (_visiting.Count > 32)
                throw new InvalidDataException($"Browser asset dependency graph exceeds 32 levels at '{catalogPath}'.");
            if (_entries.Count + _visiting.Count > 4096)
                throw new InvalidDataException("Browser asset dependency graph exceeds 4096 assets.");

            try
            {
                IReadOnlyList<PublishedCookedAssetDependency> declared = DescribeDependencies(asset, catalogPath);
                if (declared.Count > 4096)
                    throw new InvalidDataException($"Browser asset '{catalogPath}' declares more than 4096 reference occurrences.");
                SortedDictionary<string, (string Source, Type Type)> references = new(StringComparer.Ordinal);
                foreach (PublishedCookedAssetDependency dependency in declared)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    (string identity, string path) = Resolve(dependency.AssetPath);
                    if (identity == catalogPath)
                        throw new InvalidDataException($"Browser asset '{catalogPath}' references itself.");
                    if (dependency.AssetType is null || !typeof(XRAsset).IsAssignableFrom(dependency.AssetType))
                        throw new InvalidDataException($"Browser asset '{catalogPath}' declares an invalid type for '{identity}'.");
                    if (references.TryGetValue(identity, out (string Source, Type Type) previous))
                    {
                        if (!previous.Type.IsAssignableFrom(dependency.AssetType) && !dependency.AssetType.IsAssignableFrom(previous.Type))
                            throw new InvalidDataException($"Browser asset '{catalogPath}' declares incompatible types for '{identity}'.");
                        references[identity] = (path, previous.Type.IsAssignableFrom(dependency.AssetType) ? dependency.AssetType : previous.Type);
                    }
                    else
                        references.Add(identity, (path, dependency.AssetType));
                }
                if (references.Count > 64)
                    throw new InvalidDataException($"Browser asset '{catalogPath}' declares more than 64 distinct external dependencies.");

                foreach ((string identity, (string path, Type type)) in references)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_visiting.Contains(identity))
                        throw new InvalidDataException($"Browser asset dependency cycle includes '{identity}'. Cook the cycle into one asset graph.");
                    if (_entries.ContainsKey(identity))
                    {
                        if (!type.IsAssignableFrom(_assetTypes[identity]))
                            throw new InvalidDataException($"Browser asset '{identity}' has inconsistent declared types.");
                        continue;
                    }

                    XRAsset referenced = AssetManager.DeserializeAssetFile(path, type)
                        ?? throw new InvalidDataException($"Browser dependency '{identity}' did not deserialize as '{type.FullName}'.");
                    if (!type.IsInstanceOfType(referenced))
                        throw new InvalidDataException($"Browser dependency '{identity}' has type '{referenced.GetType().FullName}', expected '{type.FullName}'.");
                    referenced.FilePath = path;
                    Cook(referenced, identity, $"asset-{++_nextSourceOrdinal:D4}.bin");
                }

                string typeName = asset.GetType().AssemblyQualifiedName
                    ?? throw new InvalidOperationException($"Browser asset '{catalogPath}' has no stable runtime type identity.");
                WriteCookedAsset(asset, Path.Combine(sourceDirectory, sourceName), callbacks: _materialProjection.Callbacks);
                _entries.Add(catalogPath, (typeName, sourceName, [.. references.Keys]));
                _assetTypes.Add(catalogPath, asset.GetType());
                return catalogPath;
            }
            finally
            {
                _visiting.Remove(catalogPath);
            }
        }

        private static IReadOnlyList<PublishedCookedAssetDependency> DescribeDependencies(XRAsset asset, string catalogPath)
        {
            if (PublishedCookedAssetRegistry.TryGetDependencies(asset, out IReadOnlyList<PublishedCookedAssetDependency>? declared))
                return declared!;

            // The generic serializer embeds object-valued graph members. The exact base
            // world has one known file-valued setting outside that graph. Game-defined
            // world types need their own declaration for custom external path semantics.
            if (asset.GetType() == typeof(XRWorld))
            {
                string? skyboxPath = ((XRWorld)asset).Settings.SkyboxTexturePath;
                if (string.IsNullOrWhiteSpace(skyboxPath))
                    return Array.Empty<PublishedCookedAssetDependency>();
                if (!AssetReferencePath.IsPortable(skyboxPath)
                    && !skyboxPath.StartsWith("/game/", StringComparison.Ordinal)
                    && !skyboxPath.StartsWith("/engine/", StringComparison.Ordinal))
                {
                    throw new NotSupportedException(
                        $"Browser world '{catalogPath}' uses a non-portable skybox asset path.");
                }
                return [new PublishedCookedAssetDependency(skyboxPath, typeof(XRAsset))];
            }

            throw new NotSupportedException(
                $"Browser asset '{catalogPath}' has no serializer-owned dependency declaration for '{asset.GetType().FullName}'.");
        }

        private (string Identity, string Path) Resolve(string declaredPath)
        {
            if (string.IsNullOrWhiteSpace(declaredPath))
                throw new InvalidDataException("Browser asset serializer declared an empty external dependency path.");

            string? resolved;
            if (declaredPath.StartsWith("/game/", StringComparison.Ordinal))
            {
                string portable = AssetReferencePath.GamePrefix + declaredPath["/game/".Length..];
                if (!AssetReferencePath.TryResolve(portable, _gameRoot, _engineRoot, out resolved))
                    throw new InvalidDataException($"Invalid browser dependency '{declaredPath}'.");
            }
            else if (declaredPath.StartsWith("/engine/", StringComparison.Ordinal))
            {
                string portable = AssetReferencePath.EnginePrefix + declaredPath["/engine/".Length..];
                if (!AssetReferencePath.TryResolve(portable, _gameRoot, _engineRoot, out resolved))
                    throw new InvalidDataException($"Invalid browser dependency '{declaredPath}'.");
            }
            else if (AssetReferencePath.IsPortable(declaredPath))
            {
                if (!AssetReferencePath.TryResolve(declaredPath, _gameRoot, _engineRoot, out resolved))
                    throw new InvalidDataException($"Invalid browser dependency '{declaredPath}'.");
            }
            else
                throw new NotSupportedException($"Browser dependency '{declaredPath}' is not a portable game or engine asset reference.");

            string fullPath = Path.GetFullPath(resolved);
            string identity;
            string root;
            if (AssetReferencePath.TryCreate(_gameRoot, fullPath, AssetReferencePath.GamePrefix, out string? gameReference))
            {
                identity = "/game/" + gameReference[AssetReferencePath.GamePrefix.Length..];
                root = _gameRoot;
            }
            else if (AssetReferencePath.TryCreate(_engineRoot, fullPath, AssetReferencePath.EnginePrefix, out string? engineReference))
            {
                identity = "/engine/" + engineReference[AssetReferencePath.EnginePrefix.Length..];
                root = _engineRoot!;
            }
            else
                throw new InvalidDataException($"Browser dependency '{declaredPath}' is outside the game and engine asset roots.");

            if (!string.Equals(Path.GetExtension(fullPath), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Browser dependency '{identity}' is not a standalone .asset file.");
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"Browser dependency '{identity}' was not found.", fullPath);
            RejectLinkedSource(fullPath, root);
            return (identity, fullPath);
        }

        private static void RejectLinkedSource(string path, string root)
        {
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new NotSupportedException($"Browser asset source '{path}' cannot be a symbolic link.");
                if (string.Equals(current, root, StringComparison.Ordinal))
                    return;
            }
            throw new InvalidDataException($"Browser asset source '{path}' escaped its asset root.");
        }
    }
}
