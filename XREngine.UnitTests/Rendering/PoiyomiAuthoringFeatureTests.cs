using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Editor.MaterialAuthoring;
using XREngine.Rendering;
using XREngine.Rendering.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class PoiyomiAuthoringFeatureTests
{
    [Test]
    public void OnValueActionGraph_SelectsOnlyMatchingPresetActions()
    {
        const string source =
            "[{value:0,actions:[{type:SET_PROPERTY,data:render_queue=2000}," +
            "{type:SET_PROPERTY,data:_ZWrite=1}]}," +
            "{value:2,actions:[{type:SET_PROPERTY,data:render_queue=3000}," +
            "{type:SET_PROPERTY,data:_ZWrite=0}]}]";

        MaterialAuthoringActionGraph opaque =
            MaterialAuthoringActionGraph.ParseForValue(source, "0");
        MaterialAuthoringActionGraph transparent =
            MaterialAuthoringActionGraph.ParseForValue(source, "2");

        opaque.IsValid.ShouldBeTrue();
        opaque.Actions.Count.ShouldBe(2);
        opaque.Actions[0].Target.ShouldBe("render_queue");
        opaque.Actions[0].Value.ShouldBe("2000");
        transparent.Actions.Count.ShouldBe(2);
        transparent.Actions[0].Value.ShouldBe("3000");
        transparent.Actions[1].Target.ShouldBe("_ZWrite");
        transparent.Actions[1].Value.ShouldBe("0");
    }

    [Test]
    public void PinnedAuthoringAudit_ClassifiesEveryActiveAnnotationAndWorkflow()
    {
        IReadOnlyList<PoiyomiAuthoringAuditEntry> audit = PoiyomiAuthoringParityAudit.All;

        audit.Count(entry => entry.Kind == "annotation").ShouldBe(41);
        audit.Count(entry => entry.Kind is "menu" or "auxiliaryWindow" or "inspectorWorkflow")
            .ShouldBe(62);
        PoiyomiAuthoringParityAudit.Unclassified.ShouldBeEmpty();
        audit.ShouldAllBe(entry => entry.ActiveUsageCount > 0);
    }

    [Test]
    public void PinnedModeActionGraph_MapsEveryCoupledRenderPresetField()
    {
        IRuntimeShaderServices? previous = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new UberRuntimeShaderServices();
        try
        {
            ShaderAuthoringSchema schema = PoiyomiAuthoringSchemaCatalog.GetOrCreate(
                ShaderHelper.UberFragForward().GetUiManifest());
            ShaderAuthoringNode mode = schema.PropertyLookup["_Mode"];
            string actions = mode.Options.OnValueActions.ShouldNotBeNull();

            MaterialAuthoringActionGraph opaque =
                MaterialAuthoringActionGraph.ParseForValue(actions, "0");
            MaterialAuthoringActionGraph transparent =
                MaterialAuthoringActionGraph.ParseForValue(actions, "2");

            opaque.Actions.Count.ShouldBe(24);
            transparent.Actions.Count.ShouldBe(24);
            opaque.Actions.ShouldContain(action =>
                action.Target == "render_queue" && action.Value == "2000");
            transparent.Actions.ShouldContain(action =>
                action.Target == "render_queue" && action.Value == "3000");
            transparent.Actions.ShouldContain(action =>
                action.Target == "_ZWrite" && action.Value == "0");
            MaterialRenderStateActionAdapter.IsSupported("_BlendOp").ShouldBeTrue();
            MaterialRenderStateActionAdapter.IsSupported("_OutlineDstBlendAlpha").ShouldBeTrue();

            foreach (MaterialAuthoringAction action in opaque.Actions.Concat(transparent.Actions))
            {
                if (action.Target is "render_queue" or "render_type" ||
                    MaterialRenderStateActionAdapter.IsSupported(action.Target))
                    continue;
                schema.PropertyLookup.ContainsKey(action.Target).ShouldBeTrue(
                    $"Missing action target {action.Target}");
                schema.PropertyLookup[action.Target].ManifestProperty.ShouldNotBeNull(
                    $"Unmapped action target {action.Target}");
            }
        }
        finally
        {
            RuntimeShaderServices.Current = previous;
        }
    }
    [Test]
    public void TexturePackingGraph_CompilesExplicitWiringAndRejectsCycles()
    {
        TexturePackingNode constant = new(
            Guid.NewGuid(),
            ETexturePackingNodeKind.Constant,
            "Roughness",
            new Vector4(0.25f));
        TexturePackingNode invert = new(
            Guid.NewGuid(),
            ETexturePackingNodeKind.Invert,
            "Invert",
            default);
        TexturePackingNode output = new(
            Guid.NewGuid(),
            ETexturePackingNodeKind.Output,
            "RGBA",
            default);
        TexturePackingGraph graph = new()
        {
            Nodes = [constant, invert, output],
            Edges =
            [
                new(constant.Id, 0, invert.Id, 0),
                new(invert.Id, 0, output.Id, 0),
            ],
            OutputNode = output.Id,
        };

        graph.Validate().ShouldBeEmpty();
        TexturePackingRecipe recipe = graph.Compile(
            8,
            4,
            EMaterialTextureColorSpace.Linear,
            "png",
            95);
        recipe.Channels[0].Constant.ShouldBe(0.25f);
        recipe.Channels[0].Invert.ShouldBeTrue();

        graph.Edges.Add(new(output.Id, 0, constant.Id, 0));
        graph.Validate().ShouldContain(diagnostic => diagnostic.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void GradientCurveRampAndArrayRecipes_AreDeterministicAndValidated()
    {
        MaterialGradientAsset gradient = new()
        {
            Resolution = 3,
            Interpolation = EMaterialGradientInterpolation.Linear,
            ColorKeys =
            [
                new(1.0f, Vector4.One),
                new(0.0f, Vector4.Zero),
            ],
        };
        gradient.Normalize();
        Vector4[] baked = gradient.Bake();
        baked[0].ShouldBe(new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
        baked[1].ShouldBe(new Vector4(0.5f, 0.5f, 0.5f, 1.0f));
        baked[2].ShouldBe(Vector4.One);

        MaterialRamp4 ramp = new();
        ramp.SetStop(0, 0.8f, Vector4.UnitX);
        ramp.SetStop(3, 0.2f, Vector4.UnitY);
        ramp.Positions.X.ShouldBeLessThanOrEqualTo(ramp.Positions.Y);
        ramp.Positions.Y.ShouldBeLessThanOrEqualTo(ramp.Positions.Z);
        ramp.Positions.Z.ShouldBeLessThanOrEqualTo(ramp.Positions.W);

        MaterialTextureArrayRecipe array = new()
        {
            Layers =
            [
                new("a.png", 64, 64, "RGBA8", 1, EMaterialTextureColorSpace.Srgb, "Color"),
                new("b.png", 32, 64, "RGBA8", 1, EMaterialTextureColorSpace.Srgb, "Color"),
            ],
        };
        array.Validate().ShouldContain(diagnostic => diagnostic.Contains("dimensions", StringComparison.Ordinal));
        array.AllowResample = true;
        array.Validate().ShouldBeEmpty();
    }

    [Test]
    public void StructuralTransaction_RollsBackNonMaterialStateOnFailure()
    {
        XRMaterial material = new();
        int structuralValue = 7;
        MaterialAuthoringTransaction transaction = new("Structural rollback");
        transaction.AddStructural(
            material,
            "Metadata",
            () => structuralValue = 42,
            () => structuralValue = 7);
        transaction.Add(
            material,
            "Failing mutation",
            () => throw new InvalidOperationException("expected failure"));

        transaction.TryExecute(out MaterialAuthoringTransactionReport report).ShouldBeFalse();
        structuralValue.ShouldBe(7);
        report.Diagnostics.ShouldContain(diagnostic => diagnostic.Contains("expected failure", StringComparison.Ordinal));
    }

    [Test]
    public void LocaleFallbackRemotePolicyAndMetadataRoundTrip_AreSafe()
    {
        ShaderAuthoringNode root = new()
        {
            SemanticId = "schema/root",
            Kind = EShaderAuthoringNodeKind.Root,
            DisplayName = "Root",
        };
        ShaderAuthoringNode property = new()
        {
            SemanticId = "schema/property/color",
            Kind = EShaderAuthoringNodeKind.Property,
            DisplayName = "Color",
            SourcePropertyName = "_Color",
        };
        property.Parent = root;
        root.Children.Add(property);
        ShaderAuthoringSchema schema = new("schema", 1, "test", "fingerprint", root, []);
        MaterialAuthoringLocaleService locales = new();
        locales.ImportSourceLabels(schema);
        locales.ImportJson(
            """{"schema/property/color":"Couleur"}""",
            "fr",
            schema,
            authoringOverride: true).ShouldBeEmpty();
        locales.Resolve("fr", property).ShouldBe("Couleur");
        locales.Resolve("de", property).ShouldBe("Color");

        RemoteAuthoringPolicy disabled = new(
            false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "www.poiyomi.com" },
            1024,
            TimeSpan.FromHours(1));
        disabled.Validate(new Uri("https://www.poiyomi.com/docs")).ShouldNotBeNull();

        MaterialAuthoringMetadata metadata = new()
        {
            ImportedRenderQueue = 3000,
            ImportedShaderIdentity = "Poiyomi Toon 9.3.64",
        };
        metadata.Tags["RenderType"] = "Transparent";
        MaterialAuthoringMetadata.TryDeserialize(metadata.Serialize(), out MaterialAuthoringMetadata? restored)
            .ShouldBeTrue();
        restored.ShouldNotBeNull();
        restored.Tags["RenderType"].ShouldBe("Transparent");
    }
}
