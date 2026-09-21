using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>GPU representation of a probe ray in the persistent ray buffer SSBO (48 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DDGIRayGPU
{
    /// <summary>World space origin (xyz) and ray minimum distance tMin (w).</summary>
    public Vector4 Origin;

    /// <summary>World space unit direction (xyz) and ray maximum distance tMax (w).</summary>
    public Vector4 Direction;

    /// <summary>Global probe SSBO index (x), ray sub-index (y), frame index (z), and reserved flags (w).</summary>
    public Vector4 Params;
}
