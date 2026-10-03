using XREngine.Components;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Editor.Publishing;

/// <summary>Admits authored output, camera, and pass selections before browser content is activated.</summary>
internal sealed class BrowserRenderingCapabilityAudit(IShaderProgramArtifactResolver? resolver,
    RenderPipelineResourceProfile? outputProfile = null, IReadOnlySet<int>? inheritedScenePasses = null,
    IReadOnlyList<RenderPipelineRequirements>? inheritedPipelineRequirements = null,
    BrowserNativeSceneCapabilityAudit? nativeAdmission = null)
{
    private readonly RenderPipelineResourceProfile _outputProfile = outputProfile ?? RenderPipelineResourceProfile.Empty;
    private bool _hasCamera;
    private readonly List<(int Pass, string ScenePath, string Path, string Material, string? Mesh, string Source)> _sceneMaterialPasses = [];
    private readonly BrowserNativeSceneCapabilityAudit _nativeAdmission = nativeAdmission ?? new();
    internal List<RenderPipelineRequirements> PipelineRequirements { get; } = [];

    internal static void InspectStartup(GameStartupSettings settings, BrowserCapabilityReport? report = null)
    {
        if (settings.StartupWindows.Count > 1)
            Reject("startup.asset", "canvas-output", "Only one browser canvas output is installed.");
        for (int i = 0; i < settings.StartupWindows.Count; i++)
        {
            GameWindowStartupSettings output = settings.StartupWindows[i];
            string path = $"startup.asset/StartupWindows[{i}]";
            if (output.Width <= 0 || output.Height <= 0)
                Reject(path, "canvas-output", "Canvas startup dimensions must be positive.");
            if (output.WindowState != EWindowState.Windowed)
                Reject(path, "canvas-output", "Fullscreen and window-state requests require a page gesture and are not installed at startup.");
            if (output.TransparentFramebuffer || output.OutputHDR == true)
                Reject(path, "canvas-output", "The canvas profile requires opaque SDR presentation.");
            if (output.LocalPlayers != ELocalPlayerIndexMask.One)
                Reject(path, "canvas-output", "Split-screen local player outputs are not installed.");
        }

        // Resolve only packaged project/user selections. Desktop editor preferences and
        // desktop engine defaults are not authored requirements of a canvas output.
        UserSettings user = settings.DefaultUserSettings;
        if (BrowserRenderPipelineOutputProfile.GetVendorOperationRejection(settings) is { } vendorReason)
            Reject("startup.asset/vendor-reconstruction", "vendor-reconstruction", vendorReason);
        if (settings.DepthModeOverride is { HasOverride: true, Value: not XRCamera.EDepthMode.Normal })
            Reject("startup.asset/DepthModeOverride", "camera-depth", "The cooked coordinate contract has not admitted reversed-Z cameras.");
        EGlobalIlluminationMode gi = user.GlobalIlluminationModeOverride.HasOverride
            ? user.GlobalIlluminationModeOverride.Value
            : settings.GlobalIlluminationModeOverride.HasOverride
                ? settings.GlobalIlluminationModeOverride.Value : user.GlobalIlluminationMode;
        if (gi is not (EGlobalIlluminationMode.None or EGlobalIlluminationMode.LightProbesAndIbl))
            Reject("startup.asset/global-illumination", "global-illumination", $"Selected '{gi}' has no cooked WebGPU route.");

        void Reject(string path, string pass, string reason)
        {
            if (report is null)
                throw Unsupported(path, pass, reason);
            report.Inspect(() => throw Unsupported(path, pass, reason), "startup.asset", path, pass: pass);
        }
    }

    internal void Inspect(CameraComponent component, string path, BrowserCapabilityReport? report = null,
        string? scenePath = null)
    {
        _hasCamera = true;
        component.TryGetCreatedCamera(out XRCamera? camera);
        if (component.DefaultRenderTarget is not null)
            Reject("camera-output", "Offscreen camera targets have no installed browser output route.");
        if (component.UserInterface is { IsScreenSpace: false })
            Reject("screen-ui", "The camera output admits screen-space UI only.");
        if (camera?.PostProcessMaterial is { } postprocess)
            Reject("postprocess-material", $"Authored postprocess material '{postprocess.Name}' has no installed browser output route.");
        if (component.OutputHDROverride == true)
            Reject("canvas-output", "Camera HDR presentation has no installed browser output route.");
        if (camera is { DepthMode: not XRCamera.EDepthMode.Normal })
            Reject("camera-depth", "The cooked coordinate contract has not admitted reversed-Z cameras.");
        if ((camera?.Parameters ?? component.CameraParameters) is XROVRCameraParameters or XROpenXRFovCameraParameters)
            Reject("camera-output", "XR eye projections require a browser XR service that is not enabled.");

        RenderPipeline? assigned = component.RenderPipelineSource;
        PipelinePostProcessState? authored = null;
        if (assigned is not null)
            component.PostProcessStates.TryGetState(assigned.ID, out authored);
        else
            authored = component.PostProcessStates.DefaultState;
        RenderPipelineResourceProfile profile = _outputProfile with
        {
            AntiAliasingMode = component.AntiAliasingModeOverride ?? _outputProfile.AntiAliasingMode,
            MsaaSampleCount = component.MsaaSampleCountOverride ?? _outputProfile.MsaaSampleCount,
            OutputHDR = component.OutputHDROverride ?? _outputProfile.OutputHDR,
        };
        InspectPipeline(assigned, authored, profile, path, report, scenePath);

        void Reject(string pass, string reason)
        {
            if (report is null)
                throw Unsupported(path, pass, reason);
            report.Inspect(() => throw Unsupported(path, pass, reason), scenePath ?? string.Empty,
                path, component.GetType().FullName, pass: pass);
        }
    }

    internal void Complete(string worldPath, BrowserCapabilityReport? report = null)
    {
        // Only a genuinely unassigned output uses the host's default recipe.
        if (!_hasCamera && inheritedScenePasses is null)
            InspectPipeline(null, null, _outputProfile, worldPath, report, worldPath);
        foreach (var material in _sceneMaterialPasses)
            if (inheritedScenePasses?.Contains(material.Pass) != true &&
                !PipelineRequirements.Any(requirements => requirements.ScenePasses.Contains(material.Pass)))
            {
                string pass = material.Pass.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string reason = $"Mesh '{material.Mesh}' material '{material.Material}', source pass '{material.Source}' has no scene-mesh route declared by any published camera pipeline.";
                if (report is null)
                    throw Unsupported(material.Path, pass, reason);
                report.Inspect(() => throw Unsupported(material.Path, pass, reason), material.ScenePath,
                    material.Path, material: material.Material, pass: pass);
            }
    }

    internal void InspectGeometry(XRMesh? mesh, XRMaterial material, string scenePath, string path, string? meshName,
        CancellationToken cancellationToken)
        => _nativeAdmission.InspectGeometry(mesh, material, scenePath, path, meshName, cancellationToken);

    internal void InspectNativeScenes(BrowserCapabilityReport? report, CancellationToken cancellationToken)
    {
        _nativeAdmission.IncludeRequirements(PipelineRequirements);
        if (inheritedPipelineRequirements is not null)
            _nativeAdmission.IncludeRequirements(inheritedPipelineRequirements);
        if (nativeAdmission is null)
            _nativeAdmission.Complete(report, cancellationToken);
    }

    internal void CopyScenePassesTo(ISet<int> destination)
    {
        foreach (RenderPipelineRequirements requirements in PipelineRequirements)
            destination.UnionWith(requirements.ScenePasses);
    }

    private void InspectPipeline(RenderPipeline? pipeline, PipelinePostProcessState? authored,
        in RenderPipelineResourceProfile outputProfile, string path, BrowserCapabilityReport? report,
        string? scenePath)
    {
        RenderPipelineRequirements requirements = pipeline?.CreateRequirements(RendererBackendId.WebGPU, outputProfile, authored)
            ?? DefaultRenderPipeline.CreateWebDefaultRequirements(outputProfile, authored);
        foreach (string diagnostic in requirements.Diagnostics)
            Reject("pipeline-requirement", diagnostic);
        if (WebGpuPipelineAdmission.GetOutputProfileRejection(requirements) is { } outputReason)
            Reject("output-profile", outputReason);
        foreach (string operation in requirements.Operations)
            if (WebGpuPipelineAdmission.GetOperationRejection(operation) is { } reason)
                Reject(operation, reason);
        foreach ((string pass, string? identity) in requirements.Programs)
        {
            if (report is null)
                RequirePipelineArtifact(pass, identity, requirements.RasterPrograms.Contains(pass),
                    requirements.ComputePrograms.Contains(pass), path);
            else
                report.Inspect(() => RequirePipelineArtifact(pass, identity, requirements.RasterPrograms.Contains(pass),
                    requirements.ComputePrograms.Contains(pass), path), scenePath ?? string.Empty, path, pass: pass);
        }
        PipelineRequirements.Add(requirements);

        void Reject(string pass, string reason)
        {
            if (report is null)
                throw Unsupported(path, pass, reason);
            report.Inspect(() => throw Unsupported(path, pass, reason), scenePath ?? string.Empty, path, pass: pass);
        }
    }

    internal void InspectMaterial(XRMaterial material, string path, string? meshName, bool sceneRoute = true,
        BrowserCapabilityReport? report = null, string? scenePath = null)
    {
        InspectPass(material.RenderPass, material.RenderOptions, "base");
        foreach (MaterialPassDefinition pass in material.PassSet.Passes)
            if (pass.Enabled)
                InspectPass(pass.RenderPass, pass.RenderOptions, pass.SourcePassName ?? pass.Identity.ToString());

        void InspectPass(int pass, XREngine.Rendering.Models.Materials.RenderingParameters options, string source)
        {
            if (sceneRoute)
                _sceneMaterialPasses.Add((pass, scenePath ?? string.Empty, path, material.Name ?? string.Empty,
                    meshName, source));
            if (WebGpuPipelineAdmission.GetRasterStateRejection(options) is { } reason)
            {
                string passName = pass.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string detail = $"Mesh '{meshName}' material '{material.Name}', source pass '{source}': {reason}";
                if (report is null)
                    throw Unsupported(path, passName, detail);
                report.Inspect(() => throw Unsupported(path, passName, detail), scenePath ?? string.Empty,
                    path, material: material.Name, pass: passName);
            }
        }
    }

    private void RequirePipelineArtifact(string pass, string? identity, bool requiresRaster, bool requiresCompute, string path)
    {
        if (resolver is not BrowserShaderArtifactSource source || !source.PipelineCatalog.TryResolve(pass, out var artifact) ||
            identity is not null && artifact.Identity != identity)
            throw Unsupported(path, pass, "The selected camera pass requires its exact cooked artifact in BrowserShaderArtifactManifestPath.");
        if (requiresRaster && !WebPipelineArtifactCatalog.IsCompleteRasterProgram(artifact))
            throw Unsupported(path, pass, "The selected camera pass requires a complete cooked WebGPU raster program.");
        if (requiresCompute && !WebPipelineArtifactCatalog.IsCompleteComputeProgram(artifact))
            throw Unsupported(path, pass, "The selected camera pass requires a complete cooked WebGPU compute program.");
    }

    private static NotSupportedException Unsupported(string path, string pass, string reason)
        => new($"BrowserCook.RenderingUnsupported: '{path}', pass '{pass}': {reason}");
}
