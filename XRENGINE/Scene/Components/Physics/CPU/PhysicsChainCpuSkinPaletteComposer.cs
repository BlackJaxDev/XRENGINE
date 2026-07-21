using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Allocation-free CPU composition matching XRMeshRenderer's
/// root-bind * inverse-bind * current-world palette contract.
/// </summary>
public static class PhysicsChainCpuSkinPaletteComposer
{
    public static bool TryCompose(
        ReadOnlySpan<Matrix4x4> particleWorldMatrices,
        ReadOnlySpan<Matrix4x4> inverseBindMatrices,
        ReadOnlySpan<PhysicsChainCpuSkinPaletteMapping> mappings,
        in Matrix4x4 rootBindMatrix,
        Span<PhysicsChainCpuSkinPaletteMatrix> destination)
    {
        for (int mappingIndex = 0; mappingIndex < mappings.Length; ++mappingIndex)
        {
            PhysicsChainCpuSkinPaletteMapping mapping = mappings[mappingIndex];
            if ((uint)mapping.ParticleIndex >= (uint)particleWorldMatrices.Length
                || (uint)mapping.PaletteIndex >= (uint)inverseBindMatrices.Length
                || (uint)mapping.PaletteIndex >= (uint)destination.Length)
                return false;
        }

        for (int mappingIndex = 0; mappingIndex < mappings.Length; ++mappingIndex)
        {
            PhysicsChainCpuSkinPaletteMapping mapping = mappings[mappingIndex];
            Matrix4x4 skinMatrix = rootBindMatrix
                * inverseBindMatrices[mapping.PaletteIndex]
                * particleWorldMatrices[mapping.ParticleIndex];
            destination[mapping.PaletteIndex] = PhysicsChainCpuSkinPaletteMatrix.FromRowVectorMatrix(skinMatrix);
        }
        return true;
    }

    public static bool TryComposeCurrentAndPrevious(
        ReadOnlySpan<Matrix4x4> currentParticleWorldMatrices,
        ReadOnlySpan<Matrix4x4> previousParticleWorldMatrices,
        ReadOnlySpan<Matrix4x4> inverseBindMatrices,
        ReadOnlySpan<PhysicsChainCpuSkinPaletteMapping> mappings,
        in Matrix4x4 rootBindMatrix,
        Span<PhysicsChainCpuSkinPaletteMatrix> currentDestination,
        Span<PhysicsChainCpuSkinPaletteMatrix> previousDestination)
        => TryCompose(
                currentParticleWorldMatrices,
                inverseBindMatrices,
                mappings,
                rootBindMatrix,
                currentDestination)
            && TryCompose(
                previousParticleWorldMatrices,
                inverseBindMatrices,
                mappings,
                rootBindMatrix,
                previousDestination);
}
