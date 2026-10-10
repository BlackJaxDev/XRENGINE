using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Cook-only carrier for the explicit two-image forward alpha surface. The existing
/// opaque material and raw texture codecs keep their original payloads unchanged.
/// </summary>
public sealed class PublishedTexturedAlphaMaterial : XRMaterial, ICookedBinarySerializable
{
    private const string ReflectionWarning = "Cooked material serialization uses the existing texture and render-options contracts.";

    [RequiresUnreferencedCode(ReflectionWarning), RequiresDynamicCode(ReflectionWarning)]
    public void WriteCookedBinary(CookedBinaryWriter writer)
    {
        TexturedAlphaSurface surface = ReadSurface();
        (XRTexture2D[] textures, int opacityRole) = DescribeTextures(surface);
        writer.Write(1);
        writer.WriteValue(ID);
        writer.WriteValue(Name);
        writer.Write(surface.Values.Specular);
        writer.Write(surface.MatShininess);
        writer.Write(surface.Values.AlphaCutoff);
        writer.Write(surface.Values.Roughness);
        writer.Write(surface.Values.Metallic);
        writer.Write(surface.Values.Emission);
        writer.Write((int)surface.Values.TransparencyMode);
        writer.WriteValue(RenderOptions);
        writer.Write(TransparentSortPriority);
        writer.Write((byte)textures.Length);
        foreach (XRTexture2D texture in textures)
        {
            PublishedStandardLitTextureSettings settings = PublishedStandardLitTextureSettings.Capture(texture);
            writer.WriteValue(texture);
            settings.Write(writer);
        }
        writer.Write(opacityRole);
        writer.Write(surface.BaseColor.IsSrgb);
        writer.Write((byte)Shaders.Count);
        foreach (XRShader shader in Shaders)
        {
            writer.Write((byte)shader.Type);
            writer.WriteValue(shader.CookedArtifactIdentity);
        }
    }

    [RequiresUnreferencedCode(ReflectionWarning), RequiresDynamicCode(ReflectionWarning)]
    public long CalculateCookedBinarySize()
    {
        TexturedAlphaSurface surface = ReadSurface();
        (XRTexture2D[] textures, _) = DescribeTextures(surface);
        long size = sizeof(int) + CookedBinarySerializer.CalculateSize(ID) + CookedBinarySerializer.CalculateSize(Name)
            + 6 * sizeof(float) + sizeof(int) + CookedBinarySerializer.CalculateSize(RenderOptions) + sizeof(int)
            + sizeof(byte) + sizeof(int) + sizeof(bool) + sizeof(byte);
        foreach (XRTexture2D texture in textures)
        {
            _ = PublishedStandardLitTextureSettings.Capture(texture);
            size = checked(size + CookedBinarySerializer.CalculateSize(texture) + PublishedStandardLitTextureSettings.SerializedSize);
        }
        foreach (XRShader shader in Shaders)
            size = checked(size + sizeof(byte) + CookedBinarySerializer.CalculateSize(shader.CookedArtifactIdentity));
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
        float specular = reader.ReadSingle(), shininess = reader.ReadSingle(), cutoff = reader.ReadSingle();
        float roughness = reader.ReadSingle(), metallic = reader.ReadSingle(), emission = reader.ReadSingle();
        ETransparencyMode mode = (ETransparencyMode)reader.ReadInt32();
        if (mode is not (ETransparencyMode.Masked or ETransparencyMode.AlphaBlend)) throw Invalid("unsupported coverage mode");
        RenderingParameters options = reader.ReadValue<RenderingParameters>() ?? throw Invalid("render options are missing");
        int priority = reader.ReadInt32();
        int count = reader.ReadByte();
        if (count is < 1 or > 2) throw Invalid("the unique image table must contain one or two images");
        XRTexture2D[] textures = new XRTexture2D[count];
        for (int index = 0; index < count; index++)
        {
            textures[index] = reader.ReadValue<XRTexture2D>() ?? throw Invalid("a texture is missing or has the wrong type");
            PublishedStandardLitTextureSettings.Read(reader).ApplyTo(textures[index]);
            _ = PublishedStandardLitTextureSettings.Capture(textures[index]);
            if (index == 1 && textures[0].ID == textures[1].ID) throw Invalid("the unique image table repeats a persistent identity");
        }
        int opacityRole = reader.ReadInt32();
        if (opacityRole != count - 1) throw Invalid("the opacity role must reference the sole image or the second unique image");
        bool baseColorSrgb = reader.ReadBoolean();
        int stageCount = reader.ReadByte();
        if (stageCount is < 1 or > 2) throw Invalid("one fragment and at most one vertex stage are required");
        XRShader[] stages = new XRShader[stageCount];
        string? identity = null;
        int fragments = 0;
        for (int index = 0; index < stages.Length; index++)
        {
            EShaderType type = (EShaderType)reader.ReadByte();
            string stageIdentity = reader.ReadValue<string>() ?? throw Invalid("a cooked descriptor identity is missing");
            if (type is not (EShaderType.Vertex or EShaderType.Fragment) || identity is not null && identity != stageIdentity)
                throw Invalid("stage types or whole-program identities disagree");
            identity = stageIdentity;
            if (type == EShaderType.Fragment) fragments++;
            stages[index] = new XRShader(type) { CookedArtifactIdentity = stageIdentity };
        }
        if (fragments != 1) throw Invalid("exactly one fragment stage is required");
        // Set coverage before parameter objects: setters synchronize the cutoff.
        AlphaCutoff = cutoff;
        TransparencyMode = mode;
        Parameters =
        [
            new ShaderFloat(specular, "MatSpecularIntensity"), new ShaderFloat(shininess, "MatShininess"),
            new ShaderFloat(cutoff, "AlphaCutoff"), new ShaderFloat(roughness, "Roughness"),
            new ShaderFloat(metallic, "Metallic"), new ShaderFloat(emission, "Emission"),
        ];
        XRTexture2D baseColor = textures[0], opacity = textures[opacityRole];
        Textures = [baseColor, opacity];
        SurfaceTextureBindings =
        [
            new(EMaterialTextureSemantic.BaseColor, baseColor, IsSrgb: baseColorSrgb, WrapU: baseColor.UWrap, WrapV: baseColor.VWrap),
            new(EMaterialTextureSemantic.Opacity, opacity, WrapU: opacity.UWrap, WrapV: opacity.VWrap),
        ];
        RenderOptions = options;
        RenderPass = (int)(mode == ETransparencyMode.Masked ? EDefaultRenderPass.MaskedForward : EDefaultRenderPass.TransparentForward);
        TransparentSortPriority = priority;
        Name = name;
        AdoptPersistentID(id);
        Shaders = [.. stages];
        EngineSemantic = EngineMaterialSemanticIdentity.AuthoredLitTextureAlphaV1;
        _ = ReadSurface();
    }

    private TexturedAlphaSurface ReadSurface()
    {
        if (!TexturedAlphaSurfaceBinding.TryRead(this, out TexturedAlphaSurface surface, out string? reason))
            throw Invalid(reason ?? "the surface schema is invalid");
        int fragments = 0;
        string? identity = null;
        foreach (XRShader shader in Shaders)
        {
            if (shader.Type is not (EShaderType.Vertex or EShaderType.Fragment) ||
                string.IsNullOrWhiteSpace(shader.CookedArtifactIdentity) ||
                !string.IsNullOrWhiteSpace(shader.Source?.Text) || !string.IsNullOrWhiteSpace(shader.Source?.FilePath) ||
                identity is not null && identity != shader.CookedArtifactIdentity)
                throw Invalid("every retained vertex/fragment stage requires the same exact cooked descriptor identity");
            identity = shader.CookedArtifactIdentity;
            if (shader.Type == EShaderType.Fragment) fragments++;
        }
        if (fragments != 1) throw Invalid("exactly one fragment stage is required");
        return surface;
    }

    private static (XRTexture2D[] Textures, int OpacityRole) DescribeTextures(in TexturedAlphaSurface surface)
    {
        XRTexture2D baseColor = (XRTexture2D)surface.BaseColor.Texture, opacity = (XRTexture2D)surface.Opacity.Texture;
        if (ReferenceEquals(baseColor, opacity)) return ([baseColor], 0);
        if (baseColor.ID == opacity.ID) throw Invalid("distinct images share one persistent identity");
        return ([baseColor, opacity], 1);
    }

    private static InvalidDataException Invalid(string reason) => new($"CookedMaterial.TexturedAlphaInvalid: {reason}.");
}
