using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Source-free authored material with a bounded typed payload. Images are serialized once;
/// both semantic roles and legacy sampler slots are reconstructed from those same objects.
/// This carrier does not change ordinary desktop XRMaterial serialization.
/// </summary>
public sealed class PublishedStandardLitTextureMaterial : XRMaterial, ICookedBinarySerializable
{
    private const string ReflectionWarning = "Cooked material serialization uses the existing cooked texture and render-options contracts.";

    [RequiresUnreferencedCode(ReflectionWarning), RequiresDynamicCode(ReflectionWarning)]
    public void WriteCookedBinary(CookedBinaryWriter writer)
    {
        StandardLitTextureSurface surface = ReadSurface();
        (XRTexture2D[] textures, int[] roles) = DescribeTextures(surface);
        writer.Write(1);
        writer.WriteValue(ID);
        writer.WriteValue(Name);
        writer.Write(surface.Values.BaseColor.X);
        writer.Write(surface.Values.BaseColor.Y);
        writer.Write(surface.Values.BaseColor.Z);
        writer.Write(surface.Values.Specular);
        writer.Write(surface.Values.Roughness);
        writer.Write(surface.Values.Metallic);
        writer.Write(surface.Values.Emission);
        writer.WriteValue(RenderOptions);
        writer.Write(TransparentSortPriority);
        writer.Write((byte)textures.Length);
        foreach (XRTexture2D texture in textures)
        {
            PublishedStandardLitTextureSettings settings = PublishedStandardLitTextureSettings.Capture(texture);
            writer.WriteValue(texture);
            settings.Write(writer);
        }
        foreach (int role in roles) writer.Write(role);
        writer.Write(surface.BaseColor.IsSrgb);
    }

    [RequiresUnreferencedCode(ReflectionWarning), RequiresDynamicCode(ReflectionWarning)]
    public long CalculateCookedBinarySize()
    {
        StandardLitTextureSurface surface = ReadSurface();
        (XRTexture2D[] textures, _) = DescribeTextures(surface);
        long size = sizeof(int) + CookedBinarySerializer.CalculateSize(ID) + CookedBinarySerializer.CalculateSize(Name)
            + 7 * sizeof(float) + CookedBinarySerializer.CalculateSize(RenderOptions) + sizeof(int)
            + sizeof(byte) + 4 * sizeof(int) + sizeof(bool);
        foreach (XRTexture2D texture in textures)
        {
            _ = PublishedStandardLitTextureSettings.Capture(texture);
            size = checked(size + CookedBinarySerializer.CalculateSize(texture) + PublishedStandardLitTextureSettings.SerializedSize);
        }
        return size;
    }

    [RequiresUnreferencedCode(ReflectionWarning), RequiresDynamicCode(ReflectionWarning)]
    public void ReadCookedBinary(CookedBinaryReader reader)
    {
        if (Parameters.Length != 0 || Textures.Count != 0 || Shaders.Count != 0 || SurfaceTextureBindings.Length != 0)
            throw Invalid("decode requires a fresh material");
        using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
        try
        {
            ReadPayload(reader);
            publication.Complete();
        }
        catch
        {
            Parameters = [];
            Textures = [];
            SurfaceTextureBindings = [];
            Destroy(now: true);
            throw;
        }
    }

    [RequiresUnreferencedCode(ReflectionWarning), RequiresDynamicCode(ReflectionWarning)]
    private void ReadPayload(CookedBinaryReader reader)
    {
        if (reader.ReadInt32() != 1) throw Invalid("unsupported payload version");
        Guid id = reader.ReadValue<Guid>();
        string? name = reader.ReadValue<string>();
        Vector3 color = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        float specular = reader.ReadSingle(), roughness = reader.ReadSingle(), metallic = reader.ReadSingle(), emission = reader.ReadSingle();
        RenderingParameters options = reader.ReadValue<RenderingParameters>() ?? throw Invalid("render options are missing");
        int priority = reader.ReadInt32();
        int textureCount = reader.ReadByte();
        if (textureCount is < 1 or > 4) throw Invalid("texture count must be one through four");
        XRTexture2D[] textures = new XRTexture2D[textureCount];
        for (int index = 0; index < textureCount; index++)
        {
            textures[index] = reader.ReadValue<XRTexture2D>() ?? throw Invalid("a texture is missing or has the wrong type");
            PublishedStandardLitTextureSettings.Read(reader).ApplyTo(textures[index]);
            _ = PublishedStandardLitTextureSettings.Capture(textures[index]);
            for (int previous = 0; previous < index; previous++)
                if (textures[index].ID == textures[previous].ID)
                    throw Invalid("the unique texture table contains a duplicate persistent identity");
        }
        Span<int> roles = stackalloc int[4];
        int used = 0;
        for (int index = 0; index < roles.Length; index++)
        {
            int role = roles[index] = reader.ReadInt32();
            if (role < (index == 0 ? 0 : -1) || role >= textureCount) throw Invalid("a texture role is outside the unique table");
            if (role >= 0) used |= 1 << role;
        }
        if (used != (1 << textureCount) - 1) throw Invalid("the unique texture table contains an unreferenced image");
        bool baseColorSrgb = reader.ReadBoolean();
        XRTexture2D albedo = textures[roles[0]];
        XRTexture2D? normalMap = roles[1] < 0 ? null : textures[roles[1]];
        XRTexture2D? metallicMap = roles[2] < 0 ? null : textures[roles[2]];
        XRTexture2D? roughnessMap = roles[3] < 0 ? null : textures[roles[3]];
        Parameters =
        [
            new ShaderVector3(color, "BaseColor"), new ShaderFloat(1, "Opacity"),
            new ShaderFloat(specular, "Specular"), new ShaderFloat(roughness, "Roughness"),
            new ShaderFloat(metallic, "Metallic"), new ShaderFloat(emission, "Emission"),
        ];
        if (normalMap is not null)
            Parameters = [.. Parameters, new ShaderInt(0, "NormalMapMode"), new ShaderFloat(0, "HeightMapScale")];
        Textures = roughnessMap is not null
            ? normalMap is not null || metallicMap is not null ? [albedo, normalMap, metallicMap, roughnessMap] : [albedo, null, roughnessMap]
            : metallicMap is not null ? [albedo, normalMap, metallicMap]
            : normalMap is not null ? [albedo, normalMap] : [albedo];
        List<MaterialSurfaceTextureBinding> bindings = [Binding(EMaterialTextureSemantic.BaseColor, albedo, baseColorSrgb)];
        if (normalMap is not null) bindings.Add(Binding(EMaterialTextureSemantic.Normal, normalMap));
        if (metallicMap is not null) bindings.Add(Binding(EMaterialTextureSemantic.Metallic, metallicMap));
        if (roughnessMap is not null) bindings.Add(Binding(EMaterialTextureSemantic.Roughness, roughnessMap));
        SurfaceTextureBindings = [.. bindings];
        RenderOptions = options;
        RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        TransparentSortPriority = priority;
        Name = name;
        AdoptPersistentID(id);
        EngineSemantic = EngineMaterialSemanticIdentity.StandardLitTextureV1;
        _ = ReadSurface();
    }

    private static MaterialSurfaceTextureBinding Binding(EMaterialTextureSemantic semantic, XRTexture2D texture, bool srgb = false)
        => new(semantic, texture, IsSrgb: srgb, WrapU: texture.UWrap, WrapV: texture.VWrap);

    private StandardLitTextureSurface ReadSurface()
    {
        if (Shaders.Count != 0 || !StandardLitTextureSurfaceBinding.TryRead(this, out StandardLitTextureSurface surface, out _))
            throw Invalid("the carrier must contain an exact source-free opaque texture surface");
        return surface;
    }

    private static (XRTexture2D[] Textures, int[] Roles) DescribeTextures(in StandardLitTextureSurface surface)
    {
        MaterialSurfaceTextureBinding?[] bindings = [surface.BaseColor, surface.Normal, surface.Metallic, surface.Roughness];
        List<XRTexture2D> textures = new(4);
        int[] roles = [-1, -1, -1, -1];
        for (int role = 0; role < bindings.Length; role++)
        {
            if (bindings[role]?.Texture is not XRTexture2D texture) continue;
            int index = -1;
            for (int candidate = 0; candidate < textures.Count; candidate++)
            {
                if (ReferenceEquals(textures[candidate], texture)) { index = candidate; break; }
                if (textures[candidate].ID == texture.ID) throw Invalid("distinct images share one persistent identity");
            }
            if (index < 0) { index = textures.Count; textures.Add(texture); }
            roles[role] = index;
        }
        return ([.. textures], roles);
    }

    private static InvalidDataException Invalid(string reason)
        => new($"CookedMaterial.TextureSurfaceInvalid: {reason}.");
}
