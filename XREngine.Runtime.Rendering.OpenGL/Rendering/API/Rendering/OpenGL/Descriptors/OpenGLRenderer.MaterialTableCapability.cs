namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IMaterialTableBackendCapability
{
    bool IMaterialTableBackendCapability.SupportsBufferDeviceAddress => false;
    bool IMaterialTableBackendCapability.SupportsBindlessMaterialTable => SupportsBindlessTextureHandles;
    bool IMaterialTableBackendCapability.SupportsBindlessTextureHandles => SupportsBindlessTextureHandles;
    string IMaterialTableBackendCapability.BindlessMaterialUnavailableReason
        => SupportsBindlessTextureHandles ? string.Empty : "OpenGL bindless texture handles are unavailable.";
    bool IMaterialTableBackendCapability.TryEnsureMaterialTextureTable(out string reason)
    {
        reason = SupportsBindlessTextureHandles
            ? string.Empty
            : "OpenGL bindless texture handles are unavailable.";
        return SupportsBindlessTextureHandles;
    }
    bool IMaterialTableBackendCapability.TryResolveMaterialTextureReference(
        XRTexture texture,
        string semantic,
        out Materials.GPUMaterialTextureReference reference)
    {
        if (TryGetResidentBindlessTextureHandle(texture, out ulong handle))
        {
            reference = Materials.GPUMaterialTextureReference.FromOpenGLBindlessHandle(handle);
            return true;
        }

        reference = Materials.GPUMaterialTextureReference.None;
        return false;
    }
    void IMaterialTableBackendCapability.FlushMaterialTextureTableUpdates()
    {
    }
    void IMaterialTableBackendCapability.ReleaseMaterialTextureReference(
        in Materials.GPUMaterialRetiredHandle retired)
        => ReleaseResidentBindlessTextureHandle(retired.Handle);
    bool IMaterialTableBackendCapability.BeginGlobalMaterialTextureDescriptorScope(XRRenderProgram program, string consumer)
        => true;
    void IMaterialTableBackendCapability.EndGlobalMaterialTextureDescriptorScope(XRRenderProgram program)
    {
    }
}
