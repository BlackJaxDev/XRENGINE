using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.Lightmapping;

public sealed class LightmapBakeManager : XRBase
{
    private readonly XRWorldInstance _world;

    private XRTexture? _currentBakeShadowMap;

    private readonly ConcurrentQueue<LightComponent> _manualBakeRequests = new();
    private readonly Dictionary<LightComponent, uint> _lastBakedMovementVersion = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    private readonly Dictionary<RenderableMesh, BakedLightmapInfo> _bakedLightmaps = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly List<XRTexture2D> _bakedAtlases = [];
    private readonly Dictionary<uint, XRMaterial> _bakeMaterialsByUvChannel = new();
    private readonly Dictionary<uint, XRMaterial> _previewMaterialsByUvChannel = new();

    private const string ParamLightKind = "LightKind";
    private const string ParamLightColor = "LightColor";
    private const string ParamLightIntensity = "LightIntensity";
    private const string ParamLightPosition = "LightPosition";
    private const string ParamLightDirection = "LightDirection";
    private const string ParamLightRadius = "LightRadius";
    private const string ParamLightBrightness = "LightBrightness";
    private const string ParamSpotInnerCutoff = "SpotInnerCutoff";
    private const string ParamSpotOuterCutoff = "SpotOuterCutoff";
    private const string ParamSpotExponent = "SpotExponent";

    private const string ParamLightmapScale = "LightmapScale";
    private const string ParamLightmapOffset = "LightmapOffset";

    private const string ParamShadowsEnabled = "ShadowsEnabled";
    private const string ParamWorldToLightInvViewMatrix = "WorldToLightInvViewMatrix";
    private const string ParamWorldToLightProjMatrix = "WorldToLightProjMatrix";
    private const string ParamShadowBase = "ShadowBase";
    private const string ParamShadowMult = "ShadowMult";
    private const string ParamShadowBiasMin = "ShadowBiasMin";
    private const string ParamShadowBiasMax = "ShadowBiasMax";

    public LightmapBakeManager(XRWorldInstance world)
    {
        _world = world;
    }

    /// <summary>
    /// When a DynamicCached light has been stationary for at least this long, a bake will be requested.
    /// </summary>
    public float DynamicCachedStationarySeconds { get; set; } = 0.5f;

    /// <summary>
    /// UV channel index to use for lightmaps (commonly UV1).
    /// </summary>
    public uint LightmapUvChannel { get; set; } = 1;

    /// <summary>
    /// Resolution used for per-mesh lightmaps.
    /// This is a minimal first step (per-mesh textures, no atlasing).
    /// </summary>
    public uint PerMeshLightmapResolution { get; set; } = 512;

    /// <summary>
    /// Atlas page size in pixels (square). Atlases are created per bake and may span multiple pages.
    /// </summary>
    public uint LightmapAtlasResolution { get; set; } = 2048;

    /// <summary>
    /// Padding used around each tile (also acts as a guard band against bilinear bleed).
    /// </summary>
    public uint LightmapAtlasPadding { get; set; } = 4;

    /// <summary>
    /// If true, the bake shader samples the light's current shadow map (directional/spot).
    /// </summary>
    public bool BakeDirectShadows { get; set; } = true;

    public event Action<LightmapBakeResult>? BakeCompleted;

    public bool TryGetBakedLightmap(RenderableMesh mesh, [NotNullWhen(true)] out XRTexture2D? lightmap)
    {
        if (_bakedLightmaps.TryGetValue(mesh, out var info))
        {
            lightmap = info.Atlas;
            return true;
        }
        lightmap = null;
        return false;
    }

    public bool TryGetBakedLightmapInfo(RenderableMesh mesh, [NotNullWhen(true)] out BakedLightmapInfo? info)
    {
        if (_bakedLightmaps.TryGetValue(mesh, out var v))
        {
            info = v;
            return true;
        }
        info = null;
        return false;
    }

    public void RequestBake(LightComponent light)
    {
        if (light is null)
            return;

        _manualBakeRequests.Enqueue(light);
    }

    internal void Update()
    {
        ProcessDynamicCachedAutoBake();
        ProcessManualRequests();
    }

    private void ProcessDynamicCachedAutoBake()
    {
        foreach (var light in EnumerateLights(_world))
        {
            if (light.Type != ELightType.DynamicCached)
                continue;

            if (light.TimeSinceLastMovement < DynamicCachedStationarySeconds)
                continue;

            uint lastBaked = _lastBakedMovementVersion.TryGetValue(light, out var v) ? v : 0u;
            if (lastBaked == light.MovementVersion)
                continue;

            _lastBakedMovementVersion[light] = light.MovementVersion;
            _manualBakeRequests.Enqueue(light);
        }
    }

    private void ProcessManualRequests()
    {
        while (_manualBakeRequests.TryDequeue(out var light))
        {
            if (light.World is null || !ReferenceEquals(light.World, _world))
                continue;

            var request = new LightmapBakeRequest(
                StaticLayerMask: new LayerMask(1 << DefaultLayers.StaticIndex),
                LightmapUvChannel: LightmapUvChannel);

            var result = BakeLight(light, request);
            BakeCompleted?.Invoke(result);

            if (result.Status != ELightmapBakeStatus.Completed)
                Debug.LogWarning(result.Message ?? "Lightmap bake did not complete.");
        }
    }

    private LightmapBakeResult BakeLight(LightComponent light, LightmapBakeRequest request)
    {
        ArgumentNullException.ThrowIfNull(light);

        var targets = CollectStaticTargets(_world, request);

        if (targets.Count == 0)
        {
            return new LightmapBakeResult(
                light,
                targets,
                ELightmapBakeStatus.Failed,
                "No static renderables found to bake. Mark meshes as Static via SceneNode.Layer and ensure the Static layer exists.");
        }

        if (targets.Any(t => !t.HasRequiredUvChannel))
        {
            int missing = targets.Count(t => !t.HasRequiredUvChannel);
            return new LightmapBakeResult(
                light,
                targets,
                ELightmapBakeStatus.Failed,
                $"Cannot bake lightmaps: {missing} mesh(es) are missing UV channel {request.LightmapUvChannel} (need an unused UV set for lightmaps).");
        }

        uint tileResolution = Math.Max(1u, PerMeshLightmapResolution);
        uint atlasResolution = Math.Max(tileResolution, LightmapAtlasResolution);
        uint padding = LightmapAtlasPadding;
        var bakeMaterial = GetOrCreateBakeMaterial(request.LightmapUvChannel);

        // Atlased bake: pack meshes into one or more atlas pages and render each mesh into its tile in UV space.
        // (No indirect bounces, no shadows yet.)
        XRCamera camera = new(new Transform());
        var pipeline = new XRRenderPipelineInstance();

        using var _ = Engine.Rendering.State.PushRenderingPipeline(pipeline);
        using var __ = pipeline.RenderState.PushMainAttributes(
            viewport: null,
            scene: null,
            camera: camera,
            stereoRightEyeCamera: null,
            target: null,
            shadowPass: false,
            stereoPass: false,
            globalMaterialOverride: null,
            screenSpaceUI: null,
            meshRenderCommands: null);

        ClearBakedAtlases();

        // Build renderers list + atlas packing.
        var bakeList = new List<(LightmapBakeTarget Target, XRMeshRenderer Renderer)>();
        foreach (var t in targets)
        {
            var renderer = t.MeshLink.CurrentLODRenderer;
            if (renderer is null)
                continue;
            bakeList.Add((t, renderer));
        }

        if (bakeList.Count == 0)
            return new LightmapBakeResult(light, targets, ELightmapBakeStatus.Failed, "No valid mesh renderers found for bake targets.");

        var packed = PackAtlasTiles(bakeList.Count, (int)atlasResolution, (int)tileResolution, (int)padding);

        // Ensure shadow map is up to date for this bake (directional/spot supported).
        PrepareShadowMapForBake(light);

        UpdateBakeMaterialUniforms(bakeMaterial, light);

        for (int pageIndex = 0; pageIndex < packed.Pages.Count; pageIndex++)
        {
            var page = packed.Pages[pageIndex];

            XRTexture2D atlasTex = XRTexture2D.CreateFrameBufferTexture(
                (uint)page.Size,
                (uint)page.Size,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            atlasTex.Resizable = false;
            atlasTex.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            atlasTex.MinFilter = ETexMinFilter.Linear;
            atlasTex.MagFilter = ETexMagFilter.Linear;
            atlasTex.UWrap = ETexWrapMode.ClampToEdge;
            atlasTex.VWrap = ETexWrapMode.ClampToEdge;
            atlasTex.Name = $"LightmapAtlas_Page{pageIndex}";

            var depth = new XRRenderBuffer((uint)page.Size, (uint)page.Size, ERenderBufferStorage.Depth24Stencil8);
            depth.Allocate();

            var fbo = new XRFrameBuffer();
            fbo.SetRenderTargets(
                (atlasTex, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (depth, EFrameBufferAttachment.DepthStencilAttachment, 0, -1));

            using (fbo.BindForWritingState())
            {
                // Clear the full atlas once.
                using var _area = pipeline.RenderState.PushRenderArea(0, 0, page.Size, page.Size);
                AbstractRenderer.Current?.ClearColor(ColorF4.Black);
                AbstractRenderer.Current?.Clear(color: true, depth: true, stencil: false);

                foreach (var tile in page.Tiles)
                {
                    var (t, renderer) = bakeList[tile.TargetIndex];

                    // Render into the tile's inner area (guard band = padding).
                    int innerX = tile.X + tile.Padding;
                    int innerY = tile.Y + tile.Padding;
                    int innerW = tile.TileSize;
                    int innerH = tile.TileSize;

                    using var __area = pipeline.RenderState.PushRenderArea(innerX, innerY, innerW, innerH);

                    Matrix4x4 modelMatrix = t.Component.Transform.RenderMatrix;
                    renderer.Render(modelMatrix, modelMatrix, bakeMaterial, 1u, forceNoStereo: true);

                    // Store mapping for preview sampling.
                    float inv = 1.0f / page.Size;
                    var scale = new Vector2(innerW * inv, innerH * inv);
                    var offset = new Vector2(innerX * inv, innerY * inv);
                    var info = new BakedLightmapInfo(atlasTex, scale, offset, request.LightmapUvChannel, pageIndex);
                    _bakedLightmaps[t.MeshLink] = info;

                    ApplyPreviewMaterialOverride(t.MeshLink, request.LightmapUvChannel, info);
                }
            }

            _bakedAtlases.Add(atlasTex);

            depth.Destroy();
            fbo.Destroy();
        }

        return new LightmapBakeResult(light, targets, ELightmapBakeStatus.Completed, null);
    }

    private void PrepareShadowMapForBake(LightComponent light)
    {
        _currentBakeShadowMap = null;

        if (!BakeDirectShadows || !light.CastsShadows)
            return;

        // Directional/spot use 2D shadow maps; point uses cubemap.
        if (light is not XREngine.Components.Lights.DirectionalLightComponent &&
            light is not SpotLightComponent &&
            light is not PointLightComponent)
            return;

        // Ensure a shadow map exists.
        if (light.ShadowMap is null)
        {
            uint w = Math.Max(1u, light.ShadowMapResolutionWidth);
            uint h = Math.Max(1u, light.ShadowMapResolutionHeight);
            if (w == 1u && h == 1u)
            {
                w = 2048u;
                h = 2048u;
            }
            light.SetShadowMapResolution(w, h);
        }

        // Render shadow map now so it matches current scene state.
        light.RenderShadowMap(collectVisibleNow: true);

        _currentBakeShadowMap = TryGetShadowSamplerTexture(light);
    }

    private static XRTexture? TryGetShadowSamplerTexture(LightComponent light)
    {
        var mat = light.ShadowMap?.Material;
        if (mat is null)
            return null;

        // SpotLight uses color attachment 0 as sampler (mat.Textures[1]); Directional uses depth texture (mat.Textures[0]).
        if (light is SpotLightComponent)
        {
            if (mat.Textures.Count >= 2)
                return mat.Textures[1];
            return null;
        }

        if (mat.Textures.Count >= 1)
            return mat.Textures[0];

        return null;
    }

    private void ClearBakedAtlases()
    {
        _bakedLightmaps.Clear();
        foreach (var tex in _bakedAtlases)
            tex.Destroy();
        _bakedAtlases.Clear();
    }

    public readonly record struct BakedLightmapInfo(XRTexture2D Atlas, Vector2 Scale, Vector2 Offset, uint UvChannel, int PageIndex);

    private sealed record AtlasPackResult(List<AtlasPage> Pages);

    private sealed record AtlasPage(int Size, List<AtlasTile> Tiles);

    private readonly record struct AtlasTile(int TargetIndex, int X, int Y, int TileSize, int Padding);

    private static AtlasPackResult PackAtlasTiles(int tileCount, int atlasSize, int tileSize, int padding)
    {
        // Shelf packer. Each allocated tile includes an outer padding band on all sides.
        int outerSize = checked(tileSize + (padding * 2));
        if (outerSize > atlasSize)
            throw new InvalidOperationException($"Atlas too small: atlas={atlasSize}, tile={tileSize}, padding={padding}.");

        var pages = new List<AtlasPage>();
        var tiles = new List<AtlasTile>();

        int cursorX = 0;
        int cursorY = 0;
        int shelfHeight = outerSize;
        int pageIndex = 0;

        void NewPage()
        {
            if (tiles.Count > 0)
                pages.Add(new AtlasPage(atlasSize, tiles));
            tiles = new List<AtlasTile>();
            cursorX = 0;
            cursorY = 0;
            shelfHeight = outerSize;
            pageIndex++;
        }

        for (int i = 0; i < tileCount; i++)
        {
            if (cursorX + outerSize > atlasSize)
            {
                cursorX = 0;
                cursorY += shelfHeight;
            }

            if (cursorY + outerSize > atlasSize)
            {
                NewPage();
            }

            tiles.Add(new AtlasTile(i, cursorX, cursorY, tileSize, padding));
            cursorX += outerSize;
        }

        if (tiles.Count > 0)
            pages.Add(new AtlasPage(atlasSize, tiles));

        return new AtlasPackResult(pages);
    }

    private XRMaterial GetOrCreateBakeMaterial(uint uvChannel)
    {
        if (_bakeMaterialsByUvChannel.TryGetValue(uvChannel, out var mat))
            return mat;

        // Geometry shader remaps triangles into UV space.
        // We rely on the engine-generated vertex shader (handles variable attribute layouts).
        uint uvLocation = 4u + uvChannel;
        string geom = $@"
#version 460

layout (triangles) in;
layout (triangle_strip, max_vertices = 3) out;

layout (location = 1) in vec3 InFragNorm[];
layout (location = 20) in vec3 InFragPosLocal[];
layout (location = {uvLocation}) in vec2 InFragUV[];

layout (location = 1) out vec3 OutFragNorm;
layout (location = 20) out vec3 OutFragPosLocal;

void main()
{{
    for (int i = 0; i < 3; ++i)
    {{
        vec2 uv = InFragUV[i];
        vec2 ndc = uv * 2.0 - 1.0;
        gl_Position = vec4(ndc, 0.0, 1.0);
        OutFragNorm = InFragNorm[i];
        OutFragPosLocal = InFragPosLocal[i];
        EmitVertex();
    }}
    EndPrimitive();
}}
";

        string frag = @"
#version 460

layout (location = 0) out vec4 OutColor;
layout (location = 1) in vec3 FragNorm;
layout (location = 20) in vec3 FragPosLocal;

uniform mat4 ModelMatrix;

uniform int LightKind; // 0=Directional, 1=Point, 2=Spot
uniform vec3 LightColor;
uniform float LightIntensity;

uniform vec3 LightPosition;
uniform vec3 LightDirection;

uniform float LightRadius;
uniform float LightBrightness;

uniform float SpotInnerCutoff;
uniform float SpotOuterCutoff;
uniform float SpotExponent;

uniform int ShadowsEnabled;
uniform mat4 WorldToLightInvViewMatrix;
uniform mat4 WorldToLightProjMatrix;
uniform sampler2D ShadowMap;
uniform samplerCube ShadowCube;
uniform float ShadowBase;
uniform float ShadowMult;
uniform float ShadowBiasMin;
uniform float ShadowBiasMax;

float GetShadowBias(in float NoL)
{
    float mapped = pow(ShadowBase * (1.0 - NoL), ShadowMult);
    return mix(ShadowBiasMin, ShadowBiasMax, mapped);
}

// 0 = shadowed, 1 = lit
float ReadShadowMap2D(in vec3 fragPosWS, in float NoL, in mat4 lightMatrix)
{
    vec4 fragPosLightSpace = lightMatrix * vec4(fragPosWS, 1.0);
    vec3 fragCoord = fragPosLightSpace.xyz / fragPosLightSpace.w;
    fragCoord = fragCoord * 0.5 + 0.5;

    // Outside the shadow map projection => treat as lit.
    if (fragCoord.x < 0.0 || fragCoord.x > 1.0 || fragCoord.y < 0.0 || fragCoord.y > 1.0 || fragCoord.z < 0.0 || fragCoord.z > 1.0)
        return 1.0;

    float bias = GetShadowBias(NoL);

    // 3x3 PCF
    float shadow = 0.0;
    vec2 texelSize = 1.0 / vec2(textureSize(ShadowMap, 0));
    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            float pcfDepth = texture(ShadowMap, fragCoord.xy + vec2(x, y) * texelSize).r;
            shadow += (fragCoord.z - bias) > pcfDepth ? 0.0 : 1.0;
        }
    }
    shadow *= 0.111111111;
    return shadow;
}

// returns 1 lit, 0 shadow
float ReadPointShadowMap(in float farPlaneDist, in vec3 fragToLightWS, in float lightDist, in float NoL)
{
    float bias = GetShadowBias(NoL);
    float closestDepth = texture(ShadowCube, fragToLightWS).r * farPlaneDist;
    return (lightDist - bias) <= closestDepth ? 1.0 : 0.0;
}

void main()
{
    vec3 N = normalize(FragNorm);
    vec3 worldPos = (ModelMatrix * vec4(FragPosLocal, 1.0)).xyz;

    vec3 L;
    float atten = 1.0;

    if (LightKind == 0)
    {
        L = normalize(-LightDirection);
    }
    else
    {
        vec3 toLight = LightPosition - worldPos;
        float dist = length(toLight);
        if (dist <= 1e-6)
            dist = 1e-6;

        L = toLight / dist;

        // Simple falloff: clamp to radius.
        float r = max(LightRadius, 1e-6);
        float nd = 1.0 - clamp(dist / r, 0.0, 1.0);
        atten = nd * LightBrightness;

        if (LightKind == 2)
        {
            vec3 lightToFragDir = normalize(worldPos - LightPosition);
            float cosAngle = dot(normalize(LightDirection), lightToFragDir);
            float spot = smoothstep(SpotOuterCutoff, SpotInnerCutoff, cosAngle);
            atten *= pow(max(spot, 0.0), max(SpotExponent, 0.0));
        }
    }

    float ndotl = max(dot(N, L), 0.0);

    float shadow = 1.0;
    if (ShadowsEnabled != 0)
    {
        if (LightKind == 1)
        {
            // Match DeferredLightingPoint.fs logic.
            float lightDist = dist / max(LightBrightness, 1e-6);
            float farPlaneDist = LightRadius / max(LightBrightness, 1e-6);
            shadow = ReadPointShadowMap(farPlaneDist, -L, lightDist, ndotl);
        }
        else
        {
            mat4 lightMatrix = WorldToLightProjMatrix * inverse(WorldToLightInvViewMatrix);
            shadow = ReadShadowMap2D(worldPos, ndotl, lightMatrix);
        }
    }

    vec3 result = LightColor * (ndotl * LightIntensity * atten) * shadow;
    OutColor = vec4(result, 1.0);
}
";

        ShaderVar[] parameters =
        [
            new ShaderInt(0, ParamLightKind),
            new ShaderVector3(Vector3.One, ParamLightColor),
            new ShaderFloat(1.0f, ParamLightIntensity),
            new ShaderVector3(Vector3.Zero, ParamLightPosition),
            new ShaderVector3(Globals.Forward, ParamLightDirection),
            new ShaderFloat(10.0f, ParamLightRadius),
            new ShaderFloat(1.0f, ParamLightBrightness),
            new ShaderFloat(0.9f, ParamSpotInnerCutoff),
            new ShaderFloat(0.8f, ParamSpotOuterCutoff),
            new ShaderFloat(1.0f, ParamSpotExponent),

            new ShaderInt(0, ParamShadowsEnabled),
            new ShaderMat4(Matrix4x4.Identity, ParamWorldToLightInvViewMatrix),
            new ShaderMat4(Matrix4x4.Identity, ParamWorldToLightProjMatrix),
            new ShaderFloat(1.0f, ParamShadowBase),
            new ShaderFloat(1.0f, ParamShadowMult),
            new ShaderFloat(0.00001f, ParamShadowBiasMin),
            new ShaderFloat(0.004f, ParamShadowBiasMax),
        ];

        mat = new XRMaterial(
            parameters,
            new XRShader(EShaderType.Geometry, geom),
            new XRShader(EShaderType.Fragment, frag));

        mat.Name = $"LightmapBake_UV{uvChannel}";
        mat.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
        mat.RenderOptions.CullMode = ECullMode.None;
        mat.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
        mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.None;

        // Bind shadow map sampler at render time if available.
        mat.SettingUniforms += BakeMaterial_SettingUniforms;

        _bakeMaterialsByUvChannel[uvChannel] = mat;
        return mat;
    }

    private void BakeMaterial_SettingUniforms(XRMaterialBase @base, XRRenderProgram program)
    {
        if (_currentBakeShadowMap is null)
            return;

        // Match engine deferred shaders which bind the shadow sampler at texture unit 4.
        if (_currentBakeShadowMap is XRTextureCube)
            program.Sampler("ShadowCube", _currentBakeShadowMap, 4);
        else
            program.Sampler("ShadowMap", _currentBakeShadowMap, 4);
    }

    private XRMaterial GetOrCreatePreviewMaterial(uint uvChannel)
    {
        if (_previewMaterialsByUvChannel.TryGetValue(uvChannel, out var mat))
            return mat;

        uint uvLocation = 4u + uvChannel;
        string frag = $@"
#version 460

layout (location = 0) out vec4 OutColor;
layout (location = {uvLocation}) in vec2 FragUV;

uniform sampler2D Texture0;
uniform vec2 LightmapScale;
uniform vec2 LightmapOffset;

void main()
{{
    vec2 uv = FragUV * LightmapScale + LightmapOffset;
    vec3 lm = texture(Texture0, uv).rgb;
    OutColor = vec4(lm, 1.0);
}}
";

        ShaderVar[] parameters =
        [
            new ShaderVector2(Vector2.One, ParamLightmapScale),
            new ShaderVector2(Vector2.Zero, ParamLightmapOffset),
        ];

        mat = new XRMaterial(parameters, new XRShader(EShaderType.Fragment, frag));
        mat.Name = $"LightmapPreview_UV{uvChannel}";
        mat.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
        mat.RenderOptions.CullMode = ECullMode.Back;
        mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.None;
        _previewMaterialsByUvChannel[uvChannel] = mat;
        return mat;
    }

    private static void UpdateBakeMaterialUniforms(XRMaterial bakeMaterial, LightComponent light)
    {
        // Default values.
        int kind = 0;
        Vector3 pos = light.Transform.RenderTranslation;
        Vector3 dir = light.Transform.RenderForward;
        float radius = 10.0f;
        float brightness = 1.0f;
        float inner = 0.9f;
        float outer = 0.8f;
        float exponent = 1.0f;

        switch (light)
        {
            case XREngine.Components.Lights.DirectionalLightComponent:
                kind = 0;
                break;
            case PointLightComponent p:
                kind = 1;
                radius = p.Radius;
                brightness = p.Brightness;
                break;
            case SpotLightComponent s:
                kind = 2;
                radius = s.Distance;
                brightness = s.Brightness;
                inner = MathF.Cos(XREngine.Data.Core.XRMath.DegToRad(s.InnerCutoffAngleDegrees));
                outer = MathF.Cos(XREngine.Data.Core.XRMath.DegToRad(s.OuterCutoffAngleDegrees));
                exponent = s.Exponent;
                break;
        }

        // Note: ShaderVar.Name == uniform name.
        SetMaterialParam(bakeMaterial, ParamLightKind, kind);
        SetMaterialParam(bakeMaterial, ParamLightColor, (Vector3)light.Color);
        SetMaterialParam(bakeMaterial, ParamLightIntensity, light.DiffuseIntensity);
        SetMaterialParam(bakeMaterial, ParamLightPosition, pos);
        SetMaterialParam(bakeMaterial, ParamLightDirection, dir);
        SetMaterialParam(bakeMaterial, ParamLightRadius, radius);
        SetMaterialParam(bakeMaterial, ParamLightBrightness, brightness);
        SetMaterialParam(bakeMaterial, ParamSpotInnerCutoff, inner);
        SetMaterialParam(bakeMaterial, ParamSpotOuterCutoff, outer);
        SetMaterialParam(bakeMaterial, ParamSpotExponent, exponent);

        bool enableShadow = false;
        Matrix4x4 worldToLightInvView = Matrix4x4.Identity;
        Matrix4x4 worldToLightProj = Matrix4x4.Identity;

        if (light.CastsShadows && light.ShadowMap is not null)
        {
            if (light is OneViewLightComponent ov && light is XREngine.Components.Lights.DirectionalLightComponent or SpotLightComponent)
            {
                enableShadow = true;
                worldToLightInvView = ov.ShadowCamera?.Transform.RenderMatrix ?? Matrix4x4.Identity;
                worldToLightProj = ov.ShadowCamera?.ProjectionMatrix ?? Matrix4x4.Identity;
            }
            else if (light is PointLightComponent)
            {
                // Point shadows are sampled from cubemap; matrices are unused.
                enableShadow = true;
            }
        }

        SetMaterialParam(bakeMaterial, ParamShadowsEnabled, enableShadow ? 1 : 0);
        SetMaterialParam(bakeMaterial, ParamWorldToLightInvViewMatrix, worldToLightInvView);
        SetMaterialParam(bakeMaterial, ParamWorldToLightProjMatrix, worldToLightProj);
        SetMaterialParam(bakeMaterial, ParamShadowBase, light.ShadowExponentBase);
        SetMaterialParam(bakeMaterial, ParamShadowMult, light.ShadowExponent);
        SetMaterialParam(bakeMaterial, ParamShadowBiasMin, light.ShadowMinBias);
        SetMaterialParam(bakeMaterial, ParamShadowBiasMax, light.ShadowMaxBias);
    }

    private static void SetMaterialParam<T>(XRMaterial material, string name, T value) where T : struct
    {
        foreach (var p in material.Parameters)
        {
            if (!string.Equals(p.Name, name, StringComparison.Ordinal))
                continue;
            if (p is ShaderVar<T> typed)
                typed.Value = value;
            return;
        }
    }

    private void ApplyPreviewMaterialOverride(RenderableMesh mesh, uint uvChannel, BakedLightmapInfo info)
    {
        var previewBase = GetOrCreatePreviewMaterial(uvChannel);

        // Create a per-mesh material instance so we can bind the correct atlas page + per-mesh UV transform.
        ShaderVar[] parameters =
        [
            new ShaderVector2(info.Scale, ParamLightmapScale),
            new ShaderVector2(info.Offset, ParamLightmapOffset),
        ];

        var preview = new XRMaterial(parameters, new XRTexture?[] { info.Atlas }, previewBase.Shaders)
        {
            Name = $"{previewBase.Name}_{mesh.Component.SceneNode.Name}",
            RenderPass = previewBase.RenderPass,
            RenderOptions = previewBase.RenderOptions,
        };

        foreach (var cmd in mesh.RenderInfo.RenderCommands)
        {
            if (cmd is IRenderCommandMesh m)
                m.MaterialOverride = preview;
        }
    }

    private static List<LightmapBakeTarget> CollectStaticTargets(XRWorldInstance world, LightmapBakeRequest request)
    {
        List<LightmapBakeTarget> targets = [];

        foreach (var root in world.RootNodes)
        {
            root.IterateHierarchy(node =>
            {
                if (!request.StaticLayerMask.Contains(node.Layer))
                    return;

                foreach (var component in node.Components)
                {
                    if (component is not RenderableComponent renderable)
                        continue;

                    foreach (var link in renderable.Meshes)
                    {
                        XRMesh? mesh = TryGetBakeMesh(link);
                        if (mesh is null)
                            continue;

                        bool hasUv = mesh.TexCoordCount > request.LightmapUvChannel;
                        targets.Add(new LightmapBakeTarget(node, renderable, link, mesh, request.LightmapUvChannel, hasUv));
                    }
                }
            });
        }

        return targets;
    }

    private static XRMesh? TryGetBakeMesh(RenderableMesh link)
    {
        // Prefer LOD0 for bakes (stable across camera distance).
        var lod0 = link.LODs.First?.Value?.Renderer?.Mesh;
        return lod0 ?? link.CurrentLODMesh;
    }

    private static IEnumerable<LightComponent> EnumerateLights(XRWorldInstance world)
    {
        foreach (var root in world.RootNodes)
        {
            List<LightComponent> lights = [];
            root.IterateComponents<LightComponent>(l => lights.Add(l), iterateChildHierarchy: true);
            foreach (var l in lights)
                yield return l;
        }
    }
}

public enum ELightmapBakeStatus
{
    Queued,
    Completed,
    Failed,
    NotImplemented,
}

public readonly record struct LightmapBakeRequest(LayerMask StaticLayerMask, uint LightmapUvChannel);

public readonly record struct LightmapBakeTarget(
    SceneNode Node,
    RenderableComponent Component,
    RenderableMesh MeshLink,
    XRMesh Mesh,
    uint LightmapUvChannel,
    bool HasRequiredUvChannel);

public readonly record struct LightmapBakeResult(
    LightComponent Light,
    IReadOnlyList<LightmapBakeTarget> Targets,
    ELightmapBakeStatus Status,
    string? Message);
