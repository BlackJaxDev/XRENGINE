namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_IfElse : ViewportStateRenderCommand<VPRC_PopRenderArea>
    {
        public Func<bool>? ConditionEvaluator { get; set; }

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
            bool cond = ConditionEvaluator?.Invoke() ?? false;
            //if (NeedsCollecVisible)
            //    cond = _lastCondition;
            //else
            //{
            //    if (ConditionEvaluator is null)
            //        return;

            //    cond = ConditionEvaluator();
            //}

            if (cond)
                TrueCommands?.Execute();
            else
                FalseCommands?.Execute();
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
