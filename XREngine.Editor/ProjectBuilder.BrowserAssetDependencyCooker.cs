using System.Reflection;
using XREngine.Core.Files;
using XREngine.Components.Scripting;
using XREngine.Rendering;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Editor;

internal static partial class ProjectBuilder
{
    /// <summary>Cooks the serializer-declared external asset graph into browser catalog identities.</summary>
    private sealed class BrowserAssetDependencyCooker(string gameRoot, string? engineRoot, string sourceDirectory,
        IShaderProgramArtifactResolver? resolver, CancellationToken cancellationToken) : IDisposable
    {
        private readonly string _gameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        private readonly string? _engineRoot = engineRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(engineRoot));
        private readonly Dictionary<string, (string TypeName, string Source, string[] Dependencies)> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Type> _assetTypes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);
        private readonly Dictionary<XRMesh, (MeshletGenerationSettings Meshlets, MeshLodGenerationSettings Lods,
            MeshletGenerationSettingsSnapshot MeshletSnapshot, MeshLodGenerationSettingsSnapshot LodSnapshot)> _meshletPolicies = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<XRMesh, MeshletPayload> _cookedMeshletPayloads = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<SubMesh, bool> _plannedSubMeshes = new(ReferenceEqualityComparer.Instance);
        private int _nextSourceOrdinal;
        private bool _meshletBackendVerified;
        private readonly Publishing.BrowserMaterialCookProjection _materialProjection = new(engineRoot, resolver as Publishing.BrowserShaderArtifactSource);
        private readonly Assembly _gameAssembly = GameCSProjLoader.GetLoadedAssembly("GAME")
            ?? throw new InvalidOperationException("BrowserCook.GameAssemblyMissing: the compiled game must be loaded before cooking.");
        private CookedBinarySerializationCallbacks? _cookCallbacks;

        public void Dispose() => _materialProjection.Dispose();

        public IReadOnlyDictionary<string, (string TypeName, string Source, string[] Dependencies)> Entries => _entries;

        /// <summary>Resolves the host's portable scene spelling to an admitted project asset.</summary>
        public (string Identity, string Path) ResolveStreamedScene(string authoredPath)
        {
            if (string.IsNullOrWhiteSpace(authoredPath))
                throw new InvalidDataException("BrowserCook.StreamedScenePathMissing: declare a saved scene asset.");
            string path = authoredPath.Trim().Replace('\\', '/');
            if (!path.StartsWith("/game/", StringComparison.Ordinal)
                && !path.StartsWith("/engine/", StringComparison.Ordinal)
                && !AssetReferencePath.IsPortable(path))
            {
                if (Path.IsPathRooted(path) || path.Contains(':'))
                    throw new NotSupportedException($"BrowserCook.StreamedScenePathInvalid: '{authoredPath}' must be a portable scene identity.");
                path = AssetReferencePath.GamePrefix + path;
            }
            string relative = path.StartsWith("/game/", StringComparison.Ordinal)
                ? path["/game/".Length..]
                : path.StartsWith("/engine/", StringComparison.Ordinal)
                    ? path["/engine/".Length..]
                    : path.StartsWith(AssetReferencePath.GamePrefix, StringComparison.OrdinalIgnoreCase)
                        ? path[AssetReferencePath.GamePrefix.Length..]
                        : path.StartsWith(AssetReferencePath.EnginePrefix, StringComparison.OrdinalIgnoreCase)
                            ? path[AssetReferencePath.EnginePrefix.Length..] : path;
            if (relative.Split('/').Any(static part => part is "" or "." or ".."))
                throw new NotSupportedException($"BrowserCook.StreamedScenePathInvalid: '{authoredPath}' contains an empty or noncanonical segment.");
            if (!Path.HasExtension(path))
                path += $".{AssetManager.AssetExtension}";
            return Resolve(path);
        }

        /// <summary>Updates a scene discovered earlier as another streamed root's declared dependency.</summary>
        public string RecookDeclaredScene(XRScene scene, string catalogPath)
        {
            if (!_entries.TryGetValue(catalogPath, out var existing)
                || !_assetTypes.TryGetValue(catalogPath, out Type? existingType)
                || existingType != scene.GetType())
                throw new InvalidDataException($"BrowserCook.StreamedSceneTypeConflict: '{catalogPath}'.");
            string[] declared = [.. DescribeDependencies(scene, catalogPath)
                .Select(dependency => Resolve(dependency.AssetPath).Identity)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            if (!declared.SequenceEqual(existing.Dependencies, StringComparer.Ordinal))
                throw new InvalidDataException($"BrowserCook.StreamedSceneDependencyChanged: '{catalogPath}'.");
            _materialProjection.PrepareMaterialConsumers(scene, catalogPath, cancellationToken);
            WriteMeshletCookedAsset(scene, Path.Combine(sourceDirectory, existing.Source));
            return existing.Source;
        }

        /// <summary>Declares a publisher-generated standalone asset as a dependency of a cooked parent.</summary>
        public void AddCookedLeaf(string parentPath, string catalogPath, Type assetType, string sourceName)
        {
            if (!_entries.TryGetValue(parentPath, out var parent)
                || _entries.Count >= 4096 || parent.Dependencies.Length >= 64
                || !File.Exists(Path.Combine(sourceDirectory, sourceName)))
                throw new InvalidDataException($"BrowserCook.GeneratedDependencyInvalid: '{catalogPath}'.");
            string typeName = assetType.AssemblyQualifiedName
                ?? throw new InvalidOperationException($"BrowserCook.GeneratedDependencyTypeInvalid: '{catalogPath}'.");
            if (_entries.TryGetValue(catalogPath, out var existing))
            {
                if (existing.TypeName != typeName || existing.Source != sourceName || existing.Dependencies.Length != 0)
                    throw new InvalidDataException($"BrowserCook.GeneratedDependencyConflict: '{catalogPath}'.");
            }
            else
            {
                _entries.Add(catalogPath, (typeName, sourceName, []));
                _assetTypes.Add(catalogPath, assetType);
            }
            if (!parent.Dependencies.Contains(catalogPath, StringComparer.Ordinal))
                _entries[parentPath] = (parent.TypeName, parent.Source,
                    [.. parent.Dependencies.Append(catalogPath).Order(StringComparer.Ordinal)]);
        }

        public string Cook(XRAsset asset, string catalogPath, string sourceName)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateGameObjectOwnership(asset);
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
                _materialProjection.PrepareMaterialConsumers(asset, catalogPath, cancellationToken);
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
                WriteMeshletCookedAsset(asset, Path.Combine(sourceDirectory, sourceName));
                _entries.Add(catalogPath, (typeName, sourceName, [.. references.Keys]));
                _assetTypes.Add(catalogPath, asset.GetType());
                return catalogPath;
            }
            finally
            {
                _visiting.Remove(catalogPath);
            }
        }

        private CookedBinarySerializationCallbacks CookCallbacks => _cookCallbacks ??= new()
        {
            OnSerializingValue = ValidateAndProject,
        };

        private void WriteMeshletCookedAsset(XRAsset asset, string destination)
        {
            _meshletPolicies.Clear();
            _cookedMeshletPayloads.Clear();
            _plannedSubMeshes.Clear();
            try
            {
                PrepareMeshletPayloads(asset);
                WriteCookedAsset(asset, destination, callbacks: CookCallbacks);
            }
            finally
            {
                _meshletPolicies.Clear();
                _cookedMeshletPayloads.Clear();
                _plannedSubMeshes.Clear();
            }
        }

        private void PrepareMeshletPayloads(XRAsset asset)
        {
            // Registered serializers own their separate graph. This prepass follows
            // the same ordinary cooked-binary members as the subsequent write.
            if (PublishedCookedAssetRegistry.IsRegistered(asset.GetType()))
                return;
            CookedBinarySerializationCallbacks callbacks = new()
            {
                OnSerializingValue = value =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateGameObjectOwnership(value);
                    if (value is SubMesh subMesh)
                        CollectEnabledMeshletRequests(subMesh);
                    return value;
                },
            };
            _ = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() =>
                CookedBinarySerializer.CalculateSize(asset, callbacks));

            foreach (var (mesh, policy) in _meshletPolicies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CookPlannedMeshlet(mesh, policy.Meshlets, policy.Lods);
            }
        }

        private void CollectEnabledMeshletRequests(SubMesh subMesh)
        {
            MeshOptimizerSubMeshSettings settings = subMesh.MeshOptimizer;
            if (_plannedSubMeshes.TryGetValue(subMesh, out bool enabled))
            {
                if (enabled != settings.Meshlets.Enabled)
                    throw new InvalidDataException($"BrowserCook.MeshletRequestChanged: submesh '{subMesh.Name}' ({subMesh.ID}) changed its enabled state during request planning.");
            }
            else
                _plannedSubMeshes.Add(subMesh, settings.Meshlets.Enabled);
            if (!settings.Meshlets.Enabled)
                return;
            MeshletGenerationSettingsSnapshot meshletSnapshot = MeshletGenerationSettingsSnapshot.From(settings.Meshlets);
            MeshLodGenerationSettingsSnapshot lodSnapshot = MeshLodGenerationSettingsSnapshot.From(settings.Lods);
            foreach (SubMeshLOD lod in subMesh.LODs)
            {
                if (lod.Mesh is not { } mesh)
                    continue;
                if (_meshletPolicies.TryGetValue(mesh, out var previous))
                {
                    if (previous.MeshletSnapshot != meshletSnapshot || previous.LodSnapshot != lodSnapshot)
                        throw new InvalidDataException($"BrowserCook.MeshletSharedMeshPolicyConflict: mesh '{mesh.Name}' ({mesh.ID}) has conflicting enabled meshlet or LOD cook settings.");
                }
                else
                    _meshletPolicies.Add(mesh, (settings.Meshlets, settings.Lods, meshletSnapshot, lodSnapshot));
            }
        }

        private void CookPlannedMeshlet(XRMesh mesh, MeshletGenerationSettings settings,
            MeshLodGenerationSettings lodSettings)
        {
            try
            {
                // Keep valid import provenance; otherwise use the persistent ID
                // instead of a source path or mutable display name.
                string sourceIdentity = mesh.MeshletPayload is { } existing &&
                    existing.IsFreshForSourceMesh(mesh) &&
                    IsPortableMeshletIdentity(existing.SourceMeshIdentity, mesh.ID)
                        ? existing.SourceMeshIdentity : $"mesh:{mesh.ID:N}";
                if (MeshOptimizerBackendServices.Current is null)
                    throw new NotSupportedException("BrowserCook.MeshletBackendUnavailable: the desktop meshoptimizer backend is not registered in the Editor cook host.");
                if (!mesh.TryGetFreshMeshletPayload(settings, lodSettings, sourceIdentity,
                        out MeshletPayload payload))
                {
                    if ((mesh.Triangles?.Count ?? 0) > 0)
                        EnsureMeshletBackend();
                    payload = mesh.GetOrCreateMeshletPayload(settings, lodSettings, sourceIdentity);
                }
                payload.ValidateForMesh(mesh, sourceIdentity);
                if ((mesh.Triangles?.Count ?? 0) > 0 && !payload.HasMeshlets)
                    throw new InvalidDataException($"BrowserCook.MeshletPayloadEmpty: mesh '{mesh.Name}' ({mesh.ID}) has triangles but its enabled native cook produced no meshlets.");
                _cookedMeshletPayloads.Add(mesh, payload);
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                throw new NotSupportedException($"BrowserCook.MeshletBackendUnavailable: the desktop meshoptimizer native backend could not cook mesh '{mesh.Name}' ({mesh.ID}).", error);
            }
        }

        private object? ValidateAndProject(object? value)
        {
            ValidateGameObjectOwnership(value);
            if (value is SubMesh subMesh)
            {
                if (!_plannedSubMeshes.TryGetValue(subMesh, out bool enabled) ||
                    enabled != subMesh.MeshOptimizer.Meshlets.Enabled)
                    throw new InvalidDataException($"BrowserCook.MeshletRequestChanged: submesh '{subMesh.Name}' ({subMesh.ID}) changed its enabled state during serialization.");
                if (enabled)
                {
                    MeshletGenerationSettingsSnapshot meshletSnapshot = MeshletGenerationSettingsSnapshot.From(subMesh.MeshOptimizer.Meshlets);
                    MeshLodGenerationSettingsSnapshot lodSnapshot = MeshLodGenerationSettingsSnapshot.From(subMesh.MeshOptimizer.Lods);
                    foreach (SubMeshLOD lod in subMesh.LODs)
                        if (lod.Mesh is { } mesh && (!_meshletPolicies.TryGetValue(mesh, out var planned) ||
                            planned.MeshletSnapshot != meshletSnapshot || planned.LodSnapshot != lodSnapshot))
                            throw new InvalidDataException($"BrowserCook.MeshletRequestChanged: mesh '{mesh.Name}' ({mesh.ID}) changed its enabled cook policy during serialization.");
                }
            }
            if (value is XRMesh source && _cookedMeshletPayloads.TryGetValue(source, out MeshletPayload? payload))
            {
                var policy = _meshletPolicies[source];
                if (!ReferenceEquals(source.MeshletPayload, payload) ||
                    !payload.IsFreshFor(source, policy.Meshlets, policy.Lods, payload.SourceMeshIdentity))
                    throw new InvalidDataException($"BrowserCook.MeshletSourceChanged: mesh '{source.Name}' ({source.ID}) changed during browser serialization.");
                payload.ValidateForMesh(source, payload.SourceMeshIdentity);
            }
            return _materialProjection.Callbacks.OnSerializingValue?.Invoke(value) ?? value;
        }

        private void EnsureMeshletBackend()
        {
            if (_meshletBackendVerified)
                return;
            IMeshOptimizerBackend? backend = MeshOptimizerBackendServices.Current;
            if (backend is null)
                throw new NotSupportedException("BrowserCook.MeshletBackendUnavailable: the desktop meshoptimizer backend is not registered in the Editor cook host.");
            try
            {
                if (backend.BuildMeshletsBound(3, MeshletPayload.PortableMaxVertices,
                        MeshletPayload.PortableMaxTriangles) == 0)
                    throw new InvalidDataException("BrowserCook.MeshletBackendInvalid: the desktop meshoptimizer backend returned no bound for one triangle.");
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                throw new NotSupportedException("BrowserCook.MeshletBackendUnavailable: the desktop meshoptimizer native library or required export is missing.", error);
            }
            _meshletBackendVerified = true;
        }

        private static bool IsPortableMeshletIdentity(string identity, Guid meshId)
        {
            if (identity == $"mesh:{meshId:N}")
                return true;
            // Model import identities start with the source's SHA-256 identity,
            // followed by the imported entity and resident LOD key.
            if (identity.Length < 66 || identity[64] != '/')
                return false;
            for (int index = 0; index < 64; index++)
                if (!char.IsAsciiHexDigit(identity[index]))
                    return false;
            if (identity.Contains('\\') || identity.Contains(":/", StringComparison.Ordinal) ||
                identity.Contains("//", StringComparison.Ordinal))
                return false;
            foreach (string segment in identity[65..].Split('/'))
                if (segment is "" or "." or "..")
                    return false;
            return true;
        }

        private void ValidateGameObjectOwnership(object? value)
        {
            if (value is null)
                return;
            Type type = value.GetType();
            if (type.Assembly.GetName().Name == _gameAssembly.GetName().Name && type.Assembly != _gameAssembly)
                throw new InvalidDataException($"BrowserCook.StaleGameObject: '{type.FullName}' belongs to an older game assembly context; reload the authored world before publishing.");
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

            // Exact base scenes serialize their node/component graph inline. Streaming
            // volume scene paths are package roots, discovered separately by the publisher.
            if (asset.GetType() == typeof(XRScene))
                return Array.Empty<PublishedCookedAssetDependency>();

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
