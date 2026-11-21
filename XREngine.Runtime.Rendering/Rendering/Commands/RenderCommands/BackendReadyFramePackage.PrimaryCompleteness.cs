using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

public sealed partial class BackendReadyFramePackage
{
    private readonly Dictionary<(int Pass, uint Key, uint Primitive), AdvancedDrawSubmissionRecord> _primaryNativeSubmissions = [];
    private readonly HashSet<(int Pass, uint Key, uint Primitive)> _duplicatePrimaryNativeSubmissions = [];

    /// <summary>Gets the enabled primary draws that have no exact native submission in this package.</summary>
    public int MissingPrimaryNativeOwnerCount { get; private set; }
    public int FirstMissingPrimaryNativePass { get; private set; } = -1;
    public uint FirstMissingPrimaryNativeStableQueryKey { get; private set; }
    public string? FirstMissingPrimaryNativeReason { get; private set; }

    /// <summary>
    /// Gets the publication sequence of the canonical draw identity that the first
    /// missing primary draw carries, or 0 when the draw has no identity for that
    /// primitive. A value that differs from <see cref="CanonicalScenePublication"/>
    /// shows that the command identity and the package publication are from different
    /// publications.
    /// </summary>
    public ulong FirstMissingPrimaryNativeIdentitySequence { get; private set; }

    /// <summary>
    /// Gets the command of the first missing primary draw, or null when no draw is missing.
    /// A diagnostic reader passes it to <see cref="GPUScene.TryGetCanonicalCompatibilityReason"/>
    /// to learn why the canonical scene did not publish the draw.
    /// </summary>
    public IRenderCommandMesh? FirstMissingPrimaryNativeCommand { get; private set; }

    /// <summary>Gets the submesh index of the first missing primary draw, or -1 when no draw is missing.</summary>
    public int FirstMissingPrimaryNativePrimitiveIndex { get; private set; } = -1;

    /// <summary>Gets the mesh name of the first missing primary draw, or null when the mesh has no name.</summary>
    public string? FirstMissingPrimaryNativeMeshName { get; private set; }

    /// <summary>Gets the material name of the first missing primary draw, or null when the material has no name.</summary>
    public string? FirstMissingPrimaryNativeMaterialName { get; private set; }

    private void ClearPrimaryNativeCompleteness()
    {
        _primaryNativeSubmissions.Clear();
        _duplicatePrimaryNativeSubmissions.Clear();
        MissingPrimaryNativeOwnerCount = 0;
        FirstMissingPrimaryNativePass = -1;
        FirstMissingPrimaryNativeStableQueryKey = 0u;
        FirstMissingPrimaryNativeReason = null;
        FirstMissingPrimaryNativeIdentitySequence = 0UL;
        FirstMissingPrimaryNativeCommand = null;
        FirstMissingPrimaryNativePrimitiveIndex = -1;
        FirstMissingPrimaryNativeMeshName = null;
        FirstMissingPrimaryNativeMaterialName = null;
    }

    /// <summary>
    /// Checks the primary pass membership against the retained canonical publication.
    /// The package stores only counts and the first failure for frame admission.
    /// </summary>
    private void CapturePrimaryNativeCompleteness()
    {
        ClearPrimaryNativeCompleteness();
        if (CanonicalFrame.FrameId == Identity.FrameId &&
            CanonicalFrame.DependencySignature == DependencySignature &&
            TryGetCanonicalPublicationSnapshot(out AdvancedGpuScenePublicationSnapshot publication))
        {
            ReadOnlySpan<AdvancedDrawSubmissionRecord> submissions = publication.Submission.Records;
            for (int i = 0; i < submissions.Length; i++)
            {
                AdvancedDrawSubmissionRecord submission = submissions[i];
                int submissionPass = unchecked((int)submission.PassIndex);
                if (!IsPrimaryPass(submissionPass))
                    continue;
                (int Pass, uint Key, uint Primitive) key =
                    (submissionPass, submission.StableQueryKey, submission.PrimitiveIndex);
                if (!_primaryNativeSubmissions.TryAdd(key, submission))
                    _duplicatePrimaryNativeSubmissions.Add(key);
            }
        }

        ReadOnlySpan<BackendReadyRenderPass> passes = Passes;
        for (int passIndex = 0; passIndex < passes.Length; passIndex++)
        {
            BackendReadyRenderPass pass = passes[passIndex];
            if (!IsPrimaryPass(pass.PassIndex))
                continue;
            for (int commandIndex = 0; commandIndex < pass.CommandCount; commandIndex++)
            {
                RenderCommand command = GetPassCommand(in pass, commandIndex);
                if (!command.Enabled || command is not IRenderCommandMesh mesh ||
                    command is RenderCommandMesh3D
                        { RenderSupplementalMaterialPass: not null })
                    continue;

                GpuSceneMeshCommandSnapshot? frozen = command is RenderCommandMesh3D frozenMesh
                    ? frozenMesh.CaptureGpuSceneSnapshot()
                    : null;
                if (frozen.HasValue &&
                    (!frozen.Value.BasePassEnabled || frozen.Value.Instances == 0u))
                    continue;
                XRMaterial? material = frozen is { } snapshot
                    ? snapshot.MaterialOverride ?? snapshot.Renderer?.Material
                    : mesh.MaterialOverride ?? mesh.Mesh?.Material;
                if (material is not null &&
                    material.PassSet.TryGetPass(EMaterialPassIdentity.Base,
                        out MaterialPassDefinition basePass) && !basePass.Enabled)
                    continue;

                AdvancedGpuSceneDrawIdentitySnapshot identities = command is RenderCommandMesh3D primary
                    ? primary.CanonicalDrawIdentitySnapshot
                    : default;
                int primitiveCount = Math.Max(1, frozen?.Renderer?.Submeshes.Count ??
                    mesh.Mesh?.Submeshes.Count ?? 0);
                for (int primitiveIndex = 0; primitiveIndex < primitiveCount; primitiveIndex++)
                {
                    (int Pass, uint Key, uint Primitive) key =
                        (pass.PassIndex, command.StableQueryKey, checked((uint)primitiveIndex));
                    string? reason = null;
                    if (_duplicatePrimaryNativeSubmissions.Contains(key))
                        reason = "DuplicateCanonicalSubmission";
                    else if (!_primaryNativeSubmissions.TryGetValue(key,
                                 out AdvancedDrawSubmissionRecord submission))
                        reason = "MissingCanonicalSubmission";
                    else if (submission.CompatibilityReason != EAdvancedCanonicalCompatibilityReason.None)
                        reason = "IncompatibleCanonicalSubmission";
                    else if ((submission.Flags & (uint)GPUIndirectRenderFlags.CpuFallbackOnly) != 0u)
                        reason = "CpuFallbackOnly";
                    else if (!identities.TryGetPrimitive(primitiveIndex,
                                 out AdvancedGpuSceneDrawIdentity identity) ||
                             identity.Publication.DatabaseEpoch !=
                                 CanonicalScenePublication.DatabaseEpoch ||
                             identity.Publication.Sequence != CanonicalScenePublication.Sequence ||
                             identity.Handle != submission.Draw)
                        reason = "CanonicalPrimitiveIdentityMismatch";

                    if (reason is null)
                        continue;
                    MissingPrimaryNativeOwnerCount++;
                    if (FirstMissingPrimaryNativePass >= 0)
                        continue;
                    FirstMissingPrimaryNativePass = pass.PassIndex;
                    FirstMissingPrimaryNativeStableQueryKey = command.StableQueryKey;
                    FirstMissingPrimaryNativeReason = reason;
                    FirstMissingPrimaryNativeIdentitySequence =
                        identities.TryGetPrimitive(primitiveIndex, out AdvancedGpuSceneDrawIdentity firstIdentity)
                            ? firstIdentity.Publication.Sequence
                            : 0UL;
                    XRMeshRenderer? firstRenderer = frozen?.Renderer ?? mesh.Mesh;
                    FirstMissingPrimaryNativeCommand = mesh;
                    FirstMissingPrimaryNativePrimitiveIndex = primitiveIndex;
                    FirstMissingPrimaryNativeMeshName = firstRenderer?.Mesh?.Name ?? firstRenderer?.Name;
                    FirstMissingPrimaryNativeMaterialName = material?.Name;
                }
            }
        }
    }

    private static bool IsPrimaryPass(int pass)
        => pass is (int)EDefaultRenderPass.OpaqueDeferred or
            (int)EDefaultRenderPass.OpaqueForward or
            (int)EDefaultRenderPass.MaskedForward;

}
