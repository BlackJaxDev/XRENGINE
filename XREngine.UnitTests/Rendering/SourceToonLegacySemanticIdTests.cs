using NUnit.Framework;
using Shouldly;
using XREngine.Editor.MaterialAuthoring;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class SourceToonLegacySemanticIdTests
{
    [Test]
    public void TryGetNode_ResolvesLegacyPrefixToCurrentNode()
    {
        ShaderAuthoringSchema schema = CreateSchema();

        schema.TryGetNode("source-toon/9.3.64/property/_Color", out ShaderAuthoringNode? current).ShouldBeTrue();
        schema.TryGetNode("poiyomi/9.3.64/property/_Color", out ShaderAuthoringNode? legacy).ShouldBeTrue();
        legacy.ShouldBeSameAs(current);
        schema.TryGetNode("poiyomi/9.3.64/property/_Missing", out _).ShouldBeFalse();
        schema.TryGetNode("other/9.3.64/property/_Color", out _).ShouldBeFalse();
    }

    [Test]
    public void GetStoredSemanticIds_ListsCurrentIdFirstThenLegacyId()
    {
        ShaderAuthoringSchema schema = CreateSchema();

        schema.GetStoredSemanticIds("source-toon/9.3.64/property/_Color").ShouldBe(
        [
            "source-toon/9.3.64/property/_Color",
            "poiyomi/9.3.64/property/_Color",
        ]);
    }

    [Test]
    public void PresetDiff_ReadsValuesSavedWithLegacyIds()
    {
        ShaderAuthoringSchema schema = CreateSchema();
        MaterialAuthoringPreset preset = new()
        {
            Name = "Legacy",
            SchemaId = "poiyomi-toon-9.3.64",
            Values = [new("poiyomi/9.3.64/property/_Color", "vec4", "(1,0,0,1)", null, null)],
        };

        MaterialAuthoringPresetDiff diff = preset.Preview(schema, new Dictionary<string, object?>());

        MaterialAuthoringPresetDiffEntry entry = diff.Entries.ShouldHaveSingleItem();
        entry.SemanticId.ShouldBe("source-toon/9.3.64/property/_Color");
        entry.IsCompatible.ShouldBeTrue();
    }

    private static ShaderAuthoringSchema CreateSchema()
    {
        ShaderAuthoringNode root = new()
        {
            SemanticId = "source-toon/9.3.64/root",
            Kind = EShaderAuthoringNodeKind.Root,
            DisplayName = "Source toon 9.3.64",
        };
        ShaderAuthoringNode color = new()
        {
            SemanticId = "source-toon/9.3.64/property/_Color",
            Kind = EShaderAuthoringNodeKind.Property,
            DisplayName = "Color",
            SourcePropertyName = "_Color",
        };
        color.Parent = root;
        root.Children.Add(color);
        return new ShaderAuthoringSchema(
            "source-toon-9.3.64",
            1,
            "Source toon 9.3.64@test",
            "fingerprint",
            root,
            [],
            [new(SourceToonAuthoringSchemaCatalog.LegacySemanticIdPrefix, SourceToonAuthoringSchemaCatalog.SemanticIdPrefix)]);
    }
}
