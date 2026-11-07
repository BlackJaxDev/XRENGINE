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
    /// Creates a new eye pipeline for <paramref name="family"/> and attaches the overlays of
    /// <paramref name="scriptPath"/> with <see cref="AttachScriptOverlays"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">The family does not implement this topology, or the script does not apply.</exception>
    public static RenderPipeline Create(
        bool stereo,
        EVrRenderPipeline family,
        ERvcPipelineMode rvcMode,
        string? scriptPath)
    {
        RenderPipeline pipeline = Create(stereo, family, rvcMode);
        AttachScriptOverlays(pipeline, scriptPath);
        return pipeline;
    }

    /// <summary>
    /// Attaches the overlays and settings of the <c>.xrs</c> script at <paramref name="scriptPath"/> to an eye pipeline
    /// that <see cref="Create"/> made. The script must derive from the same pipeline type. Its base arguments are
    /// ignored, because the eye topology selects them. An empty path does nothing.
    /// </summary>
    /// <exception cref="NotSupportedException">The script does not load, does not apply, or derives from a different type.</exception>
    public static void AttachScriptOverlays(RenderPipeline pipeline, string? scriptPath)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (string.IsNullOrWhiteSpace(scriptPath))
            return;

        string fullPath = Path.GetFullPath(scriptPath);
        if (!File.Exists(fullPath))
            throw new NotSupportedException($"VR.RenderPipelineScript '{scriptPath}' does not exist. No other pipeline is substituted.");

        RenderPipelineScriptLoadResult loaded = RenderPipelineScriptLoader.LoadFile(fullPath);
        if (!loaded.Succeeded || loaded.Pipeline is null || loaded.Problems.Any(static p => p.IsError))
        {
            string problems = string.Join("; ", loaded.Problems.Where(static p => p.IsError).Select(static p => p.ToString()));
            throw new NotSupportedException($"VR.RenderPipelineScript '{scriptPath}' has errors: {problems}. No other pipeline is substituted.");
        }

        if (loaded.Pipeline.GetType() != pipeline.GetType())
        {
            throw new NotSupportedException(
                $"VR.RenderPipelineScript '{scriptPath}' derives from {loaded.Pipeline.GetType().Name}, but VR.RenderPipeline creates {pipeline.GetType().Name}. " +
                "Select a script that derives from the selected family. No other pipeline is substituted.");
        }

        pipeline.Overlays = [.. loaded.Pipeline.Overlays];
        IReadOnlyList<string> settingErrors = pipeline.ApplyOverlaySettings();
        if (settingErrors.Count > 0)
            throw new NotSupportedException($"VR.RenderPipelineScript '{scriptPath}' has settings that do not apply: {string.Join("; ", settingErrors)}.");
        if (pipeline.OverlayReport.HasErrors)
            throw new NotSupportedException($"VR.RenderPipelineScript '{scriptPath}' has patches that do not apply to the eye pipeline. No other pipeline is substituted.");

        pipeline.OverlaySourcePath = fullPath;
    }

    /// <summary>
    /// Returns true when <see cref="Create"/> can make a pipeline for this
    /// topology and family.
    /// </summary>
    public static bool CanCreate(bool stereo, EVrRenderPipeline family)
        => Enum.IsDefined(family) && !(stereo && family == EVrRenderPipeline.Advanced);

    /// <summary>
    /// Returns true when <paramref name="pipeline"/> is exactly the pipeline
    /// that <see cref="Create"/> makes for the same arguments, with the overlays
    /// of <paramref name="scriptPath"/> when it is not empty.
    /// </summary>
    public static bool Matches(
        [NotNullWhen(true)] RenderPipeline? pipeline,
        bool stereo,
        EVrRenderPipeline family,
        ERvcPipelineMode rvcMode,
        string? scriptPath = null)
        => MatchesFamily(pipeline, stereo, family, rvcMode) && MatchesScript(pipeline, scriptPath);

    private static bool MatchesScript(RenderPipeline pipeline, string? scriptPath)
        => string.IsNullOrWhiteSpace(scriptPath)
            ? pipeline.OverlaySourcePath is null
            : string.Equals(pipeline.OverlaySourcePath, Path.GetFullPath(scriptPath), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesFamily(
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
