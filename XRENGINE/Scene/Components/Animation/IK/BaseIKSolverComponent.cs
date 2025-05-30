using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    public abstract class BaseIKSolverComponent : XRComponent, IRenderable
    {
        public BaseIKSolverComponent()
        {
            RenderedObjects =
            [
				RenderInfo = RenderInfo3D.New(this, new RenderCommandMethod3D(EDefaultRenderPass.OnTopForward, Visualize))
            ];
		}

		protected virtual void InitializeSolver() { }
        protected virtual void UpdateSolver() { }
        protected virtual void ResetTransformsToDefault() { }

        private bool IsAnimated
            => _animStateMachine != null;
        private bool AnimatePhysics
            => _animStateMachine?.StateMachine?.AnimatePhysics ?? false;

        public abstract void Visualize();

		public RenderInfo[] RenderedObjects { get; }
        public RenderInfo3D RenderInfo { get; }

		private bool _skipSolverUpdate;
        private bool _updateFrame;
        private bool _componentInitiated;
        public bool _resetTransformsToDefault = false;
        private AnimStateMachineComponent? _animStateMachine;

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            RegisterTick(ETickGroup.PostPhysics, ETickOrder.Animation, FixedUpdate);
            RegisterTick(ETickGroup.Late, ETickOrder.Animation, LateUpdate);
            RegisterTick(ETickGroup.Normal, ETickOrder.Animation, Update);

            Initialize();
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();

            UnregisterTick(ETickGroup.PostPhysics, ETickOrder.Animation, FixedUpdate);
            UnregisterTick(ETickGroup.Late, ETickOrder.Animation, LateUpdate);
            UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, Update);
        }

        private void Initialize()
        {
            if (_componentInitiated)
                return;

            FindAnimatorRecursive(Transform, true);

            InitializeSolver();
            _componentInitiated = true;
        }

        private void Update()
        {
            if (_skipSolverUpdate || AnimatePhysics)
                return;

            if (_resetTransformsToDefault)
                ResetTransformsToDefault();
        }

        private void FindAnimatorRecursive(TransformBase t, bool findInChildren)
        {
            if (IsAnimated)
                return;

            if (t is null)
                return;

            var node = t.SceneNode;
            if (node is null)
                return;

            _animStateMachine = node.GetComponent<AnimStateMachineComponent>();
            if (IsAnimated)
                return;

            if (_animStateMachine == null && findInChildren)
                _animStateMachine = node.FindFirstDescendantComponent<AnimStateMachineComponent>();

            if (!IsAnimated && t.Parent != null)
                FindAnimatorRecursive(t.Parent, false);
        }

        private void FixedUpdate()
        {
            if (_skipSolverUpdate)
                _skipSolverUpdate = false;
            
            _updateFrame = true;

            if (AnimatePhysics && _resetTransformsToDefault)
                ResetTransformsToDefault();
        }

        private void LateUpdate()
        {
            if (_skipSolverUpdate)
                return;

            // Check if either animatePhysics is false or FixedUpdate has been called
            if (!AnimatePhysics)
                _updateFrame = true;

            if (!_updateFrame)
                return;

            _updateFrame = false;

            UpdateSolver();
        }

        public void UpdateSolverExternal()
        {
            if (!IsActive)
                return;

            _skipSolverUpdate = true;
            UpdateSolver();
        }
    }
}
