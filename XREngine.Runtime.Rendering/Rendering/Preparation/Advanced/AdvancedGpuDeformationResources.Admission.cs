using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public sealed partial class AdvancedGpuDeformationResources
{
    /// <summary>
    /// Validates cold native deformation with the runtime's canonical sparse-morph packer
    /// and compute-skinning source contract. No renderer, API wrapper or dispatch is needed.
    /// </summary>
    public static bool TryInspectNativeMesh(XRMesh mesh, out string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        reason = string.Empty;
        if (!mesh.HasSkinning && !mesh.HasBlendshapes)
            return true;
        if (!CanReadCanonicalVertices(mesh))
        {
            reason = "Aggregate deformation requires complete canonical source vertices.";
            return false;
        }
        try
        {
            using IDisposable suppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
            const EAdvancedDeformationMeshPreparationPolicy policy = EAdvancedDeformationMeshPreparationPolicy.CanonicalSparseMorphs;
            PendingGpuDeformationMeshPreparation pending = CreatePendingMesh(mesh, 0, 0, policy);
            uint steps = 0;
            while (pending.Stage < PendingGpuDeformationMeshPreparation.Spill)
            {
                if ((steps++ & 0xFFFu) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                AdvanceManagedMeshPreparation(pending);
            }
            if (!MatchesPendingSource(pending, policy) || pending.RecordCount != pending.PackedRecordCount ||
                pending.DeltaCount != pending.PackedDeltaCount)
            {
                reason = "The canonical deformation source changed during cold preparation.";
                return false;
            }
            if (mesh.HasSkinning)
            {
                mesh.EnsureComputeSkinningBuffers();
                CaptureSkinningWitness(pending);
                if (!MatchesSkinningWitness(pending) ||
                    !HasReadableSkinningInput(pending.SkinningState!.CoreIndices) ||
                    !HasReadableSkinningInput(pending.SkinningState.CoreWeights) ||
                    pending.SkinningState.SpillHeaders is { } headers && !HasReadableSkinningInput(headers) ||
                    pending.SpillCount != 0 && !HasReadableSkinningInput(pending.SkinningState.SpillEntries))
                {
                    reason = "Compute skinning requires complete retained canonical influence buffers.";
                    return false;
                }
                for (uint index = 0; index < pending.SpillCount; index++)
                {
                    if ((index & 0xFFFu) == 0) cancellationToken.ThrowIfCancellationRequested();
                    _ = ReadSpillInfluence(pending.SkinningState!, index);
                }
                for (uint index = 0; index < pending.VertexCount; index++)
                {
                    if ((index & 0xFFFu) == 0) cancellationToken.ThrowIfCancellationRequested();
                    _ = ReadSkinInfluence(pending.SkinningState!, index);
                }
            }
            return true;
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException or OverflowException)
        {
            reason = error.Message;
            return false;
        }
    }

    private static bool HasReadableSkinningInput(XRDataBuffer? buffer)
        => buffer is { IsDestroyed: false, GpuProduced: false, HasGpuCompressedPayload: false, ClientSideSource: { } source } &&
            source.Length >= buffer.Length && buffer.TryGetAddress(out VoidPtr address) && address != VoidPtr.Zero;
}
