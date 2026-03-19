using System.ComponentModel;
using System.Numerics;
using XREngine.Data;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Transforms
{
    [DisplayName("Lagged Translation Transform")]
    /// <summary>
    /// Moves the scene node to the parent's position + an offset in local space, lagging behind by a specified amount for smooth movement.
    /// </summary>
    public class LaggedTranslationTransform(TransformBase? parent, Vector3 translation) : TransformBase(parent)
    {
        public LaggedTranslationTransform() : this(null) { }
        public LaggedTranslationTransform(TransformBase? parent) : this(parent, Vector3.Zero) { }

        protected Vector3 _currentTranslation = translation;
        protected Vector3 _desiredTranslation;
        protected float _invTransInterpSec = 40.0f;

        public Vector3 CurrentTranslation
        {
            get => _currentTranslation;
            set => SetField(ref _currentTranslation, value);
        }
        public Vector3 DesiredTranslation
        {
            get => _desiredTranslation;
            set => SetField(ref _desiredTranslation, value);
        }
        public float InverseTranslationInterpSeconds
        {
            get => _invTransInterpSec;
            set => SetField(ref _invTransInterpSec, value);
        }

        public override Vector3 LocalTranslation
            => _currentTranslation;

        public override Quaternion LocalRotation
            => Quaternion.Identity;

        public override Quaternion InverseLocalRotation
            => Quaternion.Identity;

        public override Vector3 WorldTranslation
            => Parent is null
                ? _currentTranslation
                : Vector3.Transform(_currentTranslation, ParentWorldMatrix);

        public override Quaternion WorldRotation
            => Parent?.WorldRotation ?? Quaternion.Identity;

        public override Quaternion InverseWorldRotation
            => Parent?.InverseWorldRotation ?? Quaternion.Identity;

        public override Quaternion RenderRotation
            => Parent?.RenderRotation ?? Quaternion.Identity;

        public override Quaternion InverseRenderRotation
            => Parent?.InverseRenderRotation ?? Quaternion.Identity;

        protected override void OnSceneNodeActivated()
            => RegisterTick(ETickGroup.Normal, (int)ETickOrder.Logic, Tick);
        protected override void OnSceneNodeDeactivated()
            => UnregisterTick(ETickGroup.Normal, (int)ETickOrder.Logic, Tick);

        protected virtual void Tick()
        {
            _currentTranslation = Interp.CosineTo(_currentTranslation, _desiredTranslation, Engine.SmoothedDelta, _invTransInterpSec);
            MarkLocalModified();
        }
        protected override Matrix4x4 CreateLocalMatrix()
            => Matrix4x4.CreateTranslation(_currentTranslation);

        public void SetTranslation(Vector3 translation)
        {
            _desiredTranslation = translation;
            _currentTranslation = translation;
        }
    }
}
