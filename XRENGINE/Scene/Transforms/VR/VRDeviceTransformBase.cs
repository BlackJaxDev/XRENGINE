using OpenVR.NET.Devices;
using System.Numerics;
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

        protected internal override void OnSceneNodeActivated()
        {
            base.OnSceneNodeActivated();
            Engine.VRState.RecalcMatrixOnDraw += VRState_RecalcMatrixOnDraw;
            Engine.Time.Timer.PreUpdateFrame += MarkLocalModified;
        }
        protected internal override void OnSceneNodeDeactivated()
        {
            base.OnSceneNodeDeactivated();
            Engine.VRState.RecalcMatrixOnDraw -= VRState_RecalcMatrixOnDraw;
            Engine.Time.Timer.PreUpdateFrame -= MarkLocalModified;
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
                _localMatrixOffset = value;
                MarkLocalModified();
            }
        }

        /// <summary>
        /// Occurs directly before rendering to recalculate the render matrix based on the VR state.
        /// </summary>
        private void VRState_RecalcMatrixOnDraw()
        {
            var device = Device;

            Matrix4x4 mtx;
            if (device is null)
            {
                if (LocalMatrixOffset.HasValue)
                    mtx = LocalMatrixOffset.Value;
                else
                    mtx = Matrix4x4.Identity;
            }
            else
            {
                mtx = device.RenderDeviceToAbsoluteTrackingMatrix;
                if (LocalMatrixOffset.HasValue)
                    mtx *= LocalMatrixOffset.Value;
            }

            SetRenderMatrix(mtx * ParentRenderMatrix, true);
        }

        /// <summary>
        /// Updates the local matrix based on the VR device's tracking matrix and the optional local matrix offset.
        /// Uses the VR state's prediction time to guess what the matrix will be at render time.
        /// </summary>
        /// <returns></returns>
        protected override Matrix4x4 CreateLocalMatrix()
        {
            var device = Device;

            Matrix4x4 mtx;
            if (device is null)
                mtx = LocalMatrixOffset ?? Matrix4x4.Identity;
            else
            {
                mtx = device.DeviceToAbsoluteTrackingMatrix;
                if (LocalMatrixOffset.HasValue)
                    mtx *= LocalMatrixOffset.Value;
            }

            return mtx;
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
