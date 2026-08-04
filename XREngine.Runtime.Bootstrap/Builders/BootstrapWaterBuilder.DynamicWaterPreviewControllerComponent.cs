using System.Numerics;
using XREngine.Components;
using XREngine.Rendering;

namespace XREngine.Runtime.Bootstrap.Builders;

public static partial class BootstrapWaterBuilder
{
    public sealed class DynamicWaterPreviewControllerComponent : XRComponent
    {
        private XRMaterial? _waterMaterial;

        public XRMaterial? WaterMaterial
        {
            get => _waterMaterial;
            set => SetField(ref _waterMaterial, value);
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateInteractors);
            UpdateInteractors();
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnregisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateInteractors);
        }

        private void UpdateInteractors()
        {
            XRMaterial? material = WaterMaterial;
            if (material is null)
                return;

            float time = (float)Engine.ElapsedTime;
            Vector3 center = Transform.WorldTranslation;

            Vector3 spherePosition = center + new Vector3(
                MathF.Cos(time * 0.85f) * 3.2f,
                0.06f + MathF.Sin(time * 1.70f) * 0.08f,
                MathF.Sin(time * 0.85f) * 2.1f);
            const float sphereRadius = 0.55f;

            Vector3 capsuleCenter = center + new Vector3(
                MathF.Sin(time * 0.47f + 1.2f) * 2.5f,
                0.38f + MathF.Sin(time * 0.31f) * 0.18f,
                MathF.Cos(time * 0.61f + 0.45f) * 3.0f);
            Vector3 capsuleAxis = Vector3.Normalize(new Vector3(
                MathF.Sin(time * 0.73f) * 0.28f,
                1.0f,
                MathF.Cos(time * 0.73f) * 0.22f));
            const float capsuleHalfHeight = 1.0f;
            const float capsuleRadius = 0.32f;
            Vector3 capsuleStart = capsuleCenter + capsuleAxis * capsuleHalfHeight;
            Vector3 capsuleEnd = capsuleCenter - capsuleAxis * capsuleHalfHeight;

            material.SetInt("InteractorSphereCount", 1);
            material.SetVector4("InteractorSphere0", new Vector4(spherePosition, sphereRadius));
            material.SetVector4("InteractorSphere1", Vector4.Zero);
            material.SetVector4("InteractorSphere2", Vector4.Zero);
            material.SetVector4("InteractorSphere3", Vector4.Zero);

            material.SetInt("InteractorCapsuleCount", 1);
            material.SetVector4("InteractorCapsuleStart0", new Vector4(capsuleStart, capsuleRadius));
            material.SetVector4("InteractorCapsuleEnd0", new Vector4(capsuleEnd, capsuleRadius));
            material.SetVector4("InteractorCapsuleStart1", Vector4.Zero);
            material.SetVector4("InteractorCapsuleEnd1", Vector4.Zero);
            material.SetVector4("InteractorCapsuleStart2", Vector4.Zero);
            material.SetVector4("InteractorCapsuleEnd2", Vector4.Zero);
            material.SetVector4("InteractorCapsuleStart3", Vector4.Zero);
            material.SetVector4("InteractorCapsuleEnd3", Vector4.Zero);
        }
    }
}
