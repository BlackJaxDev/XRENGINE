using System.Numerics;
using XREngine.Input;
using XREngine.Scene.Transforms;

namespace XREngine.Data.Components.Scene
{
    /// <summary>
    /// The transfrom base class for all VR device transforms.
    /// Retrieves the transform matrix from the VR api automatically.
    /// Supports an optional local matrix offset.
    /// </summary>
    public abstract class VRDeviceTransformBase : TransformBase, IVrTrackingPoseSource
    {
        protected VRDeviceTransformBase()
        {
            //ForceManualRecalc = true;
        }
        protected VRDeviceTransformBase(TransformBase parent) : base(parent)
        {
            //ForceManualRecalc = true;
        }

        /// <summary>
        /// Indicates whether this transform has an associated VR device.
        /// </summary>
        public bool HasDevice => Device is not null;

        /// <summary>
        /// The device index of the VR device.
        /// Will be uint.MaxValue if there is no associated device.
        /// </summary>
        public uint DeviceIndex => Device?.DeviceIndex ?? uint.MaxValue;

        /// <summary>
        /// The VR device associated with this transform.
        /// </summary>
        public abstract RuntimeVrDeviceInfo? Device { get; }

        private bool _syntheticPoseEnabled;
        private bool _syntheticPoseValid = true;
        private long _syntheticSnapshotId;
        private long _syntheticSampleTime;

        /// <summary>Explicit debug pose mode; never enabled by a failed real-device lookup.</summary>
        public bool SyntheticPoseEnabled
        {
            get => _syntheticPoseEnabled;
            set { if (SetField(ref _syntheticPoseEnabled, value)) MarkLocalModified(); }
        }

        public bool SyntheticPoseValid
        {
            get => _syntheticPoseValid;
            set => SetField(ref _syntheticPoseValid, value);
        }

        public long SyntheticSnapshotId
        {
            get => _syntheticSnapshotId;
            set => SetField(ref _syntheticSnapshotId, value);
        }

        public long SyntheticSampleTime
        {
            get => _syntheticSampleTime;
            set => SetField(ref _syntheticSampleTime, value);
        }

        /// <summary>
        /// Resolves a currently usable simulation pose without accepting a render fallback.
        /// OpenXR samples share the runtime frame identifier and predicted display time.
        /// </summary>
        public virtual bool TryGetCurrentWorldPose(out Matrix4x4 pose, out long snapshotId, out long sampleTime)
        {
            pose = default;
            snapshotId = 0;
            sampleTime = 0;
            if (SyntheticPoseEnabled)
            {
                if (!SyntheticPoseValid)
                    return false;
                Matrix4x4 synthetic = LocalMatrixOffset ?? Matrix4x4.Identity;
                if (!IsFinite(synthetic))
                    return false;
                pose = synthetic * ParentWorldMatrix;
                snapshotId = SyntheticSnapshotId;
                sampleTime = SyntheticSampleTime;
                return IsFinite(pose);
            }

            if (_simulationPoseOwner is not null)
            {
                if (!_simulationPoseValid)
                    return false;
                pose = _simulationPose * (LocalMatrixOffset ?? Matrix4x4.Identity) * ParentWorldMatrix;
                snapshotId = _simulationSnapshotId;
                sampleTime = _simulationSampleTime;
                return IsFinite(pose);
            }

            Matrix4x4 localPose;
            if (RuntimeVrStateServices.IsOpenXRActive)
            {
                if (!RuntimeVrStateServices.TryGetCurrentPoseSnapshot(out long beforeSnapshot, out long beforeTime) ||
                    !TryGetCurrentLocalPose(RuntimeVrPoseTiming.Predicted, out localPose) ||
                    !RuntimeVrStateServices.TryGetCurrentPoseSnapshot(out snapshotId, out sampleTime) ||
                    beforeSnapshot != snapshotId || beforeTime != sampleTime)
                    return false;
            }
            else
            {
                if (!TryGetCurrentLocalPose(RuntimeVrPoseTiming.Predicted, out localPose))
                    return false;
            }

            pose = localPose * ParentWorldMatrix;
            return IsFinite(pose);
        }

        private static bool IsFinite(in Matrix4x4 matrix)
            => float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) && float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14) &&
               float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) && float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24) &&
               float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) && float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34) &&
               float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) && float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);

        protected override void OnSceneNodeActivated()
        {
            base.OnSceneNodeActivated();
            RuntimeVrStateServices.RecalcMatrixOnDraw += VRState_RecalcMatrixOnDraw;
            RuntimeVrStateServices.FrameAdvanced += MarkLocalModified;
        }
        protected override void OnSceneNodeDeactivated()
        {
            base.OnSceneNodeDeactivated();
            RuntimeVrStateServices.RecalcMatrixOnDraw -= VRState_RecalcMatrixOnDraw;
            RuntimeVrStateServices.FrameAdvanced -= MarkLocalModified;
        }

        public Matrix4x4? _localMatrixOffset = null;
        /// <summary>
        /// Offsets the tracking matrix of the VR device by this matrix.
        /// </summary>
        public Matrix4x4? LocalMatrixOffset
        {
            get => _localMatrixOffset;
            set
            {
                if (SetField(ref _localMatrixOffset, value))
                    MarkLocalModified();
            }
        }

        private Matrix4x4 _lastValidLocalPose = Matrix4x4.Identity;
        private Matrix4x4 _lastValidRenderLocalPose = Matrix4x4.Identity;
        private bool _hasValidLocalPose;
        private bool _hasValidRenderPose;

        private object? _simulationPoseOwner;
        private Matrix4x4 _simulationPose;
        private bool _simulationPoseValid;
        private long _simulationGeneration, _simulationSnapshotId, _simulationSampleTime;

        /// <summary>Freezes the predicted source for a complete simulation tick; late rendering remains runtime-owned.</summary>
        public bool PublishSimulationPose(object owner, in RuntimeVrTrackingSnapshot snapshot, Matrix4x4 pose, bool valid)
        {
            ArgumentNullException.ThrowIfNull(owner);
            if (_simulationPoseOwner is not null && !ReferenceEquals(_simulationPoseOwner, owner))
                return false;
            _simulationPoseOwner = owner;
            _simulationPose = pose;
            _simulationPoseValid = valid;
            _simulationGeneration = snapshot.SessionGeneration;
            _simulationSnapshotId = snapshot.SnapshotId;
            _simulationSampleTime = snapshot.SampleTime;
            MarkLocalModified();
            return true;
        }

        public void ReleaseSimulationPose(object owner)
        {
            if (!ReferenceEquals(_simulationPoseOwner, owner))
                return;
            _simulationPoseOwner = null;
            MarkLocalModified();
        }

        public virtual bool PoseCurrentlyUsable => TryGetCurrentLocalPose(RuntimeVrPoseTiming.Predicted, out _);
        public virtual string? TrackingIdentity => this is VRTrackerTransform tracker
            ? tracker.OpenXrTrackerPersistentPath ?? tracker.Device?.PersistentIdentity
            : Device?.PersistentIdentity;
        public virtual long TrackingSessionGeneration => _simulationPoseOwner is null ? GetPublishedSnapshot().SessionGeneration : _simulationGeneration;
        public virtual long TrackingSnapshotId => _simulationPoseOwner is null ? GetPublishedSnapshot().SnapshotId : _simulationSnapshotId;

        private static RuntimeVrTrackingSnapshot GetPublishedSnapshot()
        {
            RuntimeVrStateServices.TryCopyTrackingSnapshot(Span<RuntimeVrTrackerPose>.Empty, out RuntimeVrTrackingSnapshot snapshot, out _);
            return snapshot;
        }

        private void VRState_RecalcMatrixOnDraw(RuntimeVrPoseTiming timing)
        {
            if (SyntheticPoseEnabled)
            {
                if (SyntheticPoseValid)
                    SetRenderMatrix((LocalMatrixOffset ?? Matrix4x4.Identity) * ParentRenderMatrix);
                return;
            }
            if (TryGetCurrentLocalPose(timing, out Matrix4x4 pose))
            {
                _lastValidRenderLocalPose = pose;
                _hasValidRenderPose = true;
            }
            else if (IsManualPoseSource)
                pose = LocalMatrixOffset ?? Matrix4x4.Identity;
            else if (_hasValidRenderPose)
                pose = _lastValidRenderLocalPose;
            else if (_hasValidLocalPose)
                pose = _lastValidLocalPose;
            else
                return;

            bool isOpenXrHeadset = RuntimeVrStateServices.IsOpenXRActive && this is XREngine.Scene.Transforms.VRHeadsetTransform;
            SetRenderMatrix(pose * ParentRenderMatrix, recalcAllChildRenderMatrices: !isOpenXrHeadset);
        }

        /// <summary>Retains the last valid runtime pose on loss. Retention never makes a sample usable for calibration.</summary>
        protected override Matrix4x4 CreateLocalMatrix()
        {
            if (SyntheticPoseEnabled)
                return LocalMatrixOffset ?? Matrix4x4.Identity;
            if (TryGetCurrentLocalPose(RuntimeVrPoseTiming.Predicted, out Matrix4x4 pose))
            {
                _lastValidLocalPose = pose;
                _hasValidLocalPose = true;
                return pose;
            }
            return IsManualPoseSource ? LocalMatrixOffset ?? Matrix4x4.Identity : _lastValidLocalPose;
        }

        private bool IsManualPoseSource => this is VRTrackerTransform tracker && tracker.Device is null && string.IsNullOrWhiteSpace(tracker.OpenXrTrackerUserPath);

        /// <summary>Reads current runtime validity, without falling back to a retained matrix or another runtime.</summary>
        public virtual bool TryGetCurrentLocalPose(RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        {
            if (SyntheticPoseEnabled)
            {
                pose = LocalMatrixOffset ?? Matrix4x4.Identity;
                return SyntheticPoseValid && IsFinite(pose);
            }
            if (timing == RuntimeVrPoseTiming.Predicted && _simulationPoseOwner is not null)
            {
                pose = _simulationPose * (LocalMatrixOffset ?? Matrix4x4.Identity);
                return _simulationPoseValid;
            }
            pose = Matrix4x4.Identity;
            if (!RuntimeVrStateServices.IsInVR)
                return false;
            bool valid;
            if (this is XREngine.Scene.Transforms.VRHeadsetTransform)
                valid = RuntimeVrStateServices.TryGetHeadLocalPose(timing, out pose);
            else if (this is VRControllerTransform controller)
                valid = RuntimeVrStateServices.TryGetControllerLocalPose(controller.LeftHand, timing, out pose);
            else if (this is VRTrackerTransform tracker && RuntimeVrStateServices.IsOpenXRActive)
                valid = !string.IsNullOrWhiteSpace(tracker.OpenXrTrackerUserPath) && RuntimeVrStateServices.TryGetTrackerLocalPose(tracker.OpenXrTrackerUserPath, timing, out pose);
            else if (Device is { IsConnected: true } device && RuntimeVrStateServices.ActiveRuntime == RuntimeVrRuntimeKind.OpenVR)
                valid = RuntimeVrStateServices.TryGetDeviceLocalPose(device.DeviceIndex, timing, out pose);
            else
                valid = false;

            if (valid && LocalMatrixOffset.HasValue)
                pose *= LocalMatrixOffset.Value;
            return valid;
        }

        /// <summary>
        /// Sets the local matrix offset from calls that don't know what type this transform is.
        /// </summary>
        /// <param name="value"></param>
        /// <param name="networkSmoothed"></param>
        public override void DeriveLocalMatrix(Matrix4x4 value, bool networkSmoothed = false)
        {
            LocalMatrixOffset = value;
        }
    }
}
