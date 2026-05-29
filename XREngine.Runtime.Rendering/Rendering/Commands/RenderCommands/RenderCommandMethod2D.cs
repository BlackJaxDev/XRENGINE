using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands
{
    public class RenderCommandMethod2D : RenderCommand2D
    {
        public RenderCommandMethod2D(int renderPass, Action render)
            : base(renderPass) => Rendered += render;
        public RenderCommandMethod2D(Action render)
            : base((int)EDefaultRenderPass.OpaqueForward) => Rendered += render;
        public RenderCommandMethod2D(int renderPass)
            : base(renderPass) { }
        public RenderCommandMethod2D()
            : base((int)EDefaultRenderPass.OpaqueForward) { }

        public event Action? Rendered;

        /// <summary>
        /// Optional stable label used by GPU profiling dumps for callback-driven UI commands.
        /// </summary>
        public string? GpuProfilingLabel { get; set; }

        public string GetGpuProfilingLabel()
        {
            if (!string.IsNullOrWhiteSpace(GpuProfilingLabel))
                return GpuProfilingLabel!;

            Delegate[]? delegates = Rendered?.GetInvocationList();
            if (delegates is null || delegates.Length == 0)
                return nameof(RenderCommandMethod2D);

            Delegate callback = delegates[0];
            string? targetName = callback.Target?.GetType().Name;
            string methodName = callback.Method.Name;
            return string.IsNullOrWhiteSpace(targetName)
                ? methodName
                : $"{targetName}.{methodName}";
        }

        public override void Render()
        {
            var rendered = Rendered;
            if (rendered is null)
                return;

            OnPreRender();
            rendered();
            OnPostRender();
        }
    }
}
