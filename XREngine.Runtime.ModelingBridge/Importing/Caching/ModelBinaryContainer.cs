using System.Collections.ObjectModel;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Validated manifest and only the chunk payloads requested by the caller.
/// </summary>
internal sealed class ModelBinaryContainer
{
    private readonly ReadOnlyCollection<ModelBinaryChunkEntry> _chunkEntries;
    private readonly ReadOnlyCollection<ModelImportDependency> _dependencies;
    private readonly ReadOnlyDictionary<ModelBinaryChunkKey, ReadOnlyMemory<byte>> _selectedChunks;

    public ModelBinaryContainer(
        ModelBinaryCachePreamble preamble,
        ModelBinaryManifest manifest,
        IEnumerable<ModelBinaryChunkEntry> chunkEntries,
        IEnumerable<ModelImportDependency> dependencies,
        IDictionary<ModelBinaryChunkKey, ReadOnlyMemory<byte>> selectedChunks)
    {
        Preamble = preamble;
        Manifest = manifest;
        _chunkEntries = Array.AsReadOnly(chunkEntries.ToArray());
        _dependencies = Array.AsReadOnly(dependencies.ToArray());
        _selectedChunks = new ReadOnlyDictionary<ModelBinaryChunkKey, ReadOnlyMemory<byte>>(
            new Dictionary<ModelBinaryChunkKey, ReadOnlyMemory<byte>>(selectedChunks));
    }

    public ModelBinaryCachePreamble Preamble { get; }
    public ModelBinaryManifest Manifest { get; }
    public IReadOnlyList<ModelBinaryChunkEntry> ChunkEntries => _chunkEntries;
    public IReadOnlyList<ModelImportDependency> Dependencies => _dependencies;
    public IReadOnlyDictionary<ModelBinaryChunkKey, ReadOnlyMemory<byte>> SelectedChunks => _selectedChunks;
}
