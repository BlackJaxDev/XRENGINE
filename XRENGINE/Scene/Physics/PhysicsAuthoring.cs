using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Scene.Physics
{
    public enum PhysicsReplicationAuthority
    {
        LocalSimulation,
        ServerAuthoritative,
        ClientAuthoritative,
        SharedDeterministic,
    }

    /// <summary>
    /// Backend-neutral authored physics material settings. Backends convert this description into native material objects.
    /// </summary>
    public class PhysicsMaterialDefinition : XRBase
    {
        private float _staticFriction = 0.5f;
        private float _dynamicFriction = 0.5f;
        private float _restitution = 0.1f;
        private float _damping;

        [Category("Physics Material")]
        public float StaticFriction
        {
            get => _staticFriction;
            set => SetField(ref _staticFriction, value);
        }

        [Category("Physics Material")]
        public float DynamicFriction
        {
            get => _dynamicFriction;
            set => SetField(ref _dynamicFriction, value);
        }

        [Category("Physics Material")]
        public float Restitution
        {
            get => _restitution;
            set => SetField(ref _restitution, value);
        }

        [Category("Physics Material")]
        public float Damping
        {
            get => _damping;
            set => SetField(ref _damping, value);
        }
    }

    /// <summary>
    /// Backend-neutral authored collider shape entry for simple and compound rigid bodies.
    /// </summary>
    public class PhysicsColliderShape : XRBase
    {
        private bool _enabled = true;
        private string? _name;
        private IPhysicsGeometry? _geometry;
        private PhysicsMaterialDefinition? _material;
        private Vector3 _localPosition = Vector3.Zero;
        private Quaternion _localRotation = Quaternion.Identity;

        [Category("Collider")]
        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        [Category("Collider")]
        public string? Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        [Category("Collider")]
        public IPhysicsGeometry? Geometry
        {
            get => _geometry;
            set => SetField(ref _geometry, value);
        }

        [Category("Collider")]
        public PhysicsMaterialDefinition? Material
        {
            get => _material;
            set => SetField(ref _material, value);
        }

        [Category("Collider")]
        public Vector3 LocalPosition
        {
            get => _localPosition;
            set => SetField(ref _localPosition, value);
        }

        [Category("Collider")]
        public Quaternion LocalRotation
        {
            get => _localRotation;
            set => SetField(ref _localRotation, value);
        }
    }
}
