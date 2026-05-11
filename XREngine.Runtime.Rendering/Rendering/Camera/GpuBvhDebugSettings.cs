using System;
using System.Numerics;

namespace XREngine.Rendering
{
    /// <summary>
    /// Toggleable debug visualization for the scene-level GPU BVH used by the
    /// zero-readback GPU rendering path. Consumed by
    /// <see cref="Pipelines.Commands.VPRC_RenderDebugGpuBvh"/>.
    /// </summary>
    public class GpuBvhDebugSettings : PostProcessSettings
    {
        public enum NodeFilter
        {
            All = 0,
            LeavesOnly = 1,
            InternalOnly = 2,
        }

        private bool _enabled = false;
        private int _maxNodes = 16384;
        private float _lineWidth = 0.0015f;
        private Vector4 _leafColor = new(0.20f, 1.00f, 0.40f, 1.00f);
        private Vector4 _internalColor = new(1.00f, 0.65f, 0.10f, 0.55f);
        private NodeFilter _filter = NodeFilter.All;

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public int MaxNodes
        {
            get => _maxNodes;
            set => SetField(ref _maxNodes, Math.Max(1, value));
        }

        public float LineWidth
        {
            get => _lineWidth;
            set => SetField(ref _lineWidth, MathF.Max(0.0001f, value));
        }

        public Vector4 LeafColor
        {
            get => _leafColor;
            set => SetField(ref _leafColor, value);
        }

        public Vector4 InternalColor
        {
            get => _internalColor;
            set => SetField(ref _internalColor, value);
        }

        public NodeFilter Filter
        {
            get => _filter;
            set => SetField(ref _filter, value);
        }

        // No GPU uniforms to push; the corresponding pipeline command reads
        // these properties directly when issuing its compute dispatch.
        public override void SetUniforms(XRRenderProgram program) { }
    }
}
