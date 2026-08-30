using System.ComponentModel;

namespace XREngine.Rendering.Models;

/// <summary>Selects the FBX import implementation.</summary>
public enum FbxImportBackend
{
    Auto,
    Native,
    Assimp,

    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    AssimpLegacy = Assimp,
}
