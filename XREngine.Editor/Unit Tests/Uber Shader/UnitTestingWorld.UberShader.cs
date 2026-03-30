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
    private const int UberMainTexSlot = 0;
    private const int UberBumpMapSlot = 1;
    private const int UberAlphaMaskSlot = 2;
    private const int UberEmissionMapSlot = 8;
    private const int UberMatcapSlot = 9;
    private const int UberMatcapMaskSlot = 10;

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
                -6.0f - row * spacing);

            var node = gridNode.NewChild(configs[i].Name);
            var tfm = node.SetTransform<Transform>();
            tfm.Translation = pos;

            // Precompute the transform before the renderable exists so RenderableMesh sees the
            // authored startup matrix instead of snapshotting identity/origin first.
            tfm.RecalculateMatrixHierarchy(true, true, ELoopType.Sequential).GetAwaiter().GetResult();

            var model = node.AddComponent<ModelComponent>()!;
            model.Model = new Model([
                new SubMesh(
                    XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, 48),
                    CreateUberShaderMaterial(mainTex, heightTex, configs[i].Config))
            ]);

            // Re-run once after the model is attached so render bounds and command state are in sync.
            tfm.RecalculateMatrixHierarchy(true, true, ELoopType.Sequential).GetAwaiter().GetResult();
        }
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
        XRShader vertOvr = ShaderHelper.LoadEngineShader(System.IO.Path.Combine("Uber", "UberShader_OVR.vert"), EShaderType.Vertex);
        XRShader vertNv = ShaderHelper.LoadEngineShader(System.IO.Path.Combine("Uber", "UberShader_NV.vert"), EShaderType.Vertex);
        XRShader frag = ShaderHelper.LoadEngineShader(System.IO.Path.Combine("Uber", "UberShader.frag"));

        XRTexture2D main = CreateUberPreviewTexture(mainTex, "_MainTex");
        XRTexture2D bump = CreateUberPreviewTexture(auxTex, "_BumpMap");
        XRTexture2D alphaMask = CreateSolidColorTexture("_AlphaMask", ColorF4.White);
        XRTexture2D emissionMap = CreateUberPreviewTexture(mainTex, "_EmissionMap");

        var textureList = new List<XRTexture?>(new XRTexture?[UberMatcapMaskSlot + 1]);
        textureList[UberMainTexSlot] = main;
        textureList[UberBumpMapSlot] = bump;
        textureList[UberAlphaMaskSlot] = alphaMask;
        textureList[UberEmissionMapSlot] = emissionMap;

        if (config.EnableMatcap)
            textureList[UberMatcapSlot] = CreateUberPreviewTexture(mainTex, "_Matcap");

        textureList[UberMatcapMaskSlot] = CreateSolidColorTexture("_MatcapMask", ColorF4.White);

        var material = new XRMaterial(
            ModelImporter.CreateDefaultForwardPlusUberShaderParameters(),
            [.. textureList],
            vert,
            vertOvr,
            vertNv,
            frag)
        {
            RenderPass = (int)EDefaultRenderPass.OpaqueForward,
            RenderOptions = ModelImporter.CreateForwardPlusUberShaderRenderOptions(),
            Name = $"UberPreview_{(config.EnableEmission ? "Emission" : "Base")}_{(config.EnableMatcap ? "Matcap" : "NoMatcap")}",
        };

        material.SetVector4("_Color", new Vector4(config.Tint.R, config.Tint.G, config.Tint.B, config.Tint.A));
        material.SetFloat("_BumpScale", 0.4f);
        material.SetFloat("_EnableEmission", 1.0f);
        material.SetVector4(
            "_EmissionColor",
            config.EnableEmission
                ? new Vector4(1.0f, 0.7f, 0.2f, 1.0f)
                : new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
        material.SetFloat("_EmissionStrength", config.EnableEmission ? 2.5f : 0.35f);

        material.SetFloat("_MatcapEnable", config.EnableMatcap ? 1.0f : 0.0f);
        material.SetVector4("_MatcapColor", new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
        material.SetFloat("_MatcapIntensity", 1.25f);
        material.SetFloat("_MatcapReplace", 0.0f);
        material.SetFloat("_MatcapMultiply", 0.85f);
        material.SetFloat("_MatcapAdd", 0.0f);
        material.SetFloat("_MatcapLightMask", 0.0f);

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

    private static XRTexture2D CreateUberPreviewTexture(XRTexture2D source, string samplerName)
    {
        if (!string.IsNullOrWhiteSpace(source.FilePath))
            return ModelImporter.GetOrCreateUberSamplerTexture(source.FilePath!, samplerName);

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

        t.Resizable = source.Resizable;
        t.InternalCompression = source.InternalCompression;
        t.AlphaAsTransparency = source.AlphaAsTransparency;

        if (source.Mipmaps is { Length: > 0 } mipmaps)
        {
            var copies = new Mipmap2D[mipmaps.Length];
            for (int i = 0; i < mipmaps.Length; i++)
                copies[i] = mipmaps[i].Clone(cloneImage: true);
            t.Mipmaps = copies;
        }

        return t;
    }

    /// <summary>
    /// Adds a ground-plane reference grid to the Uber shader test world using DebugDrawComponent.
    /// Lines are static scene shapes (created once, not per-frame), so they do not bloat the mesh queue.
    /// </summary>
    private static void AddUberShaderReferenceGrid(SceneNode rootNode)
    {
        var gridNode = rootNode.NewChild("ReferenceGrid");
        var debug = gridNode.AddComponent<DebugDrawComponent>()!;

        const float extent = 25.0f;
        const float step = 5.0f;

        for (float x = -extent; x <= extent; x += step)
            debug.AddLine(new Vector3(x, 0.0f, -extent), new Vector3(x, 0.0f, extent), x == 0.0f ? ColorF4.White : ColorF4.Gray);

        for (float z = -extent; z <= extent; z += step)
            debug.AddLine(new Vector3(-extent, 0.0f, z), new Vector3(extent, 0.0f, z), z == 0.0f ? ColorF4.White : ColorF4.Gray);

        // Vertical Y-axis indicator
        debug.AddLine(Vector3.Zero, new Vector3(0.0f, 4.0f, 0.0f), ColorF4.LightGold);
    }
}
