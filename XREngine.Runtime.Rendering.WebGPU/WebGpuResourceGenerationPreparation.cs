using XREngine.Rendering.Resources;

namespace XREngine.Rendering.WebGPU;

/// <summary>Retains a bounded physical-materialization cursor for one unpublished generation.</summary>
internal sealed class WebGpuResourceGenerationPreparation
{
    internal int Revision { get; }
    internal GenericRenderObject[] Resources { get; }
    internal int Cursor { get; set; }

    internal WebGpuResourceGenerationPreparation(RenderResourceRegistry registry)
    {
        Revision = registry.InstanceRevision;
        XRTexture[] textures = registry.GetTextureInstanceSnapshot();
        XRDataBuffer[] buffers = registry.GetBufferInstanceSnapshot();
        XRRenderBuffer[] renderbuffers = registry.GetRenderBufferInstanceSnapshot();
        XRFrameBuffer[] framebuffers = registry.GetFrameBufferInstanceSnapshot();
        Resources = new GenericRenderObject[textures.Length + buffers.Length + renderbuffers.Length + framebuffers.Length];
        int index = 0;
        foreach (XRTexture resource in textures) Resources[index++] = resource;
        foreach (XRDataBuffer resource in buffers) Resources[index++] = resource;
        foreach (XRRenderBuffer resource in renderbuffers) Resources[index++] = resource;
        foreach (XRFrameBuffer resource in framebuffers) Resources[index++] = resource;
    }
}
