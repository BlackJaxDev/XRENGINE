using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

/// <summary>
/// Writes render pipelines as <c>.xrs</c> scripts. An authored <see cref="CustomRenderPipeline"/> writes as a
/// standalone script. A code pipeline with a registered script base writes as a derived script that holds only the
/// settings that differ from the type defaults. Any pipeline can write as an expanded standalone script.
/// </summary>
public static class RenderPipelineScriptWriter
{
    /// <summary>Returns true when <see cref="Write"/> can write <paramref name="pipeline"/>.</summary>
    public static bool CanWrite(RenderPipeline pipeline)
        => pipeline is CustomRenderPipeline || RenderPipelineScriptBases.TryGet(pipeline.GetType(), out _);

    /// <summary>Writes <paramref name="pipeline"/> as a standalone or derived script.</summary>
    /// <exception cref="InvalidOperationException">The pipeline type has no script base. Use <see cref="WriteExpanded"/>.</exception>
    public static string Write(RenderPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return RenderPipelineScript.WritePipeline(pipeline);
    }

    /// <summary>Writes a derived script whose parent is the script at <paramref name="parentPath"/>.</summary>
    /// <param name="parentPath">The parent path, relative to the new script or absolute.</param>
    public static string WriteDerivedFromScript(string parentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        return RenderPipelineScript.WriteDerivedFromScript(parentPath);
    }

    /// <summary>
    /// Writes <paramref name="pipeline"/> as a standalone script for <paramref name="profile"/>. Code items that the
    /// text cannot hold become <c>// unconverted:</c> lines, and a load reports each one.
    /// </summary>
    /// <param name="pipeline">The pipeline.</param>
    /// <param name="profile">The resource profile whose layout the script holds.</param>
    /// <param name="unconverted">When not null, receives one line for each unconverted item.</param>
    public static string WriteExpanded(RenderPipeline pipeline, RenderPipelineResourceProfile profile, List<string>? unconverted = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return RenderPipelineScript.WriteExpandedPipeline(pipeline, profile, unconverted ?? []);
    }
}
