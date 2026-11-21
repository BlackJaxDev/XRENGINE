namespace XREngine.Cutscenes;

/// <summary>
/// A track that animates one member of a component on its bound node. Its clips are PropertyTrackClip instances.
/// The member must be a float, Vector3, or Quaternion property or field. The type of the member selects the curve of each clip.
/// </summary>
public class PropertyTrack : CutsceneTrack
{
    private string _componentType = string.Empty;
    private string _memberPath = string.Empty;

    /// <summary>
    /// The type name of the component that holds the member, as a short name or a full name.
    /// An empty value selects the bound scene node itself.
    /// </summary>
    public string ComponentType
    {
        get => _componentType;
        set => SetField(ref _componentType, value);
    }

    /// <summary>
    /// The member to animate. Separate nested members with a period, for example <c>Parameters.VerticalFieldOfView</c>.
    /// Each member before the last must hold an object of a reference type.
    /// </summary>
    public string MemberPath
    {
        get => _memberPath;
        set => SetField(ref _memberPath, value);
    }
}
