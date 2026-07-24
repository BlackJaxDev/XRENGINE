namespace XREngine.Rendering;

/// <summary>
/// Backend features used by the stable bindless material-table submission path.
/// </summary>
public interface IMaterialTableBackendCapability
{
    bool SupportsBufferDeviceAddress { get; }
    bool SupportsBindlessMaterialTable { get; }
    bool SupportsBindlessTextureHandles { get; }
    string BindlessMaterialUnavailableReason { get; }
    bool TryEnsureMaterialTextureTable(out string reason);
    bool TryResolveMaterialTextureReference(
        XRTexture texture,
        string semantic,
        out Materials.GPUMaterialTextureReference reference);
    void FlushMaterialTextureTableUpdates();
    void ReleaseMaterialTextureReference(in Materials.GPUMaterialRetiredHandle retired);
    bool BeginGlobalMaterialTextureDescriptorScope(XRRenderProgram program, string consumer);
    void EndGlobalMaterialTextureDescriptorScope(XRRenderProgram program);
}
