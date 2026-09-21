using System;
using System.Numerics;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>Runtime state for a single nested DDGI cascade.</summary>
public sealed class DDGICascadeRuntimeState
{
    public int CascadeIndex { get; set; }
    public Vector3 Origin { get; set; } = Vector3.Zero;
    public Vector3 HalfExtents { get; set; } = new(10.0f, 6.0f, 10.0f);
    public IVector3 ProbeCounts { get; set; } = DDGIVolumeRuntimeState.DefaultProbeCounts;
    public Vector3 ProbeSpacing { get; set; } = Vector3.One;
    public int ProbeOffset { get; set; }
    public int UpdateInterval { get; set; } = 1;
    public int ScheduledProbeCount { get; set; } = (int)DDGIVolumeRuntimeState.DefaultMaxProbes;
    public int ProbeUpdateOffset { get; set; }
    public bool VisibilityEnabled { get; set; } = true;
    public int UpdatedProbeCount { get; set; }

    public Vector3 GridMin => DDGIVolumeRuntimeState.GetGridMin(Origin, HalfExtents, ProbeCounts);
    public Vector3 GridMax => Origin + HalfExtents;
    public int TotalProbeCount => ProbeCounts.X * ProbeCounts.Y * ProbeCounts.Z;

    public bool ShouldUpdateThisFrame(uint frameIndex)
        => (frameIndex % (uint)Math.Max(1, UpdateInterval)) == 0;

    public void SnapCenterToGrid(Vector3 cameraPosition)
        => Origin = DDGIVolumeRuntimeState.SnapToGrid(cameraPosition, ProbeSpacing);
}
