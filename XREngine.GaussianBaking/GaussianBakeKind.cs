namespace XREngine.Rendering.Models.Gaussian.Baking;

/// <summary>Identifies the type of cooked content that a bake reads.</summary>
public enum GaussianBakeKind
{
    /// <summary>A prefab with a character hierarchy. Clips can animate it.</summary>
    Avatar,

    /// <summary>A prefab with a prop hierarchy.</summary>
    Item,

    /// <summary>A world with one or more scenes.</summary>
    World,
}
