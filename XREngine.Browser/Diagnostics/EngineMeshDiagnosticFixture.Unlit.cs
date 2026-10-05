using System.Numerics;
using System.Text.Json;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
{
    private readonly List<XRMaterial> _unlitMaterials = [];
    private readonly List<XRTexture> _unlitTextures = [];
    private readonly EngineUnlitDiagnosticProfile _unlitProfile;
    private readonly List<XRMesh> _unlitWitnessMeshes = [];
    private XRMesh? _unlitBackgroundMesh;
    private VisualScene3D? _unlitVisual;
    private int _unlitCase;
    private string[]? _unlitArtifactIdentities;

    // Each entry describes authored inputs and the center of one fixed scene tile.
    // Pixel expectations are calculated independently by the browser qualification.
    private static readonly string[] UnlitCaseMetadata =
    [
        """{"semantic":"UnlitColorV1","semanticVersion":1,"factory":"CreateUnlitColorMaterialForward","coverage":"opaque","blend":"disabled","sampleU":0.175,"sampleV":0.175,"authoredColor":[1.5,0.25,0.5,0.75]}""",
        """{"semantic":"UnlitTextureV2","semanticVersion":2,"factory":"CreateUnlitTextureMaterialForward","coverage":"opaque","blend":"disabled","sampleU":0.5,"sampleV":0.175,"textureFormat":"Rgba8","textureColorSpace":"linear","authoredRgbaBytes":[64,128,192,96]}""",
        """{"semantic":"UnlitTextureV2","semanticVersion":2,"factory":"CreateUnlitTextureMaterialForward","coverage":"opaque","blend":"disabled","sampleU":0.825,"sampleV":0.175,"textureFormat":"Srgb8Alpha8","textureColorSpace":"srgb","authoredRgbaBytes":[64,128,192,96]}""",
        """{"semantic":"UnlitOpaqueTextureV3","semanticVersion":3,"factory":"CreateUnlitOpaqueTextureMaterialForward","coverage":"opaque","blend":"disabled","sampleU":0.175,"sampleV":0.5,"textureFormat":"Rgba8","textureColorSpace":"linear","authoredRgbaBytes":[64,128,192,96]}""",
        """{"semantic":"UnlitAlphaTextureV4","semanticVersion":4,"factory":"CreateUnlitAlphaTextureMaterialForward","coverage":"masked","blend":"disabled","sampleU":0.5,"sampleV":0.5,"textureFormat":"Rgba8","textureColorSpace":"linear","authoredRgbaBytes":[200,40,80,127],"alphaCutoffNumerator":128,"alphaCutoffDenominator":255,"backgroundColor":[0.125,0.25,0.375,1]}""",
        """{"semantic":"UnlitAlphaTextureV4","semanticVersion":4,"factory":"CreateUnlitAlphaTextureMaterialForward","coverage":"masked","blend":"disabled","sampleU":0.825,"sampleV":0.5,"textureFormat":"Rgba8","textureColorSpace":"linear","authoredRgbaBytes":[200,40,80,128],"alphaCutoffNumerator":128,"alphaCutoffDenominator":255,"backgroundColor":[0.125,0.25,0.375,1]}""",
        """{"semantic":"UnlitAlphaTextureV4","semanticVersion":4,"factory":"CreateUnlitAlphaTextureMaterialForward","coverage":"masked","blend":"disabled","sampleU":0.175,"sampleV":0.825,"textureFormat":"Rgba8","textureColorSpace":"linear","authoredRgbaBytes":[200,40,80,129],"alphaCutoffNumerator":128,"alphaCutoffDenominator":255,"backgroundColor":[0.125,0.25,0.375,1]}""",
        """{"semantic":"UnlitTextureArraySliceV5","semanticVersion":5,"factory":"CreateUnlitTextureArraySliceMaterialForward","coverage":"opaque","blend":"disabled","sampleU":0.5,"sampleV":0.825,"textureFormat":"Rgba8","textureColorSpace":"linear","sampledLayer":0,"arrayLayerBytes":[[16,160,96,224],[200,32,240,80]]}""",
        """{"semantic":"UnlitTextureArraySliceV5","semanticVersion":5,"factory":"CreateUnlitTextureArraySliceMaterialForward","coverage":"opaque","blend":"disabled","sampleU":0.825,"sampleV":0.825,"textureFormat":"Rgba8","textureColorSpace":"linear","sampledLayer":0,"arrayLayerBytes":[[200,32,240,80],[16,160,96,224]]}""",
    ];

    private void ConfigureUnlitMaterial(XRMaterial material, string name)
    {
        material.Name = name;
        material.RenderOptions.CullMode = ECullMode.None;
        material.RenderOptions.ExcludeFromCpuOcclusion = true;
    }

    private static XRTexture2D CreateUnlitImage(string name, byte r, byte g, byte b, byte a, bool srgb = false)
    {
        ReadOnlySpan<byte> pixels = [r, g, b, a, r, g, b, a, r, g, b, a, r, g, b, a];
        return new XRTexture2D(2, 2, pixels)
        {
            Name = name, SizedInternalFormat = srgb ? ESizedInternalFormat.Srgb8Alpha8 : ESizedInternalFormat.Rgba8,
            AutoGenerateMipmaps = false, MinFilter = ETexMinFilter.Nearest, MagFilter = ETexMagFilter.Nearest,
            UWrap = ETexWrapMode.ClampToEdge, VWrap = ETexWrapMode.ClampToEdge,
            MaxAnisotropy = 1, MinLOD = 0, MaxLOD = 0,
        };
    }

    private XRTexture2D OwnImage(string name, byte r, byte g, byte b, byte a, bool srgb = false)
    {
        XRTexture2D image = CreateUnlitImage(name, r, g, b, a, srgb);
        _unlitTextures.Add(image);
        return image;
    }

    private XRTexture2DArray OwnArray(string name, XRTexture2D first, XRTexture2D second)
    {
        XRTexture2DArray array = new(first, second)
        {
            Name = name, SizedInternalFormat = ESizedInternalFormat.Rgba8,
            MinFilter = ETexMinFilter.Nearest, MagFilter = ETexMagFilter.Nearest,
            UWrap = ETexWrapMode.ClampToEdge, VWrap = ETexWrapMode.ClampToEdge,
        };
        _unlitTextures.Add(array);
        return array;
    }

    private void InitializeUnlitScene()
    {
        _unlitBackgroundMesh = CreateUnlitBackgroundMesh();
        XRMaterial background = XRMaterial.CreateUnlitColorMaterialForward(new ColorF4(0.125f, 0.25f, 0.375f, 1));
        AddUnlitModel("Unlit discard background", _unlitBackgroundMesh, background, new Vector3(0, 0, -3));

        AddUnlitModel("Unlit HDR color", _mesh, _material, Tile(0));
        AddUnlitModel("Unlit linear RGBA", _mesh,
            XRMaterial.CreateUnlitTextureMaterialForward(OwnImage("Unlit linear RGBA", 64, 128, 192, 96)), Tile(1));
        AddUnlitModel("Unlit sRGB RGBA", _mesh,
            XRMaterial.CreateUnlitTextureMaterialForward(OwnImage("Unlit sRGB RGBA", 64, 128, 192, 96, srgb: true)), Tile(2));
        AddUnlitModel("Unlit forced opaque", _mesh,
            XRMaterial.CreateUnlitOpaqueTextureMaterialForward(OwnImage("Unlit opaque source", 64, 128, 192, 96)), Tile(3));
        for (int i = 0; i < 3; i++)
        {
            XRMaterial masked = XRMaterial.CreateUnlitAlphaTextureMaterialForward(
                OwnImage($"Unlit cutoff {i}", 200, 40, 80, checked((byte)(127 + i))));
            masked.AlphaCutoff = 128f / 255f;
            masked.TransparencyMode = ETransparencyMode.Masked;
            AddUnlitModel($"Unlit cutoff {i}", _mesh, masked, Tile(i + 4));
        }
        XRTexture2D first = OwnImage("Unlit array teal", 16, 160, 96, 224);
        XRTexture2D second = OwnImage("Unlit array magenta", 200, 32, 240, 80);
        AddUnlitModel("Unlit array teal first", _mesh,
            XRMaterial.CreateUnlitTextureArraySliceMaterialForward(OwnArray("Unlit teal then magenta", first, second)), Tile(7));
        XRTexture2D reversedFirst = OwnImage("Unlit array magenta first", 200, 32, 240, 80);
        XRTexture2D reversedSecond = OwnImage("Unlit array teal second", 16, 160, 96, 224);
        AddUnlitModel("Unlit array magenta first", _mesh,
            XRMaterial.CreateUnlitTextureArraySliceMaterialForward(OwnArray("Unlit magenta then teal", reversedFirst, reversedSecond)), Tile(8));

        if (_unlitMaterials.Count != UnlitCaseMetadata.Length + 1)
            throw new InvalidOperationException("EngineMeshDiagnostic.UnlitCaseCountMismatch.");
        if (_unlitProfile.SampleCount > 1)
            InitializeUnlitWitness();
        foreach (XRMaterial material in _unlitMaterials)
            if (!EngineUnlitSurfaceBinding.TryRead(material, out _, out string? reason))
                throw new InvalidOperationException($"EngineMeshDiagnostic.UnlitSurfaceInvalid: {material.Name}: {reason}");
    }

    private void AddUnlitModel(string name, XRMesh mesh, XRMaterial material, Vector3 translation)
    {
        ConfigureUnlitMaterial(material, name);
        _unlitMaterials.Add(material);
        SceneNode node = new(name);
        Transform transform = node.SetTransform<Transform>();
        transform.Translation = translation;
        _scene.RootNodes.Add(node);
        ModelComponent component = node.AddComponent<ModelComponent>()
            ?? throw new InvalidOperationException("EngineMeshDiagnostic.UnlitComponentMissing.");
        component.Model = new Model(new SubMesh(mesh, material));
        _renderWorld.AddWorldObject(component);
    }

    private static Vector3 Tile(int index)
        => new((index % 3) switch { 0 => -0.65f, 1 => 0f, _ => 0.65f },
            (index / 3) switch { 0 => 0.65f, 1 => 0f, _ => -0.65f }, -2);

    private static XRMesh CreateUnlitTileMesh()
        => new(
            [new Vertex(new Vector3(-0.22f, -0.22f, 0), Vector3.UnitZ, new Vector2(0, 1)),
             new Vertex(new Vector3(0.22f, -0.22f, 0), Vector3.UnitZ, new Vector2(1, 1)),
             new Vertex(new Vector3(0.22f, 0.22f, 0), Vector3.UnitZ, new Vector2(1, 0)),
             new Vertex(new Vector3(-0.22f, 0.22f, 0), Vector3.UnitZ, new Vector2(0, 0))],
            new List<ushort> { 0, 1, 2, 0, 2, 3 });

    private static XRMesh CreateUnlitBackgroundMesh()
        => new(
            [new Vertex(new Vector3(-1, -1, 0), Vector3.UnitZ, new Vector2(0, 1)),
             new Vertex(new Vector3(1, -1, 0), Vector3.UnitZ, new Vector2(1, 1)),
             new Vertex(new Vector3(1, 1, 0), Vector3.UnitZ, new Vector2(1, 0)),
             new Vertex(new Vector3(-1, 1, 0), Vector3.UnitZ, new Vector2(0, 0))],
            new List<ushort> { 0, 1, 2, 0, 2, 3 });

    private void ConfigureUnlitCamera(XRCamera camera)
    {
        ConfigureLitCamera(camera, _unlitProfile.SubmissionStrategy);
        camera.AntiAliasingModeOverride = _unlitProfile.SampleCount > 1 ? EAntiAliasingMode.Msaa : EAntiAliasingMode.None;
        camera.MsaaSampleCountOverride = _unlitProfile.SampleCount;
        PipelinePostProcessState state = camera.GetPostProcessState(_pipeline)
            ?? throw new InvalidOperationException("EngineMeshDiagnostic.PostProcessStateMissing.");
        AmbientOcclusionSettings ao = RequireSettings<AmbientOcclusionSettings>(state);
        ao.Enabled = _unlitProfile.AmbientOcclusion;
        ao.Type = AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion;
        ao.GroundTruth.Resolution = GroundTruthAmbientOcclusionSettings.EResolution.Full;
    }

    public void SetUnlitCase(int sampleCase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_unlit) throw new InvalidOperationException("EngineMeshDiagnostic.UnlitCaseRequired.");
        if ((uint)sampleCase >= (uint)UnlitCaseMetadata.Length)
            throw new ArgumentOutOfRangeException(nameof(sampleCase));
        _unlitCase = sampleCase;
    }

    public string GetUnlitState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_unlit || _unlitArtifactIdentities is null)
            throw new InvalidOperationException("EngineMeshDiagnostic.UnlitCaseRequired.");
        int index = _unlitCase;
        XRMaterial material = _unlitMaterials[index + 1];
        if (!EngineUnlitSurfaceBinding.TryRead(material, out EngineUnlitSurface surface, out string? reason))
            throw new InvalidOperationException($"EngineMeshDiagnostic.UnlitSurfaceInvalid: {reason}");
        if (surface.Semantic != CaseSemantic(index))
            throw new InvalidOperationException("EngineMeshDiagnostic.UnlitSemanticChanged.");
        XRRenderPipelineInstance instance = _viewport.RenderPipelineInstance;
        XRCamera camera = _viewport.Camera ?? throw new InvalidOperationException("EngineMeshDiagnostic.CameraMissing.");
        PipelinePostProcessState state = camera.GetPostProcessState(_pipeline)
            ?? throw new InvalidOperationException("EngineMeshDiagnostic.PostProcessStateMissing.");
        AmbientOcclusionSettings ao = RequireSettings<AmbientOcclusionSettings>(state);
        ResourceGenerationKey? committed = instance.ActiveGeneration?.Key;
        string observed = JsonSerializer.Serialize(new
        {
            sampleCase = index,
            executionProfile = _unlitProfile.Name,
            pipeline = _pipeline.GetType().Name,
            submission = RuntimeEngine.Rendering.ResolveRequestedMeshSubmissionStrategy().ToString(),
            indexedSubmission = _unlitProfile.SubmissionStrategy == EMeshSubmissionStrategy.GpuIndirectZeroReadback
                ? new
                {
                    strategy = _renderer.LastAuthoredIndexedSubmissionStrategy.ToString(),
                    reason = _renderer.LastAuthoredIndexedSubmissionReason,
                    conservativeUnboundedDraws = _renderer.LastAuthoredIndexedConservativeUnboundedDraws,
                } : null,
            target = DefaultRenderPipeline.HDRSceneTextureName,
            artifactIdentity = _unlitArtifactIdentities[index],
            source = new
            {
                id = _pipeline.ID,
                type = _pipeline.GetType().Name,
                cameraOwnsSource = ReferenceEquals(camera.RenderPipeline, _pipeline),
                instanceOwnsSource = ReferenceEquals(instance.Pipeline, _pipeline),
                committedOwnsSource = ReferenceEquals(instance.ActiveGeneration?.OwnerPipeline, _pipeline),
            },
            camera = new
            {
                antiAliasing = camera.AntiAliasingModeOverride?.ToString(),
                sampleCount = camera.MsaaSampleCountOverride,
                reversedDepth = camera.IsReversedDepth,
                aoEnabled = ao.Enabled,
                aoType = ao.Type.ToString(),
                aoResolution = ao.GroundTruth.Resolution.ToString(),
                aoQualityEnabled = RuntimeEngine.Rendering.Settings.BrowserWebGpuQuality.EnableGtao,
            },
            committed = committed is { } key ? new
            {
                pipeline = key.PipelineName,
                antiAliasing = key.AntiAliasingMode.ToString(),
                sampleCount = key.MsaaSampleCount,
                width = key.InternalWidth,
                height = key.InternalHeight,
                displayWidth = key.DisplayWidth,
                displayHeight = key.DisplayHeight,
                featureMask = key.FeatureMask,
                outputHdr = key.OutputHDR,
                stereo = key.Stereo,
            } : null,
            resourceGeneration = instance.ResourceGeneration,
            pipelineInstanceId = instance.InstanceId,
            renderDraws = _renderer.LastEngineMeshDrawCount,
            pipelineDecline = instance.LastRenderDeclineReason,
            resourceFailure = instance.LastResourceGenerationFailure,
        });
        return observed[..^1] + ",\"indexedCache\":" +
            (_unlitProfile.SubmissionStrategy == EMeshSubmissionStrategy.GpuIndirectZeroReadback
                ? _renderer.GetAuthoredIndexedCacheDiagnostics() : "null") +
            ",\"witness\":" + (_unlitProfile.SampleCount > 1 ? UnlitWitnessMetadata : "null") + "," +
            UnlitCaseMetadata[index][1..];
    }

    private static EngineMaterialSemanticIdentity CaseSemantic(int index) => index switch
    {
        0 => EngineMaterialSemanticIdentity.UnlitColorV1,
        1 or 2 => EngineMaterialSemanticIdentity.UnlitTextureV2,
        3 => EngineMaterialSemanticIdentity.UnlitOpaqueTextureV3,
        4 or 5 or 6 => EngineMaterialSemanticIdentity.UnlitAlphaTextureV4,
        7 or 8 => EngineMaterialSemanticIdentity.UnlitTextureArraySliceV5,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public void BindUnlitVariantIdentities(EngineMaterialVariantCatalog variants)
    {
        if (!_unlit || _unlitArtifactIdentities is not null)
            throw new InvalidOperationException("EngineMeshDiagnostic.UnlitVariantBindingInvalid.");
        string[] identities = new string[UnlitCaseMetadata.Length];
        for (int i = 0; i < identities.Length; i++)
        {
            EngineMaterialVariantKey key = EngineUnlitMaterialShaderGenerator.BuiltInKey(CaseSemantic(i));
            if (!variants.TryResolve(key, out ShaderProgramArtifact? artifact) || artifact is null)
                throw new InvalidOperationException($"EngineMeshDiagnostic.UnlitVariantMissing: {key}.");
            identities[i] = artifact.Identity;
        }
        _unlitArtifactIdentities = identities;
    }

    private void DisposeUnlitScene()
    {
        _unlitVisual?.Destroy();
        _unlitBackgroundMesh?.Destroy();
        foreach (XRMesh mesh in _unlitWitnessMeshes)
            mesh.Destroy();
        foreach (XRMaterial material in _unlitMaterials)
            if (!ReferenceEquals(material, _material)) material.Destroy();
        for (int i = _unlitTextures.Count - 1; i >= 0; i--)
            _unlitTextures[i].Destroy();
    }

    /// <summary>Unwinds a failed board startup before the exports can retain and dispose this fixture.</summary>
    private void CleanupFailedUnlitConstruction()
    {
        // Rollback must preserve the startup failure and continue after any teardown failure.
        static void Attempt(Action action)
        {
            try { action(); }
            catch { }
        }

        Attempt(Engine.Time.Timer.Stop);
        Attempt(() => Engine.Time.Timer.CollectVisible -= CollectFrame);
        Attempt(() => Engine.Time.Timer.SwapBuffers -= SwapFrame);
        Attempt(() => Engine.Time.Timer.RenderFrame -= RenderFrame);
        Attempt(() => _renderer.BindEngineViewport(null));
        if (_viewport is { } viewport)
        {
            Attempt(() => viewport.AutomaticallyCollectVisible = false);
            Attempt(() => viewport.AutomaticallySwapBuffers = false);
            Attempt(viewport.Destroy);
        }
        if (_renderWorld is { } renderWorld) Attempt(renderWorld.Dispose);
        if (_unlitVisual is { } visual) Attempt(visual.Destroy);
        Attempt(_scene.Dispose);
        if (_mesh is { } mesh) Attempt(() => mesh.Destroy());
        if (_unlitBackgroundMesh is { } background) Attempt(() => background.Destroy());
        foreach (XRMesh witnessMesh in _unlitWitnessMeshes)
            Attempt(() => witnessMesh.Destroy());
        foreach (XRMaterial material in _unlitMaterials)
            if (!ReferenceEquals(material, _material)) Attempt(() => material.Destroy());
        for (int i = _unlitTextures.Count - 1; i >= 0; i--)
        {
            XRTexture texture = _unlitTextures[i];
            Attempt(() => texture.Destroy());
        }
        if (_material is { } mainMaterial) Attempt(() => mainMaterial.Destroy());
        if (_pipeline is { } pipeline) Attempt(() => pipeline.Destroy());
    }
}
