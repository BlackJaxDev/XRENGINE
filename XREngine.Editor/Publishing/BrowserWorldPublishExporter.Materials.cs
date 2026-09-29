using ImageMagick;
using System.Numerics;
using XREngine.Browser;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Editor.Publishing;

public sealed partial class BrowserWorldPublishExporter
{
    private readonly Dictionary<XRTexture2D, string> _textures = new(ReferenceEqualityComparer.Instance);

    private void AddMaterial(XRMaterial material, string id, string path)
    {
        if (_materials.Count >= 256)
            throw Unsupported(path, "more than 256 materials");
        string label = $"{path}/material '{material.Name}'";
        if (material.Shaders.Count != 1 || material.FragmentShaders.Count != 1 ||
            material.HasSettingUniformsHandlers || material.HasSettingShadowUniformHandlers ||
            material.BillboardMode != EMeshBillboardMode.None || material.GetEffectiveTransparencyMode().ToString() != "Opaque" ||
            material.RenderPass != (int)EDefaultRenderPass.OpaqueForward)
            throw Unsupported(label, "custom shader stages, runtime uniforms, billboarding or transparency");
        RenderingParameters options = material.RenderOptions;
        if (options.HasBlending || options.StencilTest.IsEnabled || !options.DepthTest.IsEnabled ||
            !options.DepthTest.UpdateDepth || options.DepthTest.Function.ToString() != "Lequal" ||
            options.Winding != EWinding.CounterClockwise || !options.WriteRed || !options.WriteGreen ||
            !options.WriteBlue || !options.WriteAlpha || options.AlphaToCoverage.ToString() == "Enabled")
            throw Unsupported(label, "nonstandard depth, stencil, blending, winding, coverage or color masks");
        string cull = options.CullMode switch
        {
            ECullMode.None => "none", ECullMode.Back => "back", ECullMode.Front => "front",
            _ => throw Unsupported(label, "cull policy"),
        };
        string source = material.FragmentShaders[0].GetResolvedSource();
        Vector4 tint;
        XRTexture2D? texture = null;
        if (source == ShaderHelper.UnlitColorFragForward()!.GetResolvedSource())
        {
            if (material.Parameters.Length != 1 || material.Textures.Count != 0 || material.Parameter<ShaderVector4>("MatColor") is not { } color)
                throw Unsupported(label, "unlit color material requires only the MatColor vec4 parameter");
            tint = color.Value;
        }
        else if (source == ShaderHelper.UnlitTextureFragForward()!.GetResolvedSource())
        {
            if (material.Parameters.Length != 0 || material.Textures.Count != 1 || material.Textures[0] is not XRTexture2D texture2D)
                throw Unsupported(label, "unlit texture material requires exactly one resident 2D texture and no extra parameters");
            tint = Vector4.One;
            texture = texture2D;
        }
        else
            throw Unsupported(label, "shader is not the canonical unlit color or unlit texture forward shader");

        BrowserMaterialData data = BrowserAssetAdapter.FromXRMaterial(material,
            new BrowserMaterialRecipe(material, tint, cullMode: cull, castShadow: false, receiveShadow: false));
        string? textureId = texture is null ? null : AddTexture(texture, label);
        BrowserCookedSamplerDto? sampler = texture is null ? null : Sampler(texture, label);
        WriteAsset(id, "material", new BrowserCookedMaterialDto
        {
            Tint = [data.Tint.X, data.Tint.Y, data.Tint.Z, data.Tint.W], Texture = textureId,
            AlphaMode = data.AlphaMode, Shading = data.Shading, CullMode = data.CullMode,
            AlphaCutoff = data.AlphaCutoff, CastShadow = data.CastShadow, ReceiveShadow = data.ReceiveShadow,
            Sampler = sampler,
        }, BrowserPublishJsonContext.Default.BrowserCookedMaterialDto, textureId is null ? [] : [textureId]);
    }

    private string AddTexture(XRTexture2D texture, string path)
    {
        if (_textures.TryGetValue(texture, out string? id))
            return id;
        if (texture.MultiSample || texture.Rectangle || texture.GrabPass is not null ||
            texture.Width == 0 || texture.Height == 0 || (long)texture.Width * texture.Height * 4 > 4 * 1024 * 1024 ||
            texture.Mipmaps.Length == 0 || !texture.Mipmaps[0].HasData())
            throw Unsupported(path, $"texture '{texture.Name}' must be resident, bounded, ordinary 2D color data");
        string format = texture.SizedInternalFormat switch
        {
            ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8 => "rgba8unorm-srgb",
            ESizedInternalFormat.Rgb8 or ESizedInternalFormat.Rgba8 => "rgba8unorm",
            _ => throw Unsupported(path, $"texture '{texture.Name}' format '{texture.SizedInternalFormat}' requires an explicit browser encoder"),
        };
        int mipCount = 1;
        for (uint size = Math.Max(texture.Width, texture.Height); size > 1; size >>= 1) mipCount++;
        Mipmap2D[] nativeMips;
        if (texture.Mipmaps.Length == mipCount)
            nativeMips = texture.Mipmaps;
        else
        {
            if (texture.Mipmaps.Length != 1 || (!texture.AutoGenerateMipmaps && texture.MinFilter is not (ETexMinFilter.Nearest or ETexMinFilter.Linear)))
                throw Unsupported(path, $"texture '{texture.Name}' has an incomplete authored mip chain");
            using MagickImage image = texture.Mipmaps[0].GetImage();
            nativeMips = XRTexture2D.GetMipmapsFromImage(image);
        }
        List<byte[]> mips = [];
        int total = 0;
        try
        {
            for (int mip = 0; mip < nativeMips.Length; mip++)
            {
                _cancellation.ThrowIfCancellationRequested();
                Mipmap2D native = nativeMips[mip];
                if (native.Width != Math.Max(1u, texture.Width >> mip) || native.Height != Math.Max(1u, texture.Height >> mip) || !native.HasData())
                    throw Unsupported(path, $"texture '{texture.Name}' mip {mip} is incomplete");
                using MagickImage image = native.GetImage();
                using var pixels = image.GetPixels();
                byte[] bytes = pixels.ToByteArray(PixelMapping.RGBA) ?? throw Unsupported(path, "texture RGBA conversion failed");
                total = checked(total + bytes.Length);
                if (total > 4 * 1024 * 1024)
                    throw Unsupported(path, "texture mip payload exceeds 4 MiB");
                mips.Add(bytes);
            }
        }
        finally
        {
            if (!ReferenceEquals(nativeMips, texture.Mipmaps))
                foreach (Mipmap2D generated in nativeMips)
                    generated.Data?.Dispose();
        }
        BrowserTextureData data = new(checked((int)texture.Width), checked((int)texture.Height), format, mips);
        id = $"texture-{_textures.Count}";
        WritePayload(id + ".bin", data.CopyPackedBytes(), 4 * 1024 * 1024);
        _assets.Add((id, "texture", id + ".bin", [], new BrowserCookedTextureDto
        {
            Width = data.Width, Height = data.Height, Format = data.Format,
            MipByteLengths = mips.Select(x => x.Length).ToArray(), NormalConvention = "none", AlphaMode = "straight",
        }));
        _textures.Add(texture, id);
        return id;
    }

    private static BrowserCookedSamplerDto Sampler(XRTexture2D texture, string path)
    {
        if (texture.EnableComparison || texture.LodBias != 0 || texture.LargestMipmapLevel != 0 || texture.MinLOD > 0 ||
            texture.MaxLOD < 0 || texture.SmallestAllowedMipmapLevel < 0 || !float.IsFinite(texture.MaxAnisotropy) ||
            texture.MaxAnisotropy != MathF.Truncate(texture.MaxAnisotropy))
            throw Unsupported(path, "comparison sampling, LOD bias or fractional anisotropy");
        (string min, string mip, float maximumLod) = texture.MinFilter switch
        {
            ETexMinFilter.Nearest => ("nearest", "nearest", 0), ETexMinFilter.Linear => ("linear", "nearest", 0),
            ETexMinFilter.NearestMipmapNearest => ("nearest", "nearest", 32), ETexMinFilter.LinearMipmapNearest => ("linear", "nearest", 32),
            ETexMinFilter.NearestMipmapLinear => ("nearest", "linear", 32), ETexMinFilter.LinearMipmapLinear => ("linear", "linear", 32),
            _ => throw Unsupported(path, "texture minification filter"),
        };
        if (maximumLod > 0)
            maximumLod = Math.Min(maximumLod, Math.Min(texture.MaxLOD, texture.SmallestAllowedMipmapLevel));
        string mag = texture.MagFilter switch { ETexMagFilter.Nearest => "nearest", ETexMagFilter.Linear => "linear", _ => throw Unsupported(path, "texture magnification filter") };
        BrowserSamplerDescription data;
        try { data = new(Address(texture.UWrap, path), Address(texture.VWrap, path), min, mag, mip, "", maximumLod, checked((int)texture.MaxAnisotropy)); }
        catch (ArgumentException ex) { throw Unsupported(path, ex.Message); }
        return new BrowserCookedSamplerDto { AddressModeU = data.AddressU, AddressModeV = data.AddressV,
            MinFilter = data.MinFilter, MagFilter = data.MagFilter, MipmapFilter = data.MipmapFilter,
            LodMaxClamp = data.LodMaxClamp, MaxAnisotropy = data.MaxAnisotropy };
    }

    private static string Address(ETexWrapMode mode, string path) => mode switch
    {
        ETexWrapMode.ClampToEdge => "clamp-to-edge", ETexWrapMode.Repeat => "repeat", ETexWrapMode.MirroredRepeat => "mirror-repeat",
        _ => throw Unsupported(path, "texture border addressing"),
    };
}
