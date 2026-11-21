using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Dense shading-kernel ids of one scene publication, split by native shading family.
/// Bit <c>k</c> of a mask is set when dense kernel <c>k</c> belongs to that family. The
/// native shading pass records only the indirect dispatches of the family it binds.
/// </summary>
internal readonly record struct VulkanAdvancedNativeShadingKernelFamilies(UInt128 Standard, UInt128 Uber)
{
    private static readonly ulong UberOpaqueLayoutHash = Hash(NativeUberMaterialSchema.Opaque.LayoutHash);
    private static readonly ulong UberMaskedLayoutHash = Hash(NativeUberMaterialSchema.Masked.LayoutHash);

    /// <summary>True when the scene has at least one native Uber shading kernel.</summary>
    internal bool HasUber => Uber != UInt128.Zero;

    /// <summary>Returns true when dense kernel <paramref name="denseKernel"/> is in <paramref name="mask"/>.</summary>
    internal static bool Contains(UInt128 mask, uint denseKernel)
        => denseKernel < 128u && (mask & (UInt128.One << (int)denseKernel)) != UInt128.Zero;

    /// <summary>
    /// Builds the family masks from the published kernel table. Kernels whose layout is
    /// native Uber (opaque or masked) are Uber; every other kernel is standard.
    /// </summary>
    internal static VulkanAdvancedNativeShadingKernelFamilies Create(
        AdvancedGpuRecordTablePublicationSnapshot<AdvancedShadingKernelRecord> kernels)
    {
        UInt128 standard = UInt128.Zero, uber = UInt128.Zero;
        ReadOnlySpan<AdvancedShadingKernelRecord> records = kernels.PhysicalRecords;
        ReadOnlySpan<AdvancedGpuHandle> handles = kernels.PhysicalHandles;
        ReadOnlySpan<byte> occupancy = kernels.PhysicalOccupancy;
        int count = Math.Min(records.Length, Math.Min(handles.Length, occupancy.Length));
        for (int index = 0; index < count; ++index)
        {
            if (occupancy[index] == 0 || !kernels.TryGetDenseIndex(handles[index], out uint dense) || dense >= 128u)
                continue;

            UInt128 bit = UInt128.One << (int)dense;
            ulong layout = records[index].MaterialLayoutHash;
            if (layout == UberOpaqueLayoutHash || layout == UberMaskedLayoutHash)
                uber |= bit;
            else
                standard |= bit;
        }
        return new(standard, uber);
    }

    /// <summary>FNV-1a of a layout identity string; matches the published kernel layout hash.</summary>
    private static ulong Hash(string value)
    {
        ulong hash = 14695981039346656037ul;
        for (int i = 0; i < value.Length; ++i) { hash ^= value[i]; hash *= 1099511628211ul; }
        return hash;
    }
}
