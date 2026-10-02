using XREngine.Components;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Editor.Publishing;

/// <summary>Admits authored output, camera, and pass selections before browser content is activated.</summary>
internal sealed class BrowserRenderingCapabilityAudit(IShaderProgramArtifactResolver? resolver)
{
    private bool _hasCamera;

    internal static void InspectStartup(GameStartupSettings settings)
    {
        if (settings.StartupWindows.Count > 1)
            throw Unsupported("startup.asset", "canvas-output", "Only one browser canvas output is installed.");
        for (int i = 0; i < settings.StartupWindows.Count; i++)
        {
            GameWindowStartupSettings output = settings.StartupWindows[i];
            string path = $"startup.asset/StartupWindows[{i}]";
            if (output.Width <= 0 || output.Height <= 0)
                throw Unsupported(path, "canvas-output", "Canvas startup dimensions must be positive.");
            if (output.WindowState != EWindowState.Windowed)
                throw Unsupported(path, "canvas-output", "Fullscreen and window-state requests require a page gesture and are not installed at startup.");
            if (output.TransparentFramebuffer || output.OutputHDR == true)
                throw Unsupported(path, "canvas-output", "The canvas profile requires opaque SDR presentation.");
            if (output.LocalPlayers != ELocalPlayerIndexMask.One)
                throw Unsupported(path, "canvas-output", "Split-screen local player outputs are not installed.");
        }

        // Resolve only packaged project/user selections. Desktop editor preferences and
        // desktop engine defaults are not authored requirements of a canvas output.
        UserSettings user = settings.DefaultUserSettings;
        bool gpuDispatch = user.GPURenderDispatchOverride.HasOverride
            ? user.GPURenderDispatchOverride.Value : settings.GPURenderDispatch;
        if (gpuDispatch)
            throw Unsupported(user.GPURenderDispatchOverride.HasOverride
                ? "startup.asset/DefaultUserSettings/GPURenderDispatchOverride" : "startup.asset/GPURenderDispatch",
                "mesh-submission", "GPU-driven render dispatch has no installed WebGPU route; the browser scene output requires CpuDirect submission.");
        EAntiAliasingMode? aa = user.AntiAliasingModeOverride.HasOverride
            ? user.AntiAliasingModeOverride.Value
            : settings.AntiAliasingModeOverride.HasOverride ? settings.AntiAliasingModeOverride.Value : null;
        if (aa is not null and not EAntiAliasingMode.None)
            throw Unsupported(user.AntiAliasingModeOverride.HasOverride
                ? "startup.asset/DefaultUserSettings/AntiAliasingModeOverride" : "startup.asset/AntiAliasingModeOverride",
                "anti-aliasing", $"Explicit '{aa}' is unsupported; the canvas output requires AA None and one sample.");
        if (settings.DepthModeOverride is { HasOverride: true, Value: not XRCamera.EDepthMode.Normal })
            throw Unsupported("startup.asset/DepthModeOverride", "camera-depth", "The cooked coordinate contract has not admitted reversed-Z cameras.");
        EGlobalIlluminationMode gi = user.GlobalIlluminationModeOverride.HasOverride
            ? user.GlobalIlluminationModeOverride.Value
            : settings.GlobalIlluminationModeOverride.HasOverride
                ? settings.GlobalIlluminationModeOverride.Value : user.GlobalIlluminationMode;
        if (gi is not (EGlobalIlluminationMode.None or EGlobalIlluminationMode.LightProbesAndIbl))
            throw Unsupported("startup.asset/global-illumination", "global-illumination", $"Selected '{gi}' has no cooked WebGPU route.");
    }

    internal void Inspect(CameraComponent component, string path)
    {
        _hasCamera = true;
        component.TryGetCreatedCamera(out XRCamera? camera);
        if (component.DefaultRenderTarget is not null)
            throw Unsupported(path, "camera-output", "Offscreen camera targets have no installed browser output route.");
        if (component.UserInterface is { IsScreenSpace: false })
            throw Unsupported(path, "screen-ui", "The camera output admits screen-space UI only.");
        if (camera?.PostProcessMaterial is { } postprocess)
            throw Unsupported(path, "postprocess-material", $"Authored postprocess material '{postprocess.Name}' has no installed browser output route.");
        if (camera?.AntiAliasingModeOverride is { } cameraAa && cameraAa != EAntiAliasingMode.None)
            throw Unsupported(path, "anti-aliasing", $"Camera selects '{cameraAa}'; the canvas output requires AA None.");
        if (camera?.OutputHDROverride == true)
            throw Unsupported(path, "canvas-output", "Camera HDR presentation has no installed browser output route.");
        if (camera is { DepthMode: not XRCamera.EDepthMode.Normal })
            throw Unsupported(path, "camera-depth", "The cooked coordinate contract has not admitted reversed-Z cameras.");
        if ((camera?.Parameters ?? component.CameraParameters) is XROVRCameraParameters or XROpenXRFovCameraParameters)
            throw Unsupported(path, "camera-output", "XR eye projections require a browser XR service that is not enabled.");

        RenderPipeline? assigned = null;
        camera?.TryGetAssignedRenderPipeline(out assigned);
        if (assigned is not null && assigned.GetType() != typeof(DefaultRenderPipeline))
            throw Unsupported(path, "render-pipeline", $"Authored pipeline '{assigned.GetType().FullName}' has no installed WebGPU scene route.");

        if (assigned is DefaultRenderPipeline { Stereo: true })
            throw Unsupported(path, "camera-output", "The canvas pipeline admits a mono scene presentation.");
        if (assigned is DefaultRenderPipeline pipeline && pipeline.GetWebPipelineFeatureRejection() is { } pipelineReason)
            throw Unsupported(path, "render-pipeline", pipelineReason);

        PipelinePostProcessState? authored = null;
        if (assigned is not null)
            camera!.PostProcessStates.TryGetState(assigned.ID, out authored);
        PipelinePostProcessState state = DefaultRenderPipeline.CreateWebPostProcessAdmissionState(authored);
        InspectState(state, path);
    }

    internal void Complete(string worldPath)
    {
        // Game code may supply the first camera during activation. Its missing
        // settings still select the browser schema's enabled default effects.
        if (!_hasCamera)
            InspectState(DefaultRenderPipeline.CreateWebPostProcessAdmissionState(null), worldPath);
    }

    private void InspectState(PipelinePostProcessState state, string path)
    {
        if (DefaultRenderPipeline.GetWebPostProcessRejection(state, out string pass) is { } effectReason)
            throw Unsupported(path, pass, effectReason);
        AmbientOcclusionSettings? ao = GetSettings<AmbientOcclusionSettings>(state);
        if (DefaultRenderPipeline.GetWebAmbientOcclusionRejection(ao) is { } aoReason)
            throw Unsupported(path, CommonPostProcessStages.AmbientOcclusionStageKey, aoReason);
        RequirePipelineArtifact("tonemap", path);
        if (ao is { Enabled: true })
        {
            RequirePipelineArtifact("depth-normal", path);
            RequirePipelineArtifact("gtao-generate", path);
            RequirePipelineArtifact("gtao-blur-horizontal", path);
            RequirePipelineArtifact("gtao-blur-vertical", path);
        }
        if (GetSettings<BloomSettings>(state) is { Enabled: true })
        {
            RequirePipelineArtifact("bloom-copy", path);
            RequirePipelineArtifact("bloom-downsample", path);
            RequirePipelineArtifact("bloom-upsample", path);
            RequirePipelineArtifact("bloom-combine", path);
        }
    }

    internal static void InspectMaterial(XRMaterial material, string path, string? meshName)
    {
        InspectPass(material.RenderPass, material.RenderOptions, "base");
        foreach (MaterialPassDefinition pass in material.PassSet.Passes)
            if (pass.Enabled)
                InspectPass(pass.RenderPass, pass.RenderOptions, pass.SourcePassName ?? pass.Identity.ToString());

        void InspectPass(int pass, XREngine.Rendering.Models.Materials.RenderingParameters options, string source)
        {
            if (!DefaultRenderPipeline.IsWebSceneMeshPassSupported(pass))
                throw Unsupported(path, ((EDefaultRenderPass)pass).ToString(),
                    $"Mesh '{meshName}' material '{material.Name}', source pass '{source}' has no cooked scene output route; display debug callbacks use a separate contract.");
            if (DefaultRenderPipeline.GetWebRasterStateRejection(options) is { } reason)
                throw Unsupported(path, ((EDefaultRenderPass)pass).ToString(),
                    $"Mesh '{meshName}' material '{material.Name}', source pass '{source}': {reason}");
        }
    }

    private void RequirePipelineArtifact(string pass, string path)
    {
        if (resolver is not BrowserShaderArtifactSource source || !source.PipelineCatalog.TryResolve(pass, out _))
            throw Unsupported(path, pass, "The selected camera pass requires its exact cooked artifact in BrowserShaderArtifactManifestPath.");
    }

    private static T? GetSettings<T>(PipelinePostProcessState state) where T : class
        => state.GetStage<T>()?.TryGetBacking(out T? settings) == true ? settings : null;

    private static NotSupportedException Unsupported(string path, string pass, string reason)
        => new($"BrowserCook.RenderingUnsupported: '{path}', pass '{pass}': {reason}");
}
