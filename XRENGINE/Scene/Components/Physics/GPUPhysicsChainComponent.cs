using Extensions;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Components.Animation;
using XREngine.Scene.Transforms;
using static XREngine.Engine;

namespace XREngine.Components;

/// <summary>
/// GPU-accelerated physics chain component that performs physics calculations on the compute shader
/// </summary>
public class GPUPhysicsChainComponent : XRComponent, IRenderable
{
    #region Properties

    [Description("Transform to use as root for the physics chain")]
    public Transform? Root;

    [Description("Multiple transforms to use as roots for the physics chain")]
    public List<Transform>? Roots;

    [Description("Transforms to exclude from the physics chain")]
    public List<TransformBase>? Exclusions;

    [Description("Transforms or objects used for distance checks")]
    public TransformBase? ReferenceObject;

    [Description("Update mode for physics calculations")]
    public EUpdateMode UpdateMode = EUpdateMode.Default;

    [Range(0, 1)]
    [Description("Blend weight for physics effects")]
    public float BlendWeight = 1.0f;

    [Description("Updates per second, 0 for every frame")]
    public float UpdateRate;

    [Range(0, 1)]
    [Description("Damping factor to reduce velocity")]
    public float Damping = 0.1f;

    [Range(0, 1)]
    [Description("Elasticity factor to pull back to rest position")]
    public float Elasticity = 0.1f;

    [Range(0, 1)]
    [Description("Stiffness factor to maintain initial shape")]
    public float Stiffness = 0.1f;

    [Range(0, 1)]
    [Description("Inertia factor affecting response to parent movement")]
    public float Inert;

    [Range(0, 1)]
    [Description("Friction factor on collisions")]
    public float Friction = 0.5f;

    [Description("Radius of particles for collision")]
    public float Radius = 0.2f;

    [Description("End bone length")]
    public float EndLength;

    [Description("End bone offset")]
    public Vector3 EndOffset;

    [Description("Gravity vector")]
    public Vector3 Gravity = new(0, -9.8f, 0);

    [Description("Additional force vector")]
    public Vector3 Force;

    [Description("Axis to freeze movement on")]
    public EFreezeAxis FreezeAxis;

    [Description("Enable/disable distant objects for optimization")]
    public bool DistantDisable;

    [Description("Distance at which to disable physics")]
    public float DistanceToObject = 20.0f;

    [Description("Run physics calculations on multiple threads")]
    public bool Multithread = true;

    // Distribution curves
    public AnimationCurve? DampingDistrib;
    public AnimationCurve? ElasticityDistrib;
    public AnimationCurve? StiffnessDistrib;
    public AnimationCurve? InertDistrib;
    public AnimationCurve? FrictionDistrib;
    public AnimationCurve? RadiusDistrib;

    [Description("Colliders to check for physics interactions")]
    public List<PhysicsChainColliderBase>? Colliders;

    #endregion

    #region Internal Data Structures

    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleData
    {
        public Vector3 Position;
        public Vector3 PrevPosition;
        public Vector3 TransformPosition;
        public Vector3 TransformLocalPosition;
        public int ParentIndex;
        public float Damping;
        public float Elasticity;
        public float Stiffness;
        public float Inert;
        public float Friction;
        public float Radius;
        public float BoneLength;
        public int IsColliding;
        // Padding to ensure alignment
        public float Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleTreeData
    {
        public Vector3 LocalGravity;
        public Vector3 RestGravity;
        public int ParticleStart;
        public int ParticleCount;
        public Matrix4x4 RootWorldToLocal;
        public float BoneTotalLength;
        // Padding to ensure alignment
        public Vector3 Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ColliderData
    {
        public Vector4 Center;  // xyz: position, w: radius
        public Vector4 Params;  // Type-specific parameters
        public int Type;        // 0: Sphere, 1: Capsule, 2: Box
        public Vector3 Padding;
    }

    private class ParticleTree(Transform root)
    {
        public Transform Root = root;
        public Vector3 LocalGravity;
        public Vector3 RestGravity = Vector3.Zero;
        public List<Particle> Particles = [];
        public float BoneTotalLength;
        public Matrix4x4 RootWorldToLocalMatrix => Root.InverseWorldMatrix;
    }

    private class Particle(Transform? transform, int parentIndex)
    {
        public Transform? Transform = transform;
        public int ParentIndex = parentIndex;
        public int ChildCount;
        public Vector3 Position;
        public Vector3 PrevPosition;
        public Vector3 TransformPosition;
        public Vector3 TransformLocalPosition;
        public Matrix4x4 TransformLocalToWorldMatrix;
        public Vector3 InitLocalPosition;
        public Quaternion InitLocalRotation;
        public Vector3 EndOffset;
        public float BoneLength;
        public float Damping;
        public float Elasticity;
        public float Stiffness;
        public float Inert;
        public float Friction;
        public float Radius;
        public bool IsColliding;
    }

    #endregion

    #region Private Fields

    private XRRenderProgram? _computeProgram;
    private XRShader _calculateParticlesShader;
    private XRShader _applyConstraintsShader;
    private XRShader _skipUpdateParticlesShader;

    private XRDataBuffer? _particlesBuffer;
    private XRDataBuffer? _particleTreesBuffer;
    private XRDataBuffer? _transformMatricesBuffer;
    private XRDataBuffer? _collidersBuffer;
    
    private readonly List<ParticleTree> _particleTrees = [];
    private readonly List<PhysicsChainColliderBase> _effectiveColliders = [];
    private readonly List<ParticleData> _particlesData = [];
    private readonly List<ParticleTreeData> _particleTreesData = [];
    private readonly List<Matrix4x4> _transformMatrices = [];
    private readonly List<ColliderData> _collidersData = [];

    private int _totalParticleCount;
    private int _prepareFrame;
    private int _updateCount;
    private int _preUpdateCount;
    private float _deltaTime;
    private float _time;
    private float _weight;
    private float _objectScale = 1.0f;
    private Vector3 _objectPrevPosition;
    private Vector3 _objectMove;
    private bool _distantDisabled;
    private bool _buffersInitialized;

    #endregion

    #region Component Lifecycle

    protected internal override void OnComponentActivated()
    {
        // Load compute shader
        _calculateParticlesShader = ShaderHelper.LoadEngineShader("Compute/CalculateParticles");
        _applyConstraintsShader = ShaderHelper.LoadEngineShader("Compute/ApplyConstraints");
        _skipUpdateParticlesShader = ShaderHelper.LoadEngineShader("Compute/SkipUpdateParticles");

        SetupParticles();
        RegisterTick(ETickGroup.PostPhysics, ETickOrder.Animation, FixedUpdate);
        RegisterTick(ETickGroup.Normal, ETickOrder.Animation, Update);
        RegisterTick(ETickGroup.Late, ETickOrder.Animation, LateUpdate);
        ResetParticlesPosition();
        OnValidate();
    }

    protected internal override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        InitTransforms();
        CleanupBuffers();
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        OnValidate();
    }

    private void OnValidate()
    {
        UpdateRate = MathF.Max(UpdateRate, 0);
        Damping = Damping.Clamp(0, 1);
        Elasticity = Elasticity.Clamp(0, 1);
        Stiffness = Stiffness.Clamp(0, 1);
        Inert = Inert.Clamp(0, 1);
        Friction = Friction.Clamp(0, 1);
        Radius = MathF.Max(Radius, 0);

        if (!IsEditor || !IsPlaying)
            return;
        
        if (IsRootChanged())
        {
            InitTransforms();
            SetupParticles();
        }
        else
            UpdateParameters();
    }

    #endregion

    #region Update Methods

    private void FixedUpdate()
    {
        if (UpdateMode == EUpdateMode.FixedUpdate)
            PreUpdate();
    }

    private void Update()
    {
        if (UpdateMode != EUpdateMode.FixedUpdate)
            PreUpdate();
        
        ++_updateCount;
    }

    private void LateUpdate()
    {
        if (_preUpdateCount == 0)
            return;

        if (_updateCount > 0)
        {
            _updateCount = 0;
            ++_prepareFrame;
        }

        SetWeight(BlendWeight);
        
        CheckDistance();
        if (IsNeedUpdate())
        {
            Prepare();
            UpdateParticles();
            ApplyParticlesToTransforms();
        }

        _preUpdateCount = 0;
    }

    private void PreUpdate()
    {
        if (IsNeedUpdate())
            InitTransforms();
        
        ++_preUpdateCount;
    }

    private bool IsNeedUpdate()
        => _weight > 0 && !(DistantDisable && _distantDisabled);

    #endregion

    #region Physics Methods

    private void Prepare()
    {
        _deltaTime = Delta;
        switch (UpdateMode)
        {
            case EUpdateMode.Undilated:
                _deltaTime = UndilatedDelta;
                break;
            case EUpdateMode.FixedUpdate:
                _deltaTime = FixedDelta * _preUpdateCount;
                break;
        }

        var translation = Transform.WorldTranslation;
        _objectScale = MathF.Abs(Transform.LossyWorldScale.X);
        _objectMove = translation - _objectPrevPosition;
        _objectPrevPosition = translation;

        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];
            pt.RestGravity = pt.Root.TransformDirection(pt.LocalGravity);

            for (int j = 0; j < pt.Particles.Count; ++j)
            {
                Particle p = pt.Particles[j];
                if (p.Transform is not null)
                {
                    p.TransformPosition = p.Transform.WorldTranslation;
                    p.TransformLocalPosition = p.Transform.LocalTranslation;
                    p.TransformLocalToWorldMatrix = p.Transform.WorldMatrix;
                }
            }
        }

        _effectiveColliders.Clear();

        if (Colliders is null)
            return;
        
        for (int i = 0; i < Colliders.Count; ++i)
        {
            PhysicsChainColliderBase c = Colliders[i];
            if (c is null || !c.IsActive)
                continue;

            _effectiveColliders.Add(c);
            if (c.PrepareFrame == _prepareFrame)
                continue;
            
            c.Prepare();
            c.PrepareFrame = _prepareFrame;
        }
    }

    private void UpdateParticles()
    {
        if (_particleTrees.Count <= 0 || _computeProgram == null)
            return;

        int loop = 1;
        float timeVar = 1.0f;
        float dt = _deltaTime;

        if (UpdateMode == EUpdateMode.Default)
        {
            if (UpdateRate > 0.0f)
                timeVar = dt * UpdateRate;
        }
        else if (UpdateRate > 0.0f)
        {
            float frameTime = 1.0f / UpdateRate;
            _time += dt;
            loop = 0;

            while (_time >= frameTime)
            {
                _time -= frameTime;
                if (++loop >= 3)
                {
                    _time = 0;
                    break;
                }
            }
        }

        if (!_buffersInitialized)
            InitializeBuffers();
        else
            UpdateBufferData();

        if (loop > 0)
        {
            for (int i = 0; i < loop; ++i)
            {
                DispatchCalculateParticles(timeVar, i == 0);
                DispatchApplyConstraints(timeVar);
            }
        }
        else
        {
            DispatchSkipUpdateParticles();
        }

        // Read back results from GPU
        //_particlesBuffer?.GetData(_particlesData);
        UpdateParticlesFromGPUData();
    }

    private void DispatchCalculateParticles(float timeVar, bool applyObjectMove)
    {
        //_computeProgram!.SetFloat("DeltaTime", timeVar);
        //_computeProgram.SetFloat("ObjectScale", _objectScale);
        //_computeProgram.SetFloat("Weight", _weight);
        //_computeProgram.SetVector("Force", new Vector4(Force.X, Force.Y, Force.Z, 0));
        //_computeProgram.SetVector("Gravity", new Vector4(Gravity.X, Gravity.Y, Gravity.Z, 0));
        //_computeProgram.SetVector("ObjectMove", applyObjectMove ? new Vector4(_objectMove.X, _objectMove.Y, _objectMove.Z, 0) : Vector4.Zero);
        //_computeProgram.SetInt("FreezeAxis", (int)FreezeAxis);

        //// Set buffer bindings
        //_computeProgram.SetBuffer(_calculateParticlesKernel, "Particles", _particlesBuffer);
        //_computeProgram.SetBuffer(_calculateParticlesKernel, "ParticleTrees", _particleTreesBuffer);
        //_computeProgram.SetBuffer(_calculateParticlesKernel, "TransformMatrices", _transformMatricesBuffer);
        
        //// Dispatch compute shader
        //int threadGroupsX = (_totalParticleCount + 127) / 128;
        //_computeProgram.DispatchCompute(_calculateParticlesKernel, threadGroupsX, 1, 1);
    }

    private void DispatchApplyConstraints(float timeVar)
    {
        //_computeProgram!.SetFloat("DeltaTime", timeVar);

        //// Set buffer bindings
        //_computeProgram.SetBuffer(_applyConstraintsKernel, "Particles", _particlesBuffer);
        //_computeProgram.SetBuffer(_applyConstraintsKernel, "TransformMatrices", _transformMatricesBuffer);
        //_computeProgram.SetBuffer(_applyConstraintsKernel, "Colliders", _collidersBuffer);
        //_computeProgram.SetInt("ColliderCount", _collidersData.Count);
        
        //// Dispatch compute shader
        //int threadGroupsX = (_totalParticleCount + 127) / 128;
        //_computeProgram.Dispatch(_applyConstraintsKernel, threadGroupsX, 1, 1);
    }

    private void DispatchSkipUpdateParticles()
    {
        //// Set buffer bindings
        //_computeProgram!.SetBuffer(_skipUpdateParticlesKernel, "Particles", _particlesBuffer);
        //_computeProgram.SetBuffer(_skipUpdateParticlesKernel, "TransformMatrices", _transformMatricesBuffer);
        //_computeProgram.SetVector("ObjectMove", new Vector4(_objectMove.X, _objectMove.Y, _objectMove.Z, 0));
        
        //// Dispatch compute shader
        //int threadGroupsX = (_totalParticleCount + 127) / 128;
        //_computeProgram.Dispatch(_skipUpdateParticlesKernel, threadGroupsX, 1, 1);
    }

    private void InitializeBuffers()
    {
        CleanupBuffers();

        // Prepare GPU data
        PrepareGPUData();

        // Create GPU buffers
        //_particlesBuffer = new XRDataBuffer(BufferType.Structured, _particlesData.Count, Marshal.SizeOf<ParticleData>(), false);
        //_particleTreesBuffer = new XRDataBuffer(BufferType.Structured, _particleTreesData.Count, Marshal.SizeOf<ParticleTreeData>(), false);
        //_transformMatricesBuffer = new XRDataBuffer(BufferType.Structured, _transformMatrices.Count, Marshal.SizeOf<Matrix4x4>(), false);
        //_collidersBuffer = new XRDataBuffer(BufferType.Structured, Math.Max(_collidersData.Count, 1), Marshal.SizeOf<ColliderData>(), false);

        // Set initial data
        UpdateBufferData();

        _buffersInitialized = true;
    }

    private void UpdateBufferData()
    {
        // Clear and repopulate data arrays
        PrepareGPUData();

        // Update buffer data
        //_particlesBuffer?.SetData(_particlesData);
        //_particleTreesBuffer?.SetData(_particleTreesData);
        //_transformMatricesBuffer?.SetData(_transformMatrices);
        //_collidersBuffer?.SetData(_collidersData);
    }

    private void PrepareGPUData()
    {
        _particlesData.Clear();
        _particleTreesData.Clear();
        _transformMatrices.Clear();
        _collidersData.Clear();
        _totalParticleCount = 0;

        // Prepare particle tree data
        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];
            
            var treeData = new ParticleTreeData
            {
                LocalGravity = pt.LocalGravity,
                RestGravity = pt.RestGravity,
                ParticleStart = _totalParticleCount,
                ParticleCount = pt.Particles.Count,
                RootWorldToLocal = pt.RootWorldToLocalMatrix,
                BoneTotalLength = pt.BoneTotalLength
            };
            
            _particleTreesData.Add(treeData);
            
            // Prepare particle data
            for (int j = 0; j < pt.Particles.Count; ++j)
            {
                Particle p = pt.Particles[j];
                var particleData = new ParticleData
                {
                    Position = p.Position,
                    PrevPosition = p.PrevPosition,
                    TransformPosition = p.Transform?.WorldTranslation ?? p.Position,
                    TransformLocalPosition = p.Transform?.LocalTranslation ?? p.EndOffset,
                    ParentIndex = p.ParentIndex >= 0 ? p.ParentIndex + _totalParticleCount : -1,
                    Damping = p.Damping,
                    Elasticity = p.Elasticity,
                    Stiffness = p.Stiffness,
                    Inert = p.Inert,
                    Friction = p.Friction,
                    Radius = p.Radius,
                    BoneLength = p.BoneLength,
                    IsColliding = p.IsColliding ? 1 : 0
                };
                
                _particlesData.Add(particleData);
                
                // Add transform matrix to array
                if (p.Transform != null)
                    _transformMatrices.Add(p.TransformLocalToWorldMatrix);
                else if (p.ParentIndex >= 0)
                    _transformMatrices.Add(pt.Particles[p.ParentIndex].TransformLocalToWorldMatrix);
                else
                    _transformMatrices.Add(Matrix4x4.Identity);
            }
            
            _totalParticleCount += pt.Particles.Count;
        }

        // Prepare collider data
        //foreach (var collider in _effectiveColliders)
        //{
        //    if (collider is PhysicsChainSphereCollider sphereCollider)
        //    {
        //        _collidersData.Add(new ColliderData
        //        {
        //            Center = new Vector4(
        //                sphereCollider.Transform.WorldTranslation,
        //                sphereCollider.Radius),
        //            Type = 0
        //        });
        //    }
        //    else if (collider is PhysicsChainCapsuleCollider capsuleCollider)
        //    {
        //        Vector3 start = capsuleCollider.Transform.WorldTranslation;
        //        Vector3 end = capsuleCollider.Transform.TransformPoint(new Vector3(0, capsuleCollider.Height, 0));
                
        //        _collidersData.Add(new ColliderData
        //        {
        //            Center = new Vector4(start, capsuleCollider.Radius),
        //            Params = new Vector4(end, 0),
        //            Type = 1
        //        });
        //    }
        //    else if (collider is PhysicsChainBoxCollider boxCollider)
        //    {
        //        _collidersData.Add(new ColliderData
        //        {
        //            Center = new Vector4(boxCollider.Transform.WorldTranslation, 0),
        //            Params = new Vector4(boxCollider.Size * 0.5f, 0),
        //            Type = 2
        //        });
        //    }
        //}
    }

    private void UpdateParticlesFromGPUData()
    {
        int particleIndex = 0;
        
        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];
            
            for (int j = 0; j < pt.Particles.Count; ++j)
            {
                if (particleIndex < _particlesData.Count)
                {
                    ParticleData data = _particlesData[particleIndex];
                    Particle p = pt.Particles[j];
                    
                    p.Position = data.Position;
                    p.PrevPosition = data.PrevPosition;
                    p.IsColliding = data.IsColliding != 0;
                    
                    particleIndex++;
                }
            }
        }
    }

    private void CleanupBuffers()
    {
        _particlesBuffer?.Dispose();
        _particleTreesBuffer?.Dispose();
        _transformMatricesBuffer?.Dispose();
        _collidersBuffer?.Dispose();

        _particlesBuffer = null;
        _particleTreesBuffer = null;
        _transformMatricesBuffer = null;
        _collidersBuffer = null;
        
        _buffersInitialized = false;
    }

    #endregion

    #region Particle Setup and Management
    public void SetupParticles()
    {
        _particleTrees.Clear();

        if (Root != null)
            AppendParticleTree(Root);
        
        if (Roots != null)
        {
            for (int i = 0; i < Roots.Count; ++i)
            {
                Transform root = Roots[i];
                if (root == null)
                    continue;

                if (_particleTrees.Exists(x => x.Root == root))
                    continue;

                AppendParticleTree(root);
            }
        }

        if (_particleTrees.Count == 0)
            AppendParticleTree(SceneNode.GetTransformAs<Transform>(true)!);

        _objectScale = MathF.Abs(Transform.LossyWorldScale.X);
        _objectPrevPosition = Transform.WorldTranslation;
        _objectMove = Vector3.Zero;

        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];
            AppendParticles(pt, pt.Root, -1, 0.0f);
        }

        UpdateParameters();
        _buffersInitialized = false;
    }

    private void AppendParticleTree(Transform root)
    {
        if (root is null)
            return;

        _particleTrees.Add(new ParticleTree(root));
    }

    private void AppendParticles(ParticleTree tree, Transform? tfm, int parentIndex, float boneLength)
    {
        var ptcl = new Particle(tfm, parentIndex);

        if (tfm != null)
        {
            ptcl.Position = ptcl.PrevPosition = tfm.WorldTranslation;
            ptcl.InitLocalPosition = tfm.LocalTranslation;
            ptcl.InitLocalRotation = tfm.LocalRotation;
        }
        else //end bone
        {
            TransformBase? parent = tree.Particles[parentIndex].Transform;
            if (parent != null)
            {
                if (EndLength > 0.0f)
                {
                    TransformBase? parentParentTfm = parent.Parent;
                    Vector3 endOffset = parentParentTfm != null
                        ? parent.InverseTransformPoint(parent.WorldTranslation * 2.0f - parentParentTfm.WorldTranslation) * EndLength
                        : new Vector3(EndLength, 0.0f, 0.0f);
                    ptcl.EndOffset = endOffset;
                }
                else
                    ptcl.EndOffset = parent.InverseTransformPoint(Transform.TransformDirection(EndOffset) + parent.WorldTranslation);
                                
                ptcl.Position = ptcl.PrevPosition = parent.TransformPoint(ptcl.EndOffset);
            }
            ptcl.InitLocalPosition = Vector3.Zero;
            ptcl.InitLocalRotation = Quaternion.Identity;
        }

        if (parentIndex >= 0 && tree.Particles[parentIndex].Transform is not null)
        {
            var parentPtcl = tree.Particles[parentIndex];
            var parentTfm = parentPtcl.Transform!;
            var parentPtclPos = parentTfm.WorldTranslation;
            boneLength += (parentPtclPos - ptcl.Position).Length();
            ptcl.BoneLength = boneLength;
            tree.BoneTotalLength = MathF.Max(tree.BoneTotalLength, boneLength);
            tree.Particles[parentIndex].ChildCount += 1;
        }

        int index = tree.Particles.Count;
        tree.Particles.Add(ptcl);

        if (tfm != null)
        {
            for (int i = 0; i < tfm.Children.Count; ++i)
            {
                TransformBase child = tfm.Children[i];

                bool exclude = false;
                if (Exclusions != null)
                    exclude = Exclusions.Contains(child);
                
                if (!exclude)
                    AppendParticles(tree, child as Transform, index, boneLength);
                else if (EndLength > 0.0f || EndOffset != Vector3.Zero)
                    AppendParticles(tree, null, index, boneLength);
            }

            if (tfm.Children.Count == 0 && (EndLength > 0.0f || EndOffset != Vector3.Zero))
                AppendParticles(tree, null, index, boneLength);
        }
    }

    public void UpdateParameters()
    {
        SetWeight(BlendWeight);
        for (int i = 0; i < _particleTrees.Count; ++i)
            UpdateParameters(_particleTrees[i]);
    }

    private void UpdateParameters(ParticleTree pt)
    {
        pt.LocalGravity = Vector3.TransformNormal(Gravity, pt.RootWorldToLocalMatrix);

        for (int i = 0; i < pt.Particles.Count; ++i)
        {
            Particle p = pt.Particles[i];
            p.Damping = Damping;
            p.Elasticity = Elasticity;
            p.Stiffness = Stiffness;
            p.Inert = Inert;
            p.Friction = Friction;
            p.Radius = Radius;

            if (pt.BoneTotalLength > 0)
            {
                float a = p.BoneLength / pt.BoneTotalLength;
                if (DampingDistrib != null && DampingDistrib.Keyframes.Count > 0)
                    p.Damping *= DampingDistrib.Evaluate(a);
                if (ElasticityDistrib != null && ElasticityDistrib.Keyframes.Count > 0)
                    p.Elasticity *= ElasticityDistrib.Evaluate(a);
                if (StiffnessDistrib != null && StiffnessDistrib.Keyframes.Count > 0)
                    p.Stiffness *= StiffnessDistrib.Evaluate(a);
                if (InertDistrib != null && InertDistrib.Keyframes.Count > 0)
                    p.Inert *= InertDistrib.Evaluate(a);
                if (FrictionDistrib != null && FrictionDistrib.Keyframes.Count > 0)
                    p.Friction *= FrictionDistrib.Evaluate(a);
                if (RadiusDistrib != null && RadiusDistrib.Keyframes.Count > 0)
                    p.Radius *= RadiusDistrib.Evaluate(a);
            }

            p.Damping = p.Damping.Clamp(0, 1);
            p.Elasticity = p.Elasticity.Clamp(0, 1);
            p.Stiffness = p.Stiffness.Clamp(0, 1);
            p.Inert = p.Inert.Clamp(0, 1);
            p.Friction = p.Friction.Clamp(0, 1);
            p.Radius = MathF.Max(p.Radius, 0);
        }
    }

    private bool IsRootChanged()
    {
        var roots = new List<Transform>();
        if (Root != null)
            roots.Add(Root);
        
        if (Roots != null)
            foreach (var root in Roots)
                if (root != null && !roots.Contains(root))
                    roots.Add(root);

        if (roots.Count == 0)
            roots.Add(SceneNode.GetTransformAs<Transform>(true)!);

        if (roots.Count != _particleTrees.Count)
            return true;

        for (int i = 0; i < roots.Count; ++i)
            if (roots[i] != _particleTrees[i].Root)
                return true;
        
        return false;
    }

    private void ResetParticlesPosition()
    {
        for (int i = 0; i < _particleTrees.Count; ++i)
            ResetParticlesPosition(_particleTrees[i]);

        _objectPrevPosition = Transform.WorldTranslation;
    }

    private static void ResetParticlesPosition(ParticleTree pt)
    {
        for (int i = 0; i < pt.Particles.Count; ++i)
        {
            Particle p = pt.Particles[i];
            if (p.Transform is not null)
                p.Position = p.PrevPosition = p.Transform.WorldTranslation;
            else // end bone
            {
                Transform? pb = pt.Particles[p.ParentIndex].Transform;
                if (pb is not null)
                    p.Position = p.PrevPosition = pb.TransformPoint(p.EndOffset);
            }
            p.IsColliding = false;
        }
    }

    private void InitTransforms()
    {
        for (int i = 0; i < _particleTrees.Count; ++i)
            InitTransforms(_particleTrees[i]);
    }

    private static void InitTransforms(ParticleTree pt)
    {
        for (int i = 0; i < pt.Particles.Count; ++i)
        {
            Particle p = pt.Particles[i];
            if (p.Transform is null)
                continue;
            
            p.Transform.Translation = p.InitLocalPosition;
            p.Transform.Rotation = p.InitLocalRotation;
        }
    }
    #endregion
    
    #region Transform Application
    private void ApplyParticlesToTransforms()
    {
        for (int i = 0; i < _particleTrees.Count; ++i)
            ApplyParticlesToTransforms(_particleTrees[i]);
    }

    private static void ApplyParticlesToTransforms(ParticleTree pt)
    {
        for (int i = 1; i < pt.Particles.Count; ++i)
        {
            Particle child = pt.Particles[i];
            Particle parent = pt.Particles[child.ParentIndex];

            Transform? pTfm = parent.Transform;
            Transform? cTfm = child.Transform;

            if (parent.ChildCount <= 1 && pTfm is not null) // do not modify bone orientation if has more then one child
            {
                Vector3 localPos = cTfm is not null
                    ? cTfm.Translation
                    : child.EndOffset;

                Vector3 v0 = pTfm.TransformDirection(localPos);
                Vector3 v1 = child.Position - parent.Position;
                Quaternion rot = Quaternion.Normalize(XRMath.RotationBetweenVectors(v0, v1));

                pTfm.AddWorldRotationDelta(rot);
            }

            cTfm?.SetWorldTranslation(child.Position);
        }
    }
    #endregion

    #region Distance Check
    private void CheckDistance()
    {
        if (!DistantDisable)
            return;

        TransformBase? rt = ReferenceObject;
        if (rt is null)
        {
            XRCamera? c = State.MainPlayer.ControlledPawn?.CameraComponent?.Camera;
            if (c != null)
                rt = c.Transform;
        }

        if (rt is null)
            return;

        rt.RecalculateMatrices();
        float d2 = (rt.WorldTranslation - Transform.LocalTranslation).LengthSquared();
        bool disable = d2 > DistanceToObject * DistanceToObject;
        if (disable == _distantDisabled)
            return;
        
        if (!disable)
            ResetParticlesPosition();
        _distantDisabled = disable;
    }
    #endregion

    #region Visualization
    private void Render()
    {
        if (!IsActive || Engine.Rendering.State.IsShadowPass)
            return;

        if (IsEditor && !IsPlaying && Transform.HasChanged)
        {
            SetupParticles();
        }

        for (int i = 0; i < _particleTrees.Count; ++i)
            DrawTree(_particleTrees[i]);
    }

    private void DrawTree(ParticleTree pt)
    {
        for (int i = 0; i < pt.Particles.Count; ++i)
        {
            Particle p = pt.Particles[i];
            if (p.ParentIndex >= 0)
            {
                Particle p0 = pt.Particles[p.ParentIndex];
                Engine.Rendering.Debug.RenderLine(p.Position, p0.Position, ColorF4.Orange);
            }
            if (p.Radius > 0)
            {
                float radius = p.Radius * _objectScale;
                Engine.Rendering.Debug.RenderSphere(p.Position, radius, false, ColorF4.Yellow);
            }
        }
    }
    #endregion

    #region Weight Management
    public void SetWeight(float w)
    {
        if (_weight == w)
            return;
        
        if (w == 0)
            InitTransforms();
        else if (_weight == 0)
            ResetParticlesPosition();

        _weight = BlendWeight = w;
    }

    public float Weight => _weight;

    public RenderInfo[] RenderedObjects { get; }

    #endregion
}

/// <summary>
/// Update mode for the physics chain.
/// </summary>
public enum EUpdateMode
{
    Default,
    FixedUpdate,
    Undilated
}

/// <summary>
/// Defines which axis to freeze movement on.
/// </summary>
public enum EFreezeAxis
{
    None = 0,
    X = 1,
    Y = 2,
    Z = 3
}
