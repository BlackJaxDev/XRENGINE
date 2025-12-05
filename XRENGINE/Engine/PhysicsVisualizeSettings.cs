using XREngine.Core.Files;

namespace XREngine
{
    public class PhysicsVisualizeSettings : XRAsset
    {
        public void SetAllTrue()
        {
            VisualizeEnabled = true;
            VisualizeWorldAxes = true;
            VisualizeBodyAxes = true;
            VisualizeBodyMassAxes = true;
            VisualizeBodyLinearVelocity = true;
            VisualizeBodyAngularVelocity = true;
            VisualizeContactPoint = true;
            VisualizeContactNormal = true;
            VisualizeContactError = true;
            VisualizeContactForce = true;
            VisualizeActorAxes = true;
            VisualizeCollisionAabbs = true;
            VisualizeCollisionShapes = true;
            VisualizeCollisionAxes = true;
            VisualizeCollisionCompounds = true;
            VisualizeCollisionFaceNormals = true;
            VisualizeCollisionEdges = true;
            VisualizeCollisionStatic = true;
            VisualizeCollisionDynamic = true;
            VisualizeJointLocalFrames = true;
            VisualizeJointLimits = true;
            VisualizeCullBox = true;
            VisualizeMbpRegions = true;
            VisualizeSimulationMesh = true;
            VisualizeSdf = true;
        }
        public void SetAllFalse()
        {
            VisualizeEnabled = false;
            VisualizeWorldAxes = false;
            VisualizeBodyAxes = false;
            VisualizeBodyMassAxes = false;
            VisualizeBodyLinearVelocity = false;
            VisualizeBodyAngularVelocity = false;
            VisualizeContactPoint = false;
            VisualizeContactNormal = false;
            VisualizeContactError = false;
            VisualizeContactForce = false;
            VisualizeActorAxes = false;
            VisualizeCollisionAabbs = false;
            VisualizeCollisionShapes = false;
            VisualizeCollisionAxes = false;
            VisualizeCollisionCompounds = false;
            VisualizeCollisionFaceNormals = false;
            VisualizeCollisionEdges = false;
            VisualizeCollisionStatic = false;
            VisualizeCollisionDynamic = false;
            VisualizeJointLocalFrames = false;
            VisualizeJointLimits = false;
            VisualizeCullBox = false;
            VisualizeMbpRegions = false;
            VisualizeSimulationMesh = false;
            VisualizeSdf = false;
        }

        private bool _visualizeEnabled = false;
        private bool _visualizeWorldAxes = false;
        private bool _visualizeBodyAxes = false;
        private bool _visualizeBodyMassAxes = false;
        private bool _visualizeBodyLinearVelocity = false;
        private bool _visualizeBodyAngularVelocity = false;
        private bool _visualizeContactPoint = false;
        private bool _visualizeContactNormal = false;
        private bool _visualizeContactError = false;
        private bool _visualizeContactForce = false;
        private bool _visualizeActorAxes = false;
        private bool _visualizeCollisionAabbs = false;
        private bool _visualizeCollisionShapes = false;
        private bool _visualizeCollisionAxes = false;
        private bool _visualizeCollisionCompounds = false;
        private bool _visualizeCollisionFaceNormals = false;
        private bool _visualizeCollisionEdges = false;
        private bool _visualizeCollisionStatic = false;
        private bool _visualizeCollisionDynamic = false;
        private bool _visualizeJointLocalFrames = false;
        private bool _visualizeJointLimits = false;
        private bool _visualizeCullBox = false;
        private bool _visualizeMbpRegions = false;
        private bool _visualizeSimulationMesh = false;
        private bool _visualizeSdf = false;

        public bool VisualizeEnabled
        {
            get => _visualizeEnabled;
            set => SetField(ref _visualizeEnabled, value);
        }
        public bool VisualizeWorldAxes
        {
            get => _visualizeWorldAxes;
            set => SetField(ref _visualizeWorldAxes, value);
        }
        public bool VisualizeBodyAxes
        {
            get => _visualizeBodyAxes;
            set => SetField(ref _visualizeBodyAxes, value);
        }
        public bool VisualizeBodyMassAxes
        {
            get => _visualizeBodyMassAxes;
            set => SetField(ref _visualizeBodyMassAxes, value);
        }
        public bool VisualizeBodyLinearVelocity
        {
            get => _visualizeBodyLinearVelocity;
            set => SetField(ref _visualizeBodyLinearVelocity, value);
        }
        public bool VisualizeBodyAngularVelocity
        {
            get => _visualizeBodyAngularVelocity;
            set => SetField(ref _visualizeBodyAngularVelocity, value);
        }
        public bool VisualizeContactPoint
        {
            get => _visualizeContactPoint;
            set => SetField(ref _visualizeContactPoint, value);
        }
        public bool VisualizeContactNormal
        {
            get => _visualizeContactNormal;
            set => SetField(ref _visualizeContactNormal, value);
        }
        public bool VisualizeContactError
        {
            get => _visualizeContactError;
            set => SetField(ref _visualizeContactError, value);
        }
        public bool VisualizeContactForce
        {
            get => _visualizeContactForce;
            set => SetField(ref _visualizeContactForce, value);
        }
        public bool VisualizeActorAxes
        {
            get => _visualizeActorAxes;
            set => SetField(ref _visualizeActorAxes, value);
        }
        public bool VisualizeCollisionAabbs
        {
            get => _visualizeCollisionAabbs;
            set => SetField(ref _visualizeCollisionAabbs, value);
        }
        public bool VisualizeCollisionShapes
        {
            get => _visualizeCollisionShapes;
            set => SetField(ref _visualizeCollisionShapes, value);
        }
        public bool VisualizeCollisionAxes
        {
            get => _visualizeCollisionAxes;
            set => SetField(ref _visualizeCollisionAxes, value);
        }
        public bool VisualizeCollisionCompounds
        {
            get => _visualizeCollisionCompounds;
            set => SetField(ref _visualizeCollisionCompounds, value);
        }
        public bool VisualizeCollisionFaceNormals
        {
            get => _visualizeCollisionFaceNormals;
            set => SetField(ref _visualizeCollisionFaceNormals, value);
        }
        public bool VisualizeCollisionEdges
        {
            get => _visualizeCollisionEdges;
            set => SetField(ref _visualizeCollisionEdges, value);
        }
        public bool VisualizeCollisionStatic
        {
            get => _visualizeCollisionStatic;
            set => SetField(ref _visualizeCollisionStatic, value);
        }
        public bool VisualizeCollisionDynamic
        {
            get => _visualizeCollisionDynamic;
            set => SetField(ref _visualizeCollisionDynamic, value);
        }
        public bool VisualizeJointLocalFrames
        {
            get => _visualizeJointLocalFrames;
            set => SetField(ref _visualizeJointLocalFrames, value);
        }
        public bool VisualizeJointLimits
        {
            get => _visualizeJointLimits;
            set => SetField(ref _visualizeJointLimits, value);
        }
        public bool VisualizeCullBox
        {
            get => _visualizeCullBox;
            set => SetField(ref _visualizeCullBox, value);
        }
        public bool VisualizeMbpRegions
        {
            get => _visualizeMbpRegions;
            set => SetField(ref _visualizeMbpRegions, value);
        }
        public bool VisualizeSimulationMesh
        {
            get => _visualizeSimulationMesh;
            set => SetField(ref _visualizeSimulationMesh, value);
        }
        public bool VisualizeSdf
        {
            get => _visualizeSdf;
            set => SetField(ref _visualizeSdf, value);
        }
    }
}