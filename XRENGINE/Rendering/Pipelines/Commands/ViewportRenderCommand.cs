using XREngine.Data.Core;

namespace XREngine.Rendering.Pipelines.Commands
{
    public abstract class ViewportRenderCommand : XRBase
    {
        public const string SceneShaderPath = "Scene3D";
        private bool _shouldExecute = true;
        private bool _executeInShadowPass = false;

        public ViewportRenderCommandContainer? CommandContainer { get; internal set; }
        public RenderPipeline? ParentPipeline => CommandContainer?.ParentPipeline;
        public static XRRenderPipelineInstance ActivePipelineInstance => Engine.Rendering.State.CurrentRenderingPipeline!;

        /// <summary>
        /// If true, the command will execute in the shadow pass.
        /// Otherwise, it will only execute in the main pass.
        /// </summary>
        public bool ExecuteInShadowPass
        {
            get => _executeInShadowPass;
            set => SetField(ref _executeInShadowPass, value);
        }
        /// <summary>
        /// If the command should execute.
        /// Can be used to skip commands while executing.
        /// Will be reset to true if execution is skipped.
        /// </summary>
        public bool ShouldExecute
        {
            get => _shouldExecute;
            set => SetField(ref _shouldExecute, value);
        }
        /// <summary>
        /// If true, this command's CollectVisible and SwapBuffers methods will be called.
        /// </summary>
        public virtual bool NeedsCollecVisible => false;
        /// <summary>
        /// Executes the command.
        /// </summary>
        protected abstract void Execute();
        public virtual void CollectVisible()
        {

        }
        public virtual void SwapBuffers()
        {

        }

    internal virtual void OnAttachedToContainer()
    {
    }

    internal virtual void OnParentPipelineAssigned()
    {
    }
        public void ExecuteIfShould()
        {
            if (ShouldExecute)
                Execute();
            ShouldExecute = true;
        }
    }
}
