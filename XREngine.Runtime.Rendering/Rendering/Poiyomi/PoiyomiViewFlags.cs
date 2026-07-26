namespace XREngine.Rendering.Poiyomi;

/// <summary>
/// Classifies the native render view currently consuming a Poiyomi material.
/// Flags can be combined for views such as a stereo mirror capture.
/// </summary>
[Flags]
public enum PoiyomiViewFlags
{
    None = 0,
    MainCamera = 1 << 0,
    Mirror = 1 << 1,
    SceneCapture = 1 << 2,
    Stereo = 1 << 3,
    LeftEye = 1 << 4,
    RightEye = 1 << 5,
}
