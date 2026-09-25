using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Materials;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Surface texture bindings are immutable records serialized with materials (including world
/// snapshots). YAML round-trip validation requires a parameterless constructor whose members stay
/// at their type defaults, so omitted default values read back exactly.
/// </summary>
[TestFixture]
public sealed class MaterialSurfaceTextureBindingSerializationTests
{
    [Test]
    public void YamlSerializer_RoundTrips_MaterialSurfaceTextureBindings()
    {
        XRTexture2D texture = new(1u, 1u, ColorF4.Black) { Name = "SurfaceBindingTexture" };
        XRMaterial material = new()
        {
            Name = "SurfaceBindingMaterial",
            Textures = [texture],
            SurfaceTextureBindings =
            [
                new MaterialSurfaceTextureBinding(
                    EMaterialTextureSemantic.Normal,
                    texture,
                    TexCoordSet: 1,
                    Channel: 2,
                    IsSrgb: true,
                    WrapU: ETexWrapMode.ClampToEdge,
                    WrapV: ETexWrapMode.MirroredRepeat,
                    UvScaleOffset: new Vector4(2.0f, 3.0f, 0.25f, 0.5f),
                    UvRotation: 0.75f),
                new MaterialSurfaceTextureBinding(EMaterialTextureSemantic.BaseColor, texture),
            ],
        };

        string yaml = AssetManager.Serializer.Serialize(material);
        XRMaterial clone = AssetManager.Deserializer.Deserialize<XRMaterial>(yaml).ShouldNotBeNull();

        clone.SurfaceTextureBindings.Length.ShouldBe(2);
        MaterialSurfaceTextureBinding normal = clone.SurfaceTextureBindings[0];
        normal.Semantic.ShouldBe(EMaterialTextureSemantic.Normal);
        normal.Texture.ShouldNotBeNull();
        normal.TexCoordSet.ShouldBe(1);
        normal.Channel.ShouldBe(2);
        normal.IsSrgb.ShouldBeTrue();
        normal.WrapU.ShouldBe(ETexWrapMode.ClampToEdge);
        normal.WrapV.ShouldBe(ETexWrapMode.MirroredRepeat);
        normal.UvScaleOffset.ShouldBe(new Vector4(2.0f, 3.0f, 0.25f, 0.5f));
        normal.UvRotation.ShouldBe(0.75f);

        MaterialSurfaceTextureBinding baseColor = clone.SurfaceTextureBindings[1];
        baseColor.Semantic.ShouldBe(EMaterialTextureSemantic.BaseColor);
        baseColor.TexCoordSet.ShouldBe(0);
        baseColor.IsSrgb.ShouldBeFalse();
        baseColor.WrapU.ShouldBe(ETexWrapMode.Repeat);
        baseColor.UvScaleOffset.ShouldBe(new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
    }
}
