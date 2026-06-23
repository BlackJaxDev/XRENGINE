namespace XREngine.Rendering.Materials
{
    public readonly record struct GPUMaterialHandleIndices(uint Albedo, uint Normal, uint RM)
    {
        public static readonly GPUMaterialHandleIndices Empty = new(0u, 0u, 0u);
    }
}
