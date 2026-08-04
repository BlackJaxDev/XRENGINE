using System.Numerics;
using XREngine.Components;

namespace XREngine.Runtime.Bootstrap.Builders;

public static partial class BootstrapLightingBuilder
{
    private sealed class DynamicDebugLightRigComponent : XRComponent
    {
        private DynamicDebugLightState[] _states = [];

        public void Configure(DynamicDebugLightState[] states)
        {
            SetField(ref _states, states);
            UpdateLights();
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RegisterTick(ETickGroup.Normal, ETickOrder.Animation, UpdateLights);
            UpdateLights();
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, UpdateLights);
        }

        private void UpdateLights()
        {
            float time = (float)Engine.ElapsedTime;

            for (int i = 0; i < _states.Length; i++)
                UpdateLight(_states[i], time);
        }

        private static void UpdateLight(in DynamicDebugLightState state, float time)
        {
            Vector3 position = Oscillate(
                time,
                state.Center,
                state.PositionAmplitude,
                state.PositionFrequency,
                state.PositionPhase);

            state.Transform.Translation = position;

            if (state.LookAtTarget)
            {
                Vector3 target = Oscillate(
                    time,
                    state.TargetCenter,
                    state.TargetAmplitude,
                    state.TargetFrequency,
                    state.TargetPhase);
                Vector3 forward = target - position;
                if (forward.LengthSquared() < 0.0001f)
                    forward = Globals.Forward;
                else
                    forward = Vector3.Normalize(forward);

                float roll = MathF.Sin(time * state.RotationFrequency.Z + state.RotationPhase.Z) * 0.35f;
                state.Transform.Rotation = CreateForwardRotation(forward, roll);
                return;
            }

            state.Transform.Rotation = Quaternion.CreateFromYawPitchRoll(
                MathF.Sin(time * state.RotationFrequency.Y + state.RotationPhase.Y) * 1.2f,
                MathF.Sin(time * state.RotationFrequency.X + state.RotationPhase.X) * 0.8f,
                MathF.Sin(time * state.RotationFrequency.Z + state.RotationPhase.Z) * 1.0f);
        }

        private static Vector3 Oscillate(float time, Vector3 center, Vector3 amplitude, Vector3 frequency, Vector3 phase)
            => center + new Vector3(
                MathF.Sin(time * frequency.X + phase.X) * amplitude.X,
                MathF.Sin(time * frequency.Y + phase.Y) * amplitude.Y,
                MathF.Sin(time * frequency.Z + phase.Z) * amplitude.Z);

        private static Quaternion CreateForwardRotation(Vector3 forward, float rollRadians)
        {
            Vector3 backward = -forward;
            Vector3 upSeed = MathF.Abs(Vector3.Dot(backward, Globals.Up)) > 0.99f
                ? Globals.Right
                : Globals.Up;
            Vector3 right = Vector3.Normalize(Vector3.Cross(upSeed, backward));
            Vector3 up = Vector3.Normalize(Vector3.Cross(backward, right));

            if (rollRadians != 0.0f)
            {
                Quaternion roll = Quaternion.CreateFromAxisAngle(forward, rollRadians);
                right = Vector3.Transform(right, roll);
                up = Vector3.Transform(up, roll);
            }

            Matrix4x4 basis = new(
                right.X, right.Y, right.Z, 0.0f,
                up.X, up.Y, up.Z, 0.0f,
                backward.X, backward.Y, backward.Z, 0.0f,
                0.0f, 0.0f, 0.0f, 1.0f);

            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));
        }
    }
}
