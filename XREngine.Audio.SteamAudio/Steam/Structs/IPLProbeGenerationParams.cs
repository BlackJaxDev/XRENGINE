using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Settings used to generate probes. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLProbeGenerationParams
{
    /** The algorithm to use for generating probes. */
    public IPLProbeGenerationType type;

    /** Spacing (in meters) between two neighboring probes. Only for \c IPL_PROBEGENERATIONTYPE_UNIFORMFLOOR. */
    public float spacing;

    /** Height (in meters) above the floor at which probes will be generated. Only for
        \c IPL_PROBEGENERATIONTYPE_UNIFORMFLOOR. */
    public float height;

    /** A transformation matrix that transforms an axis-aligned unit cube, with minimum and maximum vertices
        at (0, 0, 0) and (1, 1, 1), into a parallelopiped volume. Probes will be generated within this
        volume. */
    public IPLMatrix4x4 transform;
}
