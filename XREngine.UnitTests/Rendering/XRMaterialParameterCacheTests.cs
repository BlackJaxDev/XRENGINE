using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class XRMaterialParameterCacheTests
{
    [Test]
    public void ReplacingParameters_InvalidatesCachedNameIndices()
    {
        XRMaterial material = new()
        {
            Parameters =
            [
                new ShaderFloat(1.0f, "First"),
                new ShaderInt(2, "Target"),
            ],
        };

        material.Parameter<ShaderInt>("Target").ShouldNotBeNull().Value.ShouldBe(2);

        material.Parameters =
        [
            new ShaderInt(3, "Target"),
            new ShaderFloat(4.0f, "First"),
        ];

        material.Parameter<ShaderInt>("Target").ShouldNotBeNull().Value.ShouldBe(3);
        material.Parameter<ShaderFloat>("First").ShouldNotBeNull().Value.ShouldBe(4.0f);
    }

    [Test]
    public void MutatingParametersInPlace_DoesNotReturnEntryAtStaleCachedIndex()
    {
        XRMaterial material = new()
        {
            Parameters =
            [
                new ShaderFloat(1.0f, "First"),
                new ShaderInt(2, "Target"),
            ],
        };

        material.Parameter<ShaderInt>("Target").ShouldNotBeNull().Value.ShouldBe(2);

        material.Parameters[0] = new ShaderInt(3, "Target");
        material.Parameters[1] = new ShaderFloat(4.0f, "Replacement");

        material.Parameter<ShaderInt>("Target").ShouldNotBeNull().Value.ShouldBe(3);
        material.Parameter<ShaderFloat>("Replacement").ShouldNotBeNull().Value.ShouldBe(4.0f);
    }

    [Test]
    public void AssigningEqualParameterValue_DoesNotDirtyMaterialBindings()
    {
        var parameter = new ShaderFloat(1.0f, "Value");
        var material = new XRMaterial([parameter]);
        ulong initialVersion = material.BindingValueVersion;
        int valueChangedCount = 0;
        parameter.ValueChanged += _ => valueChangedCount++;

        parameter.Value = 1.0f;

        material.BindingValueVersion.ShouldBe(initialVersion);
        valueChangedCount.ShouldBe(0);

        parameter.Value = 2.0f;

        material.BindingValueVersion.ShouldBeGreaterThan(initialVersion);
        valueChangedCount.ShouldBe(1);
    }

    [Test]
    public void ShaderVarArrayYaml_UsesExplicitElementTypeDiscriminators()
    {
        const string yaml =
            """
            Parameters:
            - __type: XREngine.Rendering.Models.Materials.ShaderInt
              Value: 3
              Name: Integer
            - __type: XREngine.Rendering.Models.Materials.ShaderVector2
              Value: 4 5
              Name: Vector
            """;

        ParameterContainer container = AssetManager.Deserializer
            .Deserialize<ParameterContainer>(yaml)
            .ShouldNotBeNull();

        container.Parameters[0].ShouldBeOfType<ShaderInt>().Value.ShouldBe(3);
        container.Parameters[1].ShouldBeOfType<ShaderVector2>().Value.ShouldBe(new(4.0f, 5.0f));
    }

    private sealed class ParameterContainer
    {
        public ShaderVar[] Parameters { get; set; } = [];
    }
}
