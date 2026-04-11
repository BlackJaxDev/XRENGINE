using System.Buffers.Binary;
using System.Text.Json;

namespace XREngine.Gltf;

public static class GltfJsonLoader
{
    private const uint GlbMagic = 0x46546C67;
    private const uint GlbJsonChunkType = 0x4E4F534A;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    public static GltfRoot Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] jsonBytes = ReadJsonBytes(path);
        return JsonSerializer.Deserialize<GltfRoot>(jsonBytes, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to parse glTF JSON payload from '{path}'.");
    }

    public static byte[] ReadJsonBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string extension = Path.GetExtension(path);
        return extension.Equals(".glb", StringComparison.OrdinalIgnoreCase)
            ? ExtractJsonChunk(File.ReadAllBytes(path), path)
            : File.ReadAllBytes(path);
    }

    private static byte[] ExtractJsonChunk(byte[] glbBytes, string path)
    {
        if (glbBytes.Length < 20)
            throw new InvalidOperationException($"GLB '{path}' is too small to contain a valid header and JSON chunk.");

        ReadOnlySpan<byte> span = glbBytes;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(span);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);

        if (magic != GlbMagic)
            throw new InvalidOperationException($"File '{path}' is not a valid GLB container.");
        if (version != 2)
            throw new InvalidOperationException($"GLB '{path}' uses unsupported version {version}. Only version 2 is supported.");
        if (declaredLength > glbBytes.Length)
            throw new InvalidOperationException($"GLB '{path}' declares length {declaredLength}, which exceeds the file size {glbBytes.Length}.");

        int offset = 12;
        while (offset + 8 <= declaredLength)
        {
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
            uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 4)..]);
            offset += 8;

            if (offset + chunkLength > declaredLength)
                throw new InvalidOperationException($"GLB '{path}' contains an out-of-bounds chunk.");

            if (chunkType == GlbJsonChunkType)
                return span.Slice(offset, checked((int)chunkLength)).ToArray();

            offset += checked((int)chunkLength);
        }

        throw new InvalidOperationException($"GLB '{path}' does not contain a JSON chunk.");
    }
}