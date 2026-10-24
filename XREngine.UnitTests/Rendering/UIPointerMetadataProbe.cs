using System.Numerics;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

/// <summary>Exposes metadata capture callbacks to publication tests.</summary>
public sealed class UIPointerMetadataProbe : UIInteractableComponent, IUIPointerMetadataSource
{
    private object? _metadata;
    public object? Metadata { get => _metadata; set => SetField(ref _metadata, value); }
    public Action? OnCapture { get; set; }
    public Vector2 AcceptedSize { get; private set; }

    public object? CapturePointerMetadata(Vector2 acceptedSize)
    {
        AcceptedSize = acceptedSize;
        OnCapture?.Invoke();
        return Metadata;
    }
}
