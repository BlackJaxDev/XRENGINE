namespace XREngine.Rendering.Materials
{
    /// <summary>
    /// Backend texture handles referenced by <see cref="GPUMaterialEntry"/>.
    /// OpenGL stores ARB_bindless_texture handles split into low/high uints. Vulkan uses the same
    /// index as the descriptor-array slot and leaves the 64-bit handle zeroed.
    /// </summary>
    public struct GPUTextureHandleEntry
    {
        public ulong Handle;
        public uint Flags;
        public uint Padding0;
    }
}
