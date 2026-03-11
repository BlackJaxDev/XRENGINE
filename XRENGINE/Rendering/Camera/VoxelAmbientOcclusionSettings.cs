using System;

namespace XREngine.Rendering
{
    public sealed class VoxelAmbientOcclusionSettings : AmbientOcclusionModeSettings
    {
        private int _voxelGridResolution = 128;
        private float _coverageExtent = 24.0f;
        private float _voxelOpacityScale = 1.0f;
        private bool _temporalReuseEnabled = true;
        private bool _combineWithScreenSpaceDetail = true;
        private float _detailBlend = 0.35f;

        public VoxelAmbientOcclusionSettings(AmbientOcclusionSettings owner)
            : base(owner, nameof(AmbientOcclusionSettings.Voxel))
        {
        }

        public int VoxelGridResolution
        {
            get => _voxelGridResolution;
            set => SetValue(ref _voxelGridResolution, value, nameof(AmbientOcclusionSettings.VXAOVoxelGridResolution));
        }

        public float CoverageExtent
        {
            get => _coverageExtent;
            set => SetValue(ref _coverageExtent, value, nameof(AmbientOcclusionSettings.VXAOCoverageExtent));
        }

        public float VoxelOpacityScale
        {
            get => _voxelOpacityScale;
            set => SetValue(ref _voxelOpacityScale, value, nameof(AmbientOcclusionSettings.VXAOVoxelOpacityScale));
        }

        public bool TemporalReuseEnabled
        {
            get => _temporalReuseEnabled;
            set => SetValue(ref _temporalReuseEnabled, value, nameof(AmbientOcclusionSettings.VXAOTemporalReuseEnabled));
        }

        public bool CombineWithScreenSpaceDetail
        {
            get => _combineWithScreenSpaceDetail;
            set => SetValue(ref _combineWithScreenSpaceDetail, value, nameof(AmbientOcclusionSettings.VXAOCombineWithScreenSpaceDetail));
        }

        public float DetailBlend
        {
            get => _detailBlend;
            set => SetValue(ref _detailBlend, value, nameof(AmbientOcclusionSettings.VXAODetailBlend));
        }

        public override void ApplyUniforms(XRRenderProgram program)
        {
            program.Uniform("Radius", PositiveOr(Owner.Radius, 2.0f));
            program.Uniform("Power", PositiveOr(Owner.Power, 1.0f));
            program.Uniform("VoxelGridResolution", Math.Clamp(VoxelGridResolution, 32, 512));
            program.Uniform("CoverageExtent", PositiveOr(CoverageExtent, 24.0f));
            program.Uniform("VoxelOpacityScale", PositiveOr(VoxelOpacityScale, 1.0f));
            program.Uniform("TemporalReuseEnabled", TemporalReuseEnabled);
            program.Uniform("CombineWithScreenSpaceDetail", CombineWithScreenSpaceDetail);
            program.Uniform("DetailBlend", Math.Clamp(DetailBlend, 0.0f, 1.0f));
        }
    }
}