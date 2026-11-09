using System.Security.Cryptography;
using System.Text;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Persists the cooked meshlet payloads of one imported model in the game cache, so a later import of
/// the same model attaches them instead of running the meshlet builder again.
/// </summary>
/// <remarks>
/// <para>
/// One file holds all payloads of a model, in the <see cref="ModelBinaryMeshletSectionCodec"/> format.
/// The file name hashes the model's source identity, the meshlet cook provenance key (builder version,
/// payload, hash and codec versions) and the section format version, so a change to any of them uses a
/// new file.
/// </para>
/// <para>
/// A cached payload is never trusted by its key alone. Before it is used, it must pass
/// <see cref="XRMesh.AttachValidatedCookedMeshletPayload"/>, which checks the source mesh hash (vertex
/// positions and triangles), the vertex and triangle counts, the mesh identity and the freshness hash
/// over the meshlet and LOD settings hashes and the provenance key. It must then also be fresh for the
/// settings the import requests. A payload that fails any check is ignored and the mesh is cooked, so
/// an edited source, changed settings or a corrupt file can only cost a rebuild, never stale data. For
/// this reason the key does not need the source file's size or timestamp.
/// </para>
/// <para>
/// The file is written only when an import cooked a payload or the file held entries that the import
/// did not use. It is written to a temporary file and moved into place, so a reader never sees a
/// partial file. Imports run on job workers, never on the render thread.
/// </para>
/// </remarks>
internal sealed class ModelImportMeshletPayloadCache
{
    /// <summary>The game-cache subfolder that holds the per-model meshlet files.</summary>
    public const string CacheFolderName = "model-meshlets";

    /// <summary>Set to 0 to disable the cache for a process.</summary>
    public const string DisableEnvironmentVariable = "XRE_MODEL_MESHLET_CACHE";

    private const string FileExtension = ".xrmeshlets";
    private const string FormatTag = "model-meshlet-section/v1";

    private readonly string _modelIdentity;
    private readonly Dictionary<string, MeshletPayload> _stored;
    private readonly List<MeshletPayload> _published = [];
    private readonly HashSet<string> _publishedIdentities = new(StringComparer.Ordinal);

    private ModelImportMeshletPayloadCache(string filePath, string modelIdentity, Dictionary<string, MeshletPayload> stored, string? loadFailure)
    {
        FilePath = filePath;
        _modelIdentity = modelIdentity;
        _stored = stored;
        LoadFailure = loadFailure;
    }

    /// <summary>Gets the cache file of this model.</summary>
    public string FilePath { get; }

    /// <summary>Gets why an existing file could not be read, or null.</summary>
    public string? LoadFailure { get; }

    /// <summary>Gets the number of payloads that the file held.</summary>
    public int StoredCount => _stored.Count;

    /// <summary>Gets the meshes that attached a cached payload.</summary>
    public int HitCount { get; private set; }

    /// <summary>Gets the meshes that had to be cooked.</summary>
    public int MissCount { get; private set; }

    /// <summary>Gets the cached payloads that failed validation for their mesh.</summary>
    public int RejectedCount { get; private set; }

    /// <summary>
    /// Opens the cache file of a model in <paramref name="cacheRoot"/>. Returns null when the cache is
    /// disabled or no cache root is set.
    /// </summary>
    public static ModelImportMeshletPayloadCache? TryOpen(string? cacheRoot, string modelIdentity)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot) || string.IsNullOrWhiteSpace(modelIdentity))
            return null;
        if (string.Equals(Environment.GetEnvironmentVariable(DisableEnvironmentVariable), "0", StringComparison.Ordinal))
            return null;

        string filePath = Path.Combine(cacheRoot, CacheFolderName, ResolveFileName(modelIdentity));
        Dictionary<string, MeshletPayload> stored = new(StringComparer.Ordinal);
        string? failure = null;
        if (File.Exists(filePath))
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                foreach (ModelBinaryMeshletSectionEntry entry in ModelBinaryMeshletSectionCodec.Deserialize(bytes, declaredElementCount: 0))
                    stored[entry.Payload.SourceMeshIdentity] = entry.Payload;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or EndOfStreamException)
            {
                stored.Clear();
                failure = $"{exception.GetType().Name}: {exception.Message}";
            }
        }

        return new ModelImportMeshletPayloadCache(filePath, modelIdentity, stored, failure);
    }

    /// <summary>Gets the file name for a model identity under the current cook provenance.</summary>
    public static string ResolveFileName(string modelIdentity)
    {
        string material = string.Concat(
            FormatTag, "\n",
            MeshletPayloadUtility.CurrentCookProvenanceKey, "\n",
            modelIdentity);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash, 0, 16) + FileExtension;
    }

    /// <summary>
    /// Attaches the cached payload of <paramref name="mesh"/> when it is valid for the mesh and fresh for
    /// the requested settings.
    /// </summary>
    /// <returns>True when the import can use the attached payload without cooking.</returns>
    public bool TryAttach(
        XRMesh mesh,
        string sourceMeshIdentity,
        MeshletGenerationSettings? meshletSettings,
        MeshLodGenerationSettings? lodSettings)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!_stored.TryGetValue(sourceMeshIdentity, out MeshletPayload? payload))
        {
            ++MissCount;
            return false;
        }

        try
        {
            mesh.AttachValidatedCookedMeshletPayload(payload);
        }
        catch (InvalidDataException)
        {
            ++RejectedCount;
            ++MissCount;
            return false;
        }

        if (!mesh.TryGetFreshMeshletPayload(meshletSettings, lodSettings, sourceMeshIdentity, out _))
        {
            ++RejectedCount;
            ++MissCount;
            return false;
        }

        ++HitCount;
        return true;
    }

    /// <summary>Records the payload that the import uses for a mesh, cached or cooked.</summary>
    public void Record(MeshletPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (_publishedIdentities.Add(payload.SourceMeshIdentity))
            _published.Add(payload);
    }

    /// <summary>
    /// Writes the recorded payloads when they differ from the file: a payload was cooked, or the file
    /// held entries that this import did not use.
    /// </summary>
    /// <returns>True when the file was written.</returns>
    public bool SaveIfChanged()
    {
        bool sameSet = MissCount == 0 &&
                       LoadFailure is null &&
                       _published.Count == _stored.Count;
        if (sameSet || _published.Count == 0)
            return false;

        List<ModelBinaryMeshletSectionEntry> entries = new(_published.Count);
        for (int index = 0; index < _published.Count; ++index)
            entries.Add(new ModelBinaryMeshletSectionEntry(
                new ModelBinaryMeshletSectionKey(_modelIdentity, checked((uint)index), 0u),
                _published[index]));
        byte[] bytes = ModelBinaryMeshletSectionCodec.Serialize(entries);

        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $"{Path.GetFileName(FilePath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
