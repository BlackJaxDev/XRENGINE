using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

// Fluent builders that make script steps from code.
public sealed partial class RenderPipelineScript
{
    public sealed class Builder
    {
        private readonly List<IScriptStep> _steps = [];

        internal IReadOnlyList<IScriptStep> BuildSteps()
            => [.. _steps];

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
}
