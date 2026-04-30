using Silk.NET.OpenXR;
using System;
using System.Numerics;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    /// <summary>
    /// Returns the most recently located IPD (meters) derived from per-eye poses.
    /// Requires that <see cref="LocateViews"/> has succeeded at least once.
    /// </summary>
    public bool TryGetLatestIPD(out float ipdMeters)
    {
        ipdMeters = 0f;

        try
        {
            if (_views is null || _viewCount < 2)
                return false;

            // NOTE: _views is updated during xrLocateViews.
            var l = _views[0].Pose.Position;
            var r = _views[1].Pose.Position;
            var lp = new Vector3(l.X, l.Y, l.Z);
            var rp = new Vector3(r.X, r.Y, r.Z);

            float ipd = Vector3.Distance(lp, rp);
            if (!float.IsFinite(ipd) || ipd <= 0f)
                return false;

            ipdMeters = ipd;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
