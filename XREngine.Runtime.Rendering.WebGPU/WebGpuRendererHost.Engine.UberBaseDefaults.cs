using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private XRDataBuffer? _uberBaseZeroUv;
    private XRDataBuffer? _uberBaseDefaultColor;

    internal XRDataBuffer RequireUberBaseZeroUv()
    {
        if (_uberBaseZeroUv is not null) return _uberBaseZeroUv;
        using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
        using IDisposable suppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        XRDataBuffer buffer = new("Canonical Uber absent UV", EBufferTarget.ArrayBuffer, 1, EComponentType.Float, 2, false, false);
        buffer.SetVector2(0, Vector2.Zero);
        publication.Complete();
        SetField(ref _uberBaseZeroUv, buffer);
        return buffer;
    }

    internal XRDataBuffer RequireUberBaseDefaultColor()
    {
        if (_uberBaseDefaultColor is not null) return _uberBaseDefaultColor;
        using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
        using IDisposable suppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        XRDataBuffer buffer = new("Canonical Uber absent color", EBufferTarget.ArrayBuffer, 1, EComponentType.Float, 4, false, false);
        buffer.SetVector4(0, new Vector4(0, 0, 0, 1));
        publication.Complete();
        SetField(ref _uberBaseDefaultColor, buffer);
        return buffer;
    }

    private void DestroyUberBaseVertexDefaults()
    {
        _uberBaseZeroUv?.Destroy(now: true);
        _uberBaseDefaultColor?.Destroy(now: true);
        SetField(ref _uberBaseZeroUv, null);
        SetField(ref _uberBaseDefaultColor, null);
    }
}
