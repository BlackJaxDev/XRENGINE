namespace UnityEditor;

/// <summary>Identifies source build target groups without selecting native engine behavior.</summary>
public enum BuildTargetGroup
{
    Unknown = 0,
    Standalone = 1,
    iOS = 4,
    Android = 7,
    WebGL = 13,
    WSA = 14,
    PS4 = 19,
    Lumin = 28,
    PS5 = 33
}
