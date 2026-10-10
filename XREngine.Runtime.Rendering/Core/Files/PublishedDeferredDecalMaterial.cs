using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Versioned browser material for the engine's default texture-alpha deferred decal.
/// Projection borrows the authored image; decode applies its extra authored settings
/// only to the newly decoded image owned by this payload.
/// </summary>
public sealed class PublishedDeferredDecalMaterial : XRMaterial, ICookedBinarySerializable
{
    private const string ReflectionWarning = "Cooked deferred decal serialization uses the existing cooked texture and render-options contracts.";

    [RequiresUnreferencedCode(ReflectionWarning), RequiresDynamicCode(ReflectionWarning)]
    public void WriteCookedBinary(CookedBinaryWriter writer)
    {
        XRTexture2D image = ReadImage();
        PublishedStandardLitTextureSettings settings = PublishedStandardLitTextureSettings.Capture(image);
        writer.Write(1);
        writer.WriteValue(ID);
        writer.WriteValue(Name);
        writer.WriteValue(RenderOptions);
        writer.Write(RenderPass);
        writer.Write(TransparentSortPriority);
        writer.WriteValue(image);
        settings.Write(writer);
    }

    [RequiresUnreferencedCode(ReflectionWarning), RequiresDynamicCode(ReflectionWarning)]
    public long CalculateCookedBinarySize()
    {
        XRTexture2D image = ReadImage();
        _ = PublishedStandardLitTextureSettings.Capture(image);
        return checked(3 * sizeof(int) + CookedBinarySerializer.CalculateSize(ID)
            + CookedBinarySerializer.CalculateSize(Name)
            + CookedBinarySerializer.CalculateSize(RenderOptions)
            + CookedBinarySerializer.CalculateSize(image)
            + PublishedStandardLitTextureSettings.SerializedSize);
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
        RenderingParameters options = reader.ReadValue<RenderingParameters>() ?? throw Invalid("render options are missing");
        int renderPass = reader.ReadInt32();
        int priority = reader.ReadInt32();
        // A custom-object payload has an independent cooked reference scope, so
        // this image cannot alias an earlier or borrowed object in the world graph.
        XRTexture2D image = reader.ReadValue<XRTexture2D>() ?? throw Invalid("the decal image is missing or has the wrong type");
        PublishedStandardLitTextureSettings.Read(reader).ApplyTo(image);
        _ = PublishedStandardLitTextureSettings.Capture(image);
        Textures = [null, null, null, null, image];
        RenderOptions = options;
        RenderPass = renderPass;
        TransparentSortPriority = priority;
        Name = name;
        AdoptPersistentID(id);
        _ = ReadImage();
    }

    private XRTexture2D ReadImage()
    {
        if (!DeferredDecalMaterialContract.TryRead(this, out XRTexture2D? image, out string reason) || image is null)
            throw Invalid(reason);
        return image;
    }

    private static InvalidDataException Invalid(string reason)
        => new($"CookedMaterial.DeferredDecalInvalid: {reason}.");
}
