namespace XREngine.Components;

/// <summary>
/// Lip-sync metadata retained from a Unity avatar descriptor.
/// </summary>
public enum UnityAvatarLipSyncMode
{
    Default = 0,
    JawFlapBone = 1,
    JawFlapBlendShape = 2,
    VisemeBlendShape = 3,
    ParameterOnly = 4,
}
