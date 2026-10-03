using System.Numerics;
using System.Text.Json;

namespace XREngine.Rendering;

/// <summary>A color view and its explicit render-pass preservation and resolve policy.</summary>
public sealed class BrowserColorAttachmentPlan
{
    public BrowserColorAttachmentPlan(int viewHandle, bool clear, bool store, Vector4 clearValue, int? resolveTargetHandle = null)
    {
        ValidateHandle(viewHandle);
        if (resolveTargetHandle is int resolve) ValidateHandle(resolve);
        if (!float.IsFinite(clearValue.X) || !float.IsFinite(clearValue.Y) || !float.IsFinite(clearValue.Z) || !float.IsFinite(clearValue.W))
            throw new ArgumentOutOfRangeException(nameof(clearValue));
        ViewHandle = viewHandle;
        Clear = clear;
        Store = store;
        ClearValue = clearValue;
        ResolveTargetHandle = resolveTargetHandle;
    }

    /// <summary>Zero selects the canvas view acquired for the executing frame.</summary>
    public int ViewHandle { get; }
    public bool Clear { get; }
    public bool Store { get; }
    public Vector4 ClearValue { get; }
    public int? ResolveTargetHandle { get; }

    internal static void ValidateHandle(int handle)
    {
        if (handle != 0) _ = BrowserResourceHandle.FromPacked(handle);
    }

    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("viewHandle", ViewHandle);
        if (ResolveTargetHandle is int resolve) writer.WriteNumber("resolveTargetHandle", resolve);
        writer.WriteString("loadOp", Clear ? "clear" : "load");
        writer.WriteString("storeOp", Store ? "store" : "discard");
        writer.WriteStartArray("clearValue");
        writer.WriteNumberValue(ClearValue.X);
        writer.WriteNumberValue(ClearValue.Y);
        writer.WriteNumberValue(ClearValue.Z);
        writer.WriteNumberValue(ClearValue.W);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
