using System.Diagnostics.CodeAnalysis;

namespace XREngine.Rendering;

/// <summary>
/// Creates and identifies the VR eye pipeline for one exact family selection.
/// Each family maps to exactly one pipeline type. This factory never returns a
/// different family when the requested one cannot serve the topology; it throws.
/// </summary>
public static class OpenXrEyeRenderPipelineFactory
{
    /// <summary>
    /// Creates a new eye pipeline for <paramref name="family"/>.
    /// </summary>
    /// <param name="stereo">True for one layered stereo pipeline, false for one mono eye pipeline.</param>
    /// <param name="family">The selected VR eye pipeline family.</param>
    /// <param name="rvcMode">The requested RVC mode. Only <see cref="EVrRenderPipeline.Rvc"/> uses it.</param>
    /// <exception cref="NotSupportedException">The family does not implement this topology.</exception>
    public static RenderPipeline Create(
        bool stereo,
        EVrRenderPipeline family,
        ERvcPipelineMode rvcMode)
        => family switch
        {
            EVrRenderPipeline.Default => new DefaultRenderPipeline(stereo),
            EVrRenderPipeline.Advanced when !stereo => AdvancedRenderPipeline.CreateOpenXrEyePipeline(),
            EVrRenderPipeline.Advanced => throw new NotSupportedException(
                "VR.RenderPipeline=Advanced has no layered stereo eye pipeline. No other pipeline is substituted."),
            EVrRenderPipeline.Rvc => new RvcRenderPipeline(stereo, rvcMode),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown VR render pipeline family."),
        };

    /// <summary>
    /// Returns true when <see cref="Create"/> can make a pipeline for this
    /// topology and family.
    /// </summary>
    public static bool CanCreate(bool stereo, EVrRenderPipeline family)
        => Enum.IsDefined(family) && !(stereo && family == EVrRenderPipeline.Advanced);

    /// <summary>
    /// Returns true when <paramref name="pipeline"/> is exactly the pipeline
    /// that <see cref="Create"/> makes for the same arguments.
    /// </summary>
    public static bool Matches(
        [NotNullWhen(true)] RenderPipeline? pipeline,
        bool stereo,
        EVrRenderPipeline family,
        ERvcPipelineMode rvcMode)
        => family switch
        {
            // RvcRenderPipeline derives from DefaultRenderPipeline, so Default
            // requires the exact type.
            EVrRenderPipeline.Default =>
                pipeline is DefaultRenderPipeline defaultPipeline &&
                defaultPipeline.GetType() == typeof(DefaultRenderPipeline) &&
                defaultPipeline.Stereo == stereo,
            EVrRenderPipeline.Advanced =>
                pipeline is AdvancedRenderPipeline advancedPipeline &&
                advancedPipeline.IsOpenXrEyeProfile &&
                advancedPipeline.Stereo == stereo,
            EVrRenderPipeline.Rvc =>
                pipeline is RvcRenderPipeline rvcPipeline &&
                rvcPipeline.Stereo == stereo &&
                rvcPipeline.RvcPipelineMode == rvcMode,
            _ => false,
        };

    /// <summary>
    /// Describes an eye pipeline for diagnostics.
    /// </summary>
    public static string Describe(RenderPipeline? pipeline)
        => pipeline switch
        {
            null => "<none>",
            RvcRenderPipeline rvc => $"{nameof(RvcRenderPipeline)}(stereo={rvc.Stereo}, mode={rvc.RvcPipelineMode})",
            DefaultRenderPipeline d => $"{d.GetType().Name}(stereo={d.Stereo})",
            AdvancedRenderPipeline a => $"{a.GetType().Name}(stereo={a.Stereo}, openXrEye={a.IsOpenXrEyeProfile})",
            _ => pipeline.GetType().Name,
        };
}
