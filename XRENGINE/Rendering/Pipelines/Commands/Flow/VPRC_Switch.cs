namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_Switch : ViewportStateRenderCommand<VPRC_PopRenderArea>
    {
        public Func<int>? SwitchEvaluator { get; set; }

        private Dictionary<int, ViewportRenderCommandContainer>? _cases;
        public Dictionary<int, ViewportRenderCommandContainer>? Cases
        {
            get => _cases;
            set
            {
                _cases = value;
                if (_cases is not null)
                {
                    foreach (var container in _cases.Values)
                        AttachPipeline(container);
                }
            }
        }

        private ViewportRenderCommandContainer? _defaultCase;
        public ViewportRenderCommandContainer? DefaultCase
        {
            get => _defaultCase;
            set
            {
                _defaultCase = value;
                AttachPipeline(_defaultCase);
            }
        }

        //private int _lastSwitch = 0;

        protected override void Execute()
        {
            int sw = SwitchEvaluator?.Invoke() ?? -1;
            //if (NeedsCollecVisible)
            //    sw = _lastSwitch;
            //else
            //{
            //    if (SwitchEvaluator is null)
            //        return;

            //    sw = SwitchEvaluator();
            //}

            if (Cases?.TryGetValue(sw, out var commands) ?? false)
                commands.Execute();
            else
                DefaultCase?.Execute();
        }

        internal override void OnAttachedToContainer()
        {
            base.OnAttachedToContainer();
            if (_cases is not null)
            {
                foreach (var container in _cases.Values)
                    AttachPipeline(container);
            }
            AttachPipeline(_defaultCase);
        }

        internal override void OnParentPipelineAssigned()
        {
            base.OnParentPipelineAssigned();
            if (_cases is not null)
            {
                foreach (var container in _cases.Values)
                    AttachPipeline(container);
            }
            AttachPipeline(_defaultCase);
        }

        private void AttachPipeline(ViewportRenderCommandContainer? container)
        {
            var pipeline = CommandContainer?.ParentPipeline;
            if (container is not null && pipeline is not null && !ReferenceEquals(container.ParentPipeline, pipeline))
                container.ParentPipeline = pipeline;
        }

        //public override bool NeedsCollecVisible
        //    => (Cases?.Values.Any(c => c.CollecVisibleCommands.Count > 0) ?? false) || DefaultCase?.CollecVisibleCommands.Count > 0;

        //public override void CollectVisible()
        //{
        //    if (SwitchEvaluator is not null && (Cases?.TryGetValue(_lastSwitch = SwitchEvaluator.Invoke(), out var commands) ?? false))
        //        commands.Execute();
        //    else
        //        DefaultCase?.Execute();
        //}

        //public override void SwapBuffers()
        //{
        //    if (SwitchEvaluator is not null && (Cases?.TryGetValue(_lastSwitch, out var commands) ?? false))
        //        commands.SwapBuffers();
        //    else
        //        DefaultCase?.SwapBuffers();
        //}
    }
}
