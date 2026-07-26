namespace XREngine.Scene;

/// <summary>
/// Optional application-owned scene import boundary. Runtime.Core owns the
/// serialized scene identity without depending on editor import implementations.
/// </summary>
public interface IRuntimeSceneImportServices
{
    IReadOnlyList<SceneNode> ImportScene(string filePath);
}

/// <summary>Installation point for optional scene import support.</summary>
public static class RuntimeSceneImportServices
{
    public static IRuntimeSceneImportServices? Current { get; set; }
}
