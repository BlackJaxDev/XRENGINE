namespace XREngine
{
    /// <summary>
    /// Describes why a job is allowed to run on the render thread.
    /// </summary>
    public enum RenderThreadJobKind
    {
        Unknown = 0,
        RequiresGraphicsContext,
        RenderPipelineResource,
        TextureUpload,
        BufferUpload,
        MeshUpload,
        Shader,
        Framebuffer,
        Readback,
        GpuSynchronization,
        Screenshot,
        UiRasterization,
    }
}
