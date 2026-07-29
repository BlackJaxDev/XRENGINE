using XREngine.Scene;

namespace MonkeyBallVR;

/// <summary>
/// Saved MonkeyBall world asset. Its scene graph is authored in YAML and cooked into an explicit runtime binary payload.
/// </summary>
public sealed class MonkeyBallWorldAsset : XRWorld
{
    public MonkeyBallWorldAsset()
    {
    }

    public MonkeyBallWorldAsset(string name, params XRScene[] scenes)
        : base(name, scenes)
    {
    }
}
