namespace XREngine.Rendering
{
    [Flags]
    public enum EMemoryBarrierMask : int
    {
        None = 0,
        VertexAttribArray = 0x1,
        ElementArray = 0x2,
        Uniform = 0x4,
        TextureFetch = 0x8,
        ShaderGlobalAccess = 0x10,
        ShaderImageAccess = 0x20,
        Command = 0x40,
        PixelBuffer = 0x80,
        TextureUpdate = 0x100,
        BufferUpdate = 0x200,
        Framebuffer = 0x400,
        TransformFeedback = 0x800,
        AtomicCounter = 0x1000,
        ShaderStorage = 0x2000,
        ClientMappedBuffer = 0x4000,
        QueryBuffer = 0x8000,
        All = unchecked((int)0xFFFFFFFF),
    }
}
