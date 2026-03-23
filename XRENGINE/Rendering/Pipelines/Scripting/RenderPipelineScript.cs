using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Code-first and text-script authoring layer for render-pipeline command chains.
/// Supports fluent builder construction and parsing a custom DSL into nested VPRC containers.
/// </summary>
public sealed class RenderPipelineScript
{
    private static readonly Lazy<ScriptCommandRegistry> s_commandRegistry = new(BuildCommandRegistry, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly IReadOnlyList<IScriptStep> _steps;

    private RenderPipelineScript(IReadOnlyList<IScriptStep> steps)
        => _steps = steps;

    public static IReadOnlyDictionary<string, Type> CommandTypes => s_commandRegistry.Value.Lookup;

    public static RenderPipelineScript Create(Action<Builder> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        Builder builder = new();
        build(builder);
        return new RenderPipelineScript(builder.BuildSteps());
    }

    public static RenderPipelineScript Parse(string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        Parser parser = new(script);
        return new RenderPipelineScript(parser.ParseScript());
    }

    public static ViewportRenderCommandContainer Compile(RenderPipeline pipeline, Action<Builder> build)
        => Create(build).Compile(pipeline);

    public static ViewportRenderCommandContainer Compile(RenderPipeline pipeline, string script)
        => Parse(script).Compile(pipeline);

    public static string Export(ViewportRenderCommandContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return ScriptExporter.Export(container);
    }

    public ViewportRenderCommandContainer Compile(RenderPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return CompileContainer(pipeline, _steps, ViewportRenderCommandContainer.BranchResourceBehavior.PreserveResources);
    }

    private static ScriptCommandRegistry BuildCommandRegistry()
    {
        Dictionary<string, Type> lookup = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<Type, string> methodNames = [];
        Type baseType = typeof(ViewportRenderCommand);

        foreach (Type type in baseType.Assembly.GetTypes())
        {
            if (type.IsAbstract || type.ContainsGenericParameters || !baseType.IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
                continue;

            string scriptMethodName = ResolveScriptCommandMethodName(type);
            TryAddLookupName(lookup, scriptMethodName, type);
            methodNames[type] = scriptMethodName;

            RenderPipelineScriptCommandAttribute? attribute = type.GetCustomAttribute<RenderPipelineScriptCommandAttribute>();
            if ((attribute?.IncludeLegacyTypeNameAlias ?? false) && !string.Equals(scriptMethodName, type.Name, StringComparison.Ordinal))
                TryAddLookupName(lookup, type.Name, type);
        }

        return new ScriptCommandRegistry(lookup, methodNames);
    }

    private static void TryAddLookupName(Dictionary<string, Type> lookup, string name, Type type)
    {
        if (!lookup.TryAdd(name, type))
            throw new InvalidOperationException($"Duplicate render command script name '{name}'. Use unique command type names or script method names.");
    }

    private static string ResolveScriptCommandMethodName(Type type)
    {
        RenderPipelineScriptCommandAttribute? attribute = type.GetCustomAttribute<RenderPipelineScriptCommandAttribute>();
        if (!string.IsNullOrWhiteSpace(attribute?.MethodName))
            return attribute.MethodName;

        return type.Name.StartsWith("VPRC_", StringComparison.Ordinal)
            ? JsonNamingPolicy.CamelCase.ConvertName(type.Name["VPRC_".Length..])
            : type.Name;
    }

    private static string GetScriptCommandMethodName(Type type)
        => s_commandRegistry.Value.MethodNamesByType.TryGetValue(type, out string? methodName)
            ? methodName
            : ResolveScriptCommandMethodName(type);

    private sealed class ScriptCommandRegistry(
        IReadOnlyDictionary<string, Type> lookup,
        IReadOnlyDictionary<Type, string> methodNamesByType)
    {
        public IReadOnlyDictionary<string, Type> Lookup { get; } = lookup;
        public IReadOnlyDictionary<Type, string> MethodNamesByType { get; } = methodNamesByType;
    }

    private static ViewportRenderCommandContainer CompileContainer(
        RenderPipeline pipeline,
        IReadOnlyList<IScriptStep> steps,
        ViewportRenderCommandContainer.BranchResourceBehavior branchResources)
    {
        ViewportRenderCommandContainer container = new(pipeline)
        {
            BranchResources = branchResources
        };

        for (int i = 0; i < steps.Count; i++)
            steps[i].CompileInto(pipeline, container);

        return container;
    }

    public sealed class Builder
    {
        private readonly List<IScriptStep> _steps = [];

        internal IReadOnlyList<IScriptStep> BuildSteps()
            => _steps.ToArray();

        public Builder Command<T>(Action<T>? configure = null) where T : ViewportRenderCommand, new()
        {
            _steps.Add(new CommandStep(() =>
            {
                T command = new();
                configure?.Invoke(command);
                return command;
            }));
            return this;
        }

        public Builder Command(Func<ViewportRenderCommand> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            _steps.Add(new CommandStep(factory));
            return this;
        }

        public Builder SetVariable(string name, bool value)
            => AddConstantSetVariable(name, value);

        public Builder SetVariable(string name, int value)
            => AddConstantSetVariable(name, value);

        public Builder SetVariable(string name, uint value)
            => AddConstantSetVariable(name, value);

        public Builder SetVariable(string name, float value)
            => AddConstantSetVariable(name, value);

        public Builder SetVariable(string name, Vector2 value)
            => AddConstantSetVariable(name, value);

        public Builder SetVariable(string name, Vector3 value)
            => AddConstantSetVariable(name, value);

        public Builder SetVariable(string name, Vector4 value)
            => AddConstantSetVariable(name, value);

        public Builder SetVariable(string name, Matrix4x4 value)
            => AddConstantSetVariable(name, value);

        public Builder SetVariable(string name, string value)
            => AddConstantSetVariable(name, value);

        public Builder SetTextureVariable(string name, string textureResourceName)
            => AddSetVariable(name, cmd => cmd.TextureResourceName = textureResourceName);

        public Builder SetFrameBufferVariable(string name, string frameBufferResourceName)
            => AddSetVariable(name, cmd => cmd.FrameBufferResourceName = frameBufferResourceName);

        public Builder SetBufferVariable(string name, string bufferResourceName)
            => AddSetVariable(name, cmd => cmd.BufferResourceName = bufferResourceName);

        public Builder SetRenderBufferVariable(string name, string renderBufferResourceName)
            => AddSetVariable(name, cmd => cmd.RenderBufferResourceName = renderBufferResourceName);

        public Builder ClearVariable(string name)
            => AddSetVariable(name, cmd => cmd.ClearVariable = true);

        public Builder If(Func<bool> condition, Action<IfBuilder> build)
        {
            ArgumentNullException.ThrowIfNull(condition);
            ArgumentNullException.ThrowIfNull(build);
            IfBuilder builder = new();
            build(builder);
            _steps.Add(builder.Build(condition));
            return this;
        }

        public Builder Switch(Func<int> selector, Action<SwitchBuilder> build)
        {
            ArgumentNullException.ThrowIfNull(selector);
            ArgumentNullException.ThrowIfNull(build);
            SwitchBuilder builder = new();
            build(builder);
            _steps.Add(builder.Build(selector));
            return this;
        }

        private Builder AddStep(IScriptStep step)
        {
            _steps.Add(step);
            return this;
        }

        private Builder AddConstantSetVariable(string name, object value)
            => AddSetVariable(name, cmd => cmd.SetLiteralValue(value));

        private Builder AddSetVariable(string name, Action<VPRC_SetVariable> configure)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            _steps.Add(new SetVariableStep(name, configure));
            return this;
        }
    }

    public sealed class IfBuilder
    {
        private BranchDefinition? _trueBranch;
        private BranchDefinition? _falseBranch;
        private Action<VPRC_IfElse>? _configure;

        public IfBuilder Configure(Action<VPRC_IfElse> configure)
        {
            _configure = configure;
            return this;
        }

        public IfBuilder Then(
            Action<Builder> build,
            ViewportRenderCommandContainer.BranchResourceBehavior branchResources = ViewportRenderCommandContainer.BranchResourceBehavior.PreserveResources)
        {
            _trueBranch = BranchDefinition.Create(build, branchResources);
            return this;
        }

        public IfBuilder Else(
            Action<Builder> build,
            ViewportRenderCommandContainer.BranchResourceBehavior branchResources = ViewportRenderCommandContainer.BranchResourceBehavior.PreserveResources)
        {
            _falseBranch = BranchDefinition.Create(build, branchResources);
            return this;
        }

        internal IfStep Build(Func<bool> condition)
            => new(condition, _trueBranch, _falseBranch, _configure);
    }

    public sealed class SwitchBuilder
    {
        private readonly Dictionary<int, BranchDefinition> _cases = [];
        private BranchDefinition? _defaultCase;
        private Action<VPRC_Switch>? _configure;

        public SwitchBuilder Configure(Action<VPRC_Switch> configure)
        {
            _configure = configure;
            return this;
        }

        public SwitchBuilder Case(
            int value,
            Action<Builder> build,
            ViewportRenderCommandContainer.BranchResourceBehavior branchResources = ViewportRenderCommandContainer.BranchResourceBehavior.PreserveResources)
        {
            _cases[value] = BranchDefinition.Create(build, branchResources);
            return this;
        }

        public SwitchBuilder Default(
            Action<Builder> build,
            ViewportRenderCommandContainer.BranchResourceBehavior branchResources = ViewportRenderCommandContainer.BranchResourceBehavior.PreserveResources)
        {
            _defaultCase = BranchDefinition.Create(build, branchResources);
            return this;
        }

        internal SwitchStep Build(Func<int> selector)
            => new(selector, new Dictionary<int, BranchDefinition>(_cases), _defaultCase, _configure);
    }

    internal interface IScriptStep
    {
        void CompileInto(RenderPipeline pipeline, ViewportRenderCommandContainer container);
    }

    private sealed class CommandStep(Func<ViewportRenderCommand> factory) : IScriptStep
    {
        private readonly Func<ViewportRenderCommand> _factory = factory;

        public void CompileInto(RenderPipeline pipeline, ViewportRenderCommandContainer container)
            => container.Add(_factory());
    }

    private sealed class CommandTypeStep(string commandTypeName, IReadOnlyList<PropertyAssignment> propertyAssignments) : IScriptStep
    {
        private readonly string _commandTypeName = commandTypeName;
        private readonly IReadOnlyList<PropertyAssignment> _propertyAssignments = propertyAssignments;

        public void CompileInto(RenderPipeline pipeline, ViewportRenderCommandContainer container)
        {
            if (!CommandTypes.TryGetValue(_commandTypeName, out Type? commandType))
                throw new InvalidOperationException($"Unknown render command type '{_commandTypeName}'.");

            ViewportRenderCommand command = (ViewportRenderCommand)(Activator.CreateInstance(commandType)
                ?? throw new InvalidOperationException($"Unable to create render command '{commandType.Name}'."));

            for (int i = 0; i < _propertyAssignments.Count; i++)
                _propertyAssignments[i].Apply(command);

            container.Add(command);
        }
    }

    private sealed class SetVariableStep(string name, Action<VPRC_SetVariable> configure) : IScriptStep
    {
        private readonly string _name = name;
        private readonly Action<VPRC_SetVariable> _configure = configure;

        public void CompileInto(RenderPipeline pipeline, ViewportRenderCommandContainer container)
        {
            VPRC_SetVariable command = new()
            {
                VariableName = _name
            };
            _configure(command);
            container.Add(command);
        }
    }

    private sealed class ExpressionSetVariableStep(string name, IExpressionNode expression) : IScriptStep
    {
        private readonly string _name = name;
        private readonly IExpressionNode _expression = expression;

        public void CompileInto(RenderPipeline pipeline, ViewportRenderCommandContainer container)
        {
            VPRC_SetVariable command = new()
            {
                VariableName = _name,
                ValueEvaluator = () => _expression.Evaluate(ScriptRuntimeContext.CreateActive())
            };
            container.Add(command);
        }
    }

    internal sealed class IfStep(
        Func<bool> condition,
        BranchDefinition? trueBranch,
        BranchDefinition? falseBranch,
        Action<VPRC_IfElse>? configure) : IScriptStep
    {
        private readonly Func<bool> _condition = condition;
        private readonly BranchDefinition? _trueBranch = trueBranch;
        private readonly BranchDefinition? _falseBranch = falseBranch;
        private readonly Action<VPRC_IfElse>? _configure = configure;

        public void CompileInto(RenderPipeline pipeline, ViewportRenderCommandContainer container)
        {
            VPRC_IfElse command = new()
            {
                ConditionEvaluator = _condition,
                TrueCommands = _trueBranch?.Compile(pipeline),
                FalseCommands = _falseBranch?.Compile(pipeline)
            };
            _configure?.Invoke(command);
            container.Add(command);
        }
    }

    internal sealed class SwitchStep(
        Func<int> selector,
        IReadOnlyDictionary<int, BranchDefinition> cases,
        BranchDefinition? defaultCase,
        Action<VPRC_Switch>? configure) : IScriptStep
    {
        private readonly Func<int> _selector = selector;
        private readonly IReadOnlyDictionary<int, BranchDefinition> _cases = cases;
        private readonly BranchDefinition? _defaultCase = defaultCase;
        private readonly Action<VPRC_Switch>? _configure = configure;

        public void CompileInto(RenderPipeline pipeline, ViewportRenderCommandContainer container)
        {
            Dictionary<int, ViewportRenderCommandContainer>? compiledCases = null;
            if (_cases.Count > 0)
            {
                compiledCases = new Dictionary<int, ViewportRenderCommandContainer>(_cases.Count);
                foreach ((int key, BranchDefinition value) in _cases)
                    compiledCases[key] = value.Compile(pipeline);
            }

            VPRC_Switch command = new()
            {
                SwitchEvaluator = _selector,
                Cases = compiledCases,
                DefaultCase = _defaultCase?.Compile(pipeline)
            };
            _configure?.Invoke(command);
            container.Add(command);
        }
    }

    private sealed class RepeatStep(IExpressionNode countExpression, BranchDefinition body) : IScriptStep
    {
        private readonly IExpressionNode _countExpression = countExpression;
        private readonly BranchDefinition _body = body;

        public void CompileInto(RenderPipeline pipeline, ViewportRenderCommandContainer container)
        {
            VPRC_Repeat command = new()
            {
                Body = _body.Compile(pipeline)
            };

            try
            {
                command.Count = Convert.ToInt32(_countExpression.GetConstantValue(typeof(int)), CultureInfo.InvariantCulture);
            }
            catch
            {
                command.CountProvider = () => Convert.ToInt32(_countExpression.Evaluate(ScriptRuntimeContext.CreateActive()), CultureInfo.InvariantCulture);
            }

            container.Add(command);
        }
    }

    private sealed class ConditionalRenderStep(IReadOnlyList<PropertyAssignment> propertyAssignments, BranchDefinition body) : IScriptStep
    {
        private readonly IReadOnlyList<PropertyAssignment> _propertyAssignments = propertyAssignments;
        private readonly BranchDefinition _body = body;

        public void CompileInto(RenderPipeline pipeline, ViewportRenderCommandContainer container)
        {
            VPRC_ConditionalRender command = new()
            {
                Body = _body.Compile(pipeline)
            };

            for (int i = 0; i < _propertyAssignments.Count; i++)
                _propertyAssignments[i].Apply(command);

            container.Add(command);
        }
    }

    private sealed class ForEachCascadeStep(IReadOnlyList<PropertyAssignment> propertyAssignments, BranchDefinition body) : IScriptStep
    {
        private readonly IReadOnlyList<PropertyAssignment> _propertyAssignments = propertyAssignments;
        private readonly BranchDefinition _body = body;

        public void CompileInto(RenderPipeline pipeline, ViewportRenderCommandContainer container)
        {
            VPRC_ForEachCascade command = new()
            {
                Body = _body.Compile(pipeline)
            };

            for (int i = 0; i < _propertyAssignments.Count; i++)
                _propertyAssignments[i].Apply(command);

            container.Add(command);
        }
    }

    internal sealed class BranchDefinition(
        IReadOnlyList<IScriptStep> steps,
        ViewportRenderCommandContainer.BranchResourceBehavior branchResources)
    {
        private readonly IReadOnlyList<IScriptStep> _steps = steps;
        private readonly ViewportRenderCommandContainer.BranchResourceBehavior _branchResources = branchResources;

        public static BranchDefinition Create(
            Action<Builder> build,
            ViewportRenderCommandContainer.BranchResourceBehavior branchResources)
        {
            ArgumentNullException.ThrowIfNull(build);
            Builder builder = new();
            build(builder);
            return new BranchDefinition(builder.BuildSteps(), branchResources);
        }

        public ViewportRenderCommandContainer Compile(RenderPipeline pipeline)
            => CompileContainer(pipeline, _steps, _branchResources);
    }

    private sealed class PropertyAssignment(string propertyName, IValueNode value)
    {
        private readonly string _propertyName = propertyName;
        private readonly IValueNode _value = value;

        public void Apply(ViewportRenderCommand command)
        {
            PropertyInfo? property = command.GetType().GetProperty(_propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property is null || !property.CanWrite)
                throw new InvalidOperationException($"Property '{_propertyName}' was not found on render command '{command.GetType().Name}'.");

            object? converted = ScriptValueConverter.ConvertTo(_value, property.PropertyType);
            property.SetValue(command, converted);
        }
    }

    private interface IValueNode
    {
        object? GetConstantValue(Type? targetType = null);
    }

    private interface IExpressionNode : IValueNode
    {
        object? Evaluate(ScriptRuntimeContext context);
    }

    private sealed class LiteralNode(object? value) : IExpressionNode
    {
        private readonly object? _value = value;

        public object? Evaluate(ScriptRuntimeContext context)
            => _value;

        public object? GetConstantValue(Type? targetType = null)
            => _value;
    }

    private sealed class IdentifierNode(string name) : IExpressionNode
    {
        private readonly string _name = name;

        public object? Evaluate(ScriptRuntimeContext context)
            => context.GetVariable(_name);

        public object? GetConstantValue(Type? targetType = null)
        {
            if (targetType is not null && targetType.IsEnum)
                return Enum.Parse(targetType, _name, ignoreCase: true);
            if (targetType == typeof(string))
                return _name;
            throw new InvalidOperationException($"Identifier '{_name}' requires runtime evaluation or an enum/string target type.");
        }
    }

    private sealed class ArrayNode(IReadOnlyList<IValueNode> items) : IValueNode
    {
        private readonly IReadOnlyList<IValueNode> _items = items;

        public object? GetConstantValue(Type? targetType = null)
            => _items;

        public IReadOnlyList<IValueNode> Items => _items;
    }

    private sealed class ConstructorNode(string name, IReadOnlyList<IValueNode> args) : IValueNode
    {
        private readonly string _name = name;
        private readonly IReadOnlyList<IValueNode> _args = args;

        public object? GetConstantValue(Type? targetType = null)
        {
            float GetFloat(int index) => Convert.ToSingle(ScriptValueConverter.ConvertTo(_args[index], typeof(float)), CultureInfo.InvariantCulture);

            return _name.ToLowerInvariant() switch
            {
                "vec2" when _args.Count == 2 => new Vector2(GetFloat(0), GetFloat(1)),
                "vec3" when _args.Count == 3 => new Vector3(GetFloat(0), GetFloat(1), GetFloat(2)),
                "vec4" when _args.Count == 4 => new Vector4(GetFloat(0), GetFloat(1), GetFloat(2), GetFloat(3)),
                "mat4" when _args.Count == 16 => new Matrix4x4(
                    GetFloat(0), GetFloat(1), GetFloat(2), GetFloat(3),
                    GetFloat(4), GetFloat(5), GetFloat(6), GetFloat(7),
                    GetFloat(8), GetFloat(9), GetFloat(10), GetFloat(11),
                    GetFloat(12), GetFloat(13), GetFloat(14), GetFloat(15)),
                _ => throw new InvalidOperationException($"Unknown script constructor '{_name}'.")
            };
        }
    }

    private sealed class UnaryExpressionNode(string op, IExpressionNode operand) : IExpressionNode
    {
        private readonly string _op = op;
        private readonly IExpressionNode _operand = operand;

        public object? Evaluate(ScriptRuntimeContext context)
        {
            object? value = _operand.Evaluate(context);
            return _op switch
            {
                "!" => !ScriptRuntimeContext.ToBoolean(value),
                "-" => -Convert.ToDouble(value, CultureInfo.InvariantCulture),
                "+" => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException($"Unsupported unary operator '{_op}'.")
            };
        }

        public object? GetConstantValue(Type? targetType = null)
            => Evaluate(ScriptRuntimeContext.Empty);
    }

    private sealed class BinaryExpressionNode(string op, IExpressionNode left, IExpressionNode right) : IExpressionNode
    {
        private readonly string _op = op;
        private readonly IExpressionNode _left = left;
        private readonly IExpressionNode _right = right;

        public object? Evaluate(ScriptRuntimeContext context)
        {
            object? leftValue = _left.Evaluate(context);
            object? rightValue = _right.Evaluate(context);

            return _op switch
            {
                "&&" => ScriptRuntimeContext.ToBoolean(leftValue) && ScriptRuntimeContext.ToBoolean(rightValue),
                "||" => ScriptRuntimeContext.ToBoolean(leftValue) || ScriptRuntimeContext.ToBoolean(rightValue),
                "==" => Equals(leftValue, rightValue),
                "!=" => !Equals(leftValue, rightValue),
                "<" => Compare(leftValue, rightValue) < 0,
                ">" => Compare(leftValue, rightValue) > 0,
                "<=" => Compare(leftValue, rightValue) <= 0,
                ">=" => Compare(leftValue, rightValue) >= 0,
                "+" => Add(leftValue, rightValue),
                "-" => Convert.ToDouble(leftValue, CultureInfo.InvariantCulture) - Convert.ToDouble(rightValue, CultureInfo.InvariantCulture),
                "*" => Convert.ToDouble(leftValue, CultureInfo.InvariantCulture) * Convert.ToDouble(rightValue, CultureInfo.InvariantCulture),
                "/" => Convert.ToDouble(leftValue, CultureInfo.InvariantCulture) / Convert.ToDouble(rightValue, CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException($"Unsupported binary operator '{_op}'.")
            };
        }

        public object? GetConstantValue(Type? targetType = null)
            => Evaluate(ScriptRuntimeContext.Empty);

        private static int Compare(object? leftValue, object? rightValue)
        {
            double left = Convert.ToDouble(leftValue, CultureInfo.InvariantCulture);
            double right = Convert.ToDouble(rightValue, CultureInfo.InvariantCulture);
            return left.CompareTo(right);
        }

        private static object Add(object? leftValue, object? rightValue)
        {
            if (leftValue is string ls && rightValue is string rs)
                return string.Concat(ls, rs);
            if (leftValue is string ls2)
                return string.Concat(ls2, rightValue?.ToString());
            if (rightValue is string rs2)
                return string.Concat(leftValue?.ToString(), rs2);

            double result = Convert.ToDouble(leftValue, CultureInfo.InvariantCulture) + Convert.ToDouble(rightValue, CultureInfo.InvariantCulture);
            if (leftValue is int && rightValue is int)
                return (int)result;
            if (leftValue is uint && rightValue is uint)
                return (uint)result;
            if (leftValue is float || rightValue is float)
                return (float)result;
            return result;
        }
    }

    private sealed class IndexExpressionNode(IExpressionNode target, IExpressionNode index) : IExpressionNode
    {
        private readonly IExpressionNode _target = target;
        private readonly IExpressionNode _index = index;

        public object? Evaluate(ScriptRuntimeContext context)
        {
            object? target = _target.Evaluate(context);
            int index = Convert.ToInt32(_index.Evaluate(context), CultureInfo.InvariantCulture);

            return target switch
            {
                null => null,
                Array array => index >= 0 && index < array.Length ? array.GetValue(index) : null,
                System.Collections.IList list => index >= 0 && index < list.Count ? list[index] : null,
                IReadOnlyList<object?> readOnlyObjects => index >= 0 && index < readOnlyObjects.Count ? readOnlyObjects[index] : null,
                string text => index >= 0 && index < text.Length ? text[index].ToString() : null,
                _ => throw new InvalidOperationException($"Type '{target.GetType().Name}' does not support script indexing.")
            };
        }

        public object? GetConstantValue(Type? targetType = null)
            => Evaluate(ScriptRuntimeContext.Empty);
    }

    private sealed class ScriptRuntimeContext(XRRenderPipelineInstance? instance)
    {
        public static ScriptRuntimeContext Empty { get; } = new(null);

        public static ScriptRuntimeContext CreateActive()
            => new(ViewportRenderCommand.ActivePipelineInstance);

        public object? GetVariable(string name)
        {
            if (instance is null)
                return null;

            XRRenderPipelineInstance.PipelineVariableStore variables = instance.Variables;
            if (variables.TryGet(name, out bool boolValue))
                return boolValue;
            if (variables.TryGet(name, out int intValue))
                return intValue;
            if (variables.TryGet(name, out uint uintValue))
                return uintValue;
            if (variables.TryGet(name, out float floatValue))
                return floatValue;
            if (variables.TryGet(name, out Vector2 vector2Value))
                return vector2Value;
            if (variables.TryGet(name, out Vector3 vector3Value))
                return vector3Value;
            if (variables.TryGet(name, out Vector4 vector4Value))
                return vector4Value;
            if (variables.TryGet(name, out Matrix4x4 matrixValue))
                return matrixValue;
            if (variables.TryGet(name, out string? stringValue))
                return stringValue;
            if (variables.TryResolveTexture(instance.Resources, name, out XRTexture? texture))
                return texture;
            if (variables.TryResolveFrameBuffer(instance.Resources, name, out XRFrameBuffer? frameBuffer))
                return frameBuffer;
            if (variables.TryResolveBuffer(instance.Resources, name, out XRDataBuffer? buffer))
                return buffer;
            if (variables.TryResolveRenderBuffer(instance.Resources, name, out XRRenderBuffer? renderBuffer))
                return renderBuffer;

            return null;
        }

        public static bool ToBoolean(object? value)
            => value switch
            {
                null => false,
                bool boolValue => boolValue,
                int intValue => intValue != 0,
                uint uintValue => uintValue != 0,
                float floatValue => Math.Abs(floatValue) > float.Epsilon,
                double doubleValue => Math.Abs(doubleValue) > double.Epsilon,
                string stringValue when bool.TryParse(stringValue, out bool parsed) => parsed,
                string stringValue => !string.IsNullOrWhiteSpace(stringValue),
                _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
            };
    }

    private static class ScriptValueConverter
    {
        public static object? ConvertTo(IValueNode value, Type targetType)
        {
            Type destinationType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            object? raw = value.GetConstantValue(destinationType);
            if (raw is null)
                return null;

            if (destinationType.IsInstanceOfType(raw))
                return raw;

            if (raw is IReadOnlyList<IValueNode> listValues)
                return ConvertList(listValues, destinationType);

            if (destinationType.IsEnum)
            {
                if (raw is string enumName)
                    return Enum.Parse(destinationType, enumName, ignoreCase: true);
                return Enum.ToObject(destinationType, System.Convert.ToInt64(raw, CultureInfo.InvariantCulture));
            }

            if (destinationType == typeof(string))
                return raw.ToString();
            if (destinationType == typeof(bool))
                return System.Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
            if (destinationType == typeof(int))
                return System.Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            if (destinationType == typeof(uint))
                return System.Convert.ToUInt32(raw, CultureInfo.InvariantCulture);
            if (destinationType == typeof(float))
                return System.Convert.ToSingle(raw, CultureInfo.InvariantCulture);
            if (destinationType == typeof(double))
                return System.Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (destinationType == typeof(Vector2) || destinationType == typeof(Vector3) || destinationType == typeof(Vector4) || destinationType == typeof(Matrix4x4))
                return raw;

            TypeConverter converter = TypeDescriptor.GetConverter(destinationType);
            if (converter.CanConvertFrom(raw.GetType()))
                return converter.ConvertFrom(null, CultureInfo.InvariantCulture, raw);
            if (raw is string rawString && converter.CanConvertFrom(typeof(string)))
                return converter.ConvertFromInvariantString(rawString);

            return System.Convert.ChangeType(raw, destinationType, CultureInfo.InvariantCulture);
        }

        private static object ConvertList(IReadOnlyList<IValueNode> items, Type destinationType)
        {
            if (destinationType.IsArray)
            {
                Type elementType = destinationType.GetElementType() ?? typeof(object);
                Array array = Array.CreateInstance(elementType, items.Count);
                for (int i = 0; i < items.Count; i++)
                    array.SetValue(ConvertTo(items[i], elementType), i);
                return array;
            }

            Type? element = destinationType.IsGenericType ? destinationType.GetGenericArguments()[0] : null;
            if (element is null)
                throw new InvalidOperationException($"Unsupported list destination type '{destinationType.Name}'.");

            Type listType = typeof(List<>).MakeGenericType(element);
            var list = (System.Collections.IList)(Activator.CreateInstance(listType)
                ?? throw new InvalidOperationException($"Unable to create list type '{listType.Name}'."));
            for (int i = 0; i < items.Count; i++)
                list.Add(ConvertTo(items[i], element));
            return list;
        }
    }

    private static class ScriptExporter
    {
        private static readonly HashSet<string> s_ignoredProperties =
        [
            nameof(ViewportRenderCommand.CommandContainer),
            nameof(ViewportRenderCommand.ParentPipeline),
            nameof(VPRC_IfElse.TrueCommands),
            nameof(VPRC_IfElse.FalseCommands),
            nameof(VPRC_IfElse.ConditionEvaluator),
            nameof(VPRC_Switch.Cases),
            nameof(VPRC_Switch.DefaultCase),
            nameof(VPRC_Switch.SwitchEvaluator),
            nameof(VPRC_Repeat.Body),
            nameof(VPRC_Repeat.CountProvider),
            nameof(VPRC_Repeat.IterationConfigurator),
            nameof(VPRC_ConditionalRender.Body),
            nameof(VPRC_ForEachCascade.Body),
            nameof(VPRC_SetVariable.ValueEvaluator),
        ];

        public static string Export(ViewportRenderCommandContainer container)
        {
            System.Text.StringBuilder builder = new();
            AppendContainer(builder, container, 0);
            return builder.ToString();
        }

        private static void AppendContainer(System.Text.StringBuilder builder, ViewportRenderCommandContainer container, int depth)
        {
            for (int i = 0; i < container.Count; i++)
                AppendCommand(builder, container[i], depth);
        }

        private static void AppendCommand(System.Text.StringBuilder builder, ViewportRenderCommand command, int depth)
        {
            string indent = new(' ', depth * 4);
            switch (command)
            {
                case VPRC_IfElse ifElse:
                    builder.Append(indent).AppendLine("if (false) {");
                    if (ifElse.TrueCommands is not null)
                        AppendContainer(builder, ifElse.TrueCommands, depth + 1);
                    builder.Append(indent).Append('}');
                    if (ifElse.FalseCommands is not null)
                    {
                        builder.AppendLine(" else {");
                        AppendContainer(builder, ifElse.FalseCommands, depth + 1);
                        builder.Append(indent).AppendLine("}");
                    }
                    else
                    {
                        builder.AppendLine();
                    }
                    return;

                case VPRC_Switch switchCommand:
                    builder.Append(indent).AppendLine("switch (0) {");
                    if (switchCommand.Cases is not null)
                    {
                        foreach (KeyValuePair<int, ViewportRenderCommandContainer> pair in switchCommand.Cases.OrderBy(static x => x.Key))
                        {
                            builder.Append(indent).Append("    case ").Append(pair.Key.ToString(CultureInfo.InvariantCulture)).AppendLine(": {");
                            AppendContainer(builder, pair.Value, depth + 2);
                            builder.Append(indent).AppendLine("    }");
                        }
                    }
                    if (switchCommand.DefaultCase is not null)
                    {
                        builder.Append(indent).AppendLine("    default: {");
                        AppendContainer(builder, switchCommand.DefaultCase, depth + 2);
                        builder.Append(indent).AppendLine("    }");
                    }
                    builder.Append(indent).AppendLine("}");
                    return;

                case VPRC_Repeat repeat:
                    builder.Append(indent).Append("repeat(")
                        .Append(repeat.CountProvider is null
                            ? repeat.Count.ToString(CultureInfo.InvariantCulture)
                            : "1")
                        .AppendLine(") {");
                    if (repeat.Body is not null)
                        AppendContainer(builder, repeat.Body, depth + 1);
                    builder.Append(indent).AppendLine("}");
                    return;

                case VPRC_ConditionalRender conditionalRender:
                    builder.Append(indent).Append("when(");
                    AppendProperties(builder, conditionalRender);
                    builder.AppendLine(") {");
                    if (conditionalRender.Body is not null)
                        AppendContainer(builder, conditionalRender.Body, depth + 1);
                    builder.Append(indent).AppendLine("}");
                    return;

                case VPRC_ForEachCascade forEachCascade:
                    builder.Append(indent).Append("foreach_cascade(");
                    AppendProperties(builder, forEachCascade);
                    builder.AppendLine(") {");
                    if (forEachCascade.Body is not null)
                        AppendContainer(builder, forEachCascade.Body, depth + 1);
                    builder.Append(indent).AppendLine("}");
                    return;
            }

            builder.Append(indent).Append(GetScriptCommandMethodName(command.GetType())).Append('(');
            AppendProperties(builder, command);
            builder.AppendLine(");");
        }

        private static void AppendProperties(System.Text.StringBuilder builder, ViewportRenderCommand command)
        {
            object? defaultCommand = null;
            try
            {
                defaultCommand = Activator.CreateInstance(command.GetType());
            }
            catch
            {
            }

            bool wroteAny = false;
            foreach (PropertyInfo property in command.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0 || s_ignoredProperties.Contains(property.Name))
                    continue;

                object? value = property.GetValue(command);
                object? defaultValue = defaultCommand is null ? null : property.GetValue(defaultCommand);
                if (EqualsSerializedValue(value, defaultValue))
                    continue;

                if (!TrySerializeValue(value, out string? serialized))
                    continue;

                if (wroteAny)
                    builder.Append(", ");
                builder.Append(property.Name).Append('=').Append(serialized);
                wroteAny = true;
            }
        }

        private static bool EqualsSerializedValue(object? left, object? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is null || right is null)
                return false;

            if (left is System.Collections.IEnumerable leftEnumerable && right is System.Collections.IEnumerable rightEnumerable && left is not string && right is not string)
            {
                List<object?> leftItems = [];
                List<object?> rightItems = [];
                foreach (object? item in leftEnumerable)
                    leftItems.Add(item);
                foreach (object? item in rightEnumerable)
                    rightItems.Add(item);
                return leftItems.SequenceEqual(rightItems);
            }

            return Equals(left, right);
        }

        private static bool TrySerializeValue(object? value, out string? serialized)
        {
            switch (value)
            {
                case null:
                    serialized = "null";
                    return true;
                case bool boolValue:
                    serialized = boolValue ? "true" : "false";
                    return true;
                case string stringValue:
                    serialized = Quote(stringValue);
                    return true;
                case Enum enumValue:
                    serialized = enumValue.ToString();
                    return true;
                case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    serialized = Convert.ToString(value, CultureInfo.InvariantCulture);
                    return true;
                case Vector2 vector2:
                    serialized = $"vec2({FormatNumber(vector2.X)}, {FormatNumber(vector2.Y)})";
                    return true;
                case Vector3 vector3:
                    serialized = $"vec3({FormatNumber(vector3.X)}, {FormatNumber(vector3.Y)}, {FormatNumber(vector3.Z)})";
                    return true;
                case Vector4 vector4:
                    serialized = $"vec4({FormatNumber(vector4.X)}, {FormatNumber(vector4.Y)}, {FormatNumber(vector4.Z)}, {FormatNumber(vector4.W)})";
                    return true;
                case Matrix4x4 matrix:
                    serialized = $"mat4({FormatNumber(matrix.M11)}, {FormatNumber(matrix.M12)}, {FormatNumber(matrix.M13)}, {FormatNumber(matrix.M14)}, {FormatNumber(matrix.M21)}, {FormatNumber(matrix.M22)}, {FormatNumber(matrix.M23)}, {FormatNumber(matrix.M24)}, {FormatNumber(matrix.M31)}, {FormatNumber(matrix.M32)}, {FormatNumber(matrix.M33)}, {FormatNumber(matrix.M34)}, {FormatNumber(matrix.M41)}, {FormatNumber(matrix.M42)}, {FormatNumber(matrix.M43)}, {FormatNumber(matrix.M44)})";
                    return true;
            }

            if (value is System.Collections.IEnumerable enumerable && value is not string)
            {
                List<string> items = [];
                foreach (object? item in enumerable)
                {
                    if (!TrySerializeValue(item, out string? itemText))
                    {
                        serialized = null;
                        return false;
                    }
                    items.Add(itemText!);
                }
                serialized = $"[{string.Join(", ", items)}]";
                return true;
            }

            serialized = null;
            return false;
        }

        private static string Quote(string value)
            => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

        private static string FormatNumber(float value)
            => value.ToString("0.0###", CultureInfo.InvariantCulture);
    }

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private int _index;

        public Parser(string script)
            => _tokens = Tokenize(script);

        public IReadOnlyList<IScriptStep> ParseScript()
        {
            List<IScriptStep> steps = [];
            while (!Is(TokenKind.EndOfFile))
                steps.Add(ParseStatement());
            return steps;
        }

        private IScriptStep ParseStatement()
        {
            if (IsKeyword("set"))
            {
                Advance();
                return ParseSetStatement();
            }

            if (IsKeyword("clear") && Peek(1).Kind != TokenKind.OpenParen)
            {
                Advance();
                return ParseClearStatement();
            }

            if (IsKeyword("if"))
            {
                Advance();
                return ParseIfStatement();
            }

            if (IsKeyword("switch"))
            {
                Advance();
                return ParseSwitchStatement();
            }

            if (IsKeyword("repeat"))
            {
                Advance();
                return ParseRepeatStatement();
            }

            if (IsKeyword("when"))
            {
                Advance();
                return ParseConditionalRenderStatement();
            }

            if (IsKeyword("foreach_cascade"))
            {
                Advance();
                return ParseForEachCascadeStatement();
            }

            return ParseCommandStatement();
        }

        private IScriptStep ParseSetStatement()
        {
            string variableName = Consume(TokenKind.Identifier, "Expected variable name after 'set'.").Text;
            Consume(TokenKind.Equals, "Expected '=' in set statement.");
            IExpressionNode expression = ParseExpression();
            Consume(TokenKind.Semicolon, "Expected ';' after set statement.");

            if (TryBuildConstantVariableStep(variableName, expression, out IScriptStep? step))
                return step;

            return new ExpressionSetVariableStep(variableName, expression);
        }

        private static bool TryBuildConstantVariableStep(string variableName, IExpressionNode expression, out IScriptStep step)
        {
            try
            {
                object? value = expression.GetConstantValue();
                step = new SetVariableStep(variableName, cmd => cmd.SetLiteralValue(value));
                return true;
            }
            catch
            {
                step = null!;
                return false;
            }
        }

        private IScriptStep ParseClearStatement()
        {
            string variableName = Consume(TokenKind.Identifier, "Expected variable name after 'clear'.").Text;
            Consume(TokenKind.Semicolon, "Expected ';' after clear statement.");
            return new SetVariableStep(variableName, cmd => cmd.ClearVariable = true);
        }

        private IScriptStep ParseIfStatement()
        {
            Consume(TokenKind.OpenParen, "Expected '(' after 'if'.");
            IExpressionNode condition = ParseExpression();
            Consume(TokenKind.CloseParen, "Expected ')' after if condition.");
            BranchDefinition trueBranch = ParseBranchDefinition();
            BranchDefinition? falseBranch = null;
            if (MatchKeyword("else"))
                falseBranch = ParseBranchDefinition();

            return new IfStep(
                () => ScriptRuntimeContext.ToBoolean(condition.Evaluate(ScriptRuntimeContext.CreateActive())),
                trueBranch,
                falseBranch,
                null);
        }

        private IScriptStep ParseSwitchStatement()
        {
            Consume(TokenKind.OpenParen, "Expected '(' after 'switch'.");
            IExpressionNode selectorExpression = ParseExpression();
            Consume(TokenKind.CloseParen, "Expected ')' after switch selector.");
            Consume(TokenKind.OpenBrace, "Expected '{' to begin switch body.");

            Dictionary<int, BranchDefinition> cases = [];
            BranchDefinition? defaultCase = null;
            while (!Match(TokenKind.CloseBrace))
            {
                if (MatchKeyword("case"))
                {
                    int value = Convert.ToInt32(ParseExpression().GetConstantValue(typeof(int)), CultureInfo.InvariantCulture);
                    Consume(TokenKind.Colon, "Expected ':' after switch case value.");
                    cases[value] = ParseBranchDefinition(requireBraces: true);
                }
                else if (MatchKeyword("default"))
                {
                    Consume(TokenKind.Colon, "Expected ':' after switch default.");
                    defaultCase = ParseBranchDefinition(requireBraces: true);
                }
                else
                {
                    throw Error(Peek(), "Expected 'case' or 'default' in switch block.");
                }
            }

            return new SwitchStep(
                () => Convert.ToInt32(selectorExpression.Evaluate(ScriptRuntimeContext.CreateActive()), CultureInfo.InvariantCulture),
                cases,
                defaultCase,
                null);
        }

        private IScriptStep ParseCommandStatement()
        {
            string commandTypeName = ConsumeCommandName("Expected render command method name.").Text;
            List<PropertyAssignment> properties = ParsePropertyAssignments();
            Consume(TokenKind.Semicolon, "Expected ';' after command statement.");
            return new CommandTypeStep(commandTypeName, properties);
        }

        private IScriptStep ParseRepeatStatement()
        {
            Consume(TokenKind.OpenParen, "Expected '(' after 'repeat'.");
            IExpressionNode countExpression = ParseExpression();
            Consume(TokenKind.CloseParen, "Expected ')' after repeat count.");
            return new RepeatStep(countExpression, ParseBranchDefinition());
        }

        private IScriptStep ParseConditionalRenderStatement()
        {
            List<PropertyAssignment> properties = ParsePropertyAssignments();
            return new ConditionalRenderStep(properties, ParseBranchDefinition());
        }

        private IScriptStep ParseForEachCascadeStatement()
        {
            List<PropertyAssignment> properties = ParsePropertyAssignments();
            return new ForEachCascadeStep(properties, ParseBranchDefinition());
        }

        private BranchDefinition ParseBranchDefinition(bool requireBraces = true)
        {
            if (requireBraces)
                Consume(TokenKind.OpenBrace, "Expected '{' to begin branch body.");
            else if (!Match(TokenKind.OpenBrace))
                throw Error(Peek(), "Expected '{' to begin branch body.");

            List<IScriptStep> steps = [];
            while (!Match(TokenKind.CloseBrace))
                steps.Add(ParseStatement());
            return new BranchDefinition(steps, ViewportRenderCommandContainer.BranchResourceBehavior.PreserveResources);
        }

        private IValueNode ParseValue()
        {
            if (Match(TokenKind.OpenBracket))
            {
                List<IValueNode> items = [];
                if (!Match(TokenKind.CloseBracket))
                {
                    do items.Add(ParseValue());
                    while (Match(TokenKind.Comma));
                    Consume(TokenKind.CloseBracket, "Expected ']' after list literal.");
                }
                return new ArrayNode(items);
            }

            Token token = Peek();
            if (token.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.OpenParen)
            {
                string constructorName = Advance().Text;
                Consume(TokenKind.OpenParen, "Expected '(' after constructor name.");
                List<IValueNode> args = [];
                if (!Match(TokenKind.CloseParen))
                {
                    do args.Add(ParseValue());
                    while (Match(TokenKind.Comma));
                    Consume(TokenKind.CloseParen, "Expected ')' after constructor arguments.");
                }
                return new ConstructorNode(constructorName, args);
            }

            return ParseExpression();
        }

        private List<PropertyAssignment> ParsePropertyAssignments()
        {
            List<PropertyAssignment> properties = [];
            if (!Match(TokenKind.OpenParen))
                return properties;

            if (!Match(TokenKind.CloseParen))
            {
                do
                {
                    string propertyName = Consume(TokenKind.Identifier, "Expected command property name.").Text;
                    Consume(TokenKind.Equals, "Expected '=' after command property name.");
                    IValueNode value = ParseValue();
                    properties.Add(new PropertyAssignment(propertyName, value));
                }
                while (Match(TokenKind.Comma));

                Consume(TokenKind.CloseParen, "Expected ')' after command property list.");
            }

            return properties;
        }

        private IExpressionNode ParseExpression()
            => ParseLogicalOr();

        private IExpressionNode ParseLogicalOr()
        {
            IExpressionNode expr = ParseLogicalAnd();
            while (Match(TokenKind.OrOr))
                expr = new BinaryExpressionNode("||", expr, ParseLogicalAnd());
            return expr;
        }

        private IExpressionNode ParseLogicalAnd()
        {
            IExpressionNode expr = ParseEquality();
            while (Match(TokenKind.AndAnd))
                expr = new BinaryExpressionNode("&&", expr, ParseEquality());
            return expr;
        }

        private IExpressionNode ParseEquality()
        {
            IExpressionNode expr = ParseComparison();
            while (true)
            {
                if (Match(TokenKind.EqualEqual))
                    expr = new BinaryExpressionNode("==", expr, ParseComparison());
                else if (Match(TokenKind.BangEqual))
                    expr = new BinaryExpressionNode("!=", expr, ParseComparison());
                else
                    break;
            }
            return expr;
        }

        private IExpressionNode ParseComparison()
        {
            IExpressionNode expr = ParseTerm();
            while (true)
            {
                if (Match(TokenKind.Less))
                    expr = new BinaryExpressionNode("<", expr, ParseTerm());
                else if (Match(TokenKind.LessEqual))
                    expr = new BinaryExpressionNode("<=", expr, ParseTerm());
                else if (Match(TokenKind.Greater))
                    expr = new BinaryExpressionNode(">", expr, ParseTerm());
                else if (Match(TokenKind.GreaterEqual))
                    expr = new BinaryExpressionNode(">=", expr, ParseTerm());
                else
                    break;
            }
            return expr;
        }

        private IExpressionNode ParseTerm()
        {
            IExpressionNode expr = ParseFactor();
            while (true)
            {
                if (Match(TokenKind.Plus))
                    expr = new BinaryExpressionNode("+", expr, ParseFactor());
                else if (Match(TokenKind.Minus))
                    expr = new BinaryExpressionNode("-", expr, ParseFactor());
                else
                    break;
            }
            return expr;
        }

        private IExpressionNode ParseFactor()
        {
            IExpressionNode expr = ParseUnary();
            while (true)
            {
                if (Match(TokenKind.Star))
                    expr = new BinaryExpressionNode("*", expr, ParseUnary());
                else if (Match(TokenKind.Slash))
                    expr = new BinaryExpressionNode("/", expr, ParseUnary());
                else
                    break;
            }
            return expr;
        }

        private IExpressionNode ParseUnary()
        {
            if (Match(TokenKind.Bang))
                return new UnaryExpressionNode("!", ParseUnary());
            if (Match(TokenKind.Minus))
                return new UnaryExpressionNode("-", ParseUnary());
            if (Match(TokenKind.Plus))
                return new UnaryExpressionNode("+", ParseUnary());
            return ParsePostfix();
        }

        private IExpressionNode ParsePostfix()
        {
            IExpressionNode expression = ParsePrimary();
            while (Match(TokenKind.OpenBracket))
            {
                IExpressionNode index = ParseExpression();
                Consume(TokenKind.CloseBracket, "Expected ']' after index expression.");
                expression = new IndexExpressionNode(expression, index);
            }

            return expression;
        }

        private IExpressionNode ParsePrimary()
        {
            if (Match(TokenKind.OpenParen))
            {
                IExpressionNode expr = ParseExpression();
                Consume(TokenKind.CloseParen, "Expected ')' after expression.");
                return expr;
            }

            Token token = Advance();
            return token.Kind switch
            {
                TokenKind.Number => new LiteralNode(ParseNumericLiteral(token.Text)),
                TokenKind.String => new LiteralNode(token.Text),
                TokenKind.Keyword when string.Equals(token.Text, "true", StringComparison.OrdinalIgnoreCase) => new LiteralNode(true),
                TokenKind.Keyword when string.Equals(token.Text, "false", StringComparison.OrdinalIgnoreCase) => new LiteralNode(false),
                TokenKind.Keyword when string.Equals(token.Text, "null", StringComparison.OrdinalIgnoreCase) => new LiteralNode(null),
                TokenKind.Identifier => new IdentifierNode(token.Text),
                _ => throw Error(token, "Unexpected token in expression.")
            };
        }

        private static object ParseNumericLiteral(string text)
        {
            if (text.EndsWith("u", StringComparison.OrdinalIgnoreCase))
                return uint.Parse(text[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (text.Contains('.') || text.Contains('e', StringComparison.OrdinalIgnoreCase))
                return float.Parse(text.TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture);
            return int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private bool MatchKeyword(string keyword)
        {
            if (Peek().Kind != TokenKind.Keyword || !string.Equals(Peek().Text, keyword, StringComparison.OrdinalIgnoreCase))
                return false;

            _index++;
            return true;
        }

        private bool IsKeyword(string keyword)
            => Peek().Kind == TokenKind.Keyword && string.Equals(Peek().Text, keyword, StringComparison.OrdinalIgnoreCase);

        private bool Match(TokenKind kind)
        {
            if (!Is(kind))
                return false;
            _index++;
            return true;
        }

        private bool Is(TokenKind kind)
            => Peek().Kind == kind;

        private Token Consume(TokenKind kind, string message)
        {
            if (Is(kind))
                return Advance();
            throw Error(Peek(), message);
        }

        private Token ConsumeCommandName(string message)
        {
            Token token = Peek();
            if (token.Kind is TokenKind.Identifier or TokenKind.Keyword)
                return Advance();

            throw Error(token, message);
        }

        private Token Advance()
            => _tokens[_index++];

        private Token Peek(int offset = 0)
        {
            int index = Math.Min(_index + offset, _tokens.Count - 1);
            return _tokens[index];
        }

        private InvalidOperationException Error(Token token, string message)
            => new($"{message} (line {token.Line}, column {token.Column})");

        private static List<Token> Tokenize(string script)
        {
            List<Token> tokens = [];
            int index = 0;
            int line = 1;
            int column = 1;

            void AdvancePosition(char ch)
            {
                index++;
                if (ch == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            while (index < script.Length)
            {
                char ch = script[index];
                if (char.IsWhiteSpace(ch))
                {
                    AdvancePosition(ch);
                    continue;
                }

                if (ch == '/' && index + 1 < script.Length && script[index + 1] == '/')
                {
                    while (index < script.Length && script[index] != '\n')
                        AdvancePosition(script[index]);
                    continue;
                }

                int tokenLine = line;
                int tokenColumn = column;

                if (char.IsLetter(ch) || ch == '_')
                {
                    int start = index;
                    while (index < script.Length && (char.IsLetterOrDigit(script[index]) || script[index] == '_'))
                        AdvancePosition(script[index]);
                    string text = script[start..index];
                    TokenKind kind = text switch
                    {
                        "set" or "clear" or "if" or "else" or "switch" or "case" or "default" or "repeat" or "when" or "foreach_cascade" or "true" or "false" or "null" => TokenKind.Keyword,
                        _ => TokenKind.Identifier
                    };
                    tokens.Add(new Token(kind, text, tokenLine, tokenColumn));
                    continue;
                }

                if (char.IsDigit(ch) || (ch == '.' && index + 1 < script.Length && char.IsDigit(script[index + 1])))
                {
                    int start = index;
                    bool hasDot = ch == '.';
                    AdvancePosition(ch);
                    while (index < script.Length)
                    {
                        char current = script[index];
                        if (char.IsDigit(current))
                        {
                            AdvancePosition(current);
                            continue;
                        }
                        if (!hasDot && current == '.')
                        {
                            hasDot = true;
                            AdvancePosition(current);
                            continue;
                        }
                        if ((current == 'e' || current == 'E') && index + 1 < script.Length)
                        {
                            AdvancePosition(current);
                            if (index < script.Length && (script[index] == '+' || script[index] == '-'))
                                AdvancePosition(script[index]);
                            continue;
                        }
                        if (current == 'f' || current == 'F' || current == 'u' || current == 'U')
                        {
                            AdvancePosition(current);
                        }
                        break;
                    }
                    tokens.Add(new Token(TokenKind.Number, script[start..index], tokenLine, tokenColumn));
                    continue;
                }

                if (ch == '"')
                {
                    AdvancePosition(ch);
                    System.Text.StringBuilder builder = new();
                    while (index < script.Length)
                    {
                        char current = script[index];
                        AdvancePosition(current);
                        if (current == '"')
                            break;
                        if (current == '\\' && index < script.Length)
                        {
                            char escaped = script[index];
                            AdvancePosition(escaped);
                            builder.Append(escaped switch
                            {
                                'n' => '\n',
                                'r' => '\r',
                                't' => '\t',
                                '"' => '"',
                                '\\' => '\\',
                                _ => escaped
                            });
                        }
                        else
                        {
                            builder.Append(current);
                        }
                    }
                    tokens.Add(new Token(TokenKind.String, builder.ToString(), tokenLine, tokenColumn));
                    continue;
                }

                if (index + 1 < script.Length)
                {
                    string twoChars = script.Substring(index, 2);
                    TokenKind? compoundKind = twoChars switch
                    {
                        "&&" => TokenKind.AndAnd,
                        "||" => TokenKind.OrOr,
                        "==" => TokenKind.EqualEqual,
                        "!=" => TokenKind.BangEqual,
                        "<=" => TokenKind.LessEqual,
                        ">=" => TokenKind.GreaterEqual,
                        _ => null
                    };
                    if (compoundKind.HasValue)
                    {
                        tokens.Add(new Token(compoundKind.Value, twoChars, tokenLine, tokenColumn));
                        AdvancePosition(script[index]);
                        AdvancePosition(script[index]);
                        continue;
                    }
                }

                TokenKind singleCharKind = ch switch
                {
                    '(' => TokenKind.OpenParen,
                    ')' => TokenKind.CloseParen,
                    '{' => TokenKind.OpenBrace,
                    '}' => TokenKind.CloseBrace,
                    '[' => TokenKind.OpenBracket,
                    ']' => TokenKind.CloseBracket,
                    ',' => TokenKind.Comma,
                    ':' => TokenKind.Colon,
                    ';' => TokenKind.Semicolon,
                    '=' => TokenKind.Equals,
                    '!' => TokenKind.Bang,
                    '+' => TokenKind.Plus,
                    '-' => TokenKind.Minus,
                    '*' => TokenKind.Star,
                    '/' => TokenKind.Slash,
                    '<' => TokenKind.Less,
                    '>' => TokenKind.Greater,
                    _ => throw new InvalidOperationException($"Unexpected character '{ch}' in render pipeline script at line {line}, column {column}.")
                };
                tokens.Add(new Token(singleCharKind, ch.ToString(), tokenLine, tokenColumn));
                AdvancePosition(ch);
            }

            tokens.Add(new Token(TokenKind.EndOfFile, string.Empty, line, column));
            return tokens;
        }
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);

    private enum TokenKind
    {
        Identifier,
        Keyword,
        Number,
        String,
        OpenParen,
        CloseParen,
        OpenBrace,
        CloseBrace,
        OpenBracket,
        CloseBracket,
        Comma,
        Colon,
        Semicolon,
        Equals,
        Bang,
        Plus,
        Minus,
        Star,
        Slash,
        Less,
        Greater,
        LessEqual,
        GreaterEqual,
        EqualEqual,
        BangEqual,
        AndAnd,
        OrOr,
        EndOfFile
    }
}
