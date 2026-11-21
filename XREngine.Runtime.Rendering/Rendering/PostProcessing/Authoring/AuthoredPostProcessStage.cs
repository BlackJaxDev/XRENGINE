namespace XREngine.Rendering.PostProcessing;

/// <summary>
/// A post-process stage that an authored pipeline declares as data. A library stage reuses a stage of
/// <see cref="CommonPostProcessStages"/> by key. A custom stage lists its own parameters and stores values in a
/// <see cref="PostProcessPropertyBag"/>.
/// </summary>
public sealed class AuthoredPostProcessStage
{
    /// <summary>Gets or sets the stage key. Commands read the stage by this key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the label that the camera editor shows, or an empty string to show the key.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the stage is a library stage. A library stage ignores <see cref="Parameters"/>.</summary>
    public bool IsLibraryStage { get; set; }

    /// <summary>Gets or sets the category key, or an empty string for the default category.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Gets or sets the category label, or an empty string to show the key.</summary>
    public string CategoryDisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the order of the stage in the camera editor. Lower values come first.</summary>
    public int Order { get; set; }

    /// <summary>Gets or sets the camera editor section of the stage.</summary>
    public PipelineEditorSection EditorSection { get; set; } = PipelineEditorSection.PostProcessing;

    /// <summary>Gets or sets the parameters of a custom stage.</summary>
    public List<AuthoredPostProcessParameter> Parameters { get; set; } = [];

    /// <summary>Returns a deep copy.</summary>
    public AuthoredPostProcessStage Clone()
    {
        AuthoredPostProcessStage copy = (AuthoredPostProcessStage)MemberwiseClone();
        copy.Parameters = new List<AuthoredPostProcessParameter>(Parameters.Count);
        for (int i = 0; i < Parameters.Count; i++)
            copy.Parameters.Add(Parameters[i].Clone());

        return copy;
    }
}
