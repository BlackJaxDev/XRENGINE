using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

// Script steps that compile into render commands.
public sealed partial class RenderPipelineScript
{
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

    private sealed class CommandTypeStep(string commandTypeName, IReadOnlyList<PropertyAssignment> propertyAssignments, int line = 0, int column = 0) : IScriptStep
    {
        private readonly string _commandTypeName = commandTypeName;
        private readonly IReadOnlyList<PropertyAssignment> _propertyAssignments = propertyAssignments;

        public void CompileInto(RenderPipeline pipeline, ViewportRenderCommandContainer container)
        {
            if (!CommandTypes.TryGetValue(_commandTypeName, out Type? commandType))
                throw new ScriptSyntaxException($"Unknown render command type '{_commandTypeName}'.", line, column);

            ViewportRenderCommand? command = FindUnattachedPairedPop(container, commandType);
            if (command is null && (!ViewportRenderCommandContainer.TryCreateRegisteredCommand(commandType, out command) || command is null))
            {
                if (XRRuntimeEnvironment.IsPublishedBuild)
                    throw new InvalidOperationException($"No registered render command factory for '{commandType.Name}'.");

                command = (ViewportRenderCommand)(Activator.CreateInstance(commandType)
                    ?? throw new InvalidOperationException($"Unable to create render command '{commandType.Name}'."));
            }

            for (int i = 0; i < _propertyAssignments.Count; i++)
                _propertyAssignments[i].Apply(command);

            container.Add(command);
        }

        /// <summary>
        /// Returns the pop command of the innermost open push of the same pop type in <paramref name="container"/>. A push
        /// owns its pop instance, so the script must reuse that instance to keep the pair intact.
        /// </summary>
        private static ViewportRenderCommand? FindUnattachedPairedPop(ViewportRenderCommandContainer container, Type commandType)
        {
            if (!typeof(ViewportPopStateRenderCommand).IsAssignableFrom(commandType))
                return null;

            for (int i = container.Count - 1; i >= 0; i--)
            {
                if (container[i] is not ViewportStateRenderCommandBase push)
                    continue;

                ViewportPopStateRenderCommand pop = push.PairedPopCommand;
                if (pop.GetType() == commandType && pop.CommandContainer is null)
                    return pop;
            }

            return null;
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
        Func<bool>? evaluator,
        RenderBranchCondition? condition,
        string? label,
        BranchDefinition? trueBranch,
        BranchDefinition? falseBranch,
        Action<VPRC_IfElse>? configure) : IScriptStep
    {
        public void CompileInto(RenderPipeline pipeline, ViewportRenderCommandContainer container)
        {
            VPRC_IfElse command = new()
            {
                ConditionEvaluator = evaluator,
                Condition = condition?.Clone(),
                Label = label,
                TrueCommands = trueBranch?.Compile(pipeline),
                FalseCommands = falseBranch?.Compile(pipeline)
            };
            configure?.Invoke(command);
            container.Add(command);
        }
    }

    internal sealed class SwitchStep(
        Func<int>? selector,
        RenderBranchCondition? condition,
        string? label,
        IReadOnlyDictionary<int, BranchDefinition> cases,
        BranchDefinition? defaultCase,
        Action<VPRC_Switch>? configure) : IScriptStep
    {
        public void CompileInto(RenderPipeline pipeline, ViewportRenderCommandContainer container)
        {
            Dictionary<int, ViewportRenderCommandContainer>? compiledCases = null;
            if (cases.Count > 0)
            {
                compiledCases = new Dictionary<int, ViewportRenderCommandContainer>(cases.Count);
                foreach ((int key, BranchDefinition value) in cases)
                    compiledCases[key] = value.Compile(pipeline);
            }

            VPRC_Switch command = new()
            {
                SwitchEvaluator = selector,
                Condition = condition?.Clone(),
                Label = label,
                Cases = compiledCases,
                DefaultCase = defaultCase?.Compile(pipeline)
            };
            configure?.Invoke(command);
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

    private sealed class PropertyAssignment(string propertyName, IValueNode value, int line = 0, int column = 0)
    {
        public string PropertyName { get; } = propertyName;

        public IValueNode Value { get; } = value;

        public int Line { get; } = line;

        public int Column { get; } = column;

        public void Apply(ViewportRenderCommand command)
        {
            PropertyInfo? property = command.GetType().GetProperty(PropertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property is null || !property.CanWrite)
                throw new ScriptSyntaxException($"Property '{PropertyName}' was not found on render command '{command.GetType().Name}'.", Line, Column);

            object? converted = ConvertAssignedValue(Value, property.PropertyType, PropertyName, Line, Column);
            property.SetValue(command, converted);
        }
    }
}
