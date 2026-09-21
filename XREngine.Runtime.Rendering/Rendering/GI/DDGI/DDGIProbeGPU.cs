using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>GPU representation of a DDGI probe in the probe state buffer SSBO (32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DDGIProbeGPU
{
    /// <summary>Base world position (xyz) and probe state flags (w: 0 = active, 1 = enclosed/inactive).</summary>
    public Vector4 Position;

    /// <summary>Dynamic relocation offset vector (xyz) and confidence/distance (w).</summary>
    public Vector4 RelocationOffset;
}
