using XREngine.Rendering;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// The <c>.xrs</c> templates of the new pipeline wizard. Templates load through <see cref="RenderPipelineScriptLoader"/>,
/// the same path as saved pipelines.
/// </summary>
public static class RenderPipelineTemplates
{
    /// <summary>The template folder, relative to the engine assets folder.</summary>
    public const string RelativeDirectory = "RenderPipelines/Templates";

    /// <summary>An empty pipeline.</summary>
    public static RenderPipelineTemplate Blank { get; } = new(
        "Blank",
        "blank.xrs",
        "An empty pipeline. Add passes, resources, and commands yourself.");

    /// <summary>Forward rendering with bloom and tonemapping.</summary>
    public static RenderPipelineTemplate MinimalForward { get; } = new(
        "Minimal Forward",
        "minimal-forward.xrs",
        "Forward rendering into an HDR target, an optional bloom stage, and tonemapping to the output.");

    /// <summary>A small deferred pipeline.</summary>
    public static RenderPipelineTemplate DeferredLite { get; } = new(
        "Deferred Lite",
        "deferred-lite.xrs",
        "A G-buffer pass, a light combine into an HDR target, forward passes for transparent geometry, and tonemapping.");

    /// <summary>Gets the templates in the order that the wizard shows them.</summary>
    public static IReadOnlyList<RenderPipelineTemplate> All { get; } = [Blank, MinimalForward, DeferredLite];

    /// <summary>Gets the template folder of the running engine.</summary>
    public static string DefaultDirectory
        => Path.Combine(Engine.Assets.EngineAssetsPath, RelativeDirectory);

    /// <summary>Returns the path of <paramref name="template"/> in <paramref name="directory"/>.</summary>
    public static string GetPath(RenderPipelineTemplate template, string directory)
        => Path.Combine(directory, template.FileName);

    /// <summary>Loads <paramref name="template"/> from <paramref name="directory"/>.</summary>
    /// <exception cref="IOException">Thrown when the template file cannot be read.</exception>
    public static RenderPipelineScriptLoadResult Load(RenderPipelineTemplate template, string directory)
    {
        ArgumentNullException.ThrowIfNull(template);
        return RenderPipelineScriptLoader.LoadFile(GetPath(template, directory));
    }
}
