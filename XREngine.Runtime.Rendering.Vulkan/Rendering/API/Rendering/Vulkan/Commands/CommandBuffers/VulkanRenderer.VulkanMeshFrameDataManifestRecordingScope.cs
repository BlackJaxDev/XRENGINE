namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly struct VulkanMeshFrameDataManifestRecordingScope : IDisposable
    {
        private readonly VulkanMeshFrameDataReservationManifest _manifest;

        public VulkanMeshFrameDataManifestRecordingScope(
            VulkanMeshFrameDataReservationManifest manifest)
            => _manifest = manifest;

        public void Dispose()
            => _manifest.End();
    }
}
