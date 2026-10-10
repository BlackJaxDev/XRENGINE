using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuAdvancedDeformationInputArena
{
    private readonly Dictionary<uint, AdvancedBrowserGeometryBasisSource> _basisSources = [];
    private byte[] _emptyBasis = [];
    private int _basisInputOffset;
    private AdvancedGpuDeformationPublication _recordedBasis;

    internal void MarkBasisRecorded(in AdvancedGpuDeformationPublication publication)
        => _recordedBasis = publication;

    internal bool HasCurrentBasis(in AdvancedGpuDeformationPublication publication)
        => _recordedBasis == publication && ReferenceEquals(Output, publication.CurrentVertices);

    internal bool HasPreviousBasis(in AdvancedGpuDeformationPublication publication)
        // Canonical history becomes valid only after its producer fence was
        // submitted. An aborted frame leaves that fence Failed, regardless of
        // the optimistic mark used by current-frame ordered consumers.
        => publication.PreviousOutputValid && ReferenceEquals(Output, publication.PreviousVertices) &&
           _recordedBasis.ResourceGeneration == publication.ResourceGeneration &&
           _recordedBasis.CurrentFrameSlot == publication.PreviousFrameSlot &&
           _recordedBasis.InputGeneration != 0;

    private void PrepareAuthoredBasis(AdvancedGpuDeformationResources resources, Span<uint> header, bool reset)
    {
        if (reset) _basisSources.Clear();
        if (_lengths[3] % 64 != 0 || Output.ElementSize != 64)
            throw new NotSupportedException("WebGPU.Advanced.DeformationBasisLayout: the companion requires complete canonical 64-byte vertices.");
        header[SectionCount * 4] = checked((uint)_basisInputOffset / 4);
        header[SectionCount * 4 + 1] = _lengths[3] / 2;
        BasisOutput.EnsureCapacity(checked((int)Output.ElementCount * 32));
        XRDataBuffer jobsBuffer = resources.GetPackedInputSection(0, out uint jobBytes);
        ReadOnlySpan<AdvancedDeformationJobRecord> jobs = MemoryMarshal.Cast<byte, AdvancedDeformationJobRecord>(
            WebGpuDeformationSource.GetSourceBytes(jobsBuffer)[..checked((int)jobBytes)]);
        foreach (ref readonly AdvancedDeformationJobRecord job in jobs)
        {
            if (!resources.TryGetPackedSourceMesh(in job, out XRMesh? mesh) || mesh is null)
                throw new InvalidOperationException("WebGPU.Advanced.DeformationBasisSourceStale: the prepared source slice, LOD or source revision no longer matches.");
            if (_basisSources.TryGetValue(job.SourceVertexOffset, out AdvancedBrowserGeometryBasisSource? retained))
            {
                if (!retained.IsCurrent(mesh))
                    throw new InvalidOperationException("WebGPU.Advanced.DeformationBasisSourceChanged: a retained basis source changed within its static generation.");
                continue;
            }
            AdvancedBrowserGeometryBasisSource basis = AdvancedBrowserGeometryBasisSource.Capture(mesh, mesh.GeometryRevision)
                ?? throw new InvalidOperationException("WebGPU.Advanced.DeformationBasisSourceChanged: source capture did not retain its exact mesh revision.");
            int bytes = checked(basis.VertexCount * 32);
            uint sourceBytes = checked(job.SourceVertexOffset * 64);
            if (sourceBytes > _lengths[3] || checked((uint)basis.VertexCount * 64) > _lengths[3] - sourceBytes)
                throw new InvalidOperationException("WebGPU.Advanced.DeformationBasisSourceRange: the raw basis exceeds its prepared canonical source slice.");
            int destination = checked(_basisInputOffset + (int)job.SourceVertexOffset * 32);
            if (basis.Rejection is null)
                Storage.UploadPreparation(MemoryMarshal.AsBytes(basis.VertexWords), destination);
            else
            {
                // Unrelated packed-only materials need no authored-basis contract.
                // Authored consumers reject the same immutable source at admission.
                if (_emptyBasis.Length < bytes) Array.Resize(ref _emptyBasis, bytes);
                Storage.UploadPreparation(_emptyBasis.AsSpan(0, bytes), destination);
            }
            _basisSources.Add(job.SourceVertexOffset, basis);
        }
    }
}
