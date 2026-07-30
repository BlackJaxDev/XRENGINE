using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Fixed 64-byte delayed diagnostic row replicated per frame slot and view.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
public struct AdvancedReconstructionGpuCounters
{
    public uint ValidSurfaces;
    public uint InvalidPayloads;
    public uint StaleGenerations;
    public uint MissingGeometry;
    public uint PrimitiveOutOfBounds;
    public uint DegenerateTriangles;
    public uint DerivativeFailures;
    public uint ConservativeMipFallbacks;
    public uint ValidVelocities;
    public uint InvalidVelocities;
    public uint ReactivePixels;
    public uint MaskedEdges;
    public uint NonFiniteAttributes;
    public uint StaticSurfaces;
    public uint DeformedSurfaces;
    public uint Reserved;
}
