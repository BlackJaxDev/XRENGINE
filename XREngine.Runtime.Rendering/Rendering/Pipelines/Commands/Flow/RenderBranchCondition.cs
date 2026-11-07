using System.Globalization;
using XREngine.Rendering.PostProcessing;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// A branch condition that is data, not code, so authored pipelines can save it and tools can show it. The condition
/// reads one source value and compares it with <see cref="Value"/>. <see cref="Bind"/> resolves the key to a typed
/// accessor once, so <see cref="Evaluate(RenderPipeline?, XRRenderPipelineInstance.PipelineVariableStore?, PipelinePostProcessState?)"/>
/// does not allocate.
/// </summary>
public sealed class RenderBranchCondition
{
    /// <summary>The name of the stage parameter that holds the enable flag of a stage.</summary>
    public const string StageEnabledParameter = "Enabled";

    private RenderPipeline? _boundPipeline;
    private RenderPipelineBranchSettings? _boundSettings;
    private string? _boundKey;
    private ERenderBranchConditionSource _boundSource;
    private Func<double>? _settingGetter;
    private ERenderBranchCapability _capability;
    private bool _capabilityValid;

    /// <summary>Gets or sets the value that the condition reads.</summary>
    public ERenderBranchConditionSource Source { get; set; }

    /// <summary>
    /// Gets or sets the variable name, branch setting key (see <see cref="RenderPipeline.BranchSettings"/>), stage key,
    /// or capability name. A constant ignores it.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets how the condition compares the source value.</summary>
    public ERenderBranchConditionOperator Operator { get; set; }

    /// <summary>Gets or sets the value to compare with. For a constant source it is the source value.</summary>
    public double Value { get; set; }

    /// <summary>Gets the reason the key did not resolve at the last bind, or null when it resolved.</summary>
    [YamlIgnore]
    public string? BindError { get; private set; }

    /// <summary>Creates a condition that tests a pipeline variable.</summary>
    public static RenderBranchCondition Variable(string name, ERenderBranchConditionOperator op = ERenderBranchConditionOperator.IsTrue, double value = 0.0)
        => new() { Source = ERenderBranchConditionSource.PipelineVariable, Key = name, Operator = op, Value = value };

    /// <summary>Creates a condition that tests a pipeline setting property.</summary>
    public static RenderBranchCondition Setting(string propertyName, ERenderBranchConditionOperator op = ERenderBranchConditionOperator.IsTrue, double value = 0.0)
        => new() { Source = ERenderBranchConditionSource.PipelineSetting, Key = propertyName, Operator = op, Value = value };

    /// <summary>Creates a condition that is true when a post-process stage is enabled.</summary>
    public static RenderBranchCondition StageEnabled(string stageKey)
        => new() { Source = ERenderBranchConditionSource.StageEnabled, Key = stageKey };

    /// <summary>Creates a condition that tests a render state or backend capability.</summary>
    public static RenderBranchCondition Capability(ERenderBranchCapability capability, bool expected = true)
        => new()
        {
            Source = ERenderBranchConditionSource.Capability,
            Key = capability.ToString(),
            Operator = expected ? ERenderBranchConditionOperator.IsTrue : ERenderBranchConditionOperator.IsFalse,
        };

    /// <summary>Creates a condition with a fixed result.</summary>
    public static RenderBranchCondition Constant(bool value)
        => new() { Source = ERenderBranchConditionSource.Constant, Value = value ? 1.0 : 0.0 };

    /// <summary>
    /// Resolves the key for a pipeline. The call allocates only when the pipeline type, the source, or the key changed.
    /// </summary>
    /// <returns>True when the key resolved; otherwise false, with the reason in <see cref="BindError"/>.</returns>
    public bool Bind(RenderPipeline? pipeline)
    {
        RenderPipelineBranchSettings? settings = Source == ERenderBranchConditionSource.PipelineSetting ? pipeline?.BranchSettings : null;
        if (ReferenceEquals(_boundPipeline, pipeline) &&
            ReferenceEquals(_boundSettings, settings) &&
            _boundSource == Source &&
            string.Equals(_boundKey, Key, StringComparison.Ordinal))
        {
            return BindError is null;
        }

        _boundPipeline = pipeline;
        _boundSettings = settings;
        _boundSource = Source;
        _boundKey = Key;
        _settingGetter = null;
        _capabilityValid = false;
        BindError = null;

        switch (Source)
        {
            case ERenderBranchConditionSource.PipelineSetting:
                if (settings is null)
                    BindError = "The condition has no pipeline.";
                else if (!settings.TryGet(Key, out _settingGetter))
                    BindError = string.Concat(pipeline!.GetType().Name, " registers no branch setting '", Key, "'.");
                break;
            case ERenderBranchConditionSource.Capability:
                _capabilityValid = Enum.TryParse(Key, ignoreCase: true, out _capability);
                if (!_capabilityValid)
                    BindError = string.Concat("'", Key, "' is not a render capability.");
                break;
            case ERenderBranchConditionSource.PipelineVariable or ERenderBranchConditionSource.StageEnabled:
                if (string.IsNullOrWhiteSpace(Key))
                    BindError = "The condition has no key.";
                break;
        }

        return BindError is null;
    }

    /// <summary>Evaluates the condition for the pipeline that runs the current frame.</summary>
    public bool Evaluate()
    {
        XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        RenderPipeline? pipeline = instance?.Pipeline;
        PipelinePostProcessState? postProcess = Source == ERenderBranchConditionSource.StageEnabled
            ? RuntimeEngine.Rendering.State.RenderingCamera?.GetPostProcessState(pipeline)
            : null;
        return Evaluate(pipeline, instance?.Variables, postProcess);
    }

    /// <summary>Evaluates the condition with explicit inputs. The call does not allocate after <see cref="Bind"/>.</summary>
    public bool Evaluate(RenderPipeline? pipeline, XRRenderPipelineInstance.PipelineVariableStore? variables, PipelinePostProcessState? postProcess)
        => Compare(ReadSource(pipeline, variables, postProcess));

    /// <summary>
    /// Returns the source value as an integer for a switch. The value rounds toward zero. A source that is missing
    /// returns -1, which selects the default case.
    /// </summary>
    public int EvaluateInt(RenderPipeline? pipeline, XRRenderPipelineInstance.PipelineVariableStore? variables, PipelinePostProcessState? postProcess)
    {
        double value = ReadSource(pipeline, variables, postProcess);
        return double.IsNaN(value) ? -1 : (int)value;
    }

    /// <summary>Returns the source value as an integer for the pipeline that runs the current frame.</summary>
    public int EvaluateInt()
    {
        XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        RenderPipeline? pipeline = instance?.Pipeline;
        PipelinePostProcessState? postProcess = Source == ERenderBranchConditionSource.StageEnabled
            ? RuntimeEngine.Rendering.State.RenderingCamera?.GetPostProcessState(pipeline)
            : null;
        return EvaluateInt(pipeline, instance?.Variables, postProcess);
    }

    /// <summary>Returns the condition as script-like text, for example <c>var UseBloom == 1</c>.</summary>
    public string Describe()
    {
        string source = Source switch
        {
            ERenderBranchConditionSource.Constant => Value != 0.0 ? "true" : "false",
            ERenderBranchConditionSource.PipelineVariable => string.Concat("var ", Key),
            ERenderBranchConditionSource.PipelineSetting => string.Concat("setting ", Key),
            ERenderBranchConditionSource.StageEnabled => string.Concat("stage ", Key, " enabled"),
            ERenderBranchConditionSource.Capability => string.Concat("capability ", Key),
            _ => Key,
        };

        if (Source == ERenderBranchConditionSource.Constant)
            return source;

        string value = Value.ToString(CultureInfo.InvariantCulture);
        return Operator switch
        {
            ERenderBranchConditionOperator.IsTrue => source,
            ERenderBranchConditionOperator.IsFalse => string.Concat("!(", source, ")"),
            ERenderBranchConditionOperator.Equal => string.Concat(source, " == ", value),
            ERenderBranchConditionOperator.NotEqual => string.Concat(source, " != ", value),
            ERenderBranchConditionOperator.Greater => string.Concat(source, " > ", value),
            ERenderBranchConditionOperator.GreaterOrEqual => string.Concat(source, " >= ", value),
            ERenderBranchConditionOperator.Less => string.Concat(source, " < ", value),
            ERenderBranchConditionOperator.LessOrEqual => string.Concat(source, " <= ", value),
            _ => source,
        };
    }

    /// <inheritdoc />
    public override string ToString()
        => Describe();

    /// <summary>Returns a copy with the same data and no binding.</summary>
    public RenderBranchCondition Clone()
        => new() { Source = Source, Key = Key, Operator = Operator, Value = Value };

    private double ReadSource(RenderPipeline? pipeline, XRRenderPipelineInstance.PipelineVariableStore? variables, PipelinePostProcessState? postProcess)
    {
        switch (Source)
        {
            case ERenderBranchConditionSource.Constant:
                return Value;
            case ERenderBranchConditionSource.PipelineVariable:
                return ReadVariable(variables, Key);
            case ERenderBranchConditionSource.PipelineSetting:
                if (pipeline is null)
                    return double.NaN;
                if (!ReferenceEquals(_boundPipeline, pipeline) || !ReferenceEquals(_boundSettings, pipeline.BranchSettings))
                    Bind(pipeline);
                return _settingGetter is null ? double.NaN : _settingGetter();
            case ERenderBranchConditionSource.StageEnabled:
                return ReadStageEnabled(postProcess, Key);
            case ERenderBranchConditionSource.Capability:
                if (!_capabilityValid || _boundSource != Source || !string.Equals(_boundKey, Key, StringComparison.Ordinal))
                    Bind(pipeline);
                return _capabilityValid && ReadCapability(_capability) ? 1.0 : 0.0;
            default:
                return double.NaN;
        }
    }

    private bool Compare(double value)
    {
        // A missing source is false for every operator, so a broken key never enables a branch.
        if (double.IsNaN(value))
            return false;

        return Operator switch
        {
            ERenderBranchConditionOperator.IsTrue => value != 0.0,
            ERenderBranchConditionOperator.IsFalse => value == 0.0,
            ERenderBranchConditionOperator.Equal => value == Value,
            ERenderBranchConditionOperator.NotEqual => value != Value,
            ERenderBranchConditionOperator.Greater => value > Value,
            ERenderBranchConditionOperator.GreaterOrEqual => value >= Value,
            ERenderBranchConditionOperator.Less => value < Value,
            ERenderBranchConditionOperator.LessOrEqual => value <= Value,
            _ => false,
        };
    }

    private static double ReadVariable(XRRenderPipelineInstance.PipelineVariableStore? variables, string key)
    {
        if (variables is null)
            return double.NaN;
        if (variables.TryGet(key, out bool boolValue))
            return boolValue ? 1.0 : 0.0;
        if (variables.TryGet(key, out int intValue))
            return intValue;
        if (variables.TryGet(key, out uint uintValue))
            return uintValue;
        if (variables.TryGet(key, out float floatValue))
            return floatValue;

        return double.NaN;
    }

    private static double ReadStageEnabled(PipelinePostProcessState? postProcess, string key)
    {
        if (postProcess is null || !postProcess.TryGetStage(key, out PostProcessStageState? stage) || stage is null)
            return 0.0;

        return stage.GetValue(StageEnabledParameter, true) ? 1.0 : 0.0;
    }

    private static bool ReadCapability(ERenderBranchCapability capability)
        => capability switch
        {
            ERenderBranchCapability.StereoPass => RuntimeEngine.Rendering.State.IsStereoPass,
            ERenderBranchCapability.ShadowPass => RuntimeEngine.Rendering.State.IsShadowPass,
            ERenderBranchCapability.MirrorPass => RuntimeEngine.Rendering.State.IsMirrorPass,
            ERenderBranchCapability.SceneCapturePass => RuntimeEngine.Rendering.State.IsSceneCapturePass,
            ERenderBranchCapability.LightProbePass => RuntimeEngine.Rendering.State.IsLightProbePass,
            ERenderBranchCapability.MainPass => RuntimeEngine.Rendering.State.IsMainPass,
            ERenderBranchCapability.MultiView => RuntimeEngine.Rendering.State.HasOvrMultiViewExtension,
            ERenderBranchCapability.OpenGL => AbstractRenderer.Current?.BackendId == RendererBackendId.OpenGL,
            ERenderBranchCapability.Vulkan => AbstractRenderer.Current?.BackendId == RendererBackendId.Vulkan,
            ERenderBranchCapability.WebGpu => AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU,
            _ => false,
        };
}
