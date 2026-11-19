namespace UnityEngine;

/// <summary>Names the Unity startup point requested by an initialization attribute.</summary>
public enum RuntimeInitializeLoadType
{
    AfterSceneLoad = 0,
    BeforeSceneLoad = 1,
    AfterAssembliesLoaded = 2,
    BeforeSplashScreen = 3,
    SubsystemRegistration = 4,
}
