//using System.Collections;
//using System.ComponentModel;
//using System.Numerics;
//using System.Runtime.InteropServices;
//using XREngine.Components.Scene.Transforms;
//using XREngine.Data.Colors;
//using XREngine.Data.Rendering;
//using XREngine.Rendering;
//using XREngine.Rendering.Commands;
//using XREngine.Rendering.Info;
//using XREngine.Scene;

//namespace XREngine.Components
//{
//    public interface IParticle
//    {
//        float Life { get; set; }
//        float CameraDistance { get; set; }

//        void Update(float delta, XRDataBuffer[] instBufs, int instanceIndex, BaseParticleEmitterComponent component);
//        void Initialize(BaseParticleEmitterComponent component);
//        void GenerateParticleMesh(BaseParticleEmitterComponent component, out XRMeshRenderer mesh);
//    }
//    public abstract class BaseParticleEmitterComponent : XRComponent, IRenderable, IPreRendered
//    {
//        private int _maxParticles = 10000;
//        private bool _isEmitting = true;
//        private bool _isSimulating = true;
//        private float _elapsed = 0.0f;
//        private int _lastUsedParticle = 0;

//        [Category("Rendering")]
//        public int ActiveInstances => ParticleMesh.Instances;
//        [Category("Rendering")]
//        public XRMeshRenderer ParticleMesh
//        {
//            get => _rc.Mesh;
//            set => _rc.Mesh = value;
//        }
//        [Browsable(false)]
//        public Vector3 CameraPosition { get; private set; }
//        [Category("Rendering")]
//        public RenderInfo3D RenderInfo { get; } = new RenderInfo3D();

//        [Category("Emitter")]
//        public bool IsSimulating
//        {
//            get => _isSimulating;
//            set
//            {
//                if (_isSimulating == value)
//                    return;

//                _isSimulating = value;

//                if (IsActive)
//                {
//                    if (_isSimulating)
//                        StartSimulating();
//                    else
//                        StopSimulating();
//                }
//            }
//        }
//        [Category("Emitter")]
//        public bool IsEmitting
//        {
//            get => _isEmitting;
//            set
//            {
//                if (_isEmitting == value)
//                    return;

//                _isEmitting = value;

//                if (IsActive && !_isSimulating && _isEmitting)
//                    IsSimulating = true;
//            }
//        }

//        [Category("Emitter")]
//        public int NumPerSpawn { get; set; } = 2;
//        [Category("Emitter")]
//        public float SecPerSpawn { get; set; } = 0.1f;
//        [Category("Emitter")]
//        public float NewParticleLifeSeconds { get; set; } = 2.0f;
//        [Category("Emitter")]
//        public virtual int MaxParticles
//        {
//            get => _maxParticles;
//            set
//            {
//                _maxParticles = value;
//                if (IsSpawned)
//                {
//                    _particles.Resize(_maxParticles);
//                    var instBufs = ParticleMesh.Data.GetAllBuffersOfType(EBufferType.Other);
//                    //instBufs[0].Resize(_maxParticles);
//                    //instBufs[1].Resize(_maxParticles);
//                }
//            }
//        }

//        protected internal override void Start()
//        {
//            base.Start();
//            GenerateParticleMesh();
//            if (IsSimulating = IsEmitting)
//                StartSimulating();
//        }
//        protected internal override void Stop()
//        {
//            base.Stop();
//            IsSimulating = false;
//        }

//        private void StartSimulating()
//            => RegisterTick(ETickGroup.DuringPhysics, ETickOrder.Scene, Update);
//        private void StopSimulating()
//            => UnregisterTick(ETickGroup.DuringPhysics, ETickOrder.Scene, Update);

//        protected virtual int FindUnusedParticleIndex()
//        {
//            int minLifeIndex = -1;
//            float minLife = float.MaxValue;
//            for (int i = _lastUsedParticle; i < MaxParticles; i++)
//            {
//                var ptcl = this[i];
//                if (ptcl.Life <= 0.0f)
//                    return _lastUsedParticle = i;
//                else if (ptcl.Life < minLife)
//                {
//                    minLife = ptcl.Life;
//                    minLifeIndex = i;
//                }
//            }

//            for (int i = 0; i < _lastUsedParticle; i++)
//            {
//                var ptcl = this[i];
//                if (ptcl.Life <= 0.0f)
//                    return _lastUsedParticle = i;
//                else if (ptcl.Life < minLife)
//                {
//                    minLife = ptcl.Life;
//                    minLifeIndex = i;
//                }
//            }

//            return _lastUsedParticle = minLifeIndex; // All particles are taken, override the first one
//        }
//        private unsafe void Update(float delta)
//        {
//            if (IsEmitting)
//            {
//                _elapsed += delta;

//                while (SecPerSpawn > 0.001f && _elapsed >= SecPerSpawn)
//                {
//                    _elapsed -= SecPerSpawn;
//                    for (int i = 0; i < NumPerSpawn; ++i)
//                    {
//                        int index = FindUnusedParticleIndex();
//                        var p = this[index];
//                        p.Initialize(this);
//                        this[index] = p;
//                    }
//                }
//            }

//            int instanceCount = 0;
//            Parallel.For(0, MaxParticles, i =>
//            for (int i = 0; i < MaxParticles; ++i)
//            {
//                IParticle p = this[i];
//                if (p.Life > 0.0f)
//                {
//                    var instBufs = ParticleMesh.Mesh.GetAllBuffersOfType(EBufferType.Aux);
//                    p.Update(delta, instBufs, instanceCount, this);
//                    //Interlocked.Increment(ref instanceCount);
//                    ++instanceCount;
//                }
//                else
//                {
//                     Particles that just died will be put at the end of the buffer in SortParticles();
//                    p.CameraDistance = -1.0f;
//                }
//                this[i] = p;
//            }
//            );

//            ParticleMesh.Instances = instanceCount;
//            SortParticles();

//            if (instanceCount == 0 && !IsEmitting)
//                IsSimulating = false;
//        }

//        public abstract IParticle this[int index] { get; protected set; }
//        protected abstract void GenerateParticleMesh();
//        protected abstract void SortParticles();

//        public RenderCommandMesh3D _rc = new();
//        public void AddRenderables(RenderCommandCollection passes, XRCamera camera) => passes.Add(_rc);

//        [Browsable(false)]
//        public bool PreRenderEnabled => IsSimulating;
//        public void PreRenderUpdate(XRCamera camera) => CameraPosition = camera?.WorldPoint ?? Vector3.Zero;
//        public void PreRenderSwap() { }
//        public void PreRender(XRViewport viewport, XRCamera camera) { }
//    }
//    public class ParticleEmitterComponent<TParticle>
//        : BaseParticleEmitterComponent
//        where TParticle : struct, IParticle
//    {
//        public TParticle[] _particles;

//        public override IParticle this[int index]
//        {
//            get => _particles[index];
//            protected set => _particles[index] = (TParticle)value;
//        }
//        protected override void GenerateParticleMesh()
//        {
//            TParticle ptcl = new TParticle();
//            ptcl.GenerateParticleMesh(this, out XRMeshRenderer mesh);
//            ParticleMesh = mesh;
//        }
//        protected override void OnSpawned()
//        {
//            _particles = new TParticle[MaxParticles];
//            base.OnSpawned();
//        }

//        protected override void SortParticles()
//            => Array.Sort(_particles, _comparer);

//        private readonly ParticleComparer _comparer = new ParticleComparer();

//        private class ParticleComparer : IComparer<TParticle>, IComparer
//        {
//            public int Compare(TParticle x, TParticle y)
//                => (int)(y.CameraDistance - x.CameraDistance);
//            public int Compare(object x, object y)
//                => Compare((TParticle)x, (TParticle)y);
//        }
//    }

//    [StructLayout(LayoutKind.Sequential, Pack = 1)]
//    public struct BasicGravityParticle : IParticle
//    {
//        private float _life;
//        private float _scale;
//        private float _distance;
//        private Vector3 _position;
//        private Vector3 _velocity;
//        private ColorF4 _color;

//        public float Life { get => _life; set => _life = value; }
//        public float Scale { get => _scale; set => _scale = value; }
//        public float CameraDistance { get => _distance; set => _distance = value; }
//        public Vector3 Position { get => _position; set => _position = value; }
//        public Vector3 Velocity { get => _velocity; set => _velocity = value; }
//        public ColorF4 Color { get => _color; set => _color = value; }
        
//        public void Initialize(BaseParticleEmitterComponent component)
//        {
//            Vector3 rand = new Vector3(
//                (float)Engine.Random.NextDouble(), 
//                (float)Engine.Random.NextDouble(), 
//                (float)Engine.Random.NextDouble());

//            Position = component.WorldPoint;
//            Color = new ColorF4(rand.X, rand.Y, rand.Z, 1.0f);
//            rand.Y += 0.5f;
//            Velocity = component.WorldMatrix.UpVec * 10.0f + (rand - 0.5f) * 5.0f;
//            Life = component.NewParticleLifeSeconds;
//            Scale = (float)Engine.Random.NextDouble() + 1.0f;
//        }
//        public unsafe void Update(float delta, XRDataBuffer[] instBufs, int instanceIndex, BaseParticleEmitterComponent component)
//        {
//            Life -= delta;
//            Velocity += new Vector3(0.0f, -9.81f, 0.0f) * delta * 0.5f;
//            Position += Velocity * delta;
//            CameraDistance = (Position - component.CameraPosition).LengthSquared;

//            Vector4* posPtr = (Vector4*)instBufs[0].Address;
//            posPtr[instanceIndex] = new Vector4(Position, Scale);

//            ColorF4* colPtr = (ColorF4*)instBufs[1].Address;
//            colPtr[instanceIndex] = Color;
//        }
//        public void GenerateParticleMesh(BaseParticleEmitterComponent component, out MeshRenderer mesh)
//        {
//            Rendering.Models.TMesh data = Rendering.Models.TMesh.Create(VertexShaderDesc.JustPositions(), TVertexQuad.PosZ(1, false, 0.0f, true));

//            Vector4[] positions = new Vector4[component.MaxParticles];
//            ColorF4[] colors = new ColorF4[component.MaxParticles];

//            var posBuf = data.AddBuffer(positions, new VertexAttribInfo(EBufferType.Aux, 0), false, false, true, 1);
//            var colBuf = data.AddBuffer(colors, new VertexAttribInfo(EBufferType.Aux, 1), false, false, true, 1);

//            posBuf.Location = 1;
//            colBuf.Location = 2;

//            XRShader vert = XRShader.EngineShader("ParticleInstance.vs", EShaderType.Vertex);
//            XRShader frag = XRShader.EngineShader("ParticleInstance.fs", EShaderType.Fragment);
//            RenderingParameters rp = new RenderingParameters(true);
//            XRMaterial mat = new XRMaterial("ParticleMaterial", rp, vert, frag);
//            mesh = new MeshRenderer(data, mat);
//        }
//    };
//}
