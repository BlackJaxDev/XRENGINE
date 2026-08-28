using XREngine.Scene;
using XREngine.Scene.Importers;

namespace XREngine.Editor.Importers;

/// <summary>
/// Editor-owned runtime scene import implementation. Runtime.Core receives this
/// through <see cref="RuntimeSceneImportServices"/> and never loads editor types.
/// </summary>
internal sealed class EditorRuntimeSceneImportServices : IRuntimeSceneImportServices
{
    public IReadOnlyList<SceneNode> ImportScene(string filePath)
        => SerializedSceneImporter.Import(filePath);
}
