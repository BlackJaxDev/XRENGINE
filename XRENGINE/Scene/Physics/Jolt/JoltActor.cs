using JoltPhysicsSharp;
using System.Numerics;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Scene.Physics.Jolt
{
    // Jolt Actor base class
    public abstract class JoltActor : IAbstractPhysicsActor
    {
        public BodyID BodyID { get; protected set; }
        public JoltScene? Scene { get; private set; }

        public virtual void Destroy(bool wakeOnLostTouch = false)
        {
            Scene?.DestroyActor(this);
        }

        internal virtual void OnAddedToScene(JoltScene scene)
        {
            Scene = scene;
        }

        internal virtual void OnRemovedFromScene(JoltScene scene)
        {
            Scene = null;
        }

        public abstract XRComponent? GetOwningComponent();
    }

    // Jolt Rigid Actor base class
    public abstract class JoltRigidActor : JoltActor, IAbstractRigidPhysicsActor
    {
        private JoltShapeMetadata? _shapeMetadata;

        public abstract (Vector3 position, Quaternion rotation) Transform { get; }
        public abstract Vector3 LinearVelocity { get; }
        public abstract Vector3 AngularVelocity { get; }
        public abstract bool IsSleeping { get; }

        internal void AttachShapeMetadata(JoltShapeMetadata metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            if (_shapeMetadata is not null)
                throw new InvalidOperationException("Jolt shape metadata has already been attached to this actor.");

            _shapeMetadata = metadata;
        }

        internal void ReplaceShapeMetadata(JoltShapeMetadata metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            JoltShapeMetadata? previous = _shapeMetadata;
            _shapeMetadata = metadata;
            previous?.Dispose();
        }

        internal uint ResolveFaceIndex(SubShapeID subShapeID)
            => _shapeMetadata?.ResolveFaceIndex(subShapeID) ?? uint.MaxValue;

        internal bool TryResolveBarycentricUV(
            SubShapeID subShapeID,
            Vector3 worldPosition,
            out Vector2 uv)
        {
            if (_shapeMetadata is null)
            {
                uv = Vector2.Zero;
                return false;
            }

            (Vector3 position, Quaternion rotation) = Transform;
            Vector3 localPosition = Vector3.Transform(
                worldPosition - position,
                Quaternion.Conjugate(rotation));
            return _shapeMetadata.TryResolveBarycentricUV(subShapeID, localPosition, out uv);
        }

        internal void ReleaseShapeMetadata()
        {
            _shapeMetadata?.Dispose();
            _shapeMetadata = null;
        }
    }
}
