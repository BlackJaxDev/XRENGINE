using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using XREngine.Core.Files;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;

namespace XREngine.Rendering;

/// <summary>
/// Versioned browser payload for the canonical UI image material. The image is
/// written once; its material role and imported image settings remain independent.
/// Projection borrows authored images, while decode restores settings only onto
/// the newly decoded image owned by this payload.
/// </summary>
public sealed class PublishedUiImageMaterial : XRMaterial, ICookedBinarySerializable
{
    private const string ReflectionWarning = "Cooked UI image serialization uses the existing cooked texture and render-options contracts.";

    [RequiresUnreferencedCode(ReflectionWarning), RequiresDynamicCode(ReflectionWarning)]
    public void WriteCookedBinary(CookedBinaryWriter writer)
    {
        (XRTexture2D image, Vector4 tint, bool srgb) = ReadSurface();
        PublishedStandardLitTextureSettings settings = PublishedStandardLitTextureSettings.Capture(image);
        writer.Write(1);
        writer.WriteValue(ID);
        writer.WriteValue(Name);
        writer.Write(tint.X);
        writer.Write(tint.Y);
        writer.Write(tint.Z);
        writer.Write(tint.W);
        writer.WriteValue(RenderOptions);
        writer.Write(RenderPass);
        writer.Write(TransparentSortPriority);
        writer.WriteValue(image);
        settings.Write(writer);
        writer.Write(srgb);
    }

    [RequiresUnreferencedCode(ReflectionWarning), RequiresDynamicCode(ReflectionWarning)]
    public long CalculateCookedBinarySize()
    {
        (XRTexture2D image, _, _) = ReadSurface();
        _ = PublishedStandardLitTextureSettings.Capture(image);
        return checked(3 * sizeof(int) + CookedBinarySerializer.CalculateSize(ID)
            + CookedBinarySerializer.CalculateSize(Name) + 4 * sizeof(float)
            + CookedBinarySerializer.CalculateSize(RenderOptions)
            + CookedBinarySerializer.CalculateSize(image)
            + PublishedStandardLitTextureSettings.SerializedSize + sizeof(bool));
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
        Vector4 tint = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        RenderingParameters options = reader.ReadValue<RenderingParameters>() ?? throw Invalid("render options are missing");
        int renderPass = reader.ReadInt32();
        int priority = reader.ReadInt32();
        // The cooked custom-object decoder enters an independent reference scope
        // for this payload. An outer graph image/backreference cannot reach this
        // read, so applying settings cannot mutate a borrowed or earlier image.
        XRTexture2D image = reader.ReadValue<XRTexture2D>() ?? throw Invalid("the image is missing or has the wrong type");
        PublishedStandardLitTextureSettings.Read(reader).ApplyTo(image);
        _ = PublishedStandardLitTextureSettings.Capture(image);
        bool srgb = reader.ReadBoolean();
        Parameters = [new ShaderVector4(tint, "MatColor")];
        Textures = [image];
        SurfaceTextureBindings = [new(EMaterialTextureSemantic.BaseColor, image,
            IsSrgb: srgb, WrapU: image.UWrap, WrapV: image.VWrap)];
        RenderOptions = options;
        RenderPass = renderPass;
        TransparentSortPriority = priority;
        Name = name;
        AdoptPersistentID(id);
        EngineSemantic = EngineMaterialSemanticIdentity.UIQuadBatchedTextureV1;
        _ = ReadSurface();
    }

    private (XRTexture2D Image, Vector4 Tint, bool IsSrgb) ReadSurface()
    {
        if (EngineSemantic != EngineMaterialSemanticIdentity.UIQuadBatchedTextureV1 || Shaders.Count != 0 ||
            Parameters.Length != 1 || Parameters[0] is not ShaderVector4 { Name: "MatColor" } color ||
            Textures.Count != 1 || Textures[0] is not XRTexture2D image || image.GetType() != typeof(XRTexture2D) ||
            SurfaceTextureBindings.Length != 1 || SurfaceTextureBindings[0] is not { Semantic: EMaterialTextureSemantic.BaseColor } binding ||
            !ReferenceEquals(binding.Texture, image) || binding.TexCoordSet != 0 || binding.Channel != 0 ||
            binding.UvScaleOffset != new Vector4(1, 1, 0, 0) || binding.UvRotation != 0 ||
            binding.WrapU != image.UWrap || binding.WrapV != image.VWrap)
            throw Invalid("the carrier requires the source-free V1 image semantic, MatColor and one canonical BaseColor image role");
        if (!UIMaterialComponent.TryGetWebGpuImageProfile(image, out string? reason))
            throw Invalid(reason ?? "the image profile is unsupported");
        return (image, color.Value, binding.IsSrgb);
    }

    private static InvalidDataException Invalid(string reason)
        => new($"CookedMaterial.UiImageInvalid: {reason}.");
}
