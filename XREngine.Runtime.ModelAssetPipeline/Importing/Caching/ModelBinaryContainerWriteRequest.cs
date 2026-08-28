using System.Collections.ObjectModel;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Complete immutable input for deterministic container serialization.
/// </summary>
internal sealed class ModelBinaryContainerWriteRequest
{
    private readonly ReadOnlyCollection<ModelImportDependency> _dependencies;
    private readonly ReadOnlyCollection<ModelBinaryChunk> _chunks;

    public ModelBinaryContainerWriteRequest(
        ModelBinaryCacheWriteHeader header,
        ModelBinaryManifest manifest,
        IEnumerable<ModelImportDependency> dependencies,
        IEnumerable<ModelBinaryChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(chunks);

        Header = header;
        Manifest = manifest;
        _dependencies = Array.AsReadOnly(dependencies.ToArray());
        _chunks = Array.AsReadOnly(chunks.ToArray());
    }

    public ModelBinaryCacheWriteHeader Header { get; }
    public ModelBinaryManifest Manifest { get; }
    public IReadOnlyList<ModelImportDependency> Dependencies => _dependencies;
    public IReadOnlyList<ModelBinaryChunk> Chunks => _chunks;
}
