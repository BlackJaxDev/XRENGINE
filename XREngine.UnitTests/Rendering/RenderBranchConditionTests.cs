using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderBranchConditionTests
{
    [Test]
    public void Constant_ReturnsItsValue()
    {
        RenderBranchCondition.Constant(true).Evaluate(null, null, null).ShouldBeTrue();
        RenderBranchCondition.Constant(false).Evaluate(null, null, null).ShouldBeFalse();
        RenderBranchCondition.Constant(true).Describe().ShouldBe("true");
    }

    [Test]
    public void PipelineVariable_ReadsBoolAndIntVariables()
    {
        XRRenderPipelineInstance.PipelineVariableStore variables = new();
        variables.Set("UseFog", true);
        variables.Set("Level", 3);

        RenderBranchCondition.Variable("UseFog").Evaluate(null, variables, null).ShouldBeTrue();
        RenderBranchCondition.Variable("Level", ERenderBranchConditionOperator.GreaterOrEqual, 3).Evaluate(null, variables, null).ShouldBeTrue();
        RenderBranchCondition.Variable("Level", ERenderBranchConditionOperator.Equal, 2).Evaluate(null, variables, null).ShouldBeFalse();
        RenderBranchCondition.Variable("Level").EvaluateInt(null, variables, null).ShouldBe(3);
    }

    [Test]
    public void PipelineVariable_MissingKeyIsFalseForEveryOperator()
    {
        XRRenderPipelineInstance.PipelineVariableStore variables = new();

        RenderBranchCondition.Variable("Missing").Evaluate(null, variables, null).ShouldBeFalse();
        RenderBranchCondition.Variable("Missing", ERenderBranchConditionOperator.IsFalse).Evaluate(null, variables, null).ShouldBeFalse();
        RenderBranchCondition.Variable("Missing").EvaluateInt(null, variables, null).ShouldBe(-1);
    }

    [Test]
    public void PipelineSetting_ReadsRegisteredSettings()
    {
        BranchSettingTestPipeline pipeline = new() { UseBloom = true, Quality = 2 };
        RenderBranchCondition bloom = RenderBranchCondition.Setting(nameof(BranchSettingTestPipeline.UseBloom));
        RenderBranchCondition quality = RenderBranchCondition.Setting(nameof(BranchSettingTestPipeline.Quality), ERenderBranchConditionOperator.Greater, 1);

        bloom.Bind(pipeline).ShouldBeTrue();
        bloom.Evaluate(pipeline, null, null).ShouldBeTrue();
        quality.Evaluate(pipeline, null, null).ShouldBeTrue();

        pipeline.UseBloom = false;
        bloom.Evaluate(pipeline, null, null).ShouldBeFalse();
    }

    [Test]
    public void PipelineSetting_UnknownKeyReportsBindError()
    {
        RenderBranchCondition condition = RenderBranchCondition.Setting("NotASetting");

        condition.Bind(new BranchSettingTestPipeline()).ShouldBeFalse();
        condition.BindError.ShouldNotBeNull();
        condition.Evaluate(new BranchSettingTestPipeline(), null, null).ShouldBeFalse();
    }

    [Test]
    public void StageEnabled_ReadsTheStageEnableFlag()
    {
        PipelinePostProcessState state = new();
        state.BindToSchema(new RenderPipelinePostProcessSchema(
            new Dictionary<string, PostProcessStageDescriptor>(StringComparer.Ordinal)
            {
                ["bloom"] = new PostProcessStageDescriptor("bloom", "Bloom", [], null),
            },
            []));
        RenderBranchCondition condition = RenderBranchCondition.StageEnabled("bloom");

        condition.Evaluate(null, null, state).ShouldBeTrue();
        state.TryGetStage("bloom", out PostProcessStageState? stage).ShouldBeTrue();
        stage!.SetValue(RenderBranchCondition.StageEnabledParameter, false);
        condition.Evaluate(null, null, state).ShouldBeFalse();
        RenderBranchCondition.StageEnabled("missing").Evaluate(null, null, state).ShouldBeFalse();
    }

    [Test]
    public void Capability_ResolvesNamesAndRejectsUnknownNames()
    {
        RenderBranchCondition mainPass = RenderBranchCondition.Capability(ERenderBranchCapability.MainPass);
        mainPass.Bind(null).ShouldBeTrue();
        mainPass.Evaluate(null, null, null).ShouldBe(RuntimeEngine.Rendering.State.IsMainPass);

        RenderBranchCondition unknown = new() { Source = ERenderBranchConditionSource.Capability, Key = "Teleport" };
        unknown.Bind(null).ShouldBeFalse();
        unknown.Evaluate(null, null, null).ShouldBeFalse();
    }

    [Test]
    public void Evaluate_DoesNotAllocateAfterBind()
    {
        BranchSettingTestPipeline pipeline = new() { UseBloom = true };
        XRRenderPipelineInstance.PipelineVariableStore variables = new();
        variables.Set("Level", 3);
        PipelinePostProcessState state = new();
        state.BindToSchema(new RenderPipelinePostProcessSchema(
            new Dictionary<string, PostProcessStageDescriptor>(StringComparer.Ordinal)
            {
                ["bloom"] = new PostProcessStageDescriptor("bloom", "Bloom", [], null),
            },
            []));
        RenderBranchCondition[] conditions =
        [
            RenderBranchCondition.Constant(true),
            RenderBranchCondition.Variable("Level", ERenderBranchConditionOperator.Equal, 3),
            RenderBranchCondition.Setting(nameof(BranchSettingTestPipeline.UseBloom)),
            RenderBranchCondition.StageEnabled("bloom"),
            RenderBranchCondition.Capability(ERenderBranchCapability.StereoPass, expected: false),
        ];
        foreach (RenderBranchCondition condition in conditions)
        {
            condition.Bind(pipeline);
            condition.Evaluate(pipeline, variables, state);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool result = true;
        for (int i = 0; i < 1000; i++)
            for (int c = 0; c < conditions.Length; c++)
                result &= conditions[c].Evaluate(pipeline, variables, state);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        result.ShouldBeTrue();
        allocated.ShouldBe(0L);
    }

    [Test]
    public void Condition_RoundTripsThroughYaml()
    {
        RenderBranchCondition original = RenderBranchCondition.Variable("Level", ERenderBranchConditionOperator.LessOrEqual, 2.5);

        string yaml = AssetManager.Serializer.Serialize(original, typeof(RenderBranchCondition));
        RenderBranchCondition? clone = AssetManager.Deserializer.Deserialize<RenderBranchCondition>(yaml);

        clone.ShouldNotBeNull();
        clone!.Source.ShouldBe(ERenderBranchConditionSource.PipelineVariable);
        clone.Key.ShouldBe("Level");
        clone.Operator.ShouldBe(ERenderBranchConditionOperator.LessOrEqual);
        clone.Value.ShouldBe(2.5);
        clone.Describe().ShouldBe("var Level <= 2.5");
    }

    [Test]
    public void IfElse_ConditionTakesPriorityOverDelegate()
    {
        VPRC_IfElse branch = new()
        {
            ConditionEvaluator = static () => true,
            Condition = RenderBranchCondition.Constant(false),
        };

        branch.Condition.ShouldNotBeNull();
        branch.Condition!.Evaluate(null, null, null).ShouldBeFalse();
    }

    [Test]
    public void ResourceNameProperties_HaveTheAttribute()
    {
        Type baseType = typeof(ViewportRenderCommand);
        List<string> missing = [];
        foreach (Type type in baseType.Assembly.GetTypes())
        {
            if (type.IsAbstract || !baseType.IsAssignableFrom(type))
                continue;

            foreach (System.Reflection.PropertyInfo property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (property.PropertyType != typeof(string) || property.SetMethod is not { IsPublic: true } || !IsResourceName(property.Name))
                    continue;

                if (!Attribute.IsDefined(property, typeof(RenderResourceNameAttribute), inherit: true))
                    missing.Add(string.Concat(type.Name, ".", property.Name));
            }
        }

        missing.ShouldBeEmpty();
    }

    private static bool IsResourceName(string name)
        => !name.EndsWith("VariableName", StringComparison.Ordinal) &&
           (name.EndsWith("FBOName", StringComparison.Ordinal) ||
            name.EndsWith("FboName", StringComparison.Ordinal) ||
            name.EndsWith("TextureName", StringComparison.Ordinal) ||
            name.EndsWith("BufferName", StringComparison.Ordinal));
}
