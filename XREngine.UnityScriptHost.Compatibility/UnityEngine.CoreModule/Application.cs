using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Declares authoring environment state. Access reports unsupported until worker policy supplies it.</summary>
public static class Application
{
    /// <summary>Reports unsupported until worker policy supplies play mode.</summary>
    public static bool isPlaying => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Application.get_isPlaying()");

    /// <summary>Reports unsupported until worker policy supplies the selected Unity version.</summary>
    public static string unityVersion => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Application.get_unityVersion()");
}
