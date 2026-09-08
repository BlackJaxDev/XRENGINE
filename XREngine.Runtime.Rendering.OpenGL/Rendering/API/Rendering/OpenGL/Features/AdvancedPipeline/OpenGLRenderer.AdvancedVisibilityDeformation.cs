using Silk.NET.OpenGL;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    /// <summary>
    /// Binds the sealed meshlet streams and the aggregate deformation outputs
    /// consumed by native visibility and reconstruction. The per-slot overlay
    /// remains fenced with the rest of the visibility family; aggregate output
    /// buffers are the frame publication selected by each overlay row.
    /// </summary>
    private bool TryPrepareAdvancedVisibilityDeformation(
        OpenGLAdvancedVisibilityInputStorage input,
        AdvancedSharedGpuSceneDatabase database,
        in AdvancedGpuScenePublication publication,
        BackendReadyFramePackage package,
        OpenGLAdvancedVisibilitySlot slot,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(package);
        reason = "The GL deformation stage could not retain the sealed geometry publication.";
        if (package.State != EBackendReadyFramePackageState.Published ||
            package.CanonicalViews.IsEmpty)
        {
            reason = "The GL deformation stage does not have the sealed backend view package.";
            return false;
        }

        AdvancedGpuScenePublicationReference reference = new(publication);
        if (!database.TryGetPublicationSnapshot(in reference,
                out AdvancedGpuScenePublicationSnapshot snapshot) ||
            snapshot.DatabaseEpoch != publication.DatabaseEpoch ||
            snapshot.Draws.Sequence != publication.Sequence ||
            snapshot.Geometry.Sequence != publication.Sequence ||
            !input.TryBuildPreparedDeformations(snapshot, out reason))
        {
            return false;
        }

        slot.Upload(this, 64u, snapshot.GeometryPayloads.MeshletDescriptors.Data);
        slot.Upload(this, 65u, snapshot.GeometryPayloads.MeshletVertexIndices.Data);
        slot.Upload(this, 66u, snapshot.GeometryPayloads.MeshletTriangleWords.Data);
        slot.Upload(this, 67u, input.PreparedDeformations);

        bool requiresDeformation = false;
        foreach (AdvancedVisibilityPayload payload in input.Payloads)
            requiresDeformation |= payload.Skinned;
        if (!requiresDeformation)
        {
            reason = "Ready";
            return true;
        }

        AdvancedGpuDeformationPublication deformation = input.Deformation;
        if (deformation.ResourceGeneration == 0u || deformation.JobCount == 0u ||
            deformation.CurrentVertices is not { Length: > 0u } currentOwner ||
            GenericToAPI<GLDataBuffer>(currentOwner) is not { } current)
        {
            reason = "The sealed GL visibility family has no aggregate current deformation output.";
            return false;
        }

        current.EnsureStorageAllocatedForGpuCopy();
        if (!current.TryGetBindingId(out uint currentBuffer) || currentBuffer == 0u)
        {
            reason = "The GL aggregate current deformation output has no native buffer.";
            return false;
        }

        uint previousBuffer = currentBuffer;
        if (deformation.PreviousOutputValid)
        {
            if (deformation.PreviousVertices is not { Length: > 0u } previousOwner ||
                GenericToAPI<GLDataBuffer>(previousOwner) is not { } previous)
            {
                reason = "The sealed GL visibility family has no aggregate previous deformation output.";
                return false;
            }
            previous.EnsureStorageAllocatedForGpuCopy();
            if (!previous.TryGetBindingId(out previousBuffer) || previousBuffer == 0u)
            {
                reason = "The GL aggregate previous deformation output has no native buffer.";
                return false;
            }
        }

        RawGL.BindBufferBase(GLEnum.ShaderStorageBuffer, 62u, currentBuffer);
        RawGL.BindBufferBase(GLEnum.ShaderStorageBuffer, 63u, previousBuffer);
        // Aggregate deformation is a prior compute producer on this GL queue.
        // Make its SSBO writes visible before visibility or reconstruction reads
        // the output ranges selected by the just-uploaded sidecar.
        RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
        reason = "Ready";
        return true;
    }
}
