namespace XREngine.Animation.Importers;

/// <summary>Unity AnimationClip wrap behavior preserved at import time.</summary>
public enum EUnityAnimationWrapMode
{
    Default = 0,
    Once = 1,
    Loop = 2,
    PingPong = 4,
    ClampForever = 8,
}
