using System.Numerics;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Runtime.Bootstrap.Builders;

public static partial class BootstrapPhase524bValidationBuilder
{
    private sealed class Phase524bScenarioComponent : XRComponent
    {
        private Transform? _occluder;
        private Transform? _desktopMover;
        private Transform? _spsMover;
        private Transform? _headMotionRoot;
        private Vector3 _headMotionRootBaseTranslation;
        private Quaternion _headMotionRootBaseRotation = Quaternion.Identity;
        private bool _desktopMovingSentinelIsCameraRelative;

        public void Configure(
            Transform? occluder,
            Transform? desktopMover,
            Transform? spsMover,
            Transform? headMotionRoot,
            bool desktopMovingSentinelIsCameraRelative)
        {
            _occluder = occluder;
            _desktopMover = desktopMover;
            _spsMover = spsMover;
            _headMotionRoot = headMotionRoot;
            _desktopMovingSentinelIsCameraRelative = desktopMovingSentinelIsCameraRelative;
            if (_headMotionRoot is not null)
            {
                _headMotionRootBaseTranslation = _headMotionRoot.Translation;
                _headMotionRootBaseRotation = _headMotionRoot.Rotation;
            }
            ApplyScenario();
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            ApplyScenario();
            RegisterTick(ETickGroup.Normal, ETickOrder.Animation, ApplyScenario);
        }

        protected override void OnComponentDeactivated()
        {
            UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, ApplyScenario);
            if (_headMotionRoot is not null)
            {
                _headMotionRoot.Translation = _headMotionRootBaseTranslation;
                _headMotionRoot.Rotation = _headMotionRootBaseRotation;
            }
            base.OnComponentDeactivated();
        }

        private void ApplyScenario()
        {
            int sequenceFrame = Phase524bTemporalScenarioDiagnostics.SequenceFrame;
            _occluder?.Translation = CalculateTemporalOccluderTranslation(sequenceFrame);
            _desktopMover?.Translation = CalculateTemporalMovingSentinelTranslation(
                    sequenceFrame,
                    headsetRelative: _desktopMovingSentinelIsCameraRelative);
            _spsMover?.Translation = CalculateTemporalMovingSentinelTranslation(sequenceFrame, headsetRelative: true);
            if (_headMotionRoot is null)
                return;

            _headMotionRoot.Translation = _headMotionRootBaseTranslation +
                CalculateTemporalHeadTranslation(sequenceFrame);
            float yawRadians = CalculateTemporalHeadYawDegrees(sequenceFrame) * (MathF.PI / 180.0f);
            _headMotionRoot.Rotation = Quaternion.Normalize(
                _headMotionRootBaseRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawRadians));
        }
    }
}
