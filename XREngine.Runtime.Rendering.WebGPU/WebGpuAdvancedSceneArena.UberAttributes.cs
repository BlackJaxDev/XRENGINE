using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuAdvancedSceneArena
{
    private WebGpuAdvancedGeometryBasisRecord[] _uberAttributeRows = [];
    private readonly Dictionary<AdvancedBrowserUberAttributeSource, uint> _uberAttributeOffsets = new();

    private void WriteUberSourceAttributes(AdvancedGpuScenePublicationSnapshot snapshot)
    {
        try { WriteUberSourceAttributesCore(snapshot); }
        finally { _uberAttributeOffsets.Clear(); }
    }

    private void WriteUberSourceAttributesCore(AdvancedGpuScenePublicationSnapshot snapshot)
    {
        int count = snapshot.Draws.PhysicalRecords.Length;
        if (_uberAttributeRows.Length < count) Array.Resize(ref _uberAttributeRows, count);
        _uberAttributeRows.AsSpan(0, count).Clear();
        ReadOnlySpan<AdvancedDrawSubmissionRecord> submissions = snapshot.Submission.Records;
        ReadOnlySpan<AdvancedManagedDeformationSourceRow> sources = snapshot.Submission.DeformationSources;
        if (submissions.Length != sources.Length) throw Invalid("source records do not match the sealed submission sequence");
        for (int index = 0; index < submissions.Length; index++)
        {
            ref readonly AdvancedDrawSubmissionRecord submission = ref submissions[index];
            if (!snapshot.Materials.TryGet(submission.Material, out AdvancedMaterialRecord material) ||
                !snapshot.MaterialPayloads.TryGetUberBaseSurface(in material, out AdvancedUberBaseSurfaceRecord uber) || uber.SchemaVersion == 0) continue;
            if (snapshot.Submission.Sequence != snapshot.Draws.Sequence || snapshot.Submission.Sequence != snapshot.Geometry.Sequence)
                throw Invalid("draw, geometry and submission sequences disagree");
            ref readonly AdvancedManagedDeformationSourceRow source = ref sources[index];
            if (source.BrowserUberAttributesProducerRejection is { } rejection) throw new NotSupportedException(rejection);
            AdvancedBrowserUberAttributeSource attributes = source.BrowserUberAttributesSource ?? throw Invalid("the selected producer has no retained UV/color source");
            if (!attributes.IsCurrent(source.Mesh) || source.MeshVertexCount != attributes.VertexCount ||
                submission.DependencySignature != unchecked((source.MeshVersion ^ source.SourceVersion) * 1099511628211ul))
                throw Invalid("selected mesh, LOD, source revision or dependency signature changed after publication");
            if (attributes.Rejection is { } sourceReason) throw new NotSupportedException(sourceReason);
            if (!snapshot.Draws.TryGetDenseIndex(submission.Draw, out uint dense) ||
                !snapshot.Draws.TryGet(submission.Draw, out AdvancedDrawRecord draw) || draw.Geometry != submission.Geometry || draw.Material != submission.Material ||
                !snapshot.Geometry.TryGet(submission.Geometry, out AdvancedGeometryRecord geometry) ||
                geometry.VertexCount != attributes.VertexCount || !geometry.IsResident)
                throw Invalid("the generation-checked draw and resident geometry do not match their exact source attributes");
            if (!_uberAttributeOffsets.TryGetValue(attributes, out uint offset))
            {
                offset = checked((uint)Append(MemoryMarshal.AsBytes(attributes.VertexWords)) / sizeof(uint));
                _uberAttributeOffsets.Add(attributes, offset);
            }
            _uberAttributeRows[checked((int)dense)] = new(submission.Draw, submission.Geometry,
                unchecked((ulong)attributes.GeometryRevision), offset, checked((uint)attributes.VertexCount), source.SourceVersion, source.MeshVersion);
        }
        Write(UberSourceAttributes, _uberAttributeRows.AsSpan(0, count));
        Span<uint> directory = MemoryMarshal.Cast<byte, uint>(_bytes.AsSpan(0, HeaderBytes));
        directory[checked((int)UberSourceAttributes * DirectoryWords + 5)] = 1;
        directory[checked((int)UberSourceAttributes * DirectoryWords + 6)] = TableCount;
    }

    private static NotSupportedException Invalid(string reason)
        => new($"WebGPU.Advanced.UberAttributeAssociationInvalid: {reason}.");
}
