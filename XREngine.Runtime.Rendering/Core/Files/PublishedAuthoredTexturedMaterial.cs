using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Additive cook-only carrier for canonical forward normal/height and specular-map
/// surfaces. Existing material carriers and standalone texture bytes are unchanged.
/// </summary>
public sealed class PublishedAuthoredTexturedMaterial : XRMaterial, ICookedBinarySerializable
{
    private const string ReflectionWarning = "Cooked material serialization uses the existing texture and render-options contracts.";

    [RequiresUnreferencedCode(ReflectionWarning), RequiresDynamicCode(ReflectionWarning)]
    public void WriteCookedBinary(CookedBinaryWriter writer)
    {
        AuthoredTexturedSurface surface = ReadSurface();
        (XRTexture2D[] textures, int[] roles) = DescribeTextures(surface);
        writer.Write(1);
        writer.Write((byte)surface.TextureFlags);
        writer.WriteValue(ID);
        writer.WriteValue(Name);
        writer.Write(surface.Values.Specular);
        writer.Write(surface.MatShininess);
        writer.Write(surface.Values.Roughness);
        writer.Write(surface.Values.Metallic);
        writer.Write(surface.Values.Emission);
        writer.Write(surface.Values.AlphaCutoff);
        if (surface.Normal is not null)
        {
            writer.Write(surface.NormalMapMode);
            writer.Write(surface.HeightMapScale);
        }
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
        foreach (int role in roles) writer.Write(role);
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
        AuthoredTexturedSurface surface = ReadSurface();
        (XRTexture2D[] textures, int[] roles) = DescribeTextures(surface);
        long size = sizeof(int) + sizeof(byte) + CookedBinarySerializer.CalculateSize(ID) + CookedBinarySerializer.CalculateSize(Name)
            + 6 * sizeof(float) + (surface.Normal is null ? 0 : sizeof(int) + sizeof(float))
            + sizeof(int) + CookedBinarySerializer.CalculateSize(RenderOptions) + sizeof(int)
            + sizeof(byte) + roles.Length * sizeof(int) + sizeof(bool) + sizeof(byte);
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
        int flags = reader.ReadByte();
        if (flags is not (1 or 2 or 3 or 5 or 6 or 7)) throw Invalid("unsupported texture-role family");
        Guid id = reader.ReadValue<Guid>();
        string? name = reader.ReadValue<string>();
        float specular = reader.ReadSingle(), shininess = reader.ReadSingle(), roughness = reader.ReadSingle();
        float metallic = reader.ReadSingle(), emission = reader.ReadSingle(), cutoff = reader.ReadSingle();
        int normalMode = (flags & 1) == 0 ? 0 : reader.ReadInt32();
        float heightScale = (flags & 1) == 0 ? 1 : reader.ReadSingle();
        ETransparencyMode mode = (ETransparencyMode)reader.ReadInt32();
        if ((flags & 4) == 0 ? mode is not (ETransparencyMode.Opaque or ETransparencyMode.AlphaBlend)
            : mode is not (ETransparencyMode.Masked or ETransparencyMode.AlphaBlend))
            throw Invalid("unsupported coverage mode for the texture-role family");
        RenderingParameters options = reader.ReadValue<RenderingParameters>() ?? throw Invalid("render options are missing");
        int priority = reader.ReadInt32();
        int roleCount = 1 + ((flags & 1) == 0 ? 0 : 1) + ((flags & 2) == 0 ? 0 : 1) + ((flags & 4) == 0 ? 0 : 1);
        int count = reader.ReadByte();
        if (count < 1 || count > roleCount) throw Invalid("the unique image table must contain one image through the number of roles");
        XRTexture2D[] textures = new XRTexture2D[count];
        for (int index = 0; index < count; index++)
        {
            textures[index] = reader.ReadValue<XRTexture2D>() ?? throw Invalid("a texture is missing or has the wrong type");
            PublishedStandardLitTextureSettings.Read(reader).ApplyTo(textures[index]);
            _ = PublishedStandardLitTextureSettings.Capture(textures[index]);
            for (int previous = 0; previous < index; previous++)
                if (textures[previous].ID == textures[index].ID) throw Invalid("the unique image table repeats a persistent identity");
        }
        int[] roles = new int[roleCount];
        int nextUnique = 0;
        for (int index = 0; index < roles.Length; index++)
        {
            int role = roles[index] = reader.ReadInt32();
            if (role < 0 || role >= count || role > nextUnique) throw Invalid("image roles require canonical first-use order within the unique table");
            if (role == nextUnique) nextUnique++;
        }
        if (nextUnique != count) throw Invalid("the unique image table contains an unreferenced image");
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
        // Coverage setters can synchronize parameters; install exact shader inputs afterward.
        AlphaCutoff = cutoff;
        TransparencyMode = mode;
        List<ShaderVar> parameters =
        [
            new ShaderFloat(specular, "MatSpecularIntensity"), new ShaderFloat(shininess, "MatShininess"),
            new ShaderFloat(roughness, "Roughness"), new ShaderFloat(metallic, "Metallic"), new ShaderFloat(emission, "Emission"),
        ];
        if ((flags & 4) != 0) parameters.Add(new ShaderFloat(cutoff, "AlphaCutoff"));
        if ((flags & 1) != 0)
        {
            parameters.Add(new ShaderInt(normalMode, "NormalMapMode"));
            parameters.Add(new ShaderFloat(heightScale, "HeightMapScale"));
        }
        Parameters = [.. parameters];
        XRTexture?[] textureSlots = new XRTexture?[roleCount];
        MaterialSurfaceTextureBinding[] bindings = new MaterialSurfaceTextureBinding[roleCount];
        int slot = 0;
        AddRole(EMaterialTextureSemantic.BaseColor, baseColorSrgb);
        if ((flags & 1) != 0) AddRole(EMaterialTextureSemantic.Normal);
        if ((flags & 2) != 0) AddRole(EMaterialTextureSemantic.Specular);
        if ((flags & 4) != 0) AddRole(EMaterialTextureSemantic.Opacity);
        Textures = [.. textureSlots];
        SurfaceTextureBindings = bindings;
        RenderOptions = options;
        RenderPass = (int)(mode == ETransparencyMode.Opaque ? EDefaultRenderPass.OpaqueForward
            : mode == ETransparencyMode.Masked ? EDefaultRenderPass.MaskedForward : EDefaultRenderPass.TransparentForward);
        TransparentSortPriority = priority;
        Name = name;
        AdoptPersistentID(id);
        Shaders = [.. stages];
        EngineSemantic = EngineMaterialSemanticIdentity.AuthoredLitTexturedV1;
        _ = ReadSurface();

        void AddRole(EMaterialTextureSemantic semantic, bool isSrgb = false)
        {
            XRTexture2D texture = textures[roles[slot]];
            textureSlots[slot] = texture;
            bindings[slot++] = new(semantic, texture, IsSrgb: isSrgb, WrapU: texture.UWrap, WrapV: texture.VWrap);
        }
    }

    private AuthoredTexturedSurface ReadSurface()
    {
        if (!AuthoredTexturedSurfaceBinding.TryRead(this, out AuthoredTexturedSurface surface, out string? reason))
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

    private static (XRTexture2D[] Textures, int[] Roles) DescribeTextures(in AuthoredTexturedSurface surface)
    {
        List<XRTexture2D> textures = [];
        List<int> roles = [];
        AddRole(surface.BaseColor);
        if (surface.Normal is not null) AddRole(surface.Normal);
        if (surface.Specular is not null) AddRole(surface.Specular);
        if (surface.Opacity is not null) AddRole(surface.Opacity);
        return ([.. textures], [.. roles]);

        void AddRole(MaterialSurfaceTextureBinding binding)
        {
            XRTexture2D image = (XRTexture2D)binding.Texture;
            for (int index = 0; index < textures.Count; index++)
            {
                if (ReferenceEquals(textures[index], image)) { roles.Add(index); return; }
                if (textures[index].ID == image.ID) throw Invalid("distinct images share one persistent identity");
            }
            roles.Add(textures.Count);
            textures.Add(image);
        }
    }

    private static InvalidDataException Invalid(string reason) => new($"CookedMaterial.AuthoredTexturedInvalid: {reason}.");
}
