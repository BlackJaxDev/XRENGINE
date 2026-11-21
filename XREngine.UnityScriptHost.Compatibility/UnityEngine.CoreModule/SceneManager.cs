using XREngine.UnityScriptHost.Execution;

namespace UnityEngine.SceneManagement;

/// <summary>Declares Unity scene queries. Operations report unsupported until the worker has scene state.</summary>
public static class SceneManager
{
    /// <summary>Reports unsupported until the worker can count scenes.</summary>
    public static int sceneCount => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SceneManagement.SceneManager.get_sceneCount()");

    /// <summary>Reports unsupported until the worker can select a scene by index.</summary>
    public static Scene GetSceneAt(int index)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SceneManagement.SceneManager.GetSceneAt(System.Int32)");

    /// <summary>Reports unsupported until the worker can select the active scene.</summary>
    public static Scene GetActiveScene()
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SceneManagement.SceneManager.GetActiveScene()");

    /// <summary>Reports unsupported until the worker can move a game object between scenes.</summary>
    public static void MoveGameObjectToScene(UnityEngine.GameObject gameObject, Scene scene)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(UnityEngine.GameObject,UnityEngine.SceneManagement.Scene)");
}
