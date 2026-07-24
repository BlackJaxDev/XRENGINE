using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Core;
using XREngine.Core.Attributes;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RuntimeModularizationPhase4SerializationCompatibilityTests
{
    [Test]
    public void MovedRuntimeUiTypes_ResolveThroughLegacyFacadeTypeForwards()
    {
        Type.GetType("XREngine.Components.UICanvasComponent, XREngine")
            .ShouldBe(typeof(UICanvasComponent));
        Type.GetType("XREngine.Rendering.UI.UISvgComponent, XREngine")
            .ShouldBe(typeof(UISvgComponent));
        Type.GetType("XREngine.Rendering.UI.RiveUIComponent, XREngine")
            .ShouldBe(typeof(RiveUIComponent));
        Type.GetType("XREngine.Rendering.UI.IUICanvasInputSource, XREngine")
            .ShouldBe(typeof(IUICanvasInputSource));
    }

    [Test]
    public void MovedUiTypeRedirects_AreAvailableToReflectionAndAotMetadataDiscovery()
    {
        AssertRedirect<UISvgComponent>("XREngine.Scene.Components.UI.UISvgComponent");
        AssertRedirect<RiveUIComponent>("XREngine.Scene.Components.UI.RiveUIComponent");
        AssertRedirect<StateMachineInput>("XREngine.Scene.Components.UI.StateMachineInput");

        XRTypeRedirectRegistry.RewriteTypeName("XREngine.Scene.Components.UI.UISvgComponent")
            .ShouldBe(typeof(UISvgComponent).FullName);
        XRTypeRedirectRegistry.RewriteTypeName("XREngine.Scene.Components.UI.RiveUIComponent, XREngine")
            .ShouldBe(typeof(RiveUIComponent).AssemblyQualifiedName);
    }

    [Test]
    public void RuntimeUiYaml_RoundTripsMovedCanvasState()
    {
        UICanvasComponent original = new()
        {
            PreferOffscreenRenderingForNonScreenSpaces = false,
            AutoDisableOffscreenForBackdropBlur = false,
        };

        string yaml = AssetManager.Serializer.Serialize(original);
        UICanvasComponent clone = AssetManager.Deserializer
            .Deserialize<UICanvasComponent>(yaml)
            .ShouldNotBeNull();

        clone.PreferOffscreenRenderingForNonScreenSpaces.ShouldBeFalse();
        clone.AutoDisableOffscreenForBackdropBlur.ShouldBeFalse();
    }

    [Test]
    public void ModelingBridgeImportOptionsYaml_RoundTripsConcreteImportPolicy()
    {
        ModelImportOptions original = new()
        {
            FbxBackend = FbxImportBackend.Native,
            GltfBackend = GltfImportBackend.Assimp,
            ScaleConversion = 0.01f,
            ZUp = true,
            GenerateMeshRenderersAsync = false,
            SeparateMeshIslands = true,
            SpatialPartitionMaxTriangles = 4096,
        };

        string yaml = AssetManager.Serializer.Serialize(original);
        ModelImportOptions clone = AssetManager.Deserializer
            .Deserialize<ModelImportOptions>(yaml)
            .ShouldNotBeNull();

        clone.FbxBackend.ShouldBe(FbxImportBackend.Native);
        clone.GltfBackend.ShouldBe(GltfImportBackend.Assimp);
        clone.ScaleConversion.ShouldBe(0.01f);
        clone.ZUp.ShouldBeTrue();
        clone.GenerateMeshRenderersAsync.ShouldBeFalse();
        clone.SeparateMeshIslands.ShouldBeTrue();
        clone.SpatialPartitionMaxTriangles.ShouldBe(4096);
    }

    [Test]
    public void RuntimeRenderingModelAndMaterialYaml_RoundTripFromTheirFinalOwner()
    {
        Model model = new()
        {
            Name = "Phase4Model",
        };
        XRMaterial material = new()
        {
            Name = "Phase4Material",
        };

        string modelYaml = AssetManager.Serializer.Serialize(model);
        string materialYaml = AssetManager.Serializer.Serialize(material);

        Model modelClone = AssetManager.Deserializer.Deserialize<Model>(modelYaml).ShouldNotBeNull();
        XRMaterial materialClone = AssetManager.Deserializer.Deserialize<XRMaterial>(materialYaml).ShouldNotBeNull();

        modelClone.Name.ShouldBe("Phase4Model");
        materialClone.Name.ShouldBe("Phase4Material");
        typeof(Model).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Rendering");
        typeof(XRMaterial).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Rendering");
    }

    private static void AssertRedirect<T>(string legacyTypeName)
    {
        XRTypeRedirectAttribute attribute = typeof(T)
            .GetCustomAttributes(typeof(XRTypeRedirectAttribute), inherit: false)
            .Cast<XRTypeRedirectAttribute>()
            .Single();

        attribute.LegacyTypeNames.ShouldContain(legacyTypeName);
    }
}
