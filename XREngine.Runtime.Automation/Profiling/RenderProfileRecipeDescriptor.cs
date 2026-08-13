using XREngine.Rendering.Profiling;

namespace XREngine.Runtime.Automation.Profiling;

/// <summary>Loaded recipe identity returned by the control plane.</summary>
public sealed record RenderProfileRecipeDescriptor(string RecipeId, string Sha256, RenderProfileRecipe Recipe);
