using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>GPU uniform block for DDGI constant parameters across compute passes (96 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DDGIConstantsGPU
{
    /// <summary>Minimum corner of the volume in world units (xyz) and max probe distance (w).</summary>
    public Vector4 GridMin;
    /// <summary>Step vector between adjacent probes (xyz) and Chebyshev visibility power (w).</summary>
    public Vector4 GridSpacing;
    /// <summary>Probe counts along X, Y, Z (xyz) and total probe count (w).</summary>
    public Vector4 ProbeCounts;
    /// <summary>Irradiance atlas width/height (xy) and visibility atlas width/height (zw).</summary>
    public Vector4 AtlasParams;
    /// <summary>Hysteresis (x), normalBias (y), viewBias (z), intensity (w).</summary>
    public Vector4 TuningParams;
    /// <summary>Rays per probe (x), max updated probes (y), frame counter (z), debug flags (w).</summary>
    public Vector4 TracingParams;
}
