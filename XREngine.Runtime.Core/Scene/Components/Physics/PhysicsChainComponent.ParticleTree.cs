using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    internal sealed class ParticleTree(Transform root) : XRBase
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

        /// <summary>The world matrix of the root's parent when the latest simulation step applied its motion.</summary>
        public Matrix4x4 PreviousRootParentWorld { get; set; }
        public bool HasPreviousRootParentWorld { get; set; }
        /// <summary>The world matrix of the root's parent at the latest prepare.</summary>
        public Matrix4x4 LatestRootParentWorld { get; set; }
        /// <summary>The motion of the root's parent since the previous simulation step.</summary>
        public Matrix4x4 RootParentMotion { get; set; } = Matrix4x4.Identity;

        /// <summary>The motion of the root since the latest simulation step, added to the presented pose.</summary>
        public Vector3 PresentationOffset { get; set; }

        private Vector3 _restGravity;
        public Vector3 RestGravity
        {
            get => _restGravity;
            set => SetFieldUnchecked(ref _restGravity, value);
        }
    }
}
