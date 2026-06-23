namespace XREngine.Rendering.Materials
{
    public readonly record struct GPUMaterialTableDirtyRange(
        uint FirstIndex,
        uint IndexCount,
        uint ByteOffset,
        uint ByteCount);
}
