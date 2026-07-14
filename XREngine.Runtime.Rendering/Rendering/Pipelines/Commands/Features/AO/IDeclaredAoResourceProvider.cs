namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Implemented by AO commands to provide immutable-generation FBO and texture factories
/// used by the pipeline layout builder. Factories must RETURN the created object without
/// mutating the pipeline instance registry.
/// </summary>
internal interface IDeclaredAoResourceProvider
{
    /// <summary>Creates the named FBO for this AO mode. Must not call instance.SetFBO.</summary>
    XRFrameBuffer CreateDeclaredFrameBuffer(XRRenderPipelineInstance instance, string name);

    /// <summary>
    /// Creates a named mode-specific texture (e.g., noise, history AO/depth).
    /// Return null if the name is not handled by this provider.
    /// Must not call instance.SetTexture.
    /// </summary>
    XRTexture? CreateDeclaredTexture(XRRenderPipelineInstance instance, string name) => null;

    /// <summary>
    /// Creates a named mode-specific SSBO buffer (e.g., spatial hash buffers).
    /// Return null if the name is not handled by this provider.
    /// Must not call instance.SetBuffer.
    /// </summary>
    XRDataBuffer? CreateDeclaredBuffer(XRRenderPipelineInstance instance, string name) => null;
}