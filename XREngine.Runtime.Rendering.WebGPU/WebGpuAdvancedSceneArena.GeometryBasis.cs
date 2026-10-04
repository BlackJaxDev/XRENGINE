using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuAdvancedSceneArena
{
    internal const uint AuthoredGeometryBasis = 33;
    internal const uint AuthoredGeometryBasisSchema = 1;
    private WebGpuAdvancedGeometryBasisRecord[] _geometryBasisRows = [];
    private readonly Dictionary<AdvancedBrowserGeometryBasisSource, uint> _geometryBasisOffsets = new();

    private void WriteGeometryBasis(AdvancedGpuScenePublicationSnapshot snapshot)
    {
        try { WriteGeometryBasisCore(snapshot); }
        finally { _geometryBasisOffsets.Clear(); }
    }

    private void WriteGeometryBasisCore(AdvancedGpuScenePublicationSnapshot snapshot)
    {
        int count = snapshot.Draws.PhysicalRecords.Length;
        if (_geometryBasisRows.Length < count) Array.Resize(ref _geometryBasisRows, count);
        _geometryBasisRows.AsSpan(0, count).Clear();
        ReadOnlySpan<AdvancedDrawSubmissionRecord> submissions = snapshot.Submission.Records;
        ReadOnlySpan<AdvancedManagedDeformationSourceRow> sources = snapshot.Submission.DeformationSources;
        if (submissions.Length != sources.Length)
            throw new NotSupportedException("WebGPU.Advanced.AuthoredBasisPublicationMismatch: basis metadata must share the sealed submission sequence.");
        for (int index = 0; index < submissions.Length; index++)
        {
            ref readonly AdvancedDrawSubmissionRecord submission = ref submissions[index];
            if (!snapshot.Materials.TryGet(submission.Material, out AdvancedMaterialRecord material)) continue;
            snapshot.MaterialPayloads.TryGetEngineSurface(in material, out AdvancedEngineSurfaceRecord surface);
            bool uber = snapshot.MaterialPayloads.TryGetUberBaseSurface(in material, out AdvancedUberBaseSurfaceRecord uberSurface) &&
                uberSurface.SchemaVersion != 0;
            if (!uber && surface.SurfaceKind != AdvancedEngineSurfaceRecord.AuthoredTexturedKind)
                continue;
            if (snapshot.Submission.Sequence != snapshot.Draws.Sequence || snapshot.Submission.Sequence != snapshot.Geometry.Sequence)
                throw new NotSupportedException("WebGPU.Advanced.AuthoredBasisPublicationMismatch: basis metadata must share the sealed submission sequence.");
            ref readonly AdvancedManagedDeformationSourceRow source = ref sources[index];
            if (source.BrowserBasisProducerRejection is { } producerReason)
                throw new NotSupportedException(producerReason);
            AdvancedBrowserGeometryBasisSource basis = source.BrowserBasisSource ??
                throw new NotSupportedException("WebGPU.Advanced.AuthoredBasisProducerMetadataMissing: the submitted geometry producer did not retain its selected normal/tangent source metadata.");
            if (!basis.IsCurrent(source.Mesh) || source.MeshVertexCount != basis.VertexCount ||
                submission.DependencySignature != unchecked((source.MeshVersion ^ source.SourceVersion) * 1099511628211ul))
                throw new NotSupportedException("WebGPU.Advanced.AuthoredBasisSourceChanged: the selected mesh identity, layout revision, or submission source association changed after publication.");
            if (basis.Rejection is { } rejection) throw new NotSupportedException(rejection);
            if ((uber || (surface.RoleFlags & 2u) == 0) && basis.HasDegenerateNormals &&
                !AdvancedNativeVertexMaterialSource.RequiresCanonicalDeformationSource(basis.Mesh))
                throw new NotSupportedException("WebGPU.Advanced.AuthoredSpecularNormalUndefined: a specular-only source with zero-length normals has no authored finite normal fallback.");
            if (!snapshot.Draws.TryGetDenseIndex(submission.Draw, out uint dense) ||
                !snapshot.Draws.TryGet(submission.Draw, out AdvancedDrawRecord draw) ||
                draw.Geometry != submission.Geometry || draw.Material != submission.Material ||
                !snapshot.Geometry.TryGet(submission.Geometry, out AdvancedGeometryRecord geometry) ||
                geometry.VertexCount != basis.VertexCount)
                throw new NotSupportedException("WebGPU.Advanced.AuthoredBasisGeometryMismatch: basis metadata does not match the selected draw and generation-checked geometry/LOD.");
            if (!geometry.IsResident)
                throw new NotSupportedException("WebGPU.Advanced.AuthoredBasisFallbackMetadataMissing: nonresident fallback geometry requires its own retained source-basis association.");
            // The same immutable source may serve many distinct draws. Keep each
            // draw association while uploading the raw source basis image only once.
            if (!_geometryBasisOffsets.TryGetValue(basis, out uint offset))
            {
                offset = checked((uint)Append(MemoryMarshal.AsBytes(basis.VertexWords)) / sizeof(uint));
                _geometryBasisOffsets.Add(basis, offset);
            }
            _geometryBasisRows[checked((int)dense)] = new(submission.Draw, submission.Geometry,
                unchecked((ulong)basis.GeometryRevision), offset, checked((uint)basis.VertexCount),
                source.SourceVersion, source.MeshVersion);
        }
        Write(AuthoredGeometryBasis, _geometryBasisRows.AsSpan(0, count));
        Span<uint> directory = MemoryMarshal.Cast<byte, uint>(_bytes.AsSpan(0, HeaderBytes));
        directory[checked((int)AuthoredGeometryBasis * DirectoryWords + 5)] = AuthoredGeometryBasisSchema;
    }
}
