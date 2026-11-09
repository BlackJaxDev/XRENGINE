using System.Numerics;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Holds the blendshapes that change a mesh and the source state used to read them.
/// </summary>
public sealed class XRMeshBlendshapeEffectSnapshot
{
    private readonly XRMesh.BufferCollection _buffers;
    private readonly XRMeshBlendshapeBufferState _source;
    private readonly string[] _names;
    private readonly int _vertexCount;
    private readonly ulong _countsRevision;
    private readonly ulong _indicesRevision;
    private readonly ulong _deltasRevision;
    private readonly DataSource? _countsClientSource;
    private readonly DataSource? _indicesClientSource;
    private readonly DataSource? _deltasClientSource;
    private readonly VoidPtr _countsAddress;
    private readonly VoidPtr _indicesAddress;
    private readonly VoidPtr _deltasAddress;
    private readonly uint _countsLength;
    private readonly uint _indicesLength;
    private readonly uint _deltasLength;
    private readonly bool[] _hasEffect;

    private XRMeshBlendshapeEffectSnapshot(XRMesh mesh, XRMesh.BufferCollection buffers, XRMeshBlendshapeBufferState source, bool[] hasEffect)
    {
        _buffers = buffers;
        _source = source;
        _names = mesh.BlendshapeNames;
        _vertexCount = mesh.VertexCount;
        _countsRevision = source.Counts!.Revision;
        _indicesRevision = source.Indices!.Revision;
        _deltasRevision = source.Deltas!.Revision;
        _countsClientSource = source.Counts.ClientSideSource;
        _indicesClientSource = source.Indices.ClientSideSource;
        _deltasClientSource = source.Deltas.ClientSideSource;
        _countsAddress = _countsClientSource!.Address;
        _indicesAddress = _indicesClientSource!.Address;
        _deltasAddress = _deltasClientSource!.Address;
        _countsLength = _countsClientSource.Length;
        _indicesLength = _indicesClientSource.Length;
        _deltasLength = _deltasClientSource.Length;
        _hasEffect = hasEffect;
    }

    /// <summary>Tests whether one shape has a position, normal, or tangent delta.</summary>
    public bool HasEffect(int shapeIndex)
        => (uint)shapeIndex >= (uint)_hasEffect.Length || _hasEffect[shapeIndex];

    /// <summary>Tests whether the mesh still uses the source state in this snapshot.</summary>
    public bool IsCurrent(XRMesh mesh)
    {
        XRMeshBlendshapeBufferState source = mesh.GetBlendshapeBufferStateSnapshot();
        return ReferenceEquals(mesh.Buffers, _buffers) &&
               ReferenceEquals(source, _source) &&
               ReferenceEquals(mesh.BlendshapeNames, _names) &&
               mesh.VertexCount == _vertexCount &&
               source.Counts is { HasCpuMirror: true } counts &&
               source.Indices is { HasCpuMirror: true } indices &&
               source.Deltas is { HasCpuMirror: true } deltas &&
               counts.Revision == _countsRevision &&
               indices.Revision == _indicesRevision &&
               deltas.Revision == _deltasRevision &&
               ReferenceEquals(counts.ClientSideSource, _countsClientSource) &&
               ReferenceEquals(indices.ClientSideSource, _indicesClientSource) &&
               ReferenceEquals(deltas.ClientSideSource, _deltasClientSource) &&
               _countsClientSource is { IsDisposed: false } &&
               _indicesClientSource is { IsDisposed: false } &&
               _deltasClientSource is { IsDisposed: false } &&
               _countsClientSource.Address == _countsAddress &&
               _indicesClientSource.Address == _indicesAddress &&
               _deltasClientSource.Address == _deltasAddress &&
               _countsClientSource.Length == _countsLength &&
               _indicesClientSource.Length == _indicesLength &&
               _deltasClientSource.Length == _deltasLength;
    }

    /// <summary>
    /// Reads one stable mesh-buffer generation. Returns false if the source is busy,
    /// absent, or invalid. Callers can then keep all controls visible.
    /// </summary>
    public static bool TryCreate(XRMesh mesh, out XRMeshBlendshapeEffectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        snapshot = null!;
        XRMesh.BufferCollection buffers = mesh.Buffers;
        if (!buffers.TryAcquireGeometryReadScope(mesh, out XRMesh.BufferCollection.GeometryReadScope scope))
            return false;

        using (scope)
        {
            XRMeshBlendshapeBufferState source = mesh.GetBlendshapeBufferStateSnapshot();
            if (source.Counts is not { HasCpuMirror: true } ||
                source.Indices is not { HasCpuMirror: true } ||
                source.Deltas is not { HasCpuMirror: true })
                return false;
            if (!HasReadableLayout(source.Counts, 2u) ||
                !HasReadableLayout(source.Indices, 4u) ||
                !HasReadableLayout(source.Deltas, 3u) ||
                !IsIndexType(source.Counts.ComponentType) ||
                !IsIndexType(source.Indices.ComponentType) ||
                source.Deltas.ComponentType != EComponentType.Float)
                return false;

            bool[] hasEffect = new bool[mesh.BlendshapeNames.Length];
            XRMeshBlendshapeEffectSnapshot candidate = new(mesh, buffers, source, hasEffect);
            try
            {
                if (!XRMeshBlendshapeActiveListReader.TryCreate(mesh, out XRMeshBlendshapeActiveListReader reader) ||
                    !ReferenceEquals(reader.Counts, source.Counts) ||
                    !ReferenceEquals(reader.Indices, source.Indices) ||
                    !ReferenceEquals(reader.Deltas, source.Deltas))
                    return PublishUnknown(candidate, hasEffect, mesh, out snapshot);

                bool valid = reader.ShapeCount == hasEffect.Length;
                if (!valid)
                    return PublishUnknown(candidate, hasEffect, mesh, out snapshot);
                for (int vertex = 0; vertex < reader.VertexCount; vertex++)
                {
                    if (!reader.TryGetVertexEntriesStrict(vertex, out int first, out int count))
                    {
                        valid = false;
                        break;
                    }
                    for (int entry = first; entry < first + count; entry++)
                    {
                        if (!reader.TryReadEntryStrict(entry, out int shape, out Vector3 position, out Vector3 normal, out Vector3 tangent))
                        {
                            valid = false;
                            break;
                        }

                        if (position != Vector3.Zero || normal != Vector3.Zero || tangent != Vector3.Zero)
                            hasEffect[shape] = true;
                    }

                    if (!valid)
                        break;
                }

                if (!valid)
                    return PublishUnknown(candidate, hasEffect, mesh, out snapshot);
                if (!candidate.IsCurrent(mesh))
                    return false;

                snapshot = candidate;
                return true;
            }
            catch (ArgumentException)
            {
                return PublishUnknown(candidate, hasEffect, mesh, out snapshot);
            }
            catch (InvalidOperationException)
            {
                return PublishUnknown(candidate, hasEffect, mesh, out snapshot);
            }
            catch (OverflowException)
            {
                return PublishUnknown(candidate, hasEffect, mesh, out snapshot);
            }
        }
    }

    private static bool PublishUnknown(XRMeshBlendshapeEffectSnapshot candidate, bool[] hasEffect, XRMesh mesh, out XRMeshBlendshapeEffectSnapshot snapshot)
    {
        Array.Fill(hasEffect, true);
        snapshot = candidate;
        return candidate.IsCurrent(mesh);
    }

    private static bool HasReadableLayout(XRDataBuffer buffer, uint components)
        => buffer.ComponentCount >= components &&
           buffer.ElementSize >= components * sizeof(float) &&
           buffer.ClientSideSource is { IsDisposed: false } clientSource &&
           (ulong)buffer.ElementCount * buffer.ElementSize <= clientSource.Length;

    private static bool IsIndexType(EComponentType componentType)
        => componentType is EComponentType.Int or EComponentType.UInt or EComponentType.Float;
}
