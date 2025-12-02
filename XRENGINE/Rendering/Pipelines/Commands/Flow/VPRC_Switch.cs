using System.Runtime.CompilerServices;
using XREngine.Rendering;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_Switch : ViewportStateRenderCommand<VPRC_PopRenderArea>
    {
        public Func<int>? SwitchEvaluator { get; set; }

        private sealed class SwitchState
        {
            public ViewportRenderCommandContainer? ActiveContainer;
        }

        private readonly ConditionalWeakTable<XRRenderPipelineInstance, SwitchState> _switchStates = new();

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
            XRRenderPipelineInstance? instance = ActivePipelineInstance;
            if (instance is null)
                return;

            int sw = SwitchEvaluator?.Invoke() ?? -1;
            ViewportRenderCommandContainer? next;

            if (Cases?.TryGetValue(sw, out var commands) ?? false)
                next = commands;
            else
                next = DefaultCase;

            var state = _switchStates.GetValue(instance, _ => new SwitchState());
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
