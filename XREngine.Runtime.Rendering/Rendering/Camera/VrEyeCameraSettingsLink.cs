using System.Threading;
using XREngine.Data.Core;
using XREngine.Rendering.PostProcessing;

namespace XREngine.Rendering
{
    /// <summary>
    /// Keeps the left and right VR eye cameras on the same render settings. The eyes have their own
    /// settings, separate from the desktop camera, but the two eyes must never differ:
    /// <list type="bullet">
    /// <item>Both eye cameras use one <see cref="CameraPostProcessStateCollection"/>. When an eye
    /// camera gets a new collection, the other eye camera adopts it.</item>
    /// <item>A change to a per-camera render override (anti-aliasing mode, MSAA sample count, HDR
    /// output, TSR render scale, post-process material) on either eye camera is copied to the other
    /// eye camera at once.</item>
    /// <item><see cref="SourceCamera"/> is the eye camera that was edited last, so a runtime that
    /// rewrites both eyes each frame (OpenXR) starts from the edited eye.</item>
    /// </list>
    /// The OpenXR and OpenVR runtimes each own one link. The link subscribes to the property changes
    /// of the two cameras and unsubscribes in <see cref="Detach"/>.
    /// </summary>
    public sealed class VrEyeCameraSettingsLink
    {
        private XRCamera? _leftEyeCamera;
        private XRCamera? _rightEyeCamera;
        private XRCamera? _sourceCamera;
        private CameraPostProcessStateCollection? _sharedPostProcessStates;
        private int _syncing;

        /// <summary>The eye camera that was edited last, or the left eye camera when neither was edited.</summary>
        public XRCamera? SourceCamera => _sourceCamera;

        /// <summary>
        /// Links <paramref name="leftEyeCamera"/> and <paramref name="rightEyeCamera"/>. Both cameras get
        /// the shared post-process state collection (the left camera's collection on the first attach).
        /// When the camera pair changes, the right camera also takes the left camera's render overrides.
        /// Calling it again with the same pair only confirms the shared collection.
        /// </summary>
        public void Attach(XRCamera leftEyeCamera, XRCamera rightEyeCamera)
        {
            ArgumentNullException.ThrowIfNull(leftEyeCamera);
            ArgumentNullException.ThrowIfNull(rightEyeCamera);

            bool pairChanged = !ReferenceEquals(_leftEyeCamera, leftEyeCamera) ||
                !ReferenceEquals(_rightEyeCamera, rightEyeCamera);
            CameraPostProcessStateCollection sharedPostProcessStates =
                _sharedPostProcessStates ??= leftEyeCamera.PostProcessStates ?? new CameraPostProcessStateCollection();

            BeginMirroringSuppression();
            try
            {
                if (!ReferenceEquals(leftEyeCamera.PostProcessStates, sharedPostProcessStates))
                    leftEyeCamera.PostProcessStates = sharedPostProcessStates;
                if (!ReferenceEquals(rightEyeCamera.PostProcessStates, sharedPostProcessStates))
                    rightEyeCamera.PostProcessStates = sharedPostProcessStates;
                if (pairChanged)
                    CopyScalarSettings(leftEyeCamera, rightEyeCamera);
            }
            finally
            {
                EndMirroringSuppression();
            }

            UpdateSubscriptions(leftEyeCamera, rightEyeCamera);
            _sourceCamera ??= leftEyeCamera;
        }

        /// <summary>Stops mirroring and unsubscribes from both eye cameras.</summary>
        public void Detach()
            => UpdateSubscriptions(null, null);

        /// <summary>
        /// Stops mirroring until the matching <see cref="EndMirroringSuppression"/>. A runtime calls it
        /// while it writes the same values to both eyes itself. Calls can nest.
        /// </summary>
        public void BeginMirroringSuppression()
            => Interlocked.Increment(ref _syncing);

        /// <summary>Ends one <see cref="BeginMirroringSuppression"/>.</summary>
        public void EndMirroringSuppression()
            => Interlocked.Decrement(ref _syncing);

        /// <summary>Copies the per-camera render overrides from <paramref name="source"/> to <paramref name="destination"/>.</summary>
        public static void CopyScalarSettings(XRCamera source, XRCamera destination)
        {
            destination.AntiAliasingModeOverride = source.AntiAliasingModeOverride;
            destination.MsaaSampleCountOverride = source.MsaaSampleCountOverride;
            destination.OutputHDROverride = source.OutputHDROverride;
            destination.TsrRenderScaleOverride = source.TsrRenderScaleOverride;
            destination.PostProcessMaterial = source.PostProcessMaterial;
        }

        private void UpdateSubscriptions(XRCamera? leftEyeCamera, XRCamera? rightEyeCamera)
        {
            UpdateSubscription(ref _leftEyeCamera, leftEyeCamera);
            UpdateSubscription(ref _rightEyeCamera, rightEyeCamera);

            if (leftEyeCamera is null || rightEyeCamera is null)
                _sourceCamera = null;
            else if (!ReferenceEquals(_sourceCamera, leftEyeCamera) &&
                     !ReferenceEquals(_sourceCamera, rightEyeCamera))
            {
                _sourceCamera = leftEyeCamera;
            }
        }

        private void UpdateSubscription(ref XRCamera? subscribedCamera, XRCamera? camera)
        {
            if (ReferenceEquals(subscribedCamera, camera))
                return;

            if (subscribedCamera is not null)
                subscribedCamera.PropertyChanged -= HandleEyeCameraPropertyChanged;

            subscribedCamera = camera;

            if (subscribedCamera is not null)
                subscribedCamera.PropertyChanged += HandleEyeCameraPropertyChanged;
        }

        private void HandleEyeCameraPropertyChanged(object? sender, IXRPropertyChangedEventArgs args)
        {
            if (Volatile.Read(ref _syncing) != 0 || sender is not XRCamera sourceCamera)
                return;

            XRCamera? destinationCamera = ReferenceEquals(sourceCamera, _leftEyeCamera)
                ? _rightEyeCamera
                : ReferenceEquals(sourceCamera, _rightEyeCamera) ? _leftEyeCamera : null;
            if (destinationCamera is null)
                return;

            switch (args.PropertyName)
            {
                case nameof(XRCamera.AntiAliasingModeOverride):
                case nameof(XRCamera.MsaaSampleCountOverride):
                case nameof(XRCamera.OutputHDROverride):
                case nameof(XRCamera.TsrRenderScaleOverride):
                case nameof(XRCamera.PostProcessMaterial):
                    _sourceCamera = sourceCamera;
                    BeginMirroringSuppression();
                    try
                    {
                        CopyScalarSettings(sourceCamera, destinationCamera);
                    }
                    finally
                    {
                        EndMirroringSuppression();
                    }
                    break;
                case nameof(XRCamera.PostProcessStates):
                    _sourceCamera = sourceCamera;
                    AdoptPostProcessStates(sourceCamera, destinationCamera);
                    break;
            }
        }

        private void AdoptPostProcessStates(XRCamera sourceCamera, XRCamera destinationCamera)
        {
            CameraPostProcessStateCollection sharedPostProcessStates =
                sourceCamera.PostProcessStates ?? new CameraPostProcessStateCollection();
            _sharedPostProcessStates = sharedPostProcessStates;

            BeginMirroringSuppression();
            try
            {
                if (!ReferenceEquals(destinationCamera.PostProcessStates, sharedPostProcessStates))
                    destinationCamera.PostProcessStates = sharedPostProcessStates;
            }
            finally
            {
                EndMirroringSuppression();
            }
        }
    }
}
