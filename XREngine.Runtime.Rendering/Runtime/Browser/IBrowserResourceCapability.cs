namespace XREngine.Rendering;

/// <summary>Creates and releases resources owned by one browser renderer session.</summary>
public interface IBrowserResourceCapability
{
    int CreateMesh(BrowserMeshData mesh);
    int CreateTexture(BrowserTextureData texture);
    int CreateMaterial(BrowserMaterialData material, int textureHandle);
    void DestroyResource(int handle);
}
