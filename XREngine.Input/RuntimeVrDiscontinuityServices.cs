using System.Numerics;
using System.Threading;

namespace XREngine.Input;

/// <summary>Shares local-player discontinuities without mutating render-thread state from publishers.</summary>
public static class RuntimeVrDiscontinuityServices
{
    private static long _version;
    public static long Version => Interlocked.Read(ref _version);
    public static event Action<VrPoseDiscontinuity>? Published;

    public static void Publish(EVrPoseDiscontinuity kind, Matrix4x4? knownBasisChange = null)
    {
        long version = Interlocked.Increment(ref _version);
        Published?.Invoke(new(version, kind, knownBasisChange));
    }
}
