using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Editor.MaterialAuthoring;
using XREngine.Rendering;
using XREngine.Rendering.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class PoiyomiAuthoringSchemaTests
{
    private IRuntimeShaderServices? _previousServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new UberRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
        => RuntimeShaderServices.Current = _previousServices;

    [Test]
    public void PinnedCatalog_BuildsStableOrderedAuthoringTree()
    {
        ShaderUiManifest manifest = ShaderHelper.UberFragForward().GetUiManifest();

        ShaderAuthoringSchema first = PoiyomiAuthoringSchemaCatalog.GetOrCreate(manifest);
        ShaderAuthoringSchema second = PoiyomiAuthoringSchemaCatalog.GetOrCreate(manifest);

        second.ShouldBeSameAs(first);
        first.SchemaId.ShouldBe("poiyomi-toon-9.3.64");
        first.SourceIdentity.ShouldContain("c5aaeeb");
        first.Fingerprint.Length.ShouldBe(24);
        first.DeclarationOrder.Count.ShouldBeGreaterThan(3000);
        first.DeclarationOrder.Select(static node => node.DeclarationOrder)
            .ShouldBeInOrder(SortDirection.Ascending);

        ShaderAuthoringNode lightingMode = first.PropertyLookup["_LightingMapMode"];
        lightingMode.ManifestProperty.ShouldNotBeNull();
        lightingMode.ManifestProperty.Name.ShouldBe("_LightingMapMode");
        lightingMode.SemanticId.ShouldContain("_LightingMapMode");
        lightingMode.Ancestors().ShouldNotBeEmpty();
    }

    [Test]
    public void Expressions_UseDeterministicArithmeticLogicAndDependencies()
    {
        ShaderAuthoringExpression.TryCompile(
            "(_Mode == 2 && texture:_MainTex) || (quality ^ 2 >= 16)",
            out ShaderAuthoringExpression? expression,
            out string? diagnostic).ShouldBeTrue(diagnostic);

        expression.ShouldNotBeNull();
        expression.Dependencies.ShouldBe(new[] { "_Mode", "texture:_MainTex", "quality" }, ignoreOrder: true);
        expression.EvaluateBoolean(new TestExpressionContext(new Dictionary<string, object?>
        {
            ["_Mode"] = 1,
            ["texture:_MainTex"] = false,
            ["quality"] = 4,
        })).ShouldBeTrue();
    }

    [Test]
    public void WidgetRegistry_RejectsMetadataSuppliedExecutableTypes()
    {
        ShaderAuthoringWidgetRegistry.TryResolve("ThryTexture", out ShaderAuthoringWidgetDescriptor descriptor)
            .ShouldBeTrue();
        descriptor.Capability.ShouldBe(EShaderAuthoringWidgetCapability.Texture);
        ShaderAuthoringWidgetRegistry.TryResolve("Arbitrary.Namespace.EditorType", out _)
            .ShouldBeFalse();
        ShaderAuthoringWidgetRegistry.IsAllowlistedTool("ThryCustomGUI").ShouldBeFalse();
    }

    [Test]
    public void TexturePacker_PacksChannelsAndAppliesRemapDeterministically()
    {
        TexturePackingRecipe recipe = new()
        {
            Width = 2,
            Height = 1,
            Channels =
            [
                new()
                {
                    Kind = ETextureChannelSourceKind.Image,
                    SourceAsset = "source",
                    InputChannel = ETextureChannel.Green,
                },
                new() { Kind = ETextureChannelSourceKind.Constant, Constant = 0.25f },
                new()
                {
                    Kind = ETextureChannelSourceKind.Constant,
                    Constant = 0.25f,
                    Invert = true,
                },
                new() { Kind = ETextureChannelSourceKind.Gradient, Gradient = new() },
            ],
        };
        Dictionary<string, TexturePixelSource> sources = new(StringComparer.Ordinal)
        {
            ["source"] = new(2, 1, new[]
            {
                new Vector4(0.1f, 0.2f, 0.3f, 0.4f),
                new Vector4(0.5f, 0.6f, 0.7f, 0.8f),
            }),
        };

        Vector4[] packed = MaterialTexturePacker.Pack(recipe, sources);

        packed.Length.ShouldBe(2);
        packed[0].X.ShouldBe(0.2f, 0.0001f);
        packed[0].Y.ShouldBe(0.25f, 0.0001f);
        packed[0].Z.ShouldBe(0.75f, 0.0001f);
        packed[0].W.ShouldBe(0.0f, 0.0001f);
        packed[1].X.ShouldBe(0.6f, 0.0001f);
        packed[1].Y.ShouldBe(0.25f, 0.0001f);
        packed[1].Z.ShouldBe(0.75f, 0.0001f);
        packed[1].W.ShouldBe(1.0f, 0.0001f);
    }

    [Test]
    public void PresetAndClipboardPayloads_AreVersionedAndReadable()
    {
        MaterialAuthoringPreset preset = new()
        {
            Name = "Toon",
            SchemaId = "poiyomi-toon-9.3.64",
            Values =
            [
                new("poiyomi/9.3.64/property/_MainColor", "vec4", "(1,1,1,1)", null, EShaderUiPropertyMode.Animated),
            ],
        };

        MaterialAuthoringPreset.TryDeserialize(
            preset.Serialize(),
            out MaterialAuthoringPreset? restored,
            out string? diagnostic).ShouldBeTrue(diagnostic);
        restored.ShouldNotBeNull();
        restored.Values.Count.ShouldBe(1);

        MaterialAuthoringClipboardPayload clipboard = new()
        {
            SchemaId = preset.SchemaId,
            ScopeSemanticId = "poiyomi/9.3.64/root",
            Values = preset.Values,
        };
        MaterialAuthoringClipboardPayload.TryDeserialize(clipboard.Serialize(), out MaterialAuthoringClipboardPayload? parsed)
            .ShouldBeTrue();
        parsed.ShouldNotBeNull();
        parsed.Values[0].SemanticId.ShouldBe(preset.Values[0].SemanticId);
    }

    private sealed class TestExpressionContext(IReadOnlyDictionary<string, object?> values)
        : IShaderAuthoringExpressionContext
    {
        public bool TryResolve(string operand, out ShaderAuthoringValue value)
        {
            if (values.TryGetValue(operand, out object? resolved))
            {
                value = new(resolved);
                return true;
            }
            value = default;
            return false;
        }
    }
}
