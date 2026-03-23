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

public static partial class EditorUnitTests
{
    private static void AddUberShaderPreviewGrid(SceneNode rootNode)
    {
        var gridNode = rootNode.NewChild("UberShaderGrid");

        var mainTex = Engine.Assets.LoadEngineAsset<XRTexture2D>("Textures", "decal guide.png");
        var heightTex = Engine.Assets.LoadEngineAsset<XRTexture2D>("Textures", "heightmap.png");

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

            var debug = node.AddComponent<DebugDrawComponent>()!;
            debug.AddPoint(new Vector3(0, radius + 0.2f, 0), ColorF4.White);
            debug.AddLine(new Vector3(0, radius + 0.2f, 0), new Vector3(0, radius + 1.0f, 0), ColorF4.White);
        }

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
        XRShader vert = ShaderHelper.LoadEngineShader(System.IO.Path.Combine("Uber", "UberShader.vert"));
        XRShader frag = ShaderHelper.LoadEngineShader(System.IO.Path.Combine("Uber", "UberShader.frag"));

        XRTexture2D main = CloneTextureWithSampler(mainTex, "_MainTex");
        XRTexture2D bump = CloneTextureWithSampler(auxTex, "_BumpMap");

        var textureList = new List<XRTexture?> { main, bump };

        if (config.EnableEmission)
            textureList.Add(CloneTextureWithSampler(mainTex, "_EmissionMap"));

        if (config.EnableMatcap)
        {
            textureList.Add(CloneTextureWithSampler(mainTex, "_Matcap"));
            textureList.Add(CreateSolidColorTexture("_MatcapMask", ColorF4.White));
        }

        var material = new XRMaterial(
            ModelImporter.CreateDefaultForwardPlusUberShaderParameters(),
            [.. textureList],
            vert,
            frag)
        {
            RenderPass = (int)EDefaultRenderPass.OpaqueForward,
            RenderOptions = ModelImporter.CreateForwardPlusUberShaderRenderOptions(),
        };

        material.SetVector4("_Color", new Vector4(config.Tint.R, config.Tint.G, config.Tint.B, config.Tint.A));
        material.SetFloat("_EnableEmission", config.EnableEmission ? 1.0f : 0.0f);
        material.SetVector4("_EmissionColor", new Vector4(1.0f, 0.7f, 0.2f, 1.0f));
        material.SetFloat("_EmissionStrength", 2.5f);

        material.SetFloat("_MatcapEnable", config.EnableMatcap ? 1.0f : 0.0f);
        material.SetVector4("_MatcapColor", new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
        material.SetFloat("_MatcapIntensity", 1.0f);
        material.SetFloat("_MatcapReplace", 0.0f);
        material.SetFloat("_MatcapMultiply", 1.0f);
        material.SetFloat("_MatcapAdd", 0.0f);

        return material;
    }

    private static XRTexture2D CreateSolidColorTexture(string samplerName, ColorF4 color)
        => new(1u, 1u, color)
        {
            Name = samplerName,
            SamplerName = samplerName,
            AutoGenerateMipmaps = false,
            Resizable = false,
        };

    private static XRTexture2D CloneTextureWithSampler(XRTexture2D source, string samplerName)
    {
        var t = new XRTexture2D
        {
            FilePath = source.FilePath,
            Name = source.Name,
            SamplerName = samplerName,
            AutoGenerateMipmaps = source.AutoGenerateMipmaps,
            MagFilter = source.MagFilter,
            MinFilter = source.MinFilter,
            UWrap = source.UWrap,
            VWrap = source.VWrap,
            SizedInternalFormat = source.SizedInternalFormat,
        };

        t.Mipmaps = source.Mipmaps;
        t.Resizable = source.Resizable;
        t.InternalCompression = source.InternalCompression;
        t.AlphaAsTransparency = source.AlphaAsTransparency;

        return t;
    }
}
