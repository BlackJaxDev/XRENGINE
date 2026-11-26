using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;

namespace ModularPipelineParity;

/// <summary>
/// Builds one saved scene part as a regular indexed model draw after the browser
/// pipeline has installed its exact custom raster artifact.
/// </summary>
[Serializable]
public sealed class CustomMsaaSceneComponent : ModelComponent
{
    public const string BindingKey = "custom::msaa-scene";

    private ECustomMsaaScenePart _scenePart;
    private Model? _ownedModel;
    private SubMesh? _ownedSubMesh;
    private XRMesh? _ownedMesh;
    private XRMaterial? _ownedMaterial;
    private XRShader[]? _ownedShaders;

    /// <summary>The scene part retained in the authored world.</summary>
    public ECustomMsaaScenePart ScenePart
    {
        get => _scenePart;
        set => SetField(ref _scenePart, value);
    }

    /// <summary>Installs the selected source program on an ordinary model material.</summary>
    public void Configure(RenderPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (_ownedModel is not null)
        {
            // Each scene material keeps the exact cooked module captured at first
            // configuration; switching cameras must never rebind its shader key.
            string expectedIdentity = pipeline.GetRequiredWebPipelineArtifact(BindingKey).Identity;
            if (_ownedShaders is not { Length: 2 } stages ||
                _ownedMaterial is not { } boundMaterial ||
                boundMaterial.Shaders.Count != 2 ||
                !ReferenceEquals(boundMaterial.Shaders[0], stages[0]) ||
                !ReferenceEquals(boundMaterial.Shaders[1], stages[1]) ||
                stages[0].CookedArtifact is not { } pinnedArtifact ||
                !ReferenceEquals(stages[1].CookedArtifact, pinnedArtifact) ||
                pinnedArtifact.Identity != expectedIdentity)
                throw new InvalidOperationException("The shared MSAA scene cameras require the same scoped raster program.");
            return;
        }

        XRShader[] shaders = WebPipelineRasterProgram.CreateShaders(pipeline, BindingKey);
        XRMesh? mesh = null;
        XRMaterial? material = null;
        SubMesh? subMesh = null;
        Model? model = null;
        try
        {
            mesh = CreateMesh(ScenePart);
            Vector4 color = ScenePart switch
            {
                ECustomMsaaScenePart.OpaqueSlope => new(0.95f, 0.78f, 0.16f, 1.0f),
                ECustomMsaaScenePart.FarAlpha => new(0.8f, 0.2f, 0.1f, 0.5f),
                ECustomMsaaScenePart.NearAlpha => new(0.2f, 0.8f, 0.4f, 0.5f),
                _ => throw new ArgumentOutOfRangeException(nameof(ScenePart)),
            };
            material = new XRMaterial([new ShaderVector4(color, "MatColor")], shaders)
            {
                Name = $"CustomMsaa{ScenePart}",
                RenderPass = (int)EDefaultRenderPass.OpaqueForward,
            };
            material.RenderOptions.CullMode = ECullMode.None;
            if (ScenePart != ECustomMsaaScenePart.OpaqueSlope)
            {
                material.TransparencyMode = ETransparencyMode.AlphaBlend;
                material.RenderOptions.BlendModeAllDrawBuffers = new BlendMode
                {
                    Enabled = ERenderParamUsage.Enabled,
                    RgbSrcFactor = EBlendingFactor.SrcAlpha,
                    RgbDstFactor = EBlendingFactor.OneMinusSrcAlpha,
                    AlphaSrcFactor = EBlendingFactor.One,
                    AlphaDstFactor = EBlendingFactor.OneMinusSrcAlpha,
                };
            }

            subMesh = new SubMesh(mesh, material) { Name = $"CustomMsaa{ScenePart}" };
            model = new Model([subMesh]) { Name = $"CustomMsaa{ScenePart}" };
            Model = model;
            SetField(ref _ownedShaders, shaders, publishNotifications: false);
            SetField(ref _ownedMesh, mesh, publishNotifications: false);
            SetField(ref _ownedMaterial, material, publishNotifications: false);
            SetField(ref _ownedSubMesh, subMesh, publishNotifications: false);
            SetField(ref _ownedModel, model, publishNotifications: false);
        }
        catch
        {
            model?.Destroy();
            subMesh?.Destroy();
            material?.Destroy();
            mesh?.Destroy();
            foreach (XRShader shader in shaders)
                shader.Destroy();
            throw;
        }
    }

    protected override void OnDestroying()
    {
        base.OnDestroying();
        _ownedModel?.Destroy();
        _ownedSubMesh?.Destroy();
        _ownedMaterial?.Destroy();
        _ownedMesh?.Destroy();
        if (_ownedShaders is not null)
            foreach (XRShader shader in _ownedShaders)
                shader.Destroy();
        SetField(ref _ownedModel, null, publishNotifications: false);
        SetField(ref _ownedSubMesh, null, publishNotifications: false);
        SetField(ref _ownedMaterial, null, publishNotifications: false);
        SetField(ref _ownedMesh, null, publishNotifications: false);
        SetField(ref _ownedShaders, null, publishNotifications: false);
    }

    private static XRMesh CreateMesh(ECustomMsaaScenePart part)
    {
        Vector3 normal = Vector3.UnitZ;
        Vertex[] vertices = part switch
        {
            ECustomMsaaScenePart.OpaqueSlope =>
            [
                new(new Vector3(-1.40f, -0.72f, 0.15f), normal),
                new(new Vector3(-0.65f, -0.72f, 0.15f), normal),
                new(new Vector3(-0.22f,  0.72f, 0.15f), normal),
                new(new Vector3(-0.97f,  0.72f, 0.15f), normal),
            ],
            ECustomMsaaScenePart.FarAlpha =>
            [
                new(new Vector3(-0.72f, -0.58f, 0.00f), normal),
                new(new Vector3( 0.72f, -0.58f, 0.00f), normal),
                new(new Vector3( 0.72f,  0.58f, 0.00f), normal),
                new(new Vector3(-0.72f,  0.58f, 0.00f), normal),
            ],
            ECustomMsaaScenePart.NearAlpha =>
            [
                new(new Vector3(-0.47f, -0.42f, 0.35f), normal),
                new(new Vector3( 0.47f, -0.42f, 0.35f), normal),
                new(new Vector3( 0.47f,  0.42f, 0.35f), normal),
                new(new Vector3(-0.47f,  0.42f, 0.35f), normal),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(part)),
        };
        return new XRMesh(vertices, new List<ushort> { 0, 1, 2, 0, 2, 3 });
    }
}
