using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.GI.DDGI;

public sealed partial class GpuDdgiGeometryService
{
    private readonly DDGIMaterialTextureCache _materialTextures = new();
    public XRTexture2DArray? MaterialTextures => _materialTextures.Atlas;

    private uint ResolveMaterial(XRMaterial? material)
    {
        if (material is null)
            return 0u;
        if (_materialIndices.TryGetValue(material, out uint index))
            return index;
        MaterialBindingSourceSnapshot snapshot = MaterialBindingSourceEncoder.Encode(material);
        GPUMaterialEntry source = snapshot.Entry;
        bool legacyTextures = material.SurfaceTextureBindings.Length == 0;
        Vector3 baseColor = new(source.BaseColorOpacity.X, source.BaseColorOpacity.Y, source.BaseColorOpacity.Z);
        Vector3 emission = Vector3.Max(material.EmissiveColor ?? baseColor, Vector3.Zero)
            * Math.Max(0, material.EmissionStrength ?? source.RMSE.W);
        ETransparencyMode transparency = material.GetEffectiveTransparencyMode();
        float alphaMode = transparency is ETransparencyMode.Masked or ETransparencyMode.AlphaToCoverage ? 1
            : transparency == ETransparencyMode.Opaque ? 0 : 2;
        DDGIMaterialGpu row = new()
        {
            BaseColorOpacity = source.BaseColorOpacity,
            EmissiveMetallic = new Vector4(emission, Math.Clamp(source.RMSE.Y, 0, 1)),
            Surface = new Vector4(Math.Clamp(source.RMSE.X, 0, 1), material.NormalScale, material.AlphaCutoff, alphaMode),
            Transmission = new Vector4(Vector3.Clamp(material.TransmissionColor, Vector3.Zero, Vector3.One),
                Math.Clamp(material.Transmission, 0, 1)),
            EmissionOptions = new Vector4(material.EmissiveColor is null ? 1 : 0, 0, 0, 0),
            BaseColorMap = ResolveMap(material.GetSurfaceTexture(EMaterialTextureSemantic.BaseColor), legacyTextures ? snapshot.Albedo : null, 0, true),
            OpacityMap = ResolveMap(material.GetSurfaceTexture(EMaterialTextureSemantic.Opacity)),
            NormalMap = ResolveMap(material.GetSurfaceTexture(EMaterialTextureSemantic.Normal), legacyTextures ? snapshot.Normal : null),
            MetallicMap = ResolveMap(material.GetSurfaceTexture(EMaterialTextureSemantic.Metallic), legacyTextures ? snapshot.RM : null, 1),
            RoughnessMap = ResolveMap(material.GetSurfaceTexture(EMaterialTextureSemantic.Roughness), legacyTextures ? snapshot.RM : null),
            EmissiveMap = ResolveMap(material.GetSurfaceTexture(EMaterialTextureSemantic.Emissive)),
            TransmissionMap = ResolveMap(material.GetSurfaceTexture(EMaterialTextureSemantic.Transmission)),
        };
        index = (uint)_materials.Count;
        _materialIndices.Add(material, index);
        _materials.Add(row);
        return index;
    }

    private DDGIMaterialMapGpu ResolveMap(MaterialSurfaceTextureBinding? binding, XRTexture? legacyTexture = null, int channel = 0, bool srgb = false)
    {
        if (binding is null)
        {
            if (legacyTexture is null)
                return DDGIMaterialMapGpu.None;
            XRTexture2D? image = legacyTexture as XRTexture2D;
            bool decodesSrgb = image?.SizedInternalFormat is ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8;
            return new DDGIMaterialMapGpu
            {
                Source = new Vector4(_materialTextures.GetLayer(legacyTexture), 0, channel, srgb && !decodesSrgb ? 1 : 0),
                UvScaleOffset = new Vector4(1, 1, 0, 0),
                WrapRotation = new Vector4((float)(image?.UWrap ?? ETexWrapMode.Repeat), (float)(image?.VWrap ?? ETexWrapMode.Repeat), 0, 1),
            };
        }
        if (binding.TexCoordSet is < 0 or > 1)
            throw new InvalidOperationException("DDGI material sampling supports TEXCOORD_0 and TEXCOORD_1.");
        bool formatDecodesSrgb = binding.Texture is XRTexture2D texture &&
            texture.SizedInternalFormat is ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8;
        return new DDGIMaterialMapGpu
        {
            Source = new Vector4(_materialTextures.GetLayer(binding.Texture), binding.TexCoordSet,
                Math.Clamp(binding.Channel, 0, 3), binding.IsSrgb && !formatDecodesSrgb ? 1 : 0),
            UvScaleOffset = binding.UvScaleOffset,
            WrapRotation = new Vector4((float)binding.WrapU, (float)binding.WrapV,
                MathF.Sin(binding.UvRotation), MathF.Cos(binding.UvRotation)),
        };
    }
}
