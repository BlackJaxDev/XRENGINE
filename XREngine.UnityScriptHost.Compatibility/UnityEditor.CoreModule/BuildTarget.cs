namespace UnityEditor;

/// <summary>Identifies source build targets. These values do not enable a build backend.</summary>
public enum BuildTarget
{
    NoTarget = -2,
    StandaloneOSX = 2,
    StandaloneWindows = 5,
    iOS = 9,
    Android = 13,
    StandaloneWindows64 = 19,
    StandaloneLinux64 = 24,
    Tizen = 29,
    PSP2 = 30,
    PS4 = 31,
    XboxOne = 33,
    N3DS = 35,
    tvOS = 37,
    Switch = 38,
    Lumin = 39,
    GameCoreXboxSeries = 42,
    GameCoreXboxOne = 43,
    PS5 = 44
}
