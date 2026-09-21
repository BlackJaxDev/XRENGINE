using System.Collections.Generic;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

public sealed partial class GpuMeshBvh
{
    private readonly Dictionary<XRDataBuffer, (ulong Revision, long GeometryRevision, XRDataBuffer View)> _materialAttributeViews = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<XRDataBuffer> _selectedMaterialAttributes = new(ReferenceEqualityComparer.Instance);
    private readonly List<XRDataBuffer> _retiredMaterialAttributes = [];

    private GpuMeshBvhGeometrySources WithMaterialAttributes(in GpuMeshBvhGeometrySources source)
    {
        XRMesh mesh = source.Mesh;
        _selectedMaterialAttributes.Clear();
        XRDataBuffer? normals = source.IsGpuDeformed
            ? SkinningPrepassDispatcher.Instance.GetSkinnedBuffers(source.Renderer).normals
            : GetMaterialAttributeView(mesh.NormalsBuffer, mesh.GeometryRevision);
        XRDataBuffer? uv0;
        XRDataBuffer? uv1;
        if (mesh.Interleaved)
        {
            // UVs do not deform. Keep them sourced from the authored stream even if
            // a deformation prepass only writes position/normal output fields.
            uv0 = uv1 = !source.IsGpuDeformed && source.Interleaved is not null
                ? source.Interleaved : GetMaterialAttributeView(mesh.InterleavedVertexBuffer, mesh.GeometryRevision);
        }
        else
        {
            uv0 = mesh.TexCoordBuffers is { Length: > 0 } uvBuffers ? GetMaterialAttributeView(uvBuffers[0], mesh.GeometryRevision) : null;
            uv1 = mesh.TexCoordBuffers is { Length: > 1 } uvBuffers1 ? GetMaterialAttributeView(uvBuffers1[1], mesh.GeometryRevision) : null;
        }

        PruneMaterialAttributeViews();
        ulong revision = ((ulong)mesh.GeometryRevision ^ (mesh.NormalsBuffer?.Revision ?? 0u)) * 397u;
        revision = (revision ^ (mesh.InterleavedVertexBuffer?.Revision ?? 0u)) * 397u;
        if (mesh.TexCoordBuffers is { Length: > 0 } uvSources)
        {
            revision = (revision ^ (uvSources[0]?.Revision ?? 0u)) * 397u;
            if (uvSources.Length > 1)
                revision ^= uvSources[1]?.Revision ?? 0u;
        }
        return source with
        {
            Normals = normals,
            TexCoords0 = uv0,
            TexCoords1 = uv1,
            TexCoordsInterleaved = mesh.Interleaved,
            AttributeRevision = revision,
        };
    }

    private XRDataBuffer? GetMaterialAttributeView(XRDataBuffer? source, long geometryRevision)
    {
        if (source is null)
            return null;
        _selectedMaterialAttributes.Add(source);
        if (_materialAttributeViews.TryGetValue(source, out var cached))
        {
            if (cached.Revision == source.Revision && cached.GeometryRevision == geometryRevision && cached.View.Length == source.Length)
                return cached.View;
            cached.View.Destroy();
            cached.View.Dispose();
        }

        // Copy authored CPU upload data into a storage-capable buffer. This never
        // reads back GPU geometry; current deformed normals stay on their GPU buffer.
        XRDataBuffer view = source.Clone(cloneBuffer: true, target: EBufferTarget.ShaderStorageBuffer);
        view.AttributeName = "DDGI.MaterialAttribute";
        view.ShouldMap = false;
        view.DisposeOnPush = false;
        view.PushData();
        _materialAttributeViews[source] = (source.Revision, geometryRevision, view);
        return view;
    }

    private void PruneMaterialAttributeViews()
    {
        _retiredMaterialAttributes.Clear();
        foreach (XRDataBuffer source in _materialAttributeViews.Keys)
            if (!_selectedMaterialAttributes.Contains(source))
                _retiredMaterialAttributes.Add(source);
        for (int i = 0; i < _retiredMaterialAttributes.Count; i++)
        {
            XRDataBuffer source = _retiredMaterialAttributes[i];
            XRDataBuffer view = _materialAttributeViews[source].View;
            view.Destroy();
            view.Dispose();
            _materialAttributeViews.Remove(source);
        }
        _retiredMaterialAttributes.Clear();
    }

    private void ReleaseMaterialAttributeViews()
    {
        foreach (var entry in _materialAttributeViews.Values)
        {
            entry.View.Destroy();
            entry.View.Dispose();
        }
        _materialAttributeViews.Clear();
        _selectedMaterialAttributes.Clear();
        _retiredMaterialAttributes.Clear();
    }
}
