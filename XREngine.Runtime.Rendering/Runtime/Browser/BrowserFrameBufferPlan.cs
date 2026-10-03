using System.Buffers;
using System.Text;
using System.Text.Json;

namespace XREngine.Rendering;

/// <summary>Immutable attachment intent lowered into a render pass, without a native framebuffer object.</summary>
public sealed class BrowserFrameBufferPlan
{
    private readonly BrowserColorAttachmentPlan?[] _colors;

    public BrowserFrameBufferPlan(ReadOnlySpan<BrowserColorAttachmentPlan?> colors, BrowserDepthStencilAttachmentPlan? depthStencil = null)
    {
        if (colors.Length > 8) throw new ArgumentOutOfRangeException(nameof(colors));
        bool hasAttachment = depthStencil is not null;
        for (int i = 0; i < colors.Length; i++) hasAttachment |= colors[i] is not null;
        if (!hasAttachment) throw new ArgumentException("At least one framebuffer attachment is required.", nameof(colors));
        _colors = colors.ToArray();
        DepthStencil = depthStencil;
    }

    public ReadOnlySpan<BrowserColorAttachmentPlan?> Colors => _colors;
    public BrowserDepthStencilAttachmentPlan? DepthStencil { get; }

    /// <summary>Writes the cold command-plan description; no GPU handles leave the backend.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        writer.WriteStartArray("colors");
        for (int i = 0; i < _colors.Length; i++)
            if (_colors[i] is BrowserColorAttachmentPlan color) color.WriteTo(writer);
            else writer.WriteNullValue();
        writer.WriteEndArray();
        if (DepthStencil is not null)
        {
            writer.WritePropertyName("depthStencil");
            DepthStencil.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    public string ToJson()
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer)) WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
