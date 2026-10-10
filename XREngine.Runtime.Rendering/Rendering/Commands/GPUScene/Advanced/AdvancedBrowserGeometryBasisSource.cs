using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Immutable browser-only source facts lost by canonical normal/tangent packing.
/// Geometry revisions own the cache; publication pins retain the exact previous image.
/// No persisted or desktop vertex record consumes these flags.
/// </summary>
public sealed class AdvancedBrowserGeometryBasisSource
{
    public const uint TangentPresent = 1;
    public const uint NormalValid = 2;
    public const uint TangentBasisValid = 4;
    public const int VertexWordCount = 8;
    private static readonly ConditionalWeakTable<XRMesh, Cache> Sources = new();
    private readonly uint[] _vertexWords;
    private bool _hasDegenerateNormals;

    private AdvancedBrowserGeometryBasisSource(XRMesh mesh, long revision, uint[] vertexWords, string? rejection)
    {
        Mesh = mesh;
        GeometryRevision = revision;
        VertexCount = mesh.VertexCount;
        HasNormals = mesh.HasNormals;
        HasTangents = mesh.HasTangents;
        _vertexWords = vertexWords;
        Rejection = rejection;
    }

    public XRMesh Mesh { get; }
    public long GeometryRevision { get; }
    public int VertexCount { get; }
    public bool HasNormals { get; }
    public bool HasTangents { get; }
    public string? Rejection { get; }
    public bool HasDegenerateNormals => _hasDegenerateNormals;
    /// <summary>Each vertex is raw normal.xyz, flags, and raw tangent.xyzw, all as eight 32-bit words.</summary>
    public ReadOnlySpan<uint> VertexWords => _vertexWords;

    /// <summary>Checks authoritative source identity, including the selected mesh/LOD and its layout revision.</summary>
    public bool IsCurrent(XRMesh? mesh)
        => ReferenceEquals(mesh, Mesh) && !Mesh.IsDestroyed && Mesh.GeometryRevision == GeometryRevision &&
           Mesh.VertexCount == VertexCount && Mesh.HasNormals == HasNormals && Mesh.HasTangents == HasTangents;

    /// <summary>Scans CPU-owned source data once per mesh revision; never maps or reads GPU buffers.</summary>
    public static AdvancedBrowserGeometryBasisSource? Capture(XRMesh? mesh, long expectedRevision)
    {
        if (mesh is null || mesh.GeometryRevision != expectedRevision) return null;
        Cache cache = Sources.GetValue(mesh, static _ => new Cache());
        lock (cache)
        {
            if (cache.Source is { } retained && retained.IsCurrent(mesh)) return retained;
            AdvancedBrowserGeometryBasisSource source = Build(mesh, expectedRevision);
            if (!source.IsCurrent(mesh)) return null;
            cache.Source = source;
            return source;
        }
    }

    private static AdvancedBrowserGeometryBasisSource Build(XRMesh mesh, long revision)
    {
        if (!mesh.HasNormals)
            return new(mesh, revision, [], "WebGPU.Advanced.AuthoredBasisNormalSourceMissing: the selected mesh has no authored normal stream.");
        if (!CanReadBasis(mesh))
            return new(mesh, revision, [], "WebGPU.Advanced.AuthoredBasisProducerMetadataMissing: the external geometry producer has no CPU-owned normal/tangent validity metadata.");
        uint[] words = new uint[checked(mesh.VertexCount * VertexWordCount)];
        AdvancedBrowserGeometryBasisSource source = new(mesh, revision, words, null);
        for (int index = 0; index < source.VertexCount; index++)
        {
            // Rendering streams are authoritative even when an authoring Vertex
            // graph remains attached after SetNormal/SetTangent edits.
            Vector3 normal = mesh.GetNormal((uint)index);
            Vector4 tangent = mesh.HasTangents ? mesh.GetTangentWithSign((uint)index) : Vector4.Zero;
            if (!Finite(normal) || !Finite(new Vector3(tangent.X, tangent.Y, tangent.Z)) || !float.IsFinite(tangent.W))
                return new(mesh, revision, [], "WebGPU.Advanced.AuthoredBasisNonfiniteSource: the selected mesh contains nonfinite authored normal/tangent values.");
            bool normalValid = ValidLength(normal);
            source._hasDegenerateNormals |= !normalValid;
            uint value = normalValid ? NormalValid : 0u;
            if (mesh.HasTangents)
            {
                value |= TangentPresent;
                Vector3 direction = new(tangent.X, tangent.Y, tangent.Z);
                if (normalValid && ValidLength(direction) && ValidLength(Vector3.Cross(normal, direction) * tangent.W))
                    value |= TangentBasisValid;
            }
            int word = index * VertexWordCount;
            words[word] = BitConverter.SingleToUInt32Bits(normal.X);
            words[word + 1] = BitConverter.SingleToUInt32Bits(normal.Y);
            words[word + 2] = BitConverter.SingleToUInt32Bits(normal.Z);
            words[word + 3] = value;
            words[word + 4] = BitConverter.SingleToUInt32Bits(tangent.X);
            words[word + 5] = BitConverter.SingleToUInt32Bits(tangent.Y);
            words[word + 6] = BitConverter.SingleToUInt32Bits(tangent.Z);
            words[word + 7] = BitConverter.SingleToUInt32Bits(tangent.W);
        }
        return source;
    }

    private static bool CanReadBasis(XRMesh mesh)
    {
        if (mesh.VertexCount <= 0) return false;
        if (mesh.Interleaved)
            return Readable(mesh.InterleavedVertexBuffer) && mesh.NormalOffset is { } normal &&
                (ulong)normal + 12u <= mesh.InterleavedStride && HasAttribute(mesh.InterleavedVertexBuffer!, "Normal", normal, 3) && (!mesh.HasTangents ||
                    mesh.TangentOffset is { } tangent && (ulong)tangent + 16u <= mesh.InterleavedStride &&
                    HasAttribute(mesh.InterleavedVertexBuffer!, "Tangent", tangent, 4)) &&
                (ulong)mesh.VertexCount * mesh.InterleavedStride <= mesh.InterleavedVertexBuffer!.ClientSideSource!.Length;
        return Readable(mesh.NormalsBuffer) && mesh.NormalsBuffer!.ComponentType == EComponentType.Float && mesh.NormalsBuffer.ComponentCount == 3 &&
            mesh.NormalsBuffer.ElementCount >= mesh.VertexCount &&
            (ulong)mesh.VertexCount * 12u <= mesh.NormalsBuffer.ClientSideSource!.Length &&
            (!mesh.HasTangents || Readable(mesh.TangentsBuffer) && mesh.TangentsBuffer!.ComponentType == EComponentType.Float && mesh.TangentsBuffer.ComponentCount == 4 &&
                mesh.TangentsBuffer.ElementCount >= mesh.VertexCount &&
                (ulong)mesh.VertexCount * 16u <= mesh.TangentsBuffer.ClientSideSource!.Length);
    }

    private static bool HasAttribute(XRDataBuffer buffer, string name, uint offset, uint components)
    {
        foreach (InterleavedAttribute attribute in buffer.InterleavedAttributes)
            if (attribute.AttributeName == name && attribute.Offset == offset &&
                attribute.Type == EComponentType.Float && attribute.Count == components && !attribute.Integral) return true;
        return false;
    }

    private static bool Readable(XRDataBuffer? buffer)
        => buffer is { IsDestroyed: false, GpuProduced: false, HasGpuCompressedPayload: false,
            ClientSideSource: { } source } && source.Address != VoidPtr.Zero && source.Length > 0;

    private static bool ValidLength(Vector3 value)
        // The authored vertex applies its normal matrix before normalization.
        // Finite tiny/large vectors may become valid after that transform, so a
        // source-space squared-length threshold would lose defined behavior.
        => Finite(value) && value != Vector3.Zero;

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private sealed class Cache
    {
        internal AdvancedBrowserGeometryBasisSource? Source;
    }
}
