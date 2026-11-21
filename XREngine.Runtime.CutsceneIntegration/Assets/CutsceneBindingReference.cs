using XREngine.Data.Core;

namespace XREngine.Cutscenes;

/// <summary>Describes how a cutscene finds one target object at run time.</summary>
public class CutsceneBindingReference : XRBase
{
    private ECutsceneBindingKind _kind;
    private Guid _objectId;
    private string? _nodePath;
    private Guid _prefabAssetId;
    private Guid _prefabNodeId;
    private string? _slotName;

    /// <summary>How the target is found.</summary>
    public ECutsceneBindingKind Kind
    {
        get => _kind;
        set => SetField(ref _kind, value);
    }

    /// <summary>The identifier of the target scene object.</summary>
    public Guid ObjectId
    {
        get => _objectId;
        set => SetField(ref _objectId, value);
    }

    /// <summary>The path from the root to the target node.</summary>
    public string? NodePath
    {
        get => _nodePath;
        set => SetField(ref _nodePath, value);
    }

    /// <summary>The identifier of the prefab asset that holds the target node.</summary>
    public Guid PrefabAssetId
    {
        get => _prefabAssetId;
        set => SetField(ref _prefabAssetId, value);
    }

    /// <summary>The identifier of the target node inside the prefab.</summary>
    public Guid PrefabNodeId
    {
        get => _prefabNodeId;
        set => SetField(ref _prefabNodeId, value);
    }

    /// <summary>The slot name that the caller uses to supply a runtime target.</summary>
    public string? SlotName
    {
        get => _slotName;
        set => SetField(ref _slotName, value);
    }
}
