using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

/// <summary>Immutable full-float Uber UV/color inputs retained from the selected source mesh revision.</summary>
public sealed class AdvancedBrowserUberAttributeSource
{
    public const int VertexWordCount = 12;
    private static readonly ConditionalWeakTable<XRMesh, Cache> Sources = new();
    private readonly uint[] _words;
    private readonly uint _uvCount, _colorCount;

    private AdvancedBrowserUberAttributeSource(XRMesh mesh, long revision, uint[] words, string? rejection)
    {
        Mesh = mesh; GeometryRevision = revision; VertexCount = mesh.VertexCount;
        _uvCount = mesh.TexCoordCount; _colorCount = mesh.ColorCount;
        _words = words; Rejection = rejection;
    }

    public XRMesh Mesh { get; }
    public long GeometryRevision { get; }
    public int VertexCount { get; }
    public string? Rejection { get; }
    public ReadOnlySpan<uint> VertexWords => _words;
    public bool IsCurrent(XRMesh? mesh) => ReferenceEquals(mesh, Mesh) && !Mesh.IsDestroyed &&
        Mesh.GeometryRevision == GeometryRevision && Mesh.VertexCount == VertexCount &&
        Mesh.TexCoordCount == _uvCount && Mesh.ColorCount == _colorCount;

    public static AdvancedBrowserUberAttributeSource? Capture(XRMesh? mesh, long expectedRevision)
    {
        if (mesh is null || mesh.GeometryRevision != expectedRevision) return null;
        Cache cache = Sources.GetValue(mesh, static _ => new());
        lock (cache)
        {
            if (cache.Source is { } retained && retained.IsCurrent(mesh)) return retained;
            AdvancedBrowserUberAttributeSource source = Build(mesh, expectedRevision);
            if (!source.IsCurrent(mesh)) return null;
            return cache.Source = source;
        }
    }

    private static AdvancedBrowserUberAttributeSource Build(XRMesh mesh, long revision)
    {
        if (!CanRead(mesh)) return new(mesh, revision, [],
            "WebGPU.Advanced.UberAttributeSourceMissing: exact CPU-owned float UV0 through UV3 and color0 streams are required when present.");
        uint[] words = new uint[checked(mesh.VertexCount * VertexWordCount)];
        for (uint vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            int offset = checked((int)vertex * VertexWordCount);
            for (uint channel = 0; channel < 4; channel++)
            {
                Vector2 uv = channel < mesh.TexCoordCount ? mesh.GetTexCoord(vertex, channel) : Vector2.Zero;
                if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y)) return Invalid();
                words[offset + (int)channel * 2] = BitConverter.SingleToUInt32Bits(uv.X);
                words[offset + (int)channel * 2 + 1] = BitConverter.SingleToUInt32Bits(uv.Y);
            }
            Vector4 color = mesh.ColorCount != 0 ? mesh.GetColor(vertex, 0) : new Vector4(0, 0, 0, 1);
            for (int component = 0; component < 4; component++)
            {
                if (!float.IsFinite(color[component])) return Invalid();
                words[offset + 8 + component] = BitConverter.SingleToUInt32Bits(color[component]);
            }
        }
        return new(mesh, revision, words, null);

        AdvancedBrowserUberAttributeSource Invalid() => new(mesh, revision, [],
            "WebGPU.Advanced.UberAttributeNonfinite: authored UV/color components must be finite.");
    }

    private static bool CanRead(XRMesh mesh)
    {
        if (mesh.VertexCount <= 0) return false;
        for (uint channel = 0; channel < Math.Min(mesh.TexCoordCount, 4u); channel++)
            if (!ReadableAttribute(mesh, mesh.InterleavedVertexBuffer, mesh.TexCoordBuffers is { } coordinates && channel < coordinates.Length ? coordinates[channel] : null,
                mesh.TexCoordOffset is { } uv ? uv + channel * 8u : null, 2, channel switch { 0 => "TexCoord0", 1 => "TexCoord1", 2 => "TexCoord2", _ => "TexCoord3" })) return false;
        return mesh.ColorCount == 0 || ReadableAttribute(mesh, mesh.InterleavedVertexBuffer,
            mesh.ColorBuffers is { Length: > 0 } colors ? colors[0] : null, mesh.ColorOffset, 4, "Color0");
    }

    private static bool ReadableAttribute(XRMesh mesh, XRDataBuffer? interleaved, XRDataBuffer? stream, uint? offset, uint components, string name)
    {
        XRDataBuffer? buffer = mesh.Interleaved ? interleaved : stream;
        if (buffer is not { IsDestroyed: false, GpuProduced: false, HasGpuCompressedPayload: false, ClientSideSource: { } bytes } ||
            bytes.Address == VoidPtr.Zero) return false;
        if (!mesh.Interleaved) return buffer.ComponentType == EComponentType.Float && buffer.ComponentCount == components &&
            buffer.ElementCount >= mesh.VertexCount && (ulong)mesh.VertexCount * components * 4u <= bytes.Length;
        if (buffer.ComponentType != EComponentType.Struct || buffer.ElementSize != mesh.InterleavedStride ||
            buffer.ElementCount < mesh.VertexCount || offset is not { } start || start % sizeof(float) != 0 ||
            (ulong)start + components * 4u > mesh.InterleavedStride ||
            (ulong)mesh.VertexCount * mesh.InterleavedStride > bytes.Length) return false;
        foreach (InterleavedAttribute attribute in buffer.InterleavedAttributes)
            if (attribute.AttributeName == name && attribute.Offset == start && attribute.Count == components &&
                attribute.Type == EComponentType.Float && !attribute.Integral) return true;
        return false;
    }

    private sealed class Cache { internal AdvancedBrowserUberAttributeSource? Source; }
}
