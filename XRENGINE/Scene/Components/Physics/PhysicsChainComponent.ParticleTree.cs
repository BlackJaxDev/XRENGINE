using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    class ParticleTree(Transform root) : XRBase
    {
        public Transform Root { get; } = root;
        public Matrix4x4 RootWorldToLocalMatrix => Root.InverseWorldMatrix;

        private Vector3 _localGravity;
        public Vector3 LocalGravity
        {
            get => _localGravity;
            set => SetFieldUnchecked(ref _localGravity, value);
        }

        private float _boneTotalLength;
        public float BoneTotalLength
        {
            get => _boneTotalLength;
            set => SetFieldUnchecked(ref _boneTotalLength, value);
        }

        private List<Particle> _particles = [];
        public List<Particle> Particles
        {
            get => _particles;
            set => SetFieldUnchecked(ref _particles, value ?? []);
        }

        private Vector3 _restGravity;
        public Vector3 RestGravity
        {
            get => _restGravity;
            set => SetFieldUnchecked(ref _restGravity, value);
        }
    }
}
