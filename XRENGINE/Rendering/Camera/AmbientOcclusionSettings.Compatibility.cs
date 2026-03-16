namespace XREngine.Rendering
{
    public partial class AmbientOcclusionSettings
    {
        public float ResolutionScale
        {
            get => ScreenSpace.ResolutionScale;
            set => ScreenSpace.ResolutionScale = value;
        }

        public float SamplesPerPixel
        {
            get => SpatialHash.SamplesPerPixel;
            set => SpatialHash.SamplesPerPixel = value;
        }

        public float Distance
        {
            get => ScreenSpace.Distance;
            set => ScreenSpace.Distance = value;
        }

        public float DistanceIntensity
        {
            get => SpatialHash.DistanceIntensity;
            set => SpatialHash.DistanceIntensity = value;
        }

        public float Intensity
        {
            get => Prototype.Intensity;
            set => Prototype.Intensity = value;
        }

        public float Color
        {
            get => ScreenSpace.Color;
            set => ScreenSpace.Color = value;
        }

        public float Thickness
        {
            get => SpatialHash.Thickness;
            set => SpatialHash.Thickness = value;
        }

        public int Iterations
        {
            get => ScreenSpace.Iterations;
            set => ScreenSpace.Iterations = value;
        }

        public float Rings
        {
            get => ScreenSpace.Rings;
            set => ScreenSpace.Rings = value;
        }

        public float LumaPhi
        {
            get => ScreenSpace.LumaPhi;
            set => ScreenSpace.LumaPhi = value;
        }

        public float DepthPhi
        {
            get => MultiView.DepthPhi;
            set => MultiView.DepthPhi = value;
        }

        public float NormalPhi
        {
            get => MultiView.NormalPhi;
            set => MultiView.NormalPhi = value;
        }

        public int Samples
        {
            get => SpatialHash.Samples;
            set => SpatialHash.Samples = value;
        }

        public float SpatialHashCellSize
        {
            get => SpatialHash.CellSize;
            set => SpatialHash.CellSize = value;
        }

        public float SpatialHashMaxDistance
        {
            get => SpatialHash.MaxDistance;
            set => SpatialHash.MaxDistance = value;
        }

        public int SpatialHashSteps
        {
            get => SpatialHash.Steps;
            set => SpatialHash.Steps = value;
        }

        public float SpatialHashJitterScale
        {
            get => SpatialHash.JitterScale;
            set => SpatialHash.JitterScale = value;
        }

        public float SecondaryRadius
        {
            get => MultiView.SecondaryRadius;
            set => MultiView.SecondaryRadius = value;
        }

        public float MultiViewBlend
        {
            get => MultiView.Blend;
            set => MultiView.Blend = value;
        }

        public float MultiViewSpread
        {
            get => MultiView.Spread;
            set => MultiView.Spread = value;
        }

        public int HBAODirectionCount
        {
            get => HorizonBased.DirectionCount;
            set => HorizonBased.DirectionCount = value;
        }

        public int HBAOStepsPerDirection
        {
            get => HorizonBased.StepsPerDirection;
            set => HorizonBased.StepsPerDirection = value;
        }

        public float HBAOTangentBias
        {
            get => HorizonBased.TangentBias;
            set => HorizonBased.TangentBias = value;
        }

        public float HBAODetailAO
        {
            get => HorizonBasedPlus.DetailAO;
            set => HorizonBasedPlus.DetailAO = value;
        }

        public bool HBAOBlurEnabled
        {
            get => HorizonBasedPlus.BlurEnabled;
            set => HorizonBasedPlus.BlurEnabled = value;
        }

        public int HBAOBlurRadius
        {
            get => HorizonBasedPlus.BlurRadius;
            set => HorizonBasedPlus.BlurRadius = value;
        }

        public float HBAOBlurSharpness
        {
            get => HorizonBasedPlus.BlurSharpness;
            set => HorizonBasedPlus.BlurSharpness = value;
        }

        public bool HBAOUseInputNormals
        {
            get => HorizonBasedPlus.UseInputNormals;
            set => HorizonBasedPlus.UseInputNormals = value;
        }

        public float HBAOMetersToViewSpaceUnits
        {
            get => HorizonBasedPlus.MetersToViewSpaceUnits;
            set => HorizonBasedPlus.MetersToViewSpaceUnits = value;
        }

        public int GTAOSliceCount
        {
            get => GroundTruth.SliceCount;
            set => GroundTruth.SliceCount = value;
        }

        public int GTAOStepsPerSlice
        {
            get => GroundTruth.StepsPerSlice;
            set => GroundTruth.StepsPerSlice = value;
        }

        public bool GTAODenoiseEnabled
        {
            get => GroundTruth.DenoiseEnabled;
            set => GroundTruth.DenoiseEnabled = value;
        }

        public int GTAODenoiseRadius
        {
            get => GroundTruth.DenoiseRadius;
            set => GroundTruth.DenoiseRadius = value;
        }

        public float GTAODenoiseSharpness
        {
            get => GroundTruth.DenoiseSharpness;
            set => GroundTruth.DenoiseSharpness = value;
        }

        public bool GTAOUseInputNormals
        {
            get => GroundTruth.UseInputNormals;
            set => GroundTruth.UseInputNormals = value;
        }

        public float GTAOFalloffStartRatio
        {
            get => GroundTruth.FalloffStartRatio;
            set => GroundTruth.FalloffStartRatio = value;
        }

        public float GTAOThicknessHeuristic
        {
            get => GroundTruth.ThicknessHeuristic;
            set => GroundTruth.ThicknessHeuristic = value;
        }

        public bool GTAOMultiBounceEnabled
        {
            get => GroundTruth.MultiBounceEnabled;
            set => GroundTruth.MultiBounceEnabled = value;
        }

        public bool GTAOSpecularOcclusionEnabled
        {
            get => GroundTruth.SpecularOcclusionEnabled;
            set => GroundTruth.SpecularOcclusionEnabled = value;
        }

        public int VXAOVoxelGridResolution
        {
            get => Voxel.VoxelGridResolution;
            set => Voxel.VoxelGridResolution = value;
        }

        public float VXAOCoverageExtent
        {
            get => Voxel.CoverageExtent;
            set => Voxel.CoverageExtent = value;
        }

        public float VXAOVoxelOpacityScale
        {
            get => Voxel.VoxelOpacityScale;
            set => Voxel.VoxelOpacityScale = value;
        }

        public bool VXAOTemporalReuseEnabled
        {
            get => Voxel.TemporalReuseEnabled;
            set => Voxel.TemporalReuseEnabled = value;
        }

        public bool VXAOCombineWithScreenSpaceDetail
        {
            get => Voxel.CombineWithScreenSpaceDetail;
            set => Voxel.CombineWithScreenSpaceDetail = value;
        }

        public float VXAODetailBlend
        {
            get => Voxel.DetailBlend;
            set => Voxel.DetailBlend = value;
        }
    }
}