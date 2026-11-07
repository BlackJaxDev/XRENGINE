using System.Runtime.CompilerServices;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    [RenderCommandInfo(ERenderCommandCategory.Flow)]
    public class VPRC_IfElse : ViewportRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
        {
            requirements.Include(TrueCommands);
            requirements.Include(FalseCommands);
        }

        public string? Label { get; set; }

        public Func<bool>? ConditionEvaluator { get; set; }

        private RenderBranchCondition? _condition;

        /// <summary>
        /// Gets or sets a data condition. When it is set, it takes priority over <see cref="ConditionEvaluator"/>.
        /// Authored pipelines use it because a data condition can be saved and shown by tools.
        /// </summary>
        public RenderBranchCondition? Condition
        {
            get => _condition;
            set
            {
                if (SetField(ref _condition, value))
                    _condition?.Bind(ParentPipeline);
            }
        }

        public override string GpuProfilingName
            => string.IsNullOrWhiteSpace(Label) ? base.GpuProfilingName : $"{base.GpuProfilingName}[{Label}]";

        private sealed class BranchState
        {
            public ViewportRenderCommandContainer? ActiveContainer;
        }

        private readonly ConditionalWeakTable<XRRenderPipelineInstance, BranchState> _branchStates = new();

        private ViewportRenderCommandContainer? _trueCommands;
        public ViewportRenderCommandContainer? TrueCommands
        {
            get => _trueCommands;
            set
            {
                if (!SetField(ref _trueCommands, value)) return;
                AttachPipeline(_trueCommands);
                ParentPipeline?.NotifyCommandChainStructureChanged();
            }
        }

        private ViewportRenderCommandContainer? _falseCommands;
        public ViewportRenderCommandContainer? FalseCommands
        {
            get => _falseCommands;
            set
            {
                if (!SetField(ref _falseCommands, value)) return;
                AttachPipeline(_falseCommands);
                ParentPipeline?.NotifyCommandChainStructureChanged();
            }
        }

        //private bool _lastCondition = false;

        /// <inheritdoc />
        public override void CollectChildContainers(List<RenderCommandChildContainer> results)
        {
            if (TrueCommands is not null)
                results.Add(new RenderCommandChildContainer("True", TrueCommands));
            if (FalseCommands is not null)
                results.Add(new RenderCommandChildContainer("False", FalseCommands));
        }

        protected override void Execute()
        {
            XRRenderPipelineInstance? instance = ActivePipelineInstance;
            if (instance is null)
                return;

            bool cond = _condition is { } condition ? condition.Evaluate() : ConditionEvaluator?.Invoke() ?? false;
            ViewportRenderCommandContainer? next = cond ? TrueCommands : FalseCommands;
            instance.BranchTrace.Record(this, next);
            ExecuteBranch(instance, next);
        }

        private void ExecuteBranch(XRRenderPipelineInstance instance, ViewportRenderCommandContainer? next)
        {
            var state = _branchStates.GetValue(instance, _ => new BranchState());
            if (!ReferenceEquals(state.ActiveContainer, next))
            {
                state.ActiveContainer?.OnBranchDeselected(instance);
                state.ActiveContainer = next;
                state.ActiveContainer?.OnBranchSelected(instance);
            }

            using var branchScope = instance.PushRenderGraphBranchScope();
            next?.Execute();
        }

        internal override void OnAttachedToContainer()
        {
            base.OnAttachedToContainer();
            AttachPipeline(_trueCommands);
            AttachPipeline(_falseCommands);
        }

        internal override void OnParentPipelineAssigned()
        {
            base.OnParentPipelineAssigned();
            _condition?.Bind(ParentPipeline);
            AttachPipeline(_trueCommands);
            AttachPipeline(_falseCommands);
        }

        private void AttachPipeline(ViewportRenderCommandContainer? container)
        {
            var pipeline = CommandContainer?.ParentPipeline;
            if (container is not null && pipeline is not null && !ReferenceEquals(container.ParentPipeline, pipeline))
                container.ParentPipeline = pipeline;
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);
            TrueCommands?.BuildRenderPassMetadata(context);
            FalseCommands?.BuildRenderPassMetadata(context);
        }

        //public override bool NeedsCollecVisible
        //    => TrueCommands?.CollecVisibleCommands.Count > 0 || FalseCommands?.CollecVisibleCommands.Count > 0;

        //public override void CollectVisible()
        //{
        //    if (ConditionEvaluator is null)
        //        return;

        //    if (_lastCondition = ConditionEvaluator())
        //        TrueCommands?.CollectVisible();
        //    else
        //        FalseCommands?.CollectVisible();
        //}

        //public override void SwapBuffers()
        //{
        //    if (_lastCondition)
        //        TrueCommands?.SwapBuffers();
        //    else
        //        FalseCommands?.SwapBuffers();
        //}
    }
}
