using System.ComponentModel;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;
using static XREngine.Scene.Components.Animation.InverseKinematics;

namespace XREngine.Scene.Components.Animation
{
    public class SingleTargetIKComponent : XRComponent, IRenderable
    {
        public SingleTargetIKComponent()
        {
            RenderedObjects = [DebugRenderInfo = RenderInfo3D.New(this, EDefaultRenderPass.OpaqueForward, Render)];
        }

        public RenderInfo3D DebugRenderInfo { get; }
        public RenderInfo[] RenderedObjects { get; }
        public bool DebugTargetVisible
        {
            get => DebugRenderInfo.IsVisible;
            set => DebugRenderInfo.IsVisible = value;
        }

        private void Render()
        {
            if (Engine.Rendering.State.IsShadowPass)
                return;

            if (TargetTransform is not null)
                Engine.Rendering.Debug.RenderPoint(TargetTransform.WorldTranslation, ColorF4.Red);
        }

        private TransformBase? _targetTransform;
        /// <summary>
        /// The target transform to solve IK for.
        /// All first children of this component's scene node will be used as bones in the chain.
        /// The last bone in the chain will move to this target and the rest of the bones will adjust.
        /// </summary>
        public TransformBase? TargetTransform
        {
            get => _targetTransform;
            set => SetField(ref _targetTransform, value);
        }

        private bool _solveIK = true;
        /// <summary>
        /// If true, the component will solve IK for the chain of bones.
        /// </summary>
        public bool SolveIK
        {
            get => _solveIK;
            set => SetField(ref _solveIK, value);
        }

        private BoneChainItem[] _childChain = [];
        public BoneChainItem[] ChildChain
        {
            get => _childChain;
            private set => SetField(ref _childChain, value);
        }

        private float _tolerance = 0.01f;
        /// <summary>
        /// How close the last bone in the chain needs to be to the target to stop solving.
        /// </summary>
        public float Tolerance
        {
            get => _tolerance;
            set => SetField(ref _tolerance, value);
        }

        private int _maxIterations = 10;
        /// <summary>
        /// The maximum number of iterations to solve for the target.
        /// Keep this low to prevent performance issues.
        /// </summary>
        public int MaxIterations
        {
            get => _maxIterations;
            set => SetField(ref _maxIterations, value);
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (SolveIK)
                StartSolving();
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            if (SolveIK)
                StopSolving();
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(SolveIK):
                    if (IsActive)
                    {
                        if (SolveIK)
                            StartSolving();
                        else
                            StopSolving();
                    }
                    break;
            }
        }

        private void StopSolving()
        {
            UnregisterTick(ETickGroup.Late, ETickOrder.Scene, SolveSingleTargetIK);
            ChildChain = [];
        }

        private void StartSolving()
        {
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, SolveSingleTargetIK);
            ChildChain = GetChain();
        }

        private BoneChainItem[] GetChain()
        {
            List<BoneChainItem> chain = [];
            var child = SceneNode.Transform;
            while (child is not null && child.SceneNode is not null)
            {
                chain.Add(new(child.SceneNode, child.SceneNode.GetComponent<IKConstraintsComponent>()?.Constraints));
                child = child.FirstChild();
            }
            return [.. chain];
        }

        private void SolveSingleTargetIK()
        {
            if (TargetTransform is null)
                return;

            SolveSingleTarget(ChildChain, TargetTransform.WorldMatrix, Tolerance, MaxIterations);
        }
    }
}
