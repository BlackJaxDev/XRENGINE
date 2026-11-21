namespace UnityEngine;

/// <summary>Defines object visibility and persistence flags. Object operations must apply these flags.</summary>
[Flags]
public enum HideFlags
{
    None = 0,
    HideInHierarchy = 1,
    HideInInspector = 2,
    DontSaveInEditor = 4,
    NotEditable = 8,
    DontSaveInBuild = 16,
    DontUnloadUnusedAsset = 32,
    DontSave = DontSaveInEditor | DontSaveInBuild | DontUnloadUnusedAsset,
    HideAndDontSave = HideInHierarchy | DontSaveInEditor | NotEditable | DontSaveInBuild | DontUnloadUnusedAsset
}
