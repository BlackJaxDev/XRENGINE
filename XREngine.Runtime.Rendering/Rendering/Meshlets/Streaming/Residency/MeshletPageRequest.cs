using System.Runtime.InteropServices;

namespace XREngine.Rendering.Meshlets;

/// <summary>
/// One page request that the GPU writes to the request buffer (16 bytes). A task or request lane
/// writes it when a cluster is in the ideal cut and the page of its group is not resident.
/// </summary>
/// <param name="GlobalPage">The page table entry of the requested page.</param>
/// <param name="BandAndPriority">
/// The <see cref="EMeshletPageRequestBand"/> in bits 16 to 23, and the priority inside the band in
/// bits 0 to 15: the group error divided by the pixel threshold, quantized to 16 bits.
/// </param>
/// <param name="PixelScale">
/// The projected error of one unit of mesh-space error for the requesting group sphere and view.
/// The manager multiplies a page's largest group error by it to find the band of a dependency.
/// </param>
/// <param name="Reserved">Zero. Keeps the entry at 16 bytes.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MeshletPageRequest(uint GlobalPage, uint BandAndPriority, float PixelScale, uint Reserved = 0u)
{
    /// <summary>The bytes of one request in the request buffer.</summary>
    public const int SizeInBytes = 16;

    /// <summary>Gets the band of the request.</summary>
    public EMeshletPageRequestBand Band
        => (EMeshletPageRequestBand)Math.Min((BandAndPriority >> 16) & 0xFFu, (uint)EMeshletPageRequestBand.Foreground);

    /// <summary>Gets the priority of the request inside its band.</summary>
    public ushort Priority
        => (ushort)(BandAndPriority & 0xFFFFu);

    /// <summary>Packs a band and a priority into the <see cref="BandAndPriority"/> word.</summary>
    public static uint PackBandAndPriority(EMeshletPageRequestBand band, ushort priority)
        => ((uint)band << 16) | priority;
}
