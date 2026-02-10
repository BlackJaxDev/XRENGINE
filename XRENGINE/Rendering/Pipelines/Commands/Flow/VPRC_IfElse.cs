using System.Runtime.CompilerServices;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_IfElse : ViewportStateRenderCommand<VPRC_PopRenderArea>
    {
        public Func<bool>? ConditionEvaluator { get; set; }

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
                _trueCommands = value;
                AttachPipeline(_trueCommands);
            }
        }

        private ViewportRenderCommandContainer? _falseCommands;
        public ViewportRenderCommandContainer? FalseCommands
        {
            get => _falseCommands;
            set
            {
                _falseCommands = value;
                AttachPipeline(_falseCommands);
            }
        }

        //private bool _lastCondition = false;

        protected override void Execute()
        {
            XRRenderPipelineInstance? instance = ActivePipelineInstance;
            if (instance is null)
                return;

            bool cond = ConditionEvaluator?.Invoke() ?? false;

            if (cond)
                ExecuteBranch(instance, TrueCommands);
            else
                ExecuteBranch(instance, FalseCommands);
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
