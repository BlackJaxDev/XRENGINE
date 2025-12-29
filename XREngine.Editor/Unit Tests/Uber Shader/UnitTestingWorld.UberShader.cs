using System.Collections.Generic;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static XRWorld CreateUberShaderWorld(bool setUI, bool isServer)
    {
        ApplyRenderSettingsFromToggles();

        var scene = new XRScene("Uber Shader Scene");
        var rootNode = new SceneNode("Root Node");
        scene.RootNodes.Add(rootNode);

        Pawns.CreatePlayerPawn(setUI, isServer, rootNode);
        if (Toggles.DirLight)
            Lighting.AddDirLight(rootNode);

        AddUberShaderPreviewGrid(rootNode);

        var world = new XRWorld("Uber Shader World", scene);
        Undo.TrackWorld(world);
        return world;
    }

    private static void AddUberShaderPreviewGrid(SceneNode rootNode)
    {
        var gridNode = rootNode.NewChild("UberShaderGrid");

        // Shared textures. We override sampler names per material to match the Uber shader's uniforms.
        var mainTex = Engine.Assets.LoadEngineAsset<XRTexture2D>("Textures", "decal guide.png");
        var heightTex = Engine.Assets.LoadEngineAsset<XRTexture2D>("Textures", "heightmap.png");

        // A small set of representative configurations.
        var configs = new (string Name, UberMaterialConfig Config)[]
        {
            ("Base", new UberMaterialConfig()),
            ("Emission", new UberMaterialConfig { EnableEmission = true }),
            ("Matcap", new UberMaterialConfig { EnableMatcap = true }),
            ("Emission+Matcap", new UberMaterialConfig { EnableEmission = true, EnableMatcap = true }),
        };

        const int columns = 2;
        const float spacing = 3.0f;
        const float radius = 0.9f;

        for (int i = 0; i < configs.Length; i++)
        {
            int col = i % columns;
            int row = i / columns;

            Vector3 pos = new(
                (col - (columns - 1) * 0.5f) * spacing,
                1.25f,
                6.0f + row * spacing);

            var node = gridNode.NewChild(configs[i].Name);
            var tfm = node.SetTransform<Transform>();
            tfm.Translation = pos;

            var model = node.AddComponent<ModelComponent>()!;
            model.Model = new Model([
                new SubMesh(
                    XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, 48),
                    CreateUberShaderMaterial(mainTex, heightTex, configs[i].Config))
            ]);

            // Label in 3D using debug draw (simple point + line marker)
            var debug = node.AddComponent<DebugDrawComponent>()!;
            debug.AddPoint(new Vector3(0, radius + 0.2f, 0), ColorF4.White);
            debug.AddLine(new Vector3(0, radius + 0.2f, 0), new Vector3(0, radius + 1.0f, 0), ColorF4.White);
        }

        // Reference ground grid
        var refNode = rootNode.NewChild("ReferenceGrid");
        var refDebug = refNode.AddComponent<DebugDrawComponent>()!;
        const float extent = 25.0f;
        const float step = 5.0f;
        for (float x = -extent; x <= extent; x += step)
            refDebug.AddLine(new Vector3(x, 0.0f, -extent), new Vector3(x, 0.0f, extent), x == 0.0f ? ColorF4.White : ColorF4.Gray);
        for (float z = -extent; z <= extent; z += step)
            refDebug.AddLine(new Vector3(-extent, 0.0f, z), new Vector3(extent, 0.0f, z), z == 0.0f ? ColorF4.White : ColorF4.Gray);
    }

    private readonly record struct UberMaterialConfig
    {
        public bool EnableEmission { get; init; }
        public bool EnableMatcap { get; init; }

        public ColorF4 Tint { get; init; }

        public UberMaterialConfig()
        {
            EnableEmission = false;
            EnableMatcap = false;
            Tint = ColorF4.White;
        }
    }

    private static XRMaterial CreateUberShaderMaterial(XRTexture2D mainTex, XRTexture2D auxTex, UberMaterialConfig config)
    {
        // Load the Uber shader pair from engine shader assets.
        XRShader vert = ShaderHelper.LoadEngineShader(System.IO.Path.Combine("Uber", "UberShader.vert"));
        XRShader frag = ShaderHelper.LoadEngineShader(System.IO.Path.Combine("Uber", "UberShader.frag"));

        // Textures: bind only the ones we reference, with explicit sampler names.
        // The engine binds textures to samplers using XRTexture.SamplerName.
        XRTexture2D main = CloneTextureWithSampler(mainTex, "_MainTex");
        XRTexture2D bump = CloneTextureWithSampler(auxTex, "_BumpMap");

        var textures = new XRTexture?[] { main, bump };

        // Minimal set of uniforms to get a visible result.
        // Anything not explicitly set will take the shader default (typically 0).
        var parameters = new ShaderVar[]
        {
            new ShaderVector4(new Vector4(config.Tint.R, config.Tint.G, config.Tint.B, config.Tint.A), "_Color"),

            // Main UVs
            new ShaderVector4(new Vector4(1, 1, 0, 0), "_MainTex_ST"),
            new ShaderVector2(Vector2.Zero, "_MainTexPan"),
            new ShaderInt(0, "_MainTexUV"),

            // Normal map (disabled by default)
            new ShaderVector4(new Vector4(1, 1, 0, 0), "_BumpMap_ST"),
            new ShaderVector2(Vector2.Zero, "_BumpMapPan"),
            new ShaderInt(0, "_BumpMapUV"),
            new ShaderFloat(0.0f, "_BumpScale"),

            // Shading
            new ShaderFloat(1.0f, "_ShadingEnabled"),
            new ShaderInt(6, "_LightingMode"), // Realistic (simple lambert) to avoid ramp dependencies.
            new ShaderVector3(new Vector3(1, 1, 1), "_LightingShadowColor"),
            new ShaderFloat(1.0f, "_ShadowStrength"),
            new ShaderFloat(0.0f, "_LightingMinLightBrightness"),
            new ShaderFloat(0.0f, "_LightingMonochromatic"),
            new ShaderFloat(0.0f, "_LightingCapEnabled"),
            new ShaderFloat(10.0f, "_LightingCap"),

            // Alpha behavior
            new ShaderInt(0, "_MainAlphaMaskMode"),
            new ShaderFloat(0.0f, "_AlphaMod"),
            new ShaderFloat(1.0f, "_AlphaForceOpaque"),
            new ShaderFloat(0.5f, "_Cutoff"),
            new ShaderInt(0, "_Mode"),

            // Emission
            new ShaderFloat(config.EnableEmission ? 1.0f : 0.0f, "_EnableEmission"),
            new ShaderVector4(new Vector4(1, 0.7f, 0.2f, 1), "_EmissionColor"),
            new ShaderFloat(2.5f, "_EmissionStrength"),

            // Matcap
            new ShaderFloat(config.EnableMatcap ? 1.0f : 0.0f, "_MatcapEnable"),
            new ShaderVector4(new Vector4(1, 1, 1, 1), "_MatcapColor"),
            new ShaderFloat(1.0f, "_MatcapIntensity"),
            new ShaderFloat(0.0f, "_MatcapReplace"),
            new ShaderFloat(1.0f, "_MatcapMultiply"),
            new ShaderFloat(0.0f, "_MatcapAdd"),
        };

        // Optional feature textures.
        // We only bind these when enabled so it’s obvious (and measurable) what the material actually uses.
        var textureList = new List<XRTexture?>(textures);

        if (config.EnableEmission)
            textureList.Add(CloneTextureWithSampler(mainTex, "_EmissionMap"));

        if (config.EnableMatcap)
            textureList.Add(CloneTextureWithSampler(mainTex, "_Matcap"));

        var material = new XRMaterial(parameters, [.. textureList], vert, frag)
        {
            RenderPass = (int)EDefaultRenderPass.OpaqueForward,
        };

        return material;
    }

    private static XRTexture2D CloneTextureWithSampler(XRTexture2D source, string samplerName)
    {
        // We want multiple bindings of the same underlying texture asset under different sampler names.
        // Clone shallowly and only override the sampler name.
        var t = new XRTexture2D
        {
            FilePath = source.FilePath,
            Name = source.Name,
            SamplerName = samplerName,
            AutoGenerateMipmaps = source.AutoGenerateMipmaps,
        };

        // Share mipmaps; safe for immutable engine textures.
        t.Mipmaps = source.Mipmaps;
        t.Resizable = source.Resizable;
        t.InternalCompression = source.InternalCompression;
        t.AlphaAsTransparency = source.AlphaAsTransparency;

        return t;
    }
}
