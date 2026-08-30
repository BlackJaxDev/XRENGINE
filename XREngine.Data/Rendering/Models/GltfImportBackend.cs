using System.ComponentModel;

namespace XREngine.Rendering.Models;

/// <summary>Selects the glTF import implementation.</summary>
public enum GltfImportBackend
{
    Auto,
    Native,
    Assimp,

    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    AssimpLegacy = Assimp,
}
