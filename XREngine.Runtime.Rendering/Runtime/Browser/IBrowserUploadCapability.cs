namespace XREngine.Rendering;

/// <summary>Synchronously imports a sealed upload batch into resources owned by the renderer session.</summary>
public interface IBrowserUploadCapability
{
    void SubmitUploads(BrowserUploadBatch batch);
}
