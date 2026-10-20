using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private XRDataBuffer? _authoredZeroTangent;
    private XRDataBuffer? _authoredTangentPresence;

    /// <summary>Matches the canonical generated vertex shader's absent-tangent sentinel.</summary>
    internal XRDataBuffer RequireAuthoredZeroTangent()
    {
        if (_authoredZeroTangent is not null) return _authoredZeroTangent;
        using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
        using IDisposable suppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        XRDataBuffer buffer = new("Authored absent tangent", EBufferTarget.ArrayBuffer, 1,
            EComponentType.Float, 4, false, false);
        buffer.SetVector4(0, Vector4.Zero);
        XRDataBuffer presence = new("Authored tangent presence", EBufferTarget.ArrayBuffer, 2,
            EComponentType.Float, 1, false, false);
        presence.Set(0, 0.0f);
        presence.Set(1, 1.0f);
        publication.Complete();
        SetField(ref _authoredZeroTangent, buffer);
        SetField(ref _authoredTangentPresence, presence);
        return buffer;
    }

    internal XRDataBuffer RequireAuthoredTangentPresence()
    {
        RequireAuthoredZeroTangent();
        return _authoredTangentPresence!;
    }

    internal bool IsAuthoredConstantVertex(XRDataBuffer buffer)
        => ReferenceEquals(buffer, _authoredZeroTangent) || ReferenceEquals(buffer, _authoredTangentPresence) ||
            ReferenceEquals(buffer, _uberBaseZeroUv) || ReferenceEquals(buffer, _uberBaseDefaultColor);

    private void DestroyAuthoredZeroTangent()
    {
        DestroyUberBaseVertexDefaults();
        _authoredTangentPresence?.Destroy(now: true);
        _authoredZeroTangent?.Destroy(now: true);
        SetField(ref _authoredZeroTangent, null);
        SetField(ref _authoredTangentPresence, null);
    }
}
