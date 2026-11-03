using System.IO.Hashing;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Event-driven skip of <see cref="CommittedMaterialBindingsMatch"/>. Bound
/// textures advance <see cref="XRTexture.AdvancedSourceVersion"/> on any input
/// change of the source encoder, and the material turns that into
/// <see cref="XRMaterialBase.TextureSourceVersion"/>. A material whose witness is
/// unchanged since its last successful check skips the full source encode.
/// </summary>
public sealed partial class AdvancedGpuScenePublisher
{
    private static readonly bool s_validateMaterialSourceWitness =
        Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.AdvancedPublisherWitnessValidate) == "1";

    private readonly Dictionary<AdvancedGpuHandle, AdvancedMaterialSourceWitness> _materialSourceWitnesses = [];
    private long _materialSourceWitnessHits;
    private long _materialSourceWitnessMisses;
    private long _materialSourceWitnessValidationFailures;
    private long _materialSourceWitnessConcurrentChanges;

    /// <summary>
    /// Gets the witness hit, miss, and validation-failure counts, and the count of
    /// validated hits that a concurrent texture write changed during the check.
    /// </summary>
    internal (long Hits, long Misses, long ValidationFailures, long ConcurrentChanges) MaterialSourceWitnessCounts
        => (Interlocked.Read(ref _materialSourceWitnessHits),
            Interlocked.Read(ref _materialSourceWitnessMisses),
            Interlocked.Read(ref _materialSourceWitnessValidationFailures),
            Interlocked.Read(ref _materialSourceWitnessConcurrentChanges));

    /// <summary>
    /// Returns <see cref="CommittedMaterialBindingsMatch"/> for the material, and
    /// skips the full check when the material's witness is unchanged.
    /// </summary>
    private bool WitnessedCommittedMaterialBindingsMatch(
        AdvancedGpuHandle handle,
        XRMaterial? material,
        MaterialBindingLayout layout,
        ReadOnlySpan<AdvancedMaterialTextureBinding> bindings,
        in AdvancedEngineSurfaceRecord engineSurface,
        in AdvancedUberBaseSurfaceRecord uberSurface)
    {
        // Read the witness before the check, so a change during the check makes
        // the stored witness differ next time.
        ulong committedInputHash = HashCommittedInputs(bindings, in engineSurface, in uberSurface);
        bool hasWitness = TryCaptureMaterialSourceWitness(material, layout, committedInputHash, out AdvancedMaterialSourceWitness witness);
        if (hasWitness &&
            _materialSourceWitnesses.TryGetValue(handle, out AdvancedMaterialSourceWitness stored) &&
            stored == witness)
        {
            Interlocked.Increment(ref _materialSourceWitnessHits);
            if (s_validateMaterialSourceWitness &&
                !CommittedMaterialBindingsMatch(material, layout, bindings, in engineSurface, in uberSurface))
            {
                _materialSourceWitnesses.Remove(handle);
                // A texture write on another thread (for example a streaming
                // upgrade) can land between the witness read and the check. It
                // advances a witnessed counter, so the next check misses. Only a
                // witness that is still equal after the check is a missed write path.
                if (!TryCaptureMaterialSourceWitness(material, layout, committedInputHash, out AdvancedMaterialSourceWitness after) ||
                    after != witness)
                {
                    Interlocked.Increment(ref _materialSourceWitnessConcurrentChanges);
                    return false;
                }
                Interlocked.Increment(ref _materialSourceWitnessValidationFailures);
                Debug.LogWarning(
                    $"[AdvancedPublisher] Material source witness called material '{material?.Name ?? "<null>"}' unchanged, " +
                    "but the committed bindings no longer match. A texture input change did not advance its source version.");
                return false;
            }
            return true;
        }

        Interlocked.Increment(ref _materialSourceWitnessMisses);
        if (!CommittedMaterialBindingsMatch(material, layout, bindings, in engineSurface, in uberSurface))
        {
            _materialSourceWitnesses.Remove(handle);
            return false;
        }

        if (hasWitness)
            _materialSourceWitnesses[handle] = witness;
        else
            _materialSourceWitnesses.Remove(handle);
        return true;
    }

    /// <summary>
    /// Captures the witness of a material. Returns false when a bound texture has
    /// no source-version contract (a texture view, a GPU-authored cube or array,
    /// or an unknown type), so the material always takes the full check.
    /// </summary>
    private bool TryCaptureMaterialSourceWitness(
        XRMaterial? material,
        MaterialBindingLayout layout,
        ulong committedInputHash,
        out AdvancedMaterialSourceWitness witness)
    {
        witness = default;
        if (material is null)
            return false;

        const ulong prime = 1099511628211UL;
        ulong hash = 14695981039346656037UL;
        int count = material.Textures.Count;
        for (int index = 0; index < count; ++index)
        {
            XRTexture? texture = material.Textures[index];
            ulong counters;
            switch (texture)
            {
                case null:
                    counters = 0UL;
                    break;
                case XRTexture2D texture2D:
                    if (texture2D.IsDestroyed)
                        return false;
                    Mipmap2D[] mipmaps = texture2D.Mipmaps;
                    Mipmap2D? baseMip = mipmaps.Length > 0 ? mipmaps[0] : null;
                    counters = unchecked(
                        texture2D.CanonicalSourceContentGeneration * 31UL +
                        texture2D.ImportedSourceMetadataEpoch * 17UL +
                        (ulong)mipmaps.Length * 13UL +
                        (ulong)(uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(mipmaps) * 7UL +
                        (ulong)(uint)(baseMip?.CanonicalContentEpoch ?? 0) * 5UL +
                        (ulong)(uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(baseMip));
                    // Read the base mip shape directly as well: a write made while
                    // notifications are suppressed (for example during load) does
                    // not advance the mip content epoch.
                    hash = unchecked((hash ^ (baseMip?.Width ?? 0u)) * prime);
                    hash = unchecked((hash ^ (baseMip?.Height ?? 0u)) * prime);
                    hash = unchecked((hash ^ (ulong)(baseMip?.InternalFormat ?? default)) * prime);
                    break;
                case XRTexture2DArray array:
                    ulong layerWitness = array.CanonicalLayerContentWitness;
                    if (array.IsDestroyed || layerWitness == 0UL)
                        return false;
                    counters = layerWitness;
                    break;
                case XRTextureCube cube:
                    if (cube.IsDestroyed || !cube.TryGetCanonicalCpuSourceGeneration(out ulong cubeGeneration))
                        return false;
                    counters = cubeGeneration;
                    break;
                default:
                    return false;
            }

            hash = unchecked((hash ^ counters) * prime);
            hash = unchecked((hash ^ (ulong)(uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(texture)) * prime);
        }

        witness = new AdvancedMaterialSourceWitness(
            material,
            layout,
            material.BindingLayoutVersion,
            material.BindingValueVersion,
            material.BindingResourceVersion,
            material.ShaderStateRevision,
            material.UberStateRevision,
            material.TextureSourceVersion,
            hash,
            _resourcePublisher.CommitGeneration,
            Database.Materials.Generations,
            committedInputHash);
        return true;
    }

    /// <summary>
    /// Hashes the committed bindings and surface records that the check compares
    /// with the encoded sources. The bytes are plain GPU records, so the hash
    /// allocates nothing.
    /// </summary>
    private static ulong HashCommittedInputs(
        ReadOnlySpan<AdvancedMaterialTextureBinding> bindings,
        in AdvancedEngineSurfaceRecord engineSurface,
        in AdvancedUberBaseSurfaceRecord uberSurface)
    {
        ulong hash = XxHash64.HashToUInt64(MemoryMarshal.AsBytes(bindings));
        hash = XxHash64.HashToUInt64(
            MemoryMarshal.AsBytes(new ReadOnlySpan<AdvancedEngineSurfaceRecord>(in engineSurface)),
            unchecked((long)hash));
        return XxHash64.HashToUInt64(
            MemoryMarshal.AsBytes(new ReadOnlySpan<AdvancedUberBaseSurfaceRecord>(in uberSurface)),
            unchecked((long)hash));
    }
}
