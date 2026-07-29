namespace XREngine.Rendering.PostProcessing;

/// <summary>
/// Represents a descriptor for a post-processing category, containing information such as the category's key, display name, description, and associated stage keys.
/// </summary>
/// <param name="key">The unique key identifying the category.</param>
/// <param name="displayName">The display name of the category.</param>
/// <param name="description">The description of the category.</param>
/// <param name="stageKeys">The list of associated stage keys for the category.</param>
public sealed class PostProcessCategoryDescriptor(string key, string displayName, string? description, IReadOnlyList<string> stageKeys)
{
    /// <summary>
    /// Gets the unique key identifying the category.
    /// </summary>
    public string Key { get; } = key;
    /// <summary>
    /// Gets the display name of the category. 
    /// If the display name is null or whitespace, it defaults to the key.
    /// </summary>
    public string DisplayName { get; } = string.IsNullOrWhiteSpace(displayName) ? key : displayName;
    /// <summary>
    /// Gets the description of the category.
    /// </summary>
    public string? Description { get; } = description;
    /// <summary>
    /// Gets the list of associated stage keys for the category.
    /// </summary>
    public IReadOnlyList<string> StageKeys { get; } = stageKeys ?? [];
}
