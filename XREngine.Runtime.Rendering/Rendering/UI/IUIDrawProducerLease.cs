namespace XREngine.Rendering.UI;

/// <summary>Retains one immutable producer texture generation until draw and GPU claims end.</summary>
public interface IUIDrawProducerLease
{
    ulong Generation { get; }
    XRTexture2D Texture { get; }
    void Retain();
    void Release();
}
