namespace XREngine.Rendering;

/// <summary>Portable resource and packet capability implemented by a browser renderer module.</summary>
public interface IBrowserRendererHost : IRuntimeRendererHost, IDisposable
{
    BrowserRendererState State { get; }
    void MarkReady(int sessionId);
    void MarkFailed(bool deviceLost);
    int CreateMesh(BrowserMeshData mesh);
    int CreateTexture(BrowserTextureData texture);
    int CreateMaterial(BrowserMaterialData material, int textureHandle);
    void DestroyResource(int handle);
    void SubmitPacket(BrowserFramePacket packet);
}
