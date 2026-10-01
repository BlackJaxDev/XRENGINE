using XREngine.Scene;

namespace RollingBall;

/// <summary>
/// Saved Rolling Ball world asset. Its scene graph is authored in YAML and cooked into an explicit runtime binary payload.
/// </summary>
public sealed class RollingBallWorldAsset : XRWorld
{
    public RollingBallWorldAsset()
    {
    }

    public RollingBallWorldAsset(string name, params XRScene[] scenes)
        : base(name, scenes)
    {
    }
}
