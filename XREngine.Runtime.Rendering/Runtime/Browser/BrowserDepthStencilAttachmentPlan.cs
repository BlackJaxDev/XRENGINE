using System.Text.Json;

namespace XREngine.Rendering;

/// <summary>Depth and stencil aspect policies; read-only aspects have no load/store operations.</summary>
public sealed class BrowserDepthStencilAttachmentPlan
{
    public BrowserDepthStencilAttachmentPlan(int viewHandle, bool hasDepth = true, bool hasStencil = false,
        bool depthReadOnly = false, bool clearDepth = true, bool storeDepth = true, float depthClearValue = 1,
        bool stencilReadOnly = false, bool clearStencil = true, bool storeStencil = true, uint stencilClearValue = 0)
    {
        if (viewHandle != -1) _ = BrowserResourceHandle.FromPacked(viewHandle);
        if (!hasDepth && !hasStencil) throw new ArgumentException("At least one depth/stencil aspect must be selected.");
        if (!float.IsFinite(depthClearValue) || depthClearValue < 0 || depthClearValue > 1)
            throw new ArgumentOutOfRangeException(nameof(depthClearValue));
        ViewHandle = viewHandle;
        HasDepth = hasDepth;
        HasStencil = hasStencil;
        DepthReadOnly = depthReadOnly;
        ClearDepth = clearDepth;
        StoreDepth = storeDepth;
        DepthClearValue = depthClearValue;
        StencilReadOnly = stencilReadOnly;
        ClearStencil = clearStencil;
        StoreStencil = storeStencil;
        StencilClearValue = stencilClearValue;
    }

    /// <summary>Minus one selects the canvas depth view for the executing frame.</summary>
    public int ViewHandle { get; }
    public bool HasDepth { get; }
    public bool HasStencil { get; }
    public bool DepthReadOnly { get; }
    public bool ClearDepth { get; }
    public bool StoreDepth { get; }
    public float DepthClearValue { get; }
    public bool StencilReadOnly { get; }
    public bool ClearStencil { get; }
    public bool StoreStencil { get; }
    public uint StencilClearValue { get; }

    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("viewHandle", ViewHandle);
        if (HasDepth)
        {
            writer.WriteBoolean("depthReadOnly", DepthReadOnly);
            if (!DepthReadOnly)
            {
                writer.WriteString("depthLoadOp", ClearDepth ? "clear" : "load");
                writer.WriteString("depthStoreOp", StoreDepth ? "store" : "discard");
                writer.WriteNumber("depthClearValue", DepthClearValue);
            }
        }
        if (HasStencil)
        {
            writer.WriteBoolean("stencilReadOnly", StencilReadOnly);
            if (!StencilReadOnly)
            {
                writer.WriteString("stencilLoadOp", ClearStencil ? "clear" : "load");
                writer.WriteString("stencilStoreOp", StoreStencil ? "store" : "discard");
                writer.WriteNumber("stencilClearValue", StencilClearValue);
            }
        }
        writer.WriteEndObject();
    }
}
