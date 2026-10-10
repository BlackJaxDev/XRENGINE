using System.Buffers.Binary;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private readonly HashSet<int> _engineRecordedTextures = new(128);

    internal void MarkEngineTextureRecorded(int handle)
    {
        if (_engineRecording) _engineRecordedTextures.Add(handle);
    }

    private Span<byte> ReserveEngineTexturePreparation(int handle, int payloadBytes, int kind)
    {
        RequireOwnedResource(handle);
        if (_engineRecording && (_engineAcceptanceAttempted || _engineRecordedTextures.Contains(handle)))
            throw new InvalidOperationException("WebGPU.Texture.RecordedMutation: a preparation cannot change an earlier command's texture.");
        if (payloadBytes < 0 || _enginePreparationCount == EngineMaximumPreparations ||
            payloadBytes > EnginePreparationCapacity - _enginePreparationBytes)
            throw new InvalidOperationException("WebGPU.Texture.PreparationCapacity: exact pending transfers exceed 256 MiB or 4096 ranges.");
        EnsureEnginePreparationCapacity(_enginePreparationBytes + payloadBytes);
        Span<byte> record = EnginePreparationRecord(_enginePreparationCount);
        record.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(record, handle);
        BinaryPrimitives.WriteInt32LittleEndian(record[8..], _enginePreparationBytes);
        BinaryPrimitives.WriteInt32LittleEndian(record[12..], payloadBytes);
        BinaryPrimitives.WriteInt32LittleEndian(record[16..], kind);
        SetField(ref _enginePreparationCount, _enginePreparationCount + 1, publishNotifications: false);
        SetField(ref _enginePreparationBytes, _enginePreparationBytes + payloadBytes, publishNotifications: false);
        return record;
    }

    private void StageEngineTextureUpload(int handle, int mip, int layer, int x, int y, int width, int height, ReadOnlySpan<byte> bytes)
    {
        if (mip < 0 || layer < 0 || x < 0 || y < 0 || width <= 0 || height <= 0 || bytes.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(width), "Texture snapshots require nonempty positive subresource ranges.");
        // Pad only the transport extent. The texture's exact tightly packed image
        // remains described by its format and rectangle, including one-byte mips.
        int padded = checked((bytes.Length + 3) & ~3);
        Span<byte> record = ReserveEngineTexturePreparation(handle, padded, 1);
        BinaryPrimitives.WriteInt32LittleEndian(record[20..], mip);
        BinaryPrimitives.WriteInt32LittleEndian(record[24..], layer);
        BinaryPrimitives.WriteInt32LittleEndian(record[28..], width);
        BinaryPrimitives.WriteInt32LittleEndian(record[32..], height);
        BinaryPrimitives.WriteInt32LittleEndian(record[36..], x);
        BinaryPrimitives.WriteInt32LittleEndian(record[40..], y);
        BinaryPrimitives.WriteInt32LittleEndian(record[44..], bytes.Length);
        Span<byte> destination = _enginePreparationPayload.AsSpan(_enginePreparationBytes - padded, padded);
        destination.Clear();
        bytes.CopyTo(destination);
    }

    private void StageEngineTextureCopy(int source, int destination, int sourceMip, int destinationMip,
        int destinationLayer, int width, int height)
    {
        RequireOwnedResource(source);
        if (_engineRecording && _engineRecordedTextures.Contains(source))
            throw new InvalidOperationException("WebGPU.Texture.RecordedCopy: a recorded producer requires an ordered frame copy.");
        if (source == destination || sourceMip < 0 || destinationMip < 0 || destinationLayer < 0 || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(source), "Texture copies require distinct resources and positive mip extents.");
        Span<byte> record = ReserveEngineTexturePreparation(destination, 0, 2);
        BinaryPrimitives.WriteInt32LittleEndian(record[20..], destinationMip);
        BinaryPrimitives.WriteInt32LittleEndian(record[24..], destinationLayer);
        BinaryPrimitives.WriteInt32LittleEndian(record[28..], width);
        BinaryPrimitives.WriteInt32LittleEndian(record[32..], height);
        BinaryPrimitives.WriteInt32LittleEndian(record[36..], source);
        BinaryPrimitives.WriteInt32LittleEndian(record[40..], sourceMip);
    }

    internal int PrepareEngineTextureCopy(object owner, int source, int destination, int sourceMip, int destinationMip,
        int destinationLayer, int width, int height, int sourceLayer = 0)
    {
        RequireOwnedResource(source);
        RequireOwnedResource(destination);
        if (!_engineRecording || _engineAcceptanceAttempted)
            throw new InvalidOperationException("WebGPU.Texture.CopyFrameRequired: a producer-dependent copy requires an active unsubmitted frame.");
        // The physical command is a creation leaf for a new array generation.
        // Its source/destination dependencies remain owned by the retained plan.
        return PrepareEngineCommands(owner, FormattableString.Invariant(
            $"{{\"label\":\"Engine texture layer copy\",\"commands\":[{{\"type\":\"copyTexture\",\"source\":{source},\"destination\":{destination},\"sourceMip\":{sourceMip},\"destinationMip\":{destinationMip},\"sourceLayer\":{sourceLayer},\"destinationLayer\":{destinationLayer},\"width\":{width},\"height\":{height}}}]}}"));

    }
}
