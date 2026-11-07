using System.Runtime.CompilerServices;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    [RenderCommandInfo(ERenderCommandCategory.Flow)]
    public class VPRC_Switch : ViewportRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
        {
            requirements.ObserveSwitchCases(this);
            if (Cases is not null)
                foreach (ViewportRenderCommandContainer commands in Cases.Values)
                    requirements.Include(commands);
            requirements.Include(DefaultCase);
        }

        public string? Label { get; set; }

        public Func<int>? SwitchEvaluator { get; set; }

        private RenderBranchCondition? _condition;

        /// <summary>
        /// Gets or sets a data condition whose source value selects the case. When it is set, it takes priority over
        /// <see cref="SwitchEvaluator"/>. The operator is not used; a missing source selects the default case.
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
                if (!SetField(ref _cases, value)) return;
                if (_cases is not null)
                    foreach (var container in _cases.Values) AttachPipeline(container);
                ParentPipeline?.NotifyCommandChainStructureChanged();
            }
        }

        private ViewportRenderCommandContainer? _defaultCase;
        public ViewportRenderCommandContainer? DefaultCase
        {
            get => _defaultCase;
            set
            {
                if (!SetField(ref _defaultCase, value)) return;
                AttachPipeline(_defaultCase);
                ParentPipeline?.NotifyCommandChainStructureChanged();
            }
        }

        //private int _lastSwitch = 0;

        /// <inheritdoc />
        /// <remarks>Cases are added in ascending key order, then the default case.</remarks>
        public override void CollectChildContainers(List<RenderCommandChildContainer> results)
        {
            if (Cases is { Count: > 0 } cases)
            {
                int start = results.Count;
                foreach (KeyValuePair<int, ViewportRenderCommandContainer> pair in cases)
                {
                    if (pair.Value is not null)
                        results.Add(new RenderCommandChildContainer(string.Concat("Case ", pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)), pair.Value, pair.Key));
                }

                results.Sort(start, results.Count - start, CaseKeyComparer.Instance);
            }

            if (DefaultCase is not null)
                results.Add(new RenderCommandChildContainer("Default", DefaultCase));
        }

        private sealed class CaseKeyComparer : IComparer<RenderCommandChildContainer>
        {
            public static CaseKeyComparer Instance { get; } = new();

            public int Compare(RenderCommandChildContainer x, RenderCommandChildContainer y)
                => (x.CaseKey ?? int.MaxValue).CompareTo(y.CaseKey ?? int.MaxValue);
        }

        protected override void Execute()
        {
            XRRenderPipelineInstance? instance = ActivePipelineInstance;
            if (instance is null)
                return;

            int sw = _condition is { } condition ? condition.EvaluateInt() : SwitchEvaluator?.Invoke() ?? -1;
            ViewportRenderCommandContainer? next;

            if (Cases?.TryGetValue(sw, out var commands) ?? false)
                next = commands;
            else
                next = DefaultCase;

            instance.BranchTrace.Record(this, next);

            var state = _switchStates.GetValue(instance, _ => new SwitchState());
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
            _condition?.Bind(ParentPipeline);
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

        internal void AttachCaseContainers()
        {
            if (_cases is not null)
                foreach (var container in _cases.Values) AttachPipeline(container);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);
            if (Cases is not null)
            {
                foreach (var container in Cases.Values)
                    container?.BuildRenderPassMetadata(context);
            }
            DefaultCase?.BuildRenderPassMetadata(context);
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
