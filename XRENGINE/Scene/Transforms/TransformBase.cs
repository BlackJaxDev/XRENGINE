using Extensions;
using MemoryPack;
using System.Collections.Concurrent;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using YamlDotNet.Serialization;
using static XREngine.Engine.Rendering;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// Specifies how parent assignment should be performed.
    /// </summary>
    public enum EParentAssignmentMode
    {
        /// <summary>
        /// Performs the parent assignment immediately on the calling thread with locking.
        /// Use this when you need the hierarchy to be updated synchronously and can tolerate blocking.
        /// </summary>
        Immediate,
        
        /// <summary>
        /// Queues the parent assignment to be processed during PostUpdate.
        /// This is the safest option for multi-threaded scenarios as it doesn't block the render thread.
        /// </summary>
        Deferred,
    }

    /// <summary>
    /// Represents the basis for transforming a scene node in the hierarchy.
    /// Inherit from this class to create custom transformation implementations, or use the Transform class for default functionality.
    /// This class is thread-safe.
    /// </summary>
    [Serializable]
    [MemoryPackable(GenerateType.NoGenerate)]
    public abstract partial class TransformBase : XRWorldObjectBase, IRenderable
    {
        #region Delegates & Events

        public delegate void DelWorldMatrixChanged(TransformBase transform, Matrix4x4 worldMatrix);
        public delegate void DelLocalMatrixChanged(TransformBase transform, Matrix4x4 localMatrix);
        public delegate void DelInverseLocalMatrixChanged(TransformBase transform, Matrix4x4 localInverseMatrix);
        public delegate void DelInverseWorldMatrixChanged(TransformBase transform, Matrix4x4 worldInverseMatrix);
        public delegate void DelRenderMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix);

        public event DelLocalMatrixChanged? LocalMatrixChanged;
        public event DelInverseLocalMatrixChanged? InverseLocalMatrixChanged;
        public event DelWorldMatrixChanged? WorldMatrixChanged;
        public event DelInverseWorldMatrixChanged? InverseWorldMatrixChanged;
        public event DelRenderMatrixChanged? RenderMatrixChanged;

        #endregion

        #region Static Members

        private readonly record struct ParentReassignRequest(
            TransformBase? Child,
            TransformBase? NewParent,
            bool PreserveWorldTransform,
            Action<TransformBase, TransformBase?>? OnApplied);

        private static readonly ConcurrentQueue<ParentReassignRequest> _parentsToReassign = new();

        [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
        public static Type[] TransformTypes { get; } = GetAllTransformTypes();

        [RequiresUnreferencedCode("This method is used to find all transform types in all assemblies in the current domain and should not be trimmed.")]
        private static Type[] GetAllTransformTypes()
            => [.. AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(x => x.GetExportedTypes())
                .Where(x => x.IsSubclassOf(typeof(TransformBase)))];

        [RequiresUnreferencedCode("This method is used to find all transform types in all assemblies in the current domain and should not be trimmed.")]
        public static string[] GetFriendlyTransformTypeSelector()
            => TransformTypes.Select(FriendlyTransformName).ToArray();

        private static string FriendlyTransformName(Type x)
        {
            DisplayNameAttribute? name = x.GetCustomAttribute<DisplayNameAttribute>();
            return $"{name?.DisplayName ?? x.Name} ({x.Assembly.GetName()})";
        }

        internal static void ProcessParentReassignments()
        {
            while (_parentsToReassign.TryDequeue(out ParentReassignRequest req))
            {
                if (req.Child is null)
                    continue;

                if (req.Child.Parent != req.NewParent)
                    req.Child.SetParent(req.NewParent, req.PreserveWorldTransform, EParentAssignmentMode.Immediate);

                if (req.OnApplied is not null)
                {
                    try
                    {
                        req.OnApplied(req.Child, req.NewParent);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, "Deferred parent reassignment callback threw.");
                    }
                }
            }
        }

        public static TransformBase? FindCommonAncestor(TransformBase? a, TransformBase? b)
        {
            if (a is null || b is null)
                return null;

            var ancestorsA = new HashSet<TransformBase>();
            while (a is not null)
            {
                ancestorsA.Add(a);
                a = a.Parent;
            }

            while (b is not null)
            {
                if (ancestorsA.Contains(b))
                    return b;
                b = b.Parent;
            }

            return null;
        }

        public static TransformBase? FindCommonAncestor(params TransformBase[] transforms)
        {
            if (transforms.Length == 0)
                return null;

            TransformBase? commonAncestor = transforms.First();
            foreach (var bone in transforms)
            {
                commonAncestor = FindCommonAncestor(commonAncestor, bone);
                if (commonAncestor is null)
                    break;
            }
            return commonAncestor;
        }

        private static void ReturnChildrenCopy(TransformBase[] copy)
            => ArrayPool<TransformBase>.Shared.Return(copy);

        #endregion

        #region Fields

        private SceneNode? _sceneNode;
        private int _depth = 0;
        private TransformBase? _parent;
        private EventList<TransformBase> _children;
        private float _selectionRadius = 0.01f;
        private Capsule? _capsule = null;
        private bool _immediateLocalMatrixRecalculation = true;

        #endregion

        #region Basic Properties

        [YamlIgnore]
        [Browsable(false)]
        public bool HasChanged { get; protected set; } = false;

        [Browsable(false)]
        public RenderInfo[] RenderedObjects { get; }

        [YamlIgnore]
        [Browsable(false)]
        public float SelectionRadius
        {
            get => _selectionRadius;
            set => SetField(ref _selectionRadius, value);
        }

        [YamlIgnore]
        [Browsable(false)]
        public Capsule? Capsule
        {
            get => _capsule;
            set => SetField(ref _capsule, value);
        }

        public bool ImmediateLocalMatrixRecalculation
        {
            get => _immediateLocalMatrixRecalculation;
            set => SetField(ref _immediateLocalMatrixRecalculation, value);
        }

        #endregion

        #region Hierarchy Properties

        /// <summary>
        /// This is the scene node that this transform is attached to and affects.
        /// Scene nodes are used to house components in relation to the scene hierarchy.
        /// </summary>
        [YamlIgnore]
        [Browsable(false)]
        public virtual SceneNode? SceneNode
        {
            get => _sceneNode;
            set => SetField(ref _sceneNode, value);
        }

        [YamlIgnore]
        [Browsable(false)]
        public int Depth
        {
            get => _depth;
            private set => SetField(ref _depth, value);
        }

        /// <summary>
        /// The parent of this transform.
        /// Will affect this transform's world matrix.
        /// </summary>
        [YamlIgnore]
        [Browsable(false)]
        public virtual TransformBase? Parent
        {
            get => _parent;
            set => SetField(ref _parent, value);
        }

        [YamlIgnore]
        [Browsable(false)]
        public EventList<TransformBase> Children
        {
            get => _children;
            set
            {
                if (value is not null)
                    value.ThreadSafe = true;
                else
                    return;
                SetField(ref _children, value);
            }
        }

        public int ChildCount => _children.Count;

        #endregion

        #region Parent Transform Properties

        /// <summary>
        /// Returns the parent world rotation, or identity if no parent.
        /// </summary>
        [Browsable(false)]
        public Quaternion ParentWorldRotation
            => Parent?.WorldRotation ?? Quaternion.Identity;

        /// <summary>
        /// Returns the parent world translation, or zero if no parent.
        /// </summary>
        [Browsable(false)]
        public Vector3 ParentWorldTranslation
            => Parent?.WorldTranslation ?? Vector3.Zero;

        /// <summary>
        /// Returns the parent inverse world rotation, or identity if no parent.
        /// </summary>
        [Browsable(false)]
        public Quaternion ParentInverseWorldRotation
            => Parent?.InverseWorldRotation ?? Quaternion.Identity;

        /// <summary>
        /// Returns the parent inverse world translation, or zero if no parent.
        /// </summary>
        [Browsable(false)]
        public Vector3 ParentInverseWorldTranslation
            => Vector3.Transform(Vector3.Zero, ParentInverseWorldMatrix);

        /// <summary>
        /// Returns the parent world matrix, or identity if no parent.
        /// </summary>
        [Browsable(false)]
        public Matrix4x4 ParentWorldMatrix => Parent?.WorldMatrix ?? Matrix4x4.Identity;

        /// <summary>
        /// Returns the inverse of the parent world matrix, or identity if no parent.
        /// </summary>
        [Browsable(false)]
        public Matrix4x4 ParentInverseWorldMatrix => Parent?.InverseWorldMatrix ?? Matrix4x4.Identity;

        /// <summary>
        /// Returns the parent bind matrix, or identity if no parent.
        /// </summary>
        [Browsable(false)]
        public Matrix4x4 ParentBindMatrix => Parent?.BindMatrix ?? Matrix4x4.Identity;

        /// <summary>
        /// Returns the inverse of the parent bind matrix, or identity if no parent.
        /// </summary>
        [Browsable(false)]
        public Matrix4x4 ParentInverseBindMatrix => Parent?.InverseBindMatrix ?? Matrix4x4.Identity;

        /// <summary>
        /// Returns the parent render matrix, or identity if no parent.
        /// </summary>
        [Browsable(false)]
        public Matrix4x4 ParentRenderMatrix => Parent?.RenderMatrix ?? Matrix4x4.Identity;

        /// <summary>
        /// Returns the inverse of the parent render matrix, or identity if no parent.
        /// </summary>
        [Browsable(false)]
        public Matrix4x4 ParentInverseRenderMatrix => Parent?.InverseRenderMatrix ?? Matrix4x4.Identity;

        #endregion

        #region World Space Properties

        /// <summary>
        /// This transform's world up vector.
        /// </summary>
        [Browsable(false)]
        public Vector3 WorldUp => Vector3.TransformNormal(Globals.Up, WorldMatrix).Normalized();

        /// <summary>
        /// This transform's world right vector.
        /// </summary>
        [Browsable(false)]
        public Vector3 WorldRight => Vector3.TransformNormal(Globals.Right, WorldMatrix).Normalized();

        /// <summary>
        /// This transform's world forward vector.
        /// </summary>
        [Browsable(false)]
        public Vector3 WorldForward => Vector3.TransformNormal(Globals.Forward, WorldMatrix).Normalized();

        /// <summary>
        /// This transform's position in world space.
        /// </summary>
        [Browsable(false)]
        public virtual Vector3 WorldTranslation
        {
            get
            {
                Matrix4x4.Decompose(WorldMatrix, out _, out _, out Vector3 translation);
                return translation;
            }
        }

        /// <summary>
        /// This transform's rotation in world space.
        /// </summary>
        [Browsable(false)]
        public virtual Quaternion WorldRotation
        {
            get
            {
                Matrix4x4.Decompose(WorldMatrix, out _, out Quaternion rotation, out _);
                return rotation;
            }
        }

        /// <summary>
        /// This transform's inverse rotation in world space.
        /// </summary>
        [Browsable(false)]
        public virtual Quaternion InverseWorldRotation
        {
            get
            {
                Matrix4x4.Decompose(InverseWorldMatrix, out _, out Quaternion rotation, out _);
                return rotation;
            }
        }

        [Browsable(false)]
        public Vector3 LossyWorldScale => WorldMatrix.ExtractScale();

        #endregion

        #region Local Space Properties

        /// <summary>
        /// This transform's local up vector.
        /// </summary>
        [Browsable(false)]
        public Vector3 LocalUp => Vector3.TransformNormal(Globals.Up, LocalMatrix).Normalized();

        /// <summary>
        /// This transform's local right vector.
        /// </summary>
        [Browsable(false)]
        public Vector3 LocalRight => Vector3.TransformNormal(Globals.Right, LocalMatrix).Normalized();

        /// <summary>
        /// This transform's local forward vector.
        /// </summary>
        [Browsable(false)]
        public Vector3 LocalForward => Vector3.TransformNormal(Globals.Forward, LocalMatrix).Normalized();

        /// <summary>
        /// This transform's position in local space relative to the parent.
        /// </summary>
        [Browsable(false)]
        public Vector3 LocalTranslation
        {
            get
            {
                Matrix4x4.Decompose(LocalMatrix, out _, out _, out Vector3 translation);
                return translation;
            }
        }

        /// <summary>
        /// This transform's rotation relative to its parent.
        /// </summary>
        [Browsable(false)]
        public virtual Quaternion LocalRotation
        {
            get
            {
                Matrix4x4.Decompose(LocalMatrix, out _, out Quaternion rotation, out _);
                return rotation;
            }
        }

        /// <summary>
        /// This transform's inverse rotation relative to its parent.
        /// </summary>
        [Browsable(false)]
        public virtual Quaternion InverseLocalRotation
        {
            get
            {
                Matrix4x4.Decompose(InverseLocalMatrix, out _, out Quaternion rotation, out _);
                return rotation;
            }
        }

        #endregion

        #region Render Space Properties

        [Browsable(false)]
        public Vector3 RenderForward => Vector3.TransformNormal(Globals.Forward, RenderMatrix).Normalized();

        [Browsable(false)]
        public Vector3 RenderUp => Vector3.TransformNormal(Globals.Up, RenderMatrix).Normalized();

        [Browsable(false)]
        public Vector3 RenderRight => Vector3.TransformNormal(Globals.Right, RenderMatrix).Normalized();

        [Browsable(false)]
        public Vector3 RenderTranslation => RenderMatrix.Translation;

        [Browsable(false)]
        public Quaternion RenderRotation
        {
            get
            {
                Matrix4x4.Decompose(RenderMatrix, out _, out Quaternion rotation, out _);
                return rotation;
            }
        }

        [Browsable(false)]
        public Quaternion InverseRenderRotation
        {
            get
            {
                Matrix4x4.Decompose(InverseRenderMatrix, out _, out Quaternion rotation, out _);
                return rotation;
            }
        }

        #endregion

        #region Render Matrix

        private Matrix4x4 _renderMatrix = Matrix4x4.Identity;
        private readonly object _renderMatrixLock = new();

        /// <summary>
        /// This transform's render matrix.
        /// Thread-safe via locking for atomic access.
        /// </summary>
        [Browsable(false)]
        public Matrix4x4 RenderMatrix
        {
            get { lock (_renderMatrixLock) return _renderMatrix; }
            internal set { lock (_renderMatrixLock) _renderMatrix = value; }
        }

        private Matrix4x4 _inverseRenderMatrix = Matrix4x4.Identity;
        private volatile bool _inverseRenderMatrixDirty = true;
        private readonly object _inverseRenderMatrixLock = new();

        /// <summary>
        /// This transform's inverse render matrix.
        /// Thread-safe with lazy calculation and locking.
        /// </summary>
        [Browsable(false)]
        public Matrix4x4 InverseRenderMatrix
        {
            get
            {
                /*
                if (!_inverseRenderMatrixDirty)
                {
                    lock (_inverseRenderMatrixLock)
                        return _inverseRenderMatrix;
                }

                lock (_inverseRenderMatrixLock)
                {
                    if (!_inverseRenderMatrixDirty)
                        return _inverseRenderMatrix;
*/
                    var inverted = Matrix4x4.Invert(RenderMatrix, out var inv) ? inv : Matrix4x4.Identity;
                    _inverseRenderMatrix = inverted;
                    _inverseRenderMatrixDirty = false;
                    return inverted;
                    /*
                }
                */
            }
        }

        #endregion

        #region Local Matrix

        private bool _localChanged = false;
        private Matrix4x4 _localMatrix;
        private readonly object _localMatrixLock = new();

        /// <summary>
        /// This transform's local matrix relative to its parent.
        /// Thread-safe via locking for atomic access.
        /// </summary>
        [Browsable(false)]
        public Matrix4x4 LocalMatrix { get { lock (_localMatrixLock) return _localMatrix; } }

        #endregion

        #region World Matrix

        private bool _worldChanged = false;
        private Matrix4x4 _worldMatrix;
        private readonly object _worldMatrixLock = new();

        /// <summary>
        /// This transform's world matrix relative to the root of the scene (all ancestor transforms accounted for).
        /// Thread-safe via locking for atomic access.
        /// </summary>
        [Browsable(false)]
        public Matrix4x4 WorldMatrix { get { lock (_worldMatrixLock) return _worldMatrix; } }

        #endregion

        #region Inverse Local Matrix

        private Matrix4x4 _inverseLocalMatrix;
        private readonly object _inverseLocalMatrixLock = new();

        /// <summary>
        /// The inverse of this transform's local matrix.
        /// Thread-safe via locking for atomic access.
        /// </summary>
        [Browsable(false)]
        public Matrix4x4 InverseLocalMatrix { get { lock (_inverseLocalMatrixLock) return _inverseLocalMatrix; } }

        #endregion

        #region Inverse World Matrix

        private Matrix4x4 _inverseWorldMatrix;
        private readonly object _inverseWorldMatrixLock = new();

        /// <summary>
        /// The inverse of this transform's world matrix.
        /// Thread-safe via locking for atomic access.
        /// </summary>
        [Browsable(false)]
        public Matrix4x4 InverseWorldMatrix { get { lock (_inverseWorldMatrixLock) return _inverseWorldMatrix; } }

        #endregion

        #region Constructors

        protected TransformBase() : this(null) { }

        protected TransformBase(TransformBase? parent)
        {
            _sceneNode = null;
            Depth = parent?.Depth + 1 ?? 0;
            _children = new EventList<TransformBase>() { ThreadSafe = true };
            _children.PostAnythingAdded += ChildAdded;
            _children.PostAnythingRemoved += ChildRemoved;

            _localMatrix = Matrix4x4.Identity;
            _worldMatrix = Matrix4x4.Identity;
            _inverseLocalMatrix = Matrix4x4.Identity;
            _inverseWorldMatrix = Matrix4x4.Identity;

            RenderInfo = RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OnTopForward, RenderDebug));
            RenderInfo.Layer = DefaultLayers.GizmosIndex;
            RenderedObjects = GetDebugRenderInfo();
            DebugRender = Engine.Rendering.Settings.RenderTransformDebugInfo;

            SetParent(parent, false, EParentAssignmentMode.Immediate);
        }

        #endregion

        #region Public Methods

        public override string ToString()
            => $"{GetType().GetFriendlyName()} ({SceneNode?.Name ?? Name ?? "<no name>"})";

        public Vector3 GetWorldUp()
            => Engine.IsRenderThread ? RenderUp : WorldUp;

        public Vector3 GetWorldRight()
            => Engine.IsRenderThread ? RenderRight : WorldRight;

        public Vector3 GetWorldForward()
            => Engine.IsRenderThread ? RenderForward : WorldForward;

        public Vector3 GetWorldTranslation()
            => Engine.IsRenderThread ? RenderTranslation : WorldTranslation;

        public Quaternion GetWorldRotation()
            => Engine.IsRenderThread ? RenderRotation : WorldRotation;

        public Quaternion GetInverseWorldRotation()
            => Engine.IsRenderThread ? InverseRenderRotation : InverseWorldRotation;

        /// <summary>
        /// Used to verify if the placement info for a child is the right type before being returned to the requester.
        /// </summary>
        public virtual void VerifyPlacementInfo(UITransform childTransform, ref UIChildPlacementInfo? placementInfo) { }

        /// <summary>
        /// Used by the physics system to derive a world matrix from a physics body into the components used by this transform.
        /// </summary>
        public void DeriveWorldMatrix(Matrix4x4 value, bool networkSmoothed = false)
            => DeriveLocalMatrix(ParentInverseWorldMatrix * value, networkSmoothed);

        /// <summary>
        /// Derives components to create the local matrix from the given matrix.
        /// </summary>
        public virtual void DeriveLocalMatrix(Matrix4x4 value, bool networkSmoothed = false) { }

        #endregion

        #region Hierarchy Methods

        /// <summary>
        /// Adds a child transform to this transform.
        /// </summary>
        /// <param name="child">The transform to add as a child.</param>
        /// <param name="childPreservesWorldTransform">If true, the child's world matrix will be preserved.</param>
        /// <param name="mode">How the parent assignment should be performed.</param>
        public void AddChild(
            TransformBase child,
            bool childPreservesWorldTransform,
            EParentAssignmentMode mode,
            Action<TransformBase, TransformBase?>? onApplied = null)
        {
            if (child is null || child.Parent == this)
                return;
            child.SetParent(this, childPreservesWorldTransform, mode, onApplied);
        }

        /// <summary>
        /// Adds a child transform to this transform.
        /// </summary>
        /// <param name="child">The transform to add as a child.</param>
        /// <param name="childPreservesWorldTransform">If true, the child's world matrix will be preserved.</param>
        /// <param name="now">If true, performs immediately; if false, defers to PostUpdate.</param>
        [Obsolete("Use AddChild(child, preserveWorld, EParentAssignmentMode) instead")]
        public void AddChild(TransformBase child, bool childPreservesWorldTransform, bool now)
            => AddChild(child, childPreservesWorldTransform, now ? EParentAssignmentMode.Immediate : EParentAssignmentMode.Deferred);

        /// <summary>
        /// Removes a child transform from this transform.
        /// </summary>
        /// <param name="child">The transform to remove.</param>
        /// <param name="mode">How the parent assignment should be performed.</param>
        public void RemoveChild(
            TransformBase child,
            EParentAssignmentMode mode,
            Action<TransformBase, TransformBase?>? onApplied = null)
        {
            if (child is null || child.Parent != this)
                return;
            child.SetParent(null, false, mode, onApplied);
        }

        /// <summary>
        /// Removes a child transform from this transform.
        /// </summary>
        /// <param name="child">The transform to remove.</param>
        /// <param name="now">If true, performs immediately; if false, defers to PostUpdate.</param>
        [Obsolete("Use RemoveChild(child, EParentAssignmentMode) instead")]
        public void RemoveChild(TransformBase child, bool now)
            => RemoveChild(child, now ? EParentAssignmentMode.Immediate : EParentAssignmentMode.Deferred);

        /// <summary>
        /// Sets the parent of this transform.
        /// </summary>
        /// <param name="newParent">The new parent transform, or null to detach.</param>
        /// <param name="preserveWorldTransform">If true, the world matrix will be preserved after reparenting.</param>
        /// <param name="mode">How the parent assignment should be performed:
        /// <list type="bullet">
        /// <item><see cref="EParentAssignmentMode.Immediate"/>: Performs immediately with locking (may block)</item>
        /// <item><see cref="EParentAssignmentMode.Deferred"/>: Queues for PostUpdate processing (non-blocking, render-safe)</item>
        /// </list>
        /// </param>
        public void SetParent(
            TransformBase? newParent,
            bool preserveWorldTransform,
            EParentAssignmentMode mode,
            Action<TransformBase, TransformBase?>? onApplied = null)
        {
            switch (mode)
            {
                case EParentAssignmentMode.Immediate:
                    if (preserveWorldTransform)
                    {
                        var worldMatrix = WorldMatrix;
                        Parent = newParent;
                        DeriveWorldMatrix(worldMatrix);
                    }
                    else
                        Parent = newParent;

                    onApplied?.Invoke(this, newParent);
                    break;
                    
                case EParentAssignmentMode.Deferred:
                    _parentsToReassign.Enqueue(new ParentReassignRequest(this, newParent, preserveWorldTransform, onApplied));
                    break;
            }
        }

        /// <summary>
        /// Sets the parent of this transform.
        /// </summary>
        /// <param name="newParent">The new parent transform, or null to detach.</param>
        /// <param name="preserveWorldTransform">If true, the world matrix will be preserved after reparenting.</param>
        /// <param name="now">If true, performs immediately; if false, defers to PostUpdate.</param>
        [Obsolete("Use SetParent(newParent, preserveWorld, EParentAssignmentMode) instead")]
        public void SetParent(TransformBase? newParent, bool preserveWorldTransform, bool now = false)
            => SetParent(newParent, preserveWorldTransform, now ? EParentAssignmentMode.Immediate : EParentAssignmentMode.Deferred);

        #endregion

        #region Child Search Methods

        public TransformBase? FindChild(string name, StringComparison comp = StringComparison.Ordinal)
        {
            lock (_children)
                return _children.FirstOrDefault(x => x.Name?.Equals(name, comp) ?? false);
        }
        public TransformBase? FindChild(Func<TransformBase, bool> predicate)
        {
            lock (_children)
                return _children.FirstOrDefault(predicate);
        }
        public TransformBase? FindChildStartsWith(string name, StringComparison comp = StringComparison.Ordinal)
        {
            lock (_children)
                return _children.FirstOrDefault(x => x.Name?.StartsWith(name, comp) ?? false);
        }
        public TransformBase? FindChildEndsWith(string name, StringComparison comp = StringComparison.Ordinal)
        {
            lock (_children)
                return _children.FirstOrDefault(x => x.Name?.EndsWith(name, comp) ?? false);
        }
        public TransformBase? FindChildContains(string name, StringComparison comp = StringComparison.Ordinal)
        {
            lock (_children)
                return _children.FirstOrDefault(x => x.Name?.Contains(name, comp) ?? false);
        }

        public TransformBase? GetChild(int index)
        {
            lock (_children)
                return _children.IndexInRange(index) ? _children[index] : null;
        }
        public TransformBase? FindDescendant(string name)
        {
            lock (_children)
            {
                TransformBase? child = _children.FirstOrDefault(x => x.Name == name);
                if (child is not null)
                    return child;
                foreach (TransformBase c in _children)
                {
                    child = c.FindDescendant(name);
                    if (child is not null)
                        return child;
                }
            }
            return null;
        }

        public TransformBase? FindDescendant(Func<TransformBase, bool> predicate)
        {
            lock (_children)
            {
                TransformBase? child = _children.FirstOrDefault(predicate);
                if (child is not null)
                    return child;
                foreach (TransformBase c in _children)
                {
                    child = c.FindDescendant(predicate);
                    if (child is not null)
                        return child;
                }
            }
            return null;
        }

        public TransformBase? TryGetChildAt(int index)
        {
            lock (_children)
                return _children.IndexInRange(index) ? _children[index] : null;
        }

        #endregion

        #region Matrix Recalculation Methods

        /// <summary>
        /// Recalculates the local and world matrices for this transform.
        /// Children are not recalculated.
        /// Returns true if children need to be recalculated.
        /// </summary>
        public bool RecalculateMatrices(bool forceWorldRecalc = false, bool setRenderMatrixNow = false)
        {
            bool worldChanged = Volatile.Read(ref _worldChanged);
            bool recalcWorld = worldChanged || forceWorldRecalc;

            if (Volatile.Read(ref _localChanged))
                RecalcLocal();

            if (recalcWorld)
                RecalcWorld();

            if (setRenderMatrixNow || World is null)
                SetRenderMatrix(WorldMatrix, false).Wait();

            return recalcWorld;
        }

        /// <summary>
        /// Recalculates the local and world matrices for this transform and all children.
        /// If recalcChildrenNow is true, all children will be recalculated immediately.
        /// If false, they will be marked as dirty and recalculated at the end of the update.
        /// </summary>
        public virtual Task RecalculateMatrixHeirarchy(bool forceWorldRecalc, bool setRenderMatrixNow, ELoopType childRecalcType)
            => RecalculateMatrices(forceWorldRecalc, setRenderMatrixNow)
                ? childRecalcType switch
                {
                    ELoopType.Asynchronous => ChildrenRecalcAsync(setRenderMatrixNow),
                    ELoopType.Parallel => Task.Run(() => ChildrenRecalcParallel(setRenderMatrixNow)),
                    _ => ChildrenRecalcSequential(setRenderMatrixNow),
                }
                : Task.CompletedTask;

        public void RecalcLocal()
        {
            Matrix4x4 localMatrix = CreateLocalMatrix();
            lock (_localMatrixLock)
                _localMatrix = localMatrix;
            RecalcLocalInv();
            Volatile.Write(ref _localChanged, false);
            OnLocalMatrixChanged(localMatrix);
        }

        public void RecalcWorld()
        {
            Matrix4x4 worldMatrix = CreateWorldMatrix();
            lock (_worldMatrixLock)
                _worldMatrix = worldMatrix;
            RecalcWorldInv();
            Volatile.Write(ref _worldChanged, false);
            OnWorldMatrixChanged(worldMatrix);
        }

        internal void RecalcLocalInv()
        {
            if (!TryCreateInverseLocalMatrix(out Matrix4x4 inverted))
                return;

            lock (_inverseLocalMatrixLock)
                _inverseLocalMatrix = inverted;
            OnInverseLocalMatrixChanged(inverted);
        }

        internal void RecalcWorldInv()
        {
            if (!TryCreateInverseWorldMatrix(out Matrix4x4 inverted))
                return;

            lock (_inverseWorldMatrixLock)
                _inverseWorldMatrix = inverted;
            OnInverseWorldMatrixChanged(inverted);
        }

        public Task SetRenderMatrix(Matrix4x4 matrix, bool recalcAllChildRenderMatrices = true)
        {
            RenderMatrix = matrix;
            OnRenderMatrixChanged();

            if (recalcAllChildRenderMatrices)
                return RecalculateRenderMatrixHierarchy(Engine.Rendering.Settings.RecalcChildMatricesLoopType);
            else
                return Task.CompletedTask;
        }

        #endregion

        #region Matrix Modification Marking

        protected void MarkLocalModified()
        {
            MarkLocalModified(false);
        }
        /// <summary>
        /// Marks the local matrix as modified, which will cause it to be recalculated on the next access.
        /// This method is thread-safe and can be called from any thread.
        /// </summary>
        protected void MarkLocalModified(bool forceDefer)
        {
            if (ImmediateLocalMatrixRecalculation && !forceDefer)
            {
                RecalcLocal();
                Volatile.Write(ref _localChanged, false);
            }
            else
                Volatile.Write(ref _localChanged, true);

            MarkWorldModified();
            HasChanged = true;
        }

        /// <summary>
        /// Marks the world matrix as modified, which will cause it to be recalculated on the next access.
        /// This method is thread-safe and can be called from any thread.
        /// Children will have their world matrices updated relative to their parent when matrices are processed by the world instance.
        /// </summary>
        protected void MarkWorldModified()
        {
            Volatile.Write(ref _worldChanged, true);
            World?.AddDirtyTransform(this);
            HasChanged = true;
        }

        #endregion

        #region Overridable Matrix Creation Methods

        /// <summary>
        /// Creates the world matrix by multiplying local matrix with parent's world matrix.
        /// Snapshots parent matrix atomically to avoid reading partially-written data during recalculation.
        /// </summary>
        protected virtual Matrix4x4 CreateWorldMatrix()
        {
            // Snapshot parent reference and matrix atomically to avoid race conditions
            var parent = Parent;
            if (parent is null)
                return LocalMatrix;

            // Capture parent's world matrix once - this uses Volatile.Read internally
            // to ensure we get a complete, consistent matrix value
            Matrix4x4 parentWorldMatrix = parent.WorldMatrix;
            return LocalMatrix * parentWorldMatrix;
        }

        protected virtual bool TryCreateInverseLocalMatrix(out Matrix4x4 inverted)
            => Matrix4x4.Invert(LocalMatrix, out inverted);
        protected virtual bool TryCreateInverseWorldMatrix(out Matrix4x4 inverted)
            => Matrix4x4.Invert(WorldMatrix, out inverted);
        protected abstract Matrix4x4 CreateLocalMatrix();

        #endregion

        #region Matrix Event Handlers

        protected virtual void OnLocalMatrixChanged(Matrix4x4 localMatrix)
            => LocalMatrixChanged?.Invoke(this, localMatrix);

        protected virtual void OnWorldMatrixChanged(Matrix4x4 worldMatrix)
        {
            World?.EnqueueRenderTransformChange(this, worldMatrix);
            WorldMatrixChanged?.Invoke(this, worldMatrix);
        }

        protected virtual void OnInverseLocalMatrixChanged(Matrix4x4 localInverseMatrix)
            => InverseLocalMatrixChanged?.Invoke(this, localInverseMatrix);

        protected virtual void OnInverseWorldMatrixChanged(Matrix4x4 worldInverseMatrix)
            => InverseWorldMatrixChanged?.Invoke(this, worldInverseMatrix);

        protected virtual void OnRenderMatrixChanged()
        {
            _inverseRenderMatrixDirty = true;
            RenderMatrixChanged?.Invoke(this, RenderMatrix);
        }

        #endregion

        #region Property Change Handlers

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Parent):
                        _parent?._children.Remove(this);
                        break;
                    case nameof(Children):
                        _children.PostAnythingAdded -= ChildAdded;
                        _children.PostAnythingRemoved -= ChildRemoved;
                        lock (_children)
                        {
                            foreach (var child in _children)
                                if (child is not null)
                                {
                                    child.Parent = null;
                                    child.World = null;
                                }
                        }
                        break;
                }
            }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Parent):
                    if (_parent is not null)
                    {
                        Depth = _parent.Depth + 1;
                        _parent._children.Add(this);
                        World ??= _parent.World;
                    }
                    else
                        Depth = 0;
                    if (SceneNode is not null)
                        SceneNode.World = World;
                    MarkWorldModified();
                    break;
                case nameof(SceneNode):
                    var w = SceneNode?.World;
                    if (w is not null)
                        World = w;
                    break;
                case nameof(World):
                    foreach (var obj in RenderedObjects)
                        obj.WorldInstance = World;
                    MarkWorldModified();
                    if (SceneNode is not null)
                        SceneNode.World = World;
                    lock (_children)
                    {
                        foreach (var child in _children)
                            if (child is not null)
                                child.World = World;
                    }
                    break;
                case nameof(Children):
                    _children.PostAnythingAdded += ChildAdded;
                    _children.PostAnythingRemoved += ChildRemoved;
                    _children.ThreadSafe = true;
                    lock (_children)
                    {
                        foreach (var child in _children)
                            if (child is not null)
                            {
                                child.Parent = this;
                                child.World = World;
                            }
                    }
                    break;
                case nameof(SelectionRadius):
                    MakeCapsule();
                    break;
            }
        }

        private void ChildAdded(TransformBase e)
            => e.Parent = this;

        private void ChildRemoved(TransformBase e)
            => e.Parent = null;

        #endregion

        #region Scene Node Lifecycle

        /// <summary>
        /// Called when the scene node this transform is attached to is activated in the scene.
        /// </summary>
        protected internal virtual void OnSceneNodeActivated()
        {
        }

        /// <summary>
        /// Called when play begins for the scene containing this transform.
        /// </summary>
        protected internal virtual void OnSceneNodeBeginPlay()
        {
        }

        /// <summary>
        /// Called when the scene node this transform is attached to is deactivated in the scene.
        /// </summary>
        protected internal virtual void OnSceneNodeDeactivated()
        {
        }

        /// <summary>
        /// Called when play ends for the scene containing this transform.
        /// </summary>
        protected internal virtual void OnSceneNodeEndPlay()
        {
        }

        #endregion

        #region Debug Rendering

        [YamlIgnore]
        public bool DebugRender
        {
            get => RenderedObjects.TryGet(0)?.IsVisible ?? false;
            set
            {
                foreach (var obj in RenderedObjects)
                    obj.IsVisible = value;
            }
        }

        protected RenderInfo3D RenderInfo { get; set; }

        protected virtual RenderInfo[] GetDebugRenderInfo()
        {
            //RemakeCapsule();
            return [RenderInfo];
        }

        protected virtual void RenderDebug()
        {
            if (Engine.Rendering.State.IsShadowPass)
                return;

            var settings = Engine.Rendering.Settings;

            if (settings.RenderTransformLines)
                Engine.Rendering.Debug.RenderLine(
                    Parent?.RenderTranslation ?? Vector3.Zero,
                    RenderTranslation,
                    settings.TransformLineColor);

            if (settings.RenderTransformPoints)
                Engine.Rendering.Debug.RenderPoint(
                    RenderTranslation,
                    settings.TransformPointColor);

            if (settings.RenderTransformCapsules && Capsule is not null)
                Engine.Rendering.Debug.RenderCapsule(Capsule.Value, settings.TransformCapsuleColor);
        }

        private Capsule MakeCapsule()
        {
            Vector3 parentPos = Parent?.WorldTranslation ?? Vector3.Zero;
            Vector3 thisPos = WorldTranslation;
            Vector3 center = (parentPos + thisPos) / 2.0f;
            Vector3 dir = (thisPos - parentPos).Normalized();
            float halfHeight = Vector3.Distance(parentPos, thisPos) / 2.0f;
            return new Capsule(center, dir, SelectionRadius, halfHeight);
        }

        private void RemakeCapsule()
        {
            var c = MakeCapsule();

            bool axisAligned = Engine.Rendering.Settings.TransformCullingIsAxisAligned;
            if (axisAligned)
            {
                RenderInfo.LocalCullingVolume = c.GetAABB(true);
                RenderInfo.CullingOffsetMatrix = Matrix4x4.Identity;
            }
            else
            {
                RenderInfo.LocalCullingVolume = c.GetAABB(false, true, out Quaternion dirToUp);
                RenderInfo.CullingOffsetMatrix = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Quaternion.Inverse(dirToUp))) * Matrix4x4.CreateTranslation(c.Center);
            }

            Capsule = c;
        }

        #endregion

        #region Children Recalculation (Private)

        private void ChildrenRecalcParallel(bool setRenderMatrixNow)
        {
            var childrenCopy = RentChildrenCopy(out int count);
            try
            {
                // NOTE: Parallel.For does not understand async delegates. Use a synchronous body.
                // Each child can recurse sequentially within its own subtree to avoid nested parallelism.
                Parallel.For(0, count, i =>
                {
                    TransformBase child = childrenCopy[i];
                    child.RecalculateMatrixHeirarchy(true, setRenderMatrixNow, ELoopType.Sequential)
                        .GetAwaiter()
                        .GetResult();
                });
            }
            finally
            {
                ReturnChildrenCopy(childrenCopy);
            }
        }

        private async Task ChildrenRecalcSequential(bool setRenderMatrixNow)
        {
            var childrenCopy = RentChildrenCopy(out int count);
            try
            {
                for (int i = 0; i < count; i++)
                    await childrenCopy[i].RecalculateMatrixHeirarchy(true, setRenderMatrixNow, ELoopType.Sequential);
            }
            finally
            {
                ReturnChildrenCopy(childrenCopy);
            }
        }

        private async Task ChildrenRecalcAsync(bool setRenderMatrixNow)
        {
            var childrenCopy = RentChildrenCopy(out int count);
            try
            {
                var tasks = new Task[count];
                for (int i = 0; i < count; i++)
                    tasks[i] = childrenCopy[i].RecalculateMatrixHeirarchy(true, setRenderMatrixNow, ELoopType.Asynchronous);
                await Task.WhenAll(tasks);
            }
            finally
            {
                ReturnChildrenCopy(childrenCopy);
            }
        }

        private Task RecalculateRenderMatrixHierarchy(ELoopType childRecalcType)
            => childRecalcType switch
            {
                ELoopType.Asynchronous => AsyncChildrenRenderMatrixRecalc(),
                ELoopType.Parallel => Task.Run(ParallelChildrenRenderMatrixRecalc),
                _ => SequentialChildrenRenderMatrixRecalc(),
            };

        private void ParallelChildrenRenderMatrixRecalc()
        {
            var childrenCopy = RentChildrenCopy(out int count);
            // Snapshot render matrix once for all children
            Matrix4x4 parentRenderMatrix = RenderMatrix;
            try
            {
                // NOTE: Parallel.For does not understand async delegates. Use a synchronous body.
                Parallel.For(0, count, i =>
                {
                    TransformBase child = childrenCopy[i];
                    child.SetRenderMatrix(child.LocalMatrix * parentRenderMatrix, false)
                        .GetAwaiter()
                        .GetResult();
                });
            }
            finally
            {
                ReturnChildrenCopy(childrenCopy);
            }
        }

        private async Task SequentialChildrenRenderMatrixRecalc()
        {
            var childrenCopy = RentChildrenCopy(out int count);
            // Snapshot render matrix once for all children
            Matrix4x4 parentRenderMatrix = RenderMatrix;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    TransformBase child = childrenCopy[i];
                    await child.SetRenderMatrix(child.LocalMatrix * parentRenderMatrix, false);
                }
            }
            finally
            {
                ReturnChildrenCopy(childrenCopy);
            }
        }

        private async Task AsyncChildrenRenderMatrixRecalc()
        {
            var childrenCopy = RentChildrenCopy(out int count);
            // Snapshot render matrix once for all children
            Matrix4x4 parentRenderMatrix = RenderMatrix;
            try
            {
                var tasks = new Task[count];
                for (int i = 0; i < count; i++)
                {
                    TransformBase child = childrenCopy[i];
                    tasks[i] = child.SetRenderMatrix(child.LocalMatrix * parentRenderMatrix, true);
                }
                await Task.WhenAll(tasks);
            }
            finally
            {
                ReturnChildrenCopy(childrenCopy);
            }
        }

        private TransformBase[] RentChildrenCopy(out int count)
        {
            lock (_children)
            {
                count = _children.Count;
                TransformBase[] childrenCopy = ArrayPool<TransformBase>.Shared.Rent(count);
                _children.CopyTo(childrenCopy, 0);
                return childrenCopy;
            }
        }

        #endregion

        #region Matrix Generation Helpers (Private)

        private Matrix4x4 GenerateLocalMatrixFromWorld()
            => Parent is null || !Matrix4x4.Invert(Parent.WorldMatrix, out Matrix4x4 inverted)
                ? WorldMatrix
                : WorldMatrix * inverted;

        private Matrix4x4 GenerateInverseLocalMatrixFromInverseWorld()
            => Parent is null || !Matrix4x4.Invert(Parent.WorldMatrix, out Matrix4x4 inverted)
                ? InverseWorldMatrix
                : inverted * InverseWorldMatrix;

        #endregion

        #region IList/Collection Implementation

        public TransformBase this[int index]
        {
            get => _children[index];
            set => _children[index] = value;
        }

        public int IndexOf(TransformBase item)
            => _children.IndexOf(item);

        public void Insert(int index, TransformBase item)
            => _children.Insert(index, item);

        public void RemoveAt(int index)
            => _children.RemoveAt(index);

        public void Clear()
            => _children.Clear();

        public bool Contains(TransformBase item)
            => _children.Contains(item);

        public void CopyTo(TransformBase[] array, int arrayIndex)
            => _children.CopyTo(array, arrayIndex);

        public void Add(TransformBase item)
            => _children.Add(item);

        public bool Remove(TransformBase item)
            => _children.Remove(item);

        #endregion
    }
}