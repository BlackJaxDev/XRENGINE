using NUnit.Framework;
using Newtonsoft.Json;
using Shouldly;
using System.Linq;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Core;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Components.Scene;
using XREngine.Rendering.Models;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RuntimeModularizationPhase5SerializationCompatibilityTests
{
    [TestCase("XREngine.Components.Animation.HumanoidComponent, XREngine", typeof(HumanoidComponent))]
    [TestCase("XREngine.Components.Animation.TransformParameterDriverComponent, XREngine", typeof(TransformParameterDriverComponent))]
    [TestCase("XREngine.Components.AudioListenerComponent, XREngine", typeof(AudioListenerComponent))]
    [TestCase("XREngine.Components.AudioSourceComponent, XREngine", typeof(AudioSourceComponent))]
    [TestCase("XREngine.Data.Components.Scene.VRHeadsetComponent, XREngine", typeof(VRHeadsetComponent))]
    [TestCase("XREngine.Rendering.Models.ModelImportOptions, XREngine", typeof(ModelImportOptions))]
    public void PreMoveAssemblyQualifiedType_ResolvesToExactlyOneCurrentPublicType(string legacyTypeName, Type expectedType)
    {
        Type.GetType(legacyTypeName).ShouldBe(expectedType);

        string rewritten = XRTypeRedirectRegistry.RewriteTypeName(legacyTypeName);
        Type.GetType(rewritten).ShouldBe(expectedType);
    }

    [Test]
    public void RenamedModelImporter_ResolvesThroughThePersistedTypeRedirect()
    {
        const string legacyTypeName = "XREngine.ModelImporter, XREngine";

        string rewritten = XRTypeRedirectRegistry.RewriteTypeName(legacyTypeName);
        rewritten.ShouldContain("XREngine.ModelAssetImporter");
        Type.GetType(rewritten).ShouldBe(typeof(ModelAssetImporter));
        CookedAssetTypeReference.Resolve(legacyTypeName, typeof(ModelAssetImporter))
            .ShouldBe(typeof(ModelAssetImporter));
    }

    [TestCase("XREngine.Components.Animation.HumanoidComponent", typeof(HumanoidComponent))]
    [TestCase("XREngine.Components.AudioListenerComponent", typeof(AudioListenerComponent))]
    [TestCase("XREngine.Data.Components.Scene.VRHeadsetComponent", typeof(VRHeadsetComponent))]
    public void PreMoveSceneComponentYaml_ResolvesToTheAdapterOwnedComponent(string legacyTypeName, Type expectedType)
    {
        string yaml = $"""
Components:
- __type: {legacyTypeName}, XREngine
Name: Phase5LegacyComponent
""";

        SceneNode scene = AssetManager.Deserializer.Deserialize<SceneNode>(yaml).ShouldNotBeNull();
        scene.Components.ShouldHaveSingleItem().GetType().ShouldBe(expectedType);
    }

    [TestCase("XREngine.Components.Animation.HumanoidComponent, XREngine", typeof(HumanoidComponent))]
    [TestCase("XREngine.Components.AudioSourceComponent, XREngine", typeof(AudioSourceComponent))]
    [TestCase("XREngine.Data.Components.Scene.VRHeadsetComponent, XREngine", typeof(VRHeadsetComponent))]
    [TestCase("XREngine.Rendering.Models.ModelImportOptions, XREngine", typeof(ModelImportOptions))]
    public void PreMoveCookedTypeReference_ResolvesToTheAdapterOwnedType(string legacyTypeName, Type expectedType)
    {
        CookedAssetTypeReference.Resolve(legacyTypeName, expectedType).ShouldBe(expectedType);
        CookedAssetTypeReference.MatchesExpectedType(legacyTypeName, expectedType).ShouldBeTrue();
    }

    [Test]
    public void PreMoveModelImportYaml_ResolvesThroughThePolymorphicImportOptionsPath()
    {
        const string yaml = """
__type: XREngine.Rendering.Models.ModelImportOptions, XREngine
ScaleConversion: 0.25
ZUp: true
""";

        IXR3rdPartyImportOptions model = AssetManager.Deserializer
            .Deserialize<IXR3rdPartyImportOptions>(yaml)
            .ShouldNotBeNull();
        ModelImportOptions options = model.ShouldBeOfType<ModelImportOptions>();
        options.ScaleConversion.ShouldBe(0.25f);
        options.ZUp.ShouldBeTrue();
    }

    [Test]
    public void PreMoveMultiAdapterSceneYaml_RoundTripsEachMovedComponentOnce()
    {
        SceneNode source = new("Phase5LegacyScene");
        source.AddComponent<HumanoidComponent>().ShouldNotBeNull();
        source.AddComponent<AudioListenerComponent>().ShouldNotBeNull();
        source.AddComponent<VRHeadsetComponent>().ShouldNotBeNull();

        string yaml = AssetManager.Serializer.Serialize(source)
            .Replace(typeof(HumanoidComponent).FullName!, $"{typeof(HumanoidComponent).FullName}, XREngine", StringComparison.Ordinal)
            .Replace(typeof(AudioListenerComponent).FullName!, $"{typeof(AudioListenerComponent).FullName}, XREngine", StringComparison.Ordinal)
            .Replace(typeof(VRHeadsetComponent).FullName!, $"{typeof(VRHeadsetComponent).FullName}, XREngine", StringComparison.Ordinal);

        SceneNode clone = AssetManager.Deserializer.Deserialize<SceneNode>(yaml).ShouldNotBeNull();
        clone.GetComponent<HumanoidComponent>().ShouldNotBeNull();
        clone.GetComponent<AudioListenerComponent>().ShouldNotBeNull();
        clone.GetComponent<VRHeadsetComponent>().ShouldNotBeNull();
        clone.Components.Count(component => component is HumanoidComponent).ShouldBe(1);
        clone.Components.Count(component => component is AudioListenerComponent).ShouldBe(1);
        clone.Components.Count(component => component is VRHeadsetComponent).ShouldBe(1);
    }

    [Test]
    public void ProjectJson_UsesExplicitImportSettingsRatherThanRuntimeTypeNames()
    {
        const string projectJson = """
{
  "ModelsToImport": [
    {
      "Enabled": true,
      "Kind": "Animated",
      "ImporterBackend": "AssimpOnly",
      "Path": "Assets/LegacyAvatar.fbx",
      "Scale": 0.01,
      "ZUp": true
    }
  ]
}
""";

        UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(projectJson);
        UnitTestingWorldSettings.ModelImportSettings import = settings.ModelsToImport.ShouldHaveSingleItem();

        import.Path.ShouldBe("Assets/LegacyAvatar.fbx");
        import.Scale.ShouldBe(0.01f);
        import.ZUp.ShouldBeTrue();

        string rewritten = JsonConvert.SerializeObject(settings);
        rewritten.ShouldNotContain("ModelImportOptions");
        rewritten.ShouldNotContain("ModelAssetImporter");
    }

    [Test]
    public void MovedAdapterPublicTypes_DoNotHaveMemoryPackContracts()
    {
        // Components and importer-policy objects are serialized by the scene/YAML,
        // cooked type-reference, and explicit project JSON paths above. None exposes
        // a MemoryPack contract, so there is no second binary type identity to forward.
        typeof(HumanoidComponent).GetCustomAttributes(typeof(MemoryPack.MemoryPackableAttribute), inherit: false).ShouldBeEmpty();
        typeof(AudioListenerComponent).GetCustomAttributes(typeof(MemoryPack.MemoryPackableAttribute), inherit: false).ShouldBeEmpty();
        typeof(VRHeadsetComponent).GetCustomAttributes(typeof(MemoryPack.MemoryPackableAttribute), inherit: false).ShouldBeEmpty();
        typeof(ModelImportOptions).GetCustomAttributes(typeof(MemoryPack.MemoryPackableAttribute), inherit: false).ShouldBeEmpty();
    }
}
