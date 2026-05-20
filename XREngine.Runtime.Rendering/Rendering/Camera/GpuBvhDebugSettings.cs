using System;
using System.Numerics;

namespace XREngine.Rendering
{
    /// <summary>
    /// Toggleable render-pipeline debug visualizations that are controlled from
    /// the camera post-process stack.
    /// </summary>
    public class GpuBvhDebugSettings : PostProcessSettings
    {
        public const int DefaultFullOverdrawSaturationCount = 8;
        public const int MinFullOverdrawSaturationCount = 1;
        public const int MaxFullOverdrawSaturationCount = 256;

        public enum NodeFilter
        {
            All = 0,
            LeavesOnly = 1,
            InternalOnly = 2,
        }

        private bool _fullOverdrawEnabled = false;
        private int _fullOverdrawSaturationCount = DefaultFullOverdrawSaturationCount;
        private float _fullOverdrawOverlayOpacity = 1.0f;
        private bool _meshletDebugDisplayEnabled = false;
        private bool _enabled = false;
        private int _maxNodes = 16384;
        private float _lineWidth = 0.0015f;
        private Vector4 _leafColor = new(0.20f, 1.00f, 0.40f, 1.00f);
        private Vector4 _internalColor = new(1.00f, 0.65f, 0.10f, 0.55f);
        private NodeFilter _filter = NodeFilter.All;

        public bool FullOverdrawEnabled
        {
            get => _fullOverdrawEnabled;
            set => SetField(ref _fullOverdrawEnabled, value);
        }

        public int FullOverdrawSaturationCount
        {
            get => _fullOverdrawSaturationCount;
            set => SetField(
                ref _fullOverdrawSaturationCount,
                Math.Clamp(value, MinFullOverdrawSaturationCount, MaxFullOverdrawSaturationCount));
        }

        public float FullOverdrawOverlayOpacity
        {
            get => _fullOverdrawOverlayOpacity;
            set => SetField(ref _fullOverdrawOverlayOpacity, Math.Clamp(value, 0.0f, 1.0f));
        }

        public bool MeshletDebugDisplayEnabled
        {
            get => _meshletDebugDisplayEnabled;
            set => SetField(ref _meshletDebugDisplayEnabled, value);
        }

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

        public static bool TryResolve(XRCamera? camera, out GpuBvhDebugSettings? settings)
        {
            var stage = camera?.GetPostProcessStageState<GpuBvhDebugSettings>();
            if (stage?.TryGetBacking(out settings) == true && settings is not null)
                return true;

            settings = null;
            return false;
        }

        public static bool IsMeshletDebugDisplayEnabled(XRCamera? camera)
            => TryResolve(camera, out GpuBvhDebugSettings? settings) &&
               settings?.MeshletDebugDisplayEnabled == true;

        // No GPU uniforms to push; the corresponding pipeline command reads
        // these properties directly when issuing debug visualization passes.
        public override void SetUniforms(XRRenderProgram program) { }
    }
}
