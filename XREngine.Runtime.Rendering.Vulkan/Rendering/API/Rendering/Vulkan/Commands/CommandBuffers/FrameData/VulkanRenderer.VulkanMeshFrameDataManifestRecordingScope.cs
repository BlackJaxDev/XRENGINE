namespace XREngine.Rendering.Vulkan;

internal readonly struct VulkanMeshFrameDataManifestRecordingScope : IDisposable
{
    private readonly VulkanMeshFrameDataReservationManifest _manifest;

    public VulkanMeshFrameDataManifestRecordingScope(
        VulkanMeshFrameDataReservationManifest manifest)
        => _manifest = manifest;

    public void Dispose()
        => _manifest.End();
}
