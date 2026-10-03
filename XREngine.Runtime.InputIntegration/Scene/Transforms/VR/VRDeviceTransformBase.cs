using OpenVR.NET.Devices;
using System.Numerics;
using Valve.VR;
using XREngine.Input;
using XREngine.Scene.Transforms;

namespace XREngine.Data.Components.Scene
{
    /// <summary>
    /// The transfrom base class for all VR device transforms.
    /// Retrieves the transform matrix from the VR api automatically.
    /// Supports an optional local matrix offset.
    /// </summary>
    public abstract class VRDeviceTransformBase : TransformBase
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
        public abstract VrDevice? Device { get; }

        private bool _syntheticPoseEnabled;
        private bool _syntheticPoseValid = true;
        private long _syntheticSnapshotId;
        private long _syntheticSampleTime;
        private Matrix4x4 _lastUsableLocalPose = Matrix4x4.Identity;
        private bool _hasLastUsableLocalPose;

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

            Matrix4x4 localPose;
            if (RuntimeVrStateServices.IsOpenXRActive)
            {
                if (!RuntimeVrStateServices.TryGetCurrentPoseSnapshot(out long beforeSnapshot, out long beforeTime) ||
                    !TryGetTrackedLocalPose(RuntimeVrPoseTiming.Predicted, out localPose) ||
                    !RuntimeVrStateServices.TryGetCurrentPoseSnapshot(out snapshotId, out sampleTime) ||
                    beforeSnapshot != snapshotId || beforeTime != sampleTime)
                    return false;
            }
            else
            {
                VrDevice? device = Device;
                if (device is null || !device.IsEnabled ||
                    device.TrackingState != ETrackingResult.Running_OK)
                    return false;
                localPose = device.DeviceToAbsoluteTrackingMatrix;
                if (LocalMatrixOffset.HasValue)
                    localPose *= LocalMatrixOffset.Value;
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

        /// <summary>
        /// Occurs directly before rendering to recalculate the render matrix based on the VR state.
        /// </summary>
        private void VRState_RecalcMatrixOnDraw(RuntimeVrPoseTiming timing)
        {
            if (SyntheticPoseEnabled)
            {
                if (SyntheticPoseValid)
                    SetRenderMatrix((LocalMatrixOffset ?? Matrix4x4.Identity) * ParentRenderMatrix);
                return;
            }

            if (!TryGetTrackedLocalPose(timing, out Matrix4x4 mtx))
            {
                if (RuntimeVrStateServices.IsOpenXRActive)
                    return;

                VrDevice? device = Device;
                if (device is null || !device.IsEnabled || device.TrackingState != ETrackingResult.Running_OK)
                    return;
                mtx = device.RenderDeviceToAbsoluteTrackingMatrix;
                if (LocalMatrixOffset.HasValue)
                    mtx *= LocalMatrixOffset.Value;
                _lastUsableLocalPose = mtx;
                _hasLastUsableLocalPose = true;
            }

            Matrix4x4 renderMatrix = mtx * ParentRenderMatrix;
            bool isOpenXrHeadset = RuntimeVrStateServices.IsOpenXRActive && this is XREngine.Scene.Transforms.VRHeadsetTransform;
            SetRenderMatrix(renderMatrix, recalcAllChildRenderMatrices: !isOpenXrHeadset);
        }

        /// <summary>
        /// Updates the local matrix based on the VR device's tracking matrix and the optional local matrix offset.
        /// Uses the VR state's prediction time to guess what the matrix will be at render time.
        /// </summary>
        /// <returns></returns>
        protected override Matrix4x4 CreateLocalMatrix()
        {
            if (SyntheticPoseEnabled)
                return LocalMatrixOffset ?? Matrix4x4.Identity;

            if (TryGetTrackedLocalPose(RuntimeVrPoseTiming.Predicted, out Matrix4x4 localPose))
                return localPose;

            if (RuntimeVrStateServices.IsOpenXRActive)
                return _hasLastUsableLocalPose ? _lastUsableLocalPose : LocalMatrix;

            VrDevice? device = Device;
            if (device is null || !device.IsEnabled || device.TrackingState != ETrackingResult.Running_OK)
                return _hasLastUsableLocalPose ? _lastUsableLocalPose : LocalMatrix;

            Matrix4x4 mtx = device.DeviceToAbsoluteTrackingMatrix;
            if (LocalMatrixOffset.HasValue)
                mtx *= LocalMatrixOffset.Value;
            _lastUsableLocalPose = mtx;
            _hasLastUsableLocalPose = true;
            return mtx;
        }

        private bool TryGetTrackedLocalPose(RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        {
            pose = Matrix4x4.Identity;

            bool ok;
            if (this is XREngine.Scene.Transforms.VRHeadsetTransform)
                ok = RuntimeVrStateServices.TryGetHeadLocalPose(timing, out pose);
            else if (this is XREngine.Data.Components.Scene.VRControllerTransform ctrl)
                ok = RuntimeVrStateServices.TryGetControllerLocalPose(ctrl.LeftHand, timing, out pose);
            else if (this is XREngine.Data.Components.Scene.VRTrackerTransform tracker && RuntimeVrStateServices.IsOpenXRActive && !string.IsNullOrWhiteSpace(tracker.OpenXrTrackerUserPath))
                ok = RuntimeVrStateServices.TryGetTrackerLocalPose(tracker.OpenXrTrackerUserPath, timing, out pose);
            else
                ok = false;

            if (ok && LocalMatrixOffset.HasValue)
                pose *= LocalMatrixOffset.Value;

            if (ok)
            {
                _lastUsableLocalPose = pose;
                _hasLastUsableLocalPose = true;
            }

            return ok;
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
