using System.Numerics;

namespace XREngine.Rendering;

internal struct ProbeParamData : IEquatable<ProbeParamData>
{
    public Vector4 InfluenceInner;
    public Vector4 InfluenceOuter;
    public Vector4 InfluenceOffsetShape;
    public Vector4 ProxyCenterEnable;
    public Vector4 ProxyHalfExtents;
    public Vector4 ProxyRotation;

    public readonly bool Equals(ProbeParamData other)
        => InfluenceInner == other.InfluenceInner &&
           InfluenceOuter == other.InfluenceOuter &&
           InfluenceOffsetShape == other.InfluenceOffsetShape &&
           ProxyCenterEnable == other.ProxyCenterEnable &&
           ProxyHalfExtents == other.ProxyHalfExtents &&
           ProxyRotation == other.ProxyRotation;

    public override readonly bool Equals(object? obj)
        => obj is ProbeParamData other && Equals(other);

    public override readonly int GetHashCode()
        => HashCode.Combine(
            InfluenceInner,
            InfluenceOuter,
            InfluenceOffsetShape,
            ProxyCenterEnable,
            ProxyHalfExtents,
            ProxyRotation);
}
